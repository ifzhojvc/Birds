import argparse
import json
import os
import shutil
import subprocess
from pathlib import Path
from uuid import UUID

from verify_subscriptions import Protocol, ROOT


class QaProtocol(Protocol):
    def save(self):
        (self.artifacts / "protocol.json").write_text(json.dumps(self.report, ensure_ascii=False, indent=2), encoding="utf-8")

    def submit_photo(self):
        sample = ROOT / "sample_images/01_Cardinalis_cardinalis.jpg"
        with sample.open("rb") as source:
            accepted = self.request("POST", "/analysis/image", 202, files={"file": (sample.name, source)})
        return str(UUID(accepted["analysisId"]))

    def status(self, job):
        return self.request("GET", f"/analyses/{job}/status")

    def assert_completed_once(self, job):
        def terminal():
            status = self.status(job)
            return status if status["status"] in {"Completed", "Failed"} else None

        status = self.wait(terminal, 240)
        assert status["status"] == "Completed", status
        assert len(status["result"]["predictions"]) == 3
        rows = self.sql(f"""
            SELECT a.JobId,a.Status,a.MetadataJson,u.Status UsageStatus,
                (SELECT COUNT(*) FROM Predictions p WHERE p.AnalysisId=a.Id) Predictions,
                (SELECT COUNT(*) FROM OutboxMessages o WHERE o.AnalysisId=a.JobId AND o.PublishedAt IS NOT NULL) PublishedOutbox
            FROM Analyses a JOIN UsageRecords u ON u.JobId=a.JobId WHERE a.JobId='{job}' FOR JSON PATH
        """)
        assert len(rows) == 1 and rows[0]["UsageStatus"] == "Consumed"
        assert rows[0]["Predictions"] == 3 and rows[0]["PublishedOutbox"] == 1
        assert json.loads(rows[0]["MetadataJson"])["mock_used"] is False
        return status

    def capture_evidence(self):
        super().capture_evidence()
        if self.args.browser:
            env = {**os.environ, "BIRDLAB_E2E_URL": f"http://127.0.0.1:{self.args.port}",
                   "BIRDLAB_E2E_REPORT_DIR": str(self.artifacts / "browser")}
            command = ["node", "node_modules/@playwright/test/cli.js", "test"]
            with (self.artifacts / "browser.log").open("w", encoding="utf-8") as log:
                subprocess.run(command, cwd=ROOT / "web-app", env=env, stdout=log, stderr=subprocess.STDOUT,
                               check=True, timeout=300)
            self.record("React browser uploaded a real photo, resumed polling on reload and deleted its own SQL history row")

        self.compose("stop", "rabbitmq")
        try:
            pending = self.submit_photo()

            def publication_failed():
                rows = self.sql(f"SELECT Attempts,PublishedAt,LastError FROM OutboxMessages WHERE AnalysisId='{pending}' FOR JSON PATH")
                return rows[0] if rows and rows[0]["Attempts"] > 0 and rows[0].get("LastError") else None

            outbox = self.wait(publication_failed, 35)
            assert outbox.get("PublishedAt") is None
            assert self.status(pending)["status"] == "Pending"
        finally:
            self.compose("start", "rabbitmq")
        self.assert_completed_once(pending)
        self.record("RabbitMQ outage left an unpublished SQL Outbox row; broker restart delivered it", analysisId=pending,
                    failedAttempts=outbox["Attempts"])

        self.compose("restart", "worker")
        self.wait(lambda: self.compose("ps", "--format", "{{.Health}}", "worker").strip() == "healthy", 90)
        interrupted = self.submit_photo()

        def processing():
            status = self.status(interrupted)["status"]
            if status in {"Completed", "Failed"}:
                raise AssertionError("Worker reached a terminal state before an in-flight interruption could be verified.")
            return status == "Processing"

        self.wait(processing, 50)
        self.compose("kill", "-s", "SIGKILL", "worker")
        try:
            assert self.status(interrupted)["status"] == "Processing"
        finally:
            self.compose("start", "worker")
        recovered = self.assert_completed_once(interrupted)
        assert recovered["retryCount"] >= 1, "Interrupted job must resume from its durable delivery attempt."
        self.record("Killed a real worker during processing; redelivery completed exactly one SQL analysis", analysisId=interrupted,
                    retryCount=recovered["retryCount"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=5110)
    parser.add_argument("--browser", action="store_true")
    parser.add_argument("--report-directory", type=Path, default=ROOT / "coverage-reports/integration/qa-stack")
    args = parser.parse_args()
    if args.port == 5100:
        parser.error("Use a dedicated QA port, such as 5110.")
    commands = ("docker", "forge", "cast", "node") if args.browser else ("docker", "forge", "cast")
    for command in commands:
        if not shutil.which(command):
            parser.error(f"{command} is required.")
    args.docker = True
    args.rpc = "http://127.0.0.1:18545"
    args.skip_ai = False
    args.keep_services = False
    QaProtocol(args).run()


if __name__ == "__main__":
    main()

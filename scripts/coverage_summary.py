import argparse
import html
import json
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass, field
from pathlib import Path


COMPONENTS = {
    "python": "Python AI, Worker і Streamlit",
    "dotnet": "ASP.NET Core",
    "web": "React Web",
    "mobile": "React Native",
    "solidity": "Solidity",
}
LINE_TARGET = 90
BRANCH_TARGET = 85


@dataclass
class FileCoverage:
    name: str
    covered: int
    lines: int
    missing_lines: list[int] = field(default_factory=list)
    missing_branches: list[str] = field(default_factory=list)


@dataclass
class Report:
    counts: tuple[int, int, int]
    lines: tuple[int, int]
    branches: tuple[int, int]
    files: list[FileCoverage]
    html_path: str


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def junit(path):
    cases = ET.parse(path).getroot().findall(".//testcase")
    failed = sum(case.find("failure") is not None or case.find("error") is not None for case in cases)
    skipped = sum(case.find("skipped") is not None for case in cases)
    return len(cases) - failed - skipped, failed, skipped


def source_name(path):
    path = path.replace("\\", "/")
    for root in ("web-app/", "mobile-app/", "central-server/", "shared/"):
        marker = "/" + root
        if marker in path:
            return root + path.rsplit(marker, 1)[-1]
    return path


def python_report(folder):
    coverage = read_json(folder / "coverage.json")
    totals = coverage["totals"]
    files = [
        FileCoverage(source_name(name), row["summary"]["covered_lines"], row["summary"]["num_statements"],
                     row["missing_lines"], [f"{start}→{end if end > 0 else 'exit'}" for start, end in row["missing_branches"]])
        for name, row in coverage["files"].items()
    ]
    return Report(junit(folder / "tests.xml"), (totals["covered_lines"], totals["num_statements"]),
                  (totals["covered_branches"], totals["num_branches"]), files, "html/index.html")


def dotnet_report(folder):
    reports = sorted(folder.glob("*/coverage.cobertura.xml"), key=lambda item: item.stat().st_mtime_ns, reverse=True)
    if not reports:
        raise FileNotFoundError("Coverlet Cobertura XML")
    root = ET.parse(reports[0]).getroot()
    counters = ET.parse(folder / "tests.trx").getroot().find(".//{*}Counters")
    if counters is None:
        raise ValueError("TRX Counters")
    passed = int(counters.attrib["passed"])
    skipped = int(counters.attrib["notExecuted"])
    failed = int(counters.attrib["total"]) - passed - skipped
    lines_by_file = {}
    branches_by_file = {}
    for entry in root.findall("./packages/package/classes/class"):
        name = "central-server/" + entry.attrib["filename"].replace("\\", "/")
        lines = lines_by_file.setdefault(name, {})
        branches = branches_by_file.setdefault(name, [])
        for line in entry.findall("./lines/line"):
            number = int(line.attrib["number"])
            lines[number] = lines.get(number, 0) + int(line.attrib["hits"])
            if "condition-coverage" in line.attrib and not line.attrib["condition-coverage"].startswith("100%"):
                branches.append(f"{number} ({line.attrib['condition-coverage']})")
    files = [
        FileCoverage(name, sum(hit > 0 for hit in lines.values()), len(lines),
                     sorted(number for number, hit in lines.items() if hit == 0), sorted(set(branches_by_file[name])))
        for name, lines in lines_by_file.items()
    ]
    return Report((passed, failed, skipped), (int(root.attrib["lines-covered"]), int(root.attrib["lines-valid"])),
                  (int(root.attrib["branches-covered"]), int(root.attrib["branches-valid"])), files, "index.html")


def istanbul_report(folder, mobile):
    raw = read_json(folder / "coverage/coverage-final.json")
    summary = read_json(folder / "coverage/coverage-summary.json")
    files = []
    for path, row in raw.items():
        lines = {}
        for key, hits in row["s"].items():
            line = row["statementMap"][key]["start"]["line"]
            lines[line] = max(lines.get(line, 0), hits)
        gaps = []
        for key, hits in row["b"].items():
            branch = row["branchMap"][key]
            for index, count in enumerate(hits):
                if count == 0:
                    location = branch["locations"][index].get("start", {})
                    line = location.get("line") or branch.get("line", "?")
                    gaps.append(f"{line}:{location.get('column', '?')} ({branch['type']} {index + 1}/{len(hits)})")
        files.append(FileCoverage(source_name(path), sum(hit > 0 for hit in lines.values()), len(lines),
                                  sorted(line for line, hit in lines.items() if not hit), gaps))
    if mobile:
        tests = read_json(folder / "tests.json")
        counts = tests["numPassedTests"], tests["numFailedTests"], tests["numPendingTests"] + tests.get("numTodoTests", 0)
    else:
        counts = junit(folder / "tests.xml")
    line, branch = (summary["total"][kind] for kind in ("lines", "branches"))
    return Report(counts, (line["covered"], line["total"]), (branch["covered"], branch["total"]),
                  files, "coverage/index.html")


def solidity_report(folder):
    current = None
    files = []
    covered_branches = total_branches = 0
    for entry in (folder / "lcov.info").read_text(encoding="utf-8-sig").splitlines():
        key, _, value = entry.partition(":")
        if key == "SF":
            current = FileCoverage(source_name(value), 0, 0)
        elif key == "DA":
            number, count, *_ = value.split(",")
            current.lines += 1
            current.covered += int(count) > 0
            if not int(count):
                current.missing_lines.append(int(number))
        elif key == "BRDA":
            line, block, branch, count = value.split(",")
            hit = count not in ("0", "-")
            total_branches += 1
            covered_branches += hit
            if not hit:
                current.missing_branches.append(f"{line} (block {block}, branch {branch})")
        elif key == "end_of_record" and current is not None:
            files.append(current)
    return Report(junit(folder / "tests.xml"), (sum(file.covered for file in files), sum(file.lines for file in files)),
                  (covered_branches, total_branches), files, "index.html")


def read_report(component, folder):
    if component in ("web", "mobile"):
        return istanbul_report(folder, mobile=component == "mobile")
    reader = {"python": python_report, "dotnet": dotnet_report, "solidity": solidity_report}
    return reader[component](folder)


def metric(counts):
    covered, total = counts
    return f"{covered / total:.2%} ({covered}/{total})" if total else "—"


def ordered(files):
    return sorted((file for file in files if file.lines), key=lambda file: (file.covered / file.lines, -file.lines, file.name))


def write_html(folder, name, report):
    rows = []
    for file in ordered(report.files):
        columns = [file.name, metric((file.covered, file.lines)), ", ".join(map(str, file.missing_lines)), ", ".join(file.missing_branches)]
        rows.append("<tr>" + "".join(f"<td>{html.escape(value)}</td>" for value in columns) + "</tr>")
    contents = "\n".join(rows)
    (folder / "index.html").write_text(f"""<!doctype html>
<html lang="uk"><meta charset="utf-8"><title>{html.escape(name)} coverage</title>
<style>body{{font:16px system-ui;margin:2rem}}table{{border-collapse:collapse}}th,td{{border:1px solid #aaa;padding:.5em;text-align:left;overflow-wrap:anywhere}}td:first-child{{min-width:14em}}</style>
<h1>{html.escape(name)}</h1>
<p>Рядки {metric(report.lines)} · Гілки {metric(report.branches)}</p>
<table><thead><tr><th>Файл</th><th>Рядки</th><th>Непокриті рядки</th><th>Непокриті гілки</th></tr></thead>
<tbody>{contents}</tbody></table></html>
""", encoding="utf-8")


def generate(root, components, check):
    table = [
        "# Покриття AI Bird Lab", "",
        "| Компонент | Усього | Passed | Failed | Skipped | Рядки | Гілки | Поріг рядків | Ціль гілок |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |",
    ]
    errors = []
    all_files = []
    records = {}
    for component in components:
        folder = root / component
        label = COMPONENTS[component]
        try:
            report = read_report(component, folder)
        except (OSError, ValueError, ET.ParseError, KeyError, TypeError) as error:
            table.append(f"| {label} | — | — | — | — | — | — | Немає заміру | — |")
            if not (folder / "audit.txt").exists():
                errors.append(f"{label} · {error}")
            continue
        passed, failed, skipped = report.counts
        line_hit = report.lines[1] > 0 and report.lines[0] * 100 >= LINE_TARGET * report.lines[1]
        branch_hit = report.branches[1] > 0 and report.branches[0] * 100 >= BRANCH_TARGET * report.branches[1]
        table.append(f"| [{label}]({component}/{report.html_path}) | {passed + failed + skipped} | {passed} | {failed} | {skipped} | "
                     f"{metric(report.lines)} | {metric(report.branches)} | {'✓' if line_hit else 'Нижче 90%'} | "
                     f"{'✓' if branch_hit else 'Нижче 85%' if report.branches[1] else '—'} |")
        records[component] = asdict(report)
        all_files.extend((component, file) for file in report.files if file.lines)
        if report.html_path == "index.html":
            write_html(folder, label, report)
        if not line_hit:
            errors.append(f"{label} · покриття рядків {metric(report.lines)}, поріг {LINE_TARGET}%")
        if report.branches[1] and not branch_hit:
            errors.append(f"{label} · покриття гілок {metric(report.branches)}, поріг {BRANCH_TARGET}%")
        if not passed or failed or (component == "dotnet" and skipped):
            errors.append(f"{label} · passed {passed}, failed {failed}, skipped {skipped}")
    table += ["", "## 10 файлів із найменшим покриттям", "",
              "| Компонент | Файл | Рядки |", "| --- | --- | ---: |"]
    for component, file in sorted(all_files, key=lambda row: (row[1].covered / row[1].lines, -row[1].lines, row[1].name))[:10]:
        table.append(f"| {COMPONENTS[component]} | `{file.name}` | {metric((file.covered, file.lines))} |")
    table += ["", "Повний список непокритих рядків і гілок у [машинному звіті](summary.json) та звітах компонентів.", ""]
    if errors:
        table += ["## Діагностика", ""] + [f"- {error}" for error in errors] + [""]
    markdown = "\n".join(table)
    (root / "SUMMARY.md").write_text(markdown, encoding="utf-8")
    (root / "summary.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    print(markdown)
    return int(check and (bool(errors) or len(records) != len(components)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--components", nargs="+", choices=COMPONENTS, default=list(COMPONENTS))
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    return generate(args.directory, args.components, args.check)


if __name__ == "__main__":
    raise SystemExit(main())

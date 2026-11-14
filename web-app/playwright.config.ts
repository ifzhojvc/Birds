import { defineConfig } from "@playwright/test";
import { resolve } from "node:path";

const api = new URL(process.env.BIRDLAB_E2E_URL || "http://127.0.0.1:5110");
if (api.protocol !== "http:" || api.hostname !== "127.0.0.1" || !api.port || api.port === "5100") {
  throw new Error("Browser integration requires a dedicated loopback QA API.");
}
const browser = process.env.BIRDLAB_E2E_BROWSER === "msedge" ? "msedge" : undefined;
const reports = resolve(process.env.BIRDLAB_E2E_REPORT_DIR || "../coverage-reports/integration/browser");

export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.e2e.ts",
  timeout: 240_000,
  workers: 1,
  outputDir: resolve(reports, "results"),
  reporter: [["line"], ["junit", { outputFile: resolve(reports, "tests.xml") }]],
  use: { baseURL: "http://127.0.0.1:5173", browserName: "chromium", channel: browser },
  webServer: {
    command: "node node_modules/vite/bin/vite.js --host 127.0.0.1",
    url: "http://127.0.0.1:5173",
    reuseExistingServer: false,
    env: { VITE_API_URL: api.origin, VITE_POLL_INTERVAL_MS: "500" },
  },
});

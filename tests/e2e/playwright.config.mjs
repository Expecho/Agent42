// End-to-end smoke layers (ADR 0151). Two projects share one config:
//   devhost  - the offline DevHost (canned LLM, in-memory grains), started by `webServer` below
//   selfhost - the compose stack from tests/e2e/selfhost/compose.e2e.yaml, which the caller (CI or you) starts
// Playwright's cwd is tests/e2e, so the webServer entry sets cwd to the repo root explicitly.
import { defineConfig, devices } from "@playwright/test";
import { fileURLToPath } from "node:url";

const repoRoot = fileURLToPath(new URL("../..", import.meta.url));
const devhostUrl = process.env.E2E_BASE_URL ?? "http://localhost:61326";
const selfhostUrl = process.env.E2E_SELFHOST_URL ?? "http://127.0.0.1:8080";
const only = process.env.E2E_PROJECT; // "devhost" | "selfhost": start just that stack's webServer

export default defineConfig({
  testDir: ".",
  testMatch: /.*\.spec\.mjs/,
  fullyParallel: false,
  workers: 1,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: { trace: "retain-on-failure" },
  projects: [
    {
      name: "devhost",
      testMatch: /devhost\.spec\.mjs/,
      use: { ...devices["Desktop Chrome"], baseURL: devhostUrl, serviceWorkers: "allow" },
    },
    {
      name: "selfhost",
      testMatch: /selfhost\.spec\.mjs/,
      use: { ...devices["Desktop Chrome"], baseURL: selfhostUrl },
    },
  ],
  // The launch profile supplies port 61326 and ASPNETCORE_ENVIRONMENT=Development (Program.cs sets neither),
  // so do NOT pass --no-launch-profile. The first run builds DevHost, hence the generous timeout.
  webServer: only === "selfhost" ? undefined : {
    command: "dotnet run --project tools/Agent42.DevHost",
    cwd: repoRoot,
    url: devhostUrl,
    reuseExistingServer: !process.env.CI,
    timeout: 300_000,
    stdout: "ignore",
    stderr: "pipe",
  },
});

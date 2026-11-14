import { defineConfig } from "@playwright/test";
import { randomBytes } from "node:crypto";
const password =
  process.env.UI_TEST_PASSWORD ?? randomBytes(24).toString("hex");
process.env.UI_TEST_PASSWORD = password;
export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  timeout: 30000,
  expect: { timeout: 8000 },
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: "http://127.0.0.1:5219",
    browserName: "chromium",
    headless: true,
    launchOptions: {
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
    },
    viewport: { width: 1280, height: 720 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: ["DefaultJwt", "BasicAuth", "ApiKey", "DefaultJwt", "BasicAuth", "ApiKey"].map((mode, index) => ({
    command: `dotnet run --project tests/Wafish.ReactUi.Host -c Release --no-launch-profile${index || process.env.UI_TEST_NO_BUILD === "true" ? " --no-build" : ""}`,
    cwd: "../../..",
    url: `http://127.0.0.1:${5219 + index}${index >= 3 ? "/edge/manage/ui" : "/console"}`,
    reuseExistingServer: false,
    timeout: 120000,
    env: {
      UI_TEST_PASSWORD: password,
      UI_TEST_AUTH: mode,
      UI_TEST_PORT: String(5219 + index),
      UI_TEST_PATH_BASE: index >= 3 ? "/edge" : "",
      UI_TEST_UI_PREFIX: index >= 3 ? "manage/ui" : "console",
      UI_TEST_API_PREFIX: index >= 3 ? "manage/api" : "aneiang",
      ASPNETCORE_ENVIRONMENT: "Development",
    },
    stdout: "ignore",
    stderr: "pipe",
  })),
});

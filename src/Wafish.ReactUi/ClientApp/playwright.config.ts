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
    ignoreHTTPSErrors: true,
    launchOptions: {
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
    },
    viewport: { width: 1280, height: 720 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: [
    "DefaultJwt",
    "BasicAuth",
    "ApiKey",
    "DefaultJwt",
    "BasicAuth",
    "ApiKey",
    "DefaultJwt",
    "BasicAuth",
    "ApiKey",
    "DefaultJwt",
    "DefaultJwt",
    "DefaultJwt",
    "DefaultJwt",
    "DefaultJwt",
    "DefaultJwt",
    "DefaultJwt",
  ].map((mode, index) => ({
    command: `dotnet run --project tests/Wafish.ReactUi.Host -c Release --no-launch-profile${index || process.env.UI_TEST_NO_BUILD === "true" ? " --no-build" : ""}`,
    cwd: "../../..",
    url: `${index >= 6 ? "https" : "http"}://127.0.0.1:${5219 + index}${index >= 3 ? "/edge/manage/ui" : "/console"}`,
    reuseExistingServer: false,
    ignoreHTTPSErrors: true,
    gracefulShutdown: { signal: "SIGTERM", timeout: 5000 },
    timeout: 120000,
    env: {
      UI_TEST_PASSWORD: password,
      UI_TEST_ROOT: process.env.UI_TEST_ROOT ?? "",
      UI_TEST_HTTPS: String(index >= 6),
      UI_TEST_AUTH: mode,
      UI_TEST_PROFILE:
        [
          "large",
          "plugins",
          "storage-failure",
          "version-conflict",
          "delayed-failure",
          "activation-failure",
          "passkeys",
        ][index - 9] ?? "baseline",
      UI_TEST_PORT: String(5219 + index),
      UI_TEST_PATH_BASE: index >= 3 ? "/edge" : "",
      UI_TEST_UI_PREFIX: index >= 3 ? "manage/ui" : "console",
      UI_TEST_API_PREFIX: index >= 3 ? "manage/api" : "wafish",
      ASPNETCORE_ENVIRONMENT: "Development",
    },
    stdout: "ignore",
    stderr: "pipe",
  })),
});

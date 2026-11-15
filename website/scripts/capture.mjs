import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { chromium } from "@playwright/test";
import sharp from "sharp";

const root = fileURLToPath(new URL("../../", import.meta.url));
const temporary = await mkdtemp(path.join(tmpdir(), "wafish-marketing-"));
const password = randomBytes(24).toString("hex");
const host = spawn(
  "dotnet",
  [
    "run",
    "--project",
    "tests/Wafish.ReactUi.Host",
    "-c",
    "Release",
    "--no-build",
    "--no-launch-profile",
  ],
  {
    cwd: root,
    env: {
      ...process.env,
      UI_TEST_PORT: "5249",
      UI_TEST_PROFILE: "plugins",
      UI_TEST_PASSWORD: password,
      UI_TEST_ROOT: temporary,
      ASPNETCORE_ENVIRONMENT: "Development",
    },
    stdio: ["ignore", "ignore", "pipe"],
  },
);
let browser;
try {
  let ready = false;
  for (let count = 0; count < 120; count++) {
    try {
      if ((await fetch("http://127.0.0.1:5249/console")).ok) {
        ready = true;
        break;
      }
    } catch {}
    if (host.exitCode !== null)
      throw new Error("Screenshot fixture exited before startup.");
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  if (!ready) throw new Error("Screenshot fixture did not start.");
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({
    viewport: { width: 1440, height: 960 },
    reducedMotion: "reduce",
  });
  await page.goto("http://127.0.0.1:5249/console");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByRole("heading", { name: "Overview", exact: true }).waitFor();
  for (const name of ["catalog", "checkout", "identity", "media"]) {
    await page.goto("http://127.0.0.1:5249/console/configuration/clusters");
    await page
      .getByRole("button", { name: "New cluster", exact: true })
      .click();
    await page
      .getByRole("dialog")
      .getByLabel("Name", { exact: true })
      .fill(name);
    await page
      .getByLabel("Destination address · d1")
      .fill("http://127.0.0.1:9/");
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
    await page.goto("http://127.0.0.1:5249/console/configuration/routes");
    await page.getByRole("button", { name: "New route", exact: true }).click();
    await page
      .getByRole("dialog")
      .getByLabel("Name", { exact: true })
      .fill(name);
    await page.getByLabel("Cluster", { exact: true }).selectOption(name);
    await page.getByLabel("Match path").fill(`/api/${name}/{**catch-all}`);
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await page.getByRole("dialog").waitFor({ state: "hidden" });
  }
  await mkdir(path.join(root, "brand/screenshots"), { recursive: true });
  for (const [route, name] of [
    ["/configuration/routes", "routes"],
    ["", "overview"],
  ]) {
    await page.goto(`http://127.0.0.1:5249/console${route}`);
    await page
      .getByRole("heading", {
        name: name === "routes" ? "Routes" : "Overview",
        exact: true,
      })
      .first()
      .waitFor();
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(500);
    await sharp(await page.screenshot())
      .webp({ quality: 90 })
      .toFile(path.join(root, `brand/screenshots/console-${name}.webp`));
  }
  console.log("Captured current UI using isolated demo data.");
} finally {
  await browser?.close();
  host.kill("SIGTERM");
  await Promise.race([
    new Promise((resolve) => host.once("exit", resolve)),
    new Promise((resolve) => setTimeout(resolve, 5000)),
  ]);
  if (host.exitCode === null) host.kill("SIGKILL");
  await rm(temporary, { recursive: true, force: true });
}

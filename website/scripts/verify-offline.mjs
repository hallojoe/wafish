import { unzipSync } from "fflate";
import { readFile, writeFile, mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { chromium } from "@playwright/test";
import assert from "node:assert/strict";
import { startServer } from "./serve.mjs";

const temporary = await mkdtemp(path.join(tmpdir(), "wafish-offline-"));
let browser, server;
try {
  const files = unzipSync(await readFile("dist/downloads/wafish-talk.zip"));
  for (const [name, bytes] of Object.entries(files)) {
    const target = path.resolve(temporary, name);
    assert.ok(target.startsWith(temporary + path.sep));
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, bytes);
  }
  assert.ok(files["start.mjs"]);
  assert.ok(files["wafish/downloads/wafish-talk.pdf"]);
  server = await startServer({ root: path.join(temporary, "wafish"), port: 0 });
  browser = await chromium.launch();
  const page = await browser.newPage();
  const failures = [],
    external = [],
    errors = [];
  page.on("response", (response) => {
    if (response.status() >= 400) failures.push(response.url());
  });
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("**/*", (route) => {
    if (new URL(route.request().url()).hostname !== "127.0.0.1") {
      external.push(route.request().url());
      return route.abort();
    }
    return route.continue();
  });
  const origin = `http://127.0.0.1:${server.address().port}`;
  await page.goto(origin + "/wafish/presentation/#/8");
  await page.locator(".reveal.ready").waitFor();
  await page.locator("section.present [data-scene-next]").click();
  assert.match(
    await page.locator("section.present .scene-caption").textContent(),
    /HIT/,
  );
  const popupPromise = page.waitForEvent("popup");
  await page.getByRole("button", { name: "Speaker notes" }).click();
  const popup = await popupPromise;
  await popup.waitForLoadState();
  await popup.close();
  await page.goto(origin + "/wafish/docs/");
  await page.getByRole("button", { name: "Search documentation" }).click();
  await page.getByRole("searchbox").fill("cache");
  await page.locator(".search-results li").first().waitFor();
  assert.deepEqual(external, []);
  assert.deepEqual(failures, []);
  assert.deepEqual(errors, []);
  const pdf = await readFile(
    path.join(temporary, "wafish/downloads/wafish-talk.pdf"),
  );
  assert.equal(pdf.subarray(0, 4).toString(), "%PDF");
  console.log(
    "Offline deck, scene controls, notes, docs search, and PDF passed without external requests.",
  );
} finally {
  await browser?.close();
  if (server) await new Promise((resolve) => server.close(resolve));
  await rm(temporary, { recursive: true, force: true });
}

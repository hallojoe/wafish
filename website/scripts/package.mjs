import { readdir, readFile, writeFile, mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { chromium } from "@playwright/test";
import { zipSync, strToU8 } from "fflate";
import { startServer } from "./serve.mjs";

const root = path.resolve("dist");
const downloads = path.join(root, "downloads");
await mkdir(downloads, { recursive: true });
async function collect(directory, prefix = "") {
  const result = {};
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const key = prefix + entry.name;
    if (entry.isDirectory())
      Object.assign(
        result,
        await collect(path.join(directory, entry.name), key + "/"),
      );
    else if (!key.endsWith("wafish-talk.zip"))
      result[key] = new Uint8Array(
        await readFile(path.join(directory, entry.name)),
      );
  }
  return result;
}
await writeFile(
  path.join(downloads, "wafish-brand.zip"),
  zipSync(await collect(path.join(root, "brand")), { level: 6 }),
);
const server = await startServer({ port: 0 });
let browser;
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ reducedMotion: "reduce" });
  await page.goto(
    `http://127.0.0.1:${server.address().port}/wafish/presentation/?print-pdf`,
    { waitUntil: "networkidle" },
  );
  await page.waitForSelector(".pdf-page");
  await page.evaluate(() => document.fonts.ready);
  const pages = await page.locator(".pdf-page").count();
  if (pages !== 26) throw new Error(`Expected 26 PDF slides; got ${pages}.`);
  await page.pdf({
    path: path.join(downloads, "wafish-talk.pdf"),
    printBackground: true,
    preferCSSPageSize: true,
  });
} finally {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
}
const edition = await collect(root, "wafish/");
const deckPath = "wafish/presentation/index.html";
let html = new TextDecoder().decode(edition[deckPath]);
html = html.replace(
  /<a[^>]+href="\/wafish\/downloads\/wafish-talk.zip"[^>]*>.*?<\/a>/,
  "<span>Offline edition</span>",
);
edition[deckPath] = strToU8(html);
edition["server.mjs"] = strToU8(await readFile("scripts/serve.mjs", "utf8"));
edition["start.mjs"] = strToU8(
  `import { fileURLToPath } from 'node:url';\nimport { startServer } from './server.mjs';\nconst server = await startServer({ root: fileURLToPath(new URL('./wafish', import.meta.url)), port: Number(process.env.PORT || 4174) });\nconsole.log('Open http://127.0.0.1:' + server.address().port + '/wafish/presentation/');\n`,
);
edition["README.txt"] = strToU8(
  "Wafish offline edition\n\nRequirements: Node.js 24 or later and a current browser.\nUnzip this archive. Run: node start.mjs\nOpen http://127.0.0.1:4174/wafish/presentation/\nUse PORT to select another port if needed.\n\nNo npm install or internet connection is required.\nThe local HTTP server is required for modules, search, and speaker notes.\nArrow keys: slides. S: speaker notes. F: fullscreen.\nUse scene buttons for demonstrations. Sound starts off.\nThe PDF handout and documentation are included.\nIllustrations use deterministic example data, not live measurements.\n",
);
await writeFile(
  path.join(downloads, "wafish-talk.zip"),
  zipSync(edition, { level: 6 }),
);
for (const name of ["wafish-talk.zip", "wafish-talk.pdf", "wafish-brand.zip"]) {
  if ((await stat(path.join(downloads, name))).size < 100)
    throw new Error(`Empty download: ${name}`);
}
console.log("Exported 26-slide PDF, offline edition, and brand kit.");

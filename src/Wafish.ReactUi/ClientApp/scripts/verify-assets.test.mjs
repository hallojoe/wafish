import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { verifyAssets } from "./verify-assets.mjs";

for (const fault of ["none", "index", "entry", "css", "font", "chunk", "import", "dynamicImport", "marker", "manifestEntry", "traversal", "directory", "htmlReference"]) {
  test(`asset verification: ${fault}`, () => {
    const root = mkdtempSync(path.join(tmpdir(), "wafish-assets-"));
    try {
      mkdirSync(path.join(root, "assets"));
      mkdirSync(path.join(root, ".vite"));
      for (const file of ["entry.js", "style.css", "font.woff2", "chunk.js"])
        writeFileSync(path.join(root, "assets", file), "fixture");
      writeFileSync(path.join(root, "index.html"), `${fault === "marker" ? "" : "<!--APP_CONFIG-->"}<script src="./assets/${fault === "htmlReference" ? "missing" : "entry"}.js"></script>`);
      const manifest = {
        "index.html": { isEntry: fault !== "manifestEntry", file: "assets/entry.js", css: ["assets/style.css"], assets: ["assets/font.woff2"], imports: ["chunk"], dynamicImports: ["chunk"] },
        chunk: { file: "assets/chunk.js" },
      };
      const missing = { index: "index.html", entry: "assets/entry.js", css: "assets/style.css", font: "assets/font.woff2", chunk: "assets/chunk.js" }[fault];
      if (missing) rmSync(path.join(root, missing));
      if (fault === "import") manifest["index.html"].imports = ["missing"];
      if (fault === "dynamicImport") manifest["index.html"].dynamicImports = ["missing"];
      if (fault === "traversal") manifest.chunk.file = "../outside.js";
      if (fault === "directory") manifest.chunk.file = "assets";
      writeFileSync(path.join(root, ".vite/manifest.json"), JSON.stringify(manifest));
      if (fault === "none") assert.doesNotThrow(() => verifyAssets(root));
      else assert.throws(() => verifyAssets(root));
      const result = spawnSync(process.execPath, [fileURLToPath(new URL("./verify-assets.mjs", import.meta.url)), root], { encoding: "utf8" });
      assert.equal(result.status, fault === "none" ? 0 : 1);
      if (fault !== "none") assert.match(result.stderr, /Frontend assets are incomplete/);
    } finally { rmSync(root, { recursive: true, force: true }); }
  });
}

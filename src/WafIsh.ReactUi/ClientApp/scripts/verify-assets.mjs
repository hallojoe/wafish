import { readFileSync, statSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

export function verifyAssets(directory) {
  const root = path.resolve(directory);
  function required(file) {
    const full = path.resolve(root, file);
    if (!full.startsWith(root + path.sep) || !statSync(full).isFile())
      throw new Error(`Invalid frontend asset: ${file}`);
    return full;
  }
  const html = readFileSync(required("index.html"), "utf8");
  if (!html.includes("<!--APP_CONFIG-->")) throw new Error("Missing APP_CONFIG marker");
  const manifest = JSON.parse(readFileSync(required(".vite/manifest.json"), "utf8"));
  if (!manifest["index.html"]?.isEntry) throw new Error("Missing frontend entry in manifest");
  for (const entry of Object.values(manifest)) {
    required(entry.file);
    for (const file of [...(entry.css ?? []), ...(entry.assets ?? [])]) required(file);
    for (const key of [...(entry.imports ?? []), ...(entry.dynamicImports ?? [])])
      if (!manifest[key]) throw new Error(`Missing manifest import: ${key}`);
  }
  // Also check HTML references. A stale manifest must not conceal a missing entry asset.
  for (const match of html.matchAll(/(?:src|href)=["'](\.\/assets\/[^"']+)["']/g)) required(match[1]);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try { verifyAssets(process.argv[2]); }
  catch (error) {
    console.error(`Frontend assets are incomplete: ${error.message}. Run npm run build in ClientApp.`);
    process.exitCode = 1;
  }
}

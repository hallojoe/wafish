import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import { load } from "cheerio";

const root = path.resolve("dist");
async function files(directory) {
  return (
    await Promise.all(
      (await readdir(directory, { withFileTypes: true })).map((entry) =>
        entry.isDirectory()
          ? files(path.join(directory, entry.name))
          : path.join(directory, entry.name),
      ),
    )
  ).flat();
}
const pages = (await files(root)).filter((file) => file.endsWith(".html"));
const documents = new Map(
  await Promise.all(
    pages.map(async (file) => [file, load(await readFile(file, "utf8"))]),
  ),
);
const errors = [];
for (const [file, $] of documents) {
  for (const element of $(
    "a[href],img[src],script[src],link[href]",
  ).toArray()) {
    const source = $(element).attr("href") ?? $(element).attr("src");
    if (!source || /^(?:[a-z]+:|\/\/)/i.test(source)) continue;
    const parsed = new URL(
      source,
      `https://local/wafish/${path.relative(root, file)}`,
    );
    if (!parsed.pathname.startsWith("/wafish/")) {
      errors.push(`${file}: missing base path ${source}`);
      continue;
    }
    let target = path.join(
      root,
      decodeURIComponent(parsed.pathname.slice("/wafish/".length)),
    );
    try {
      if ((await stat(target)).isDirectory())
        target = path.join(target, "index.html");
      else await stat(target);
    } catch {
      // Downloads are created after the browser export step, and checked by package.mjs.
      if (!parsed.pathname.startsWith("/wafish/downloads/"))
        errors.push(`${file}: missing ${source}`);
      continue;
    }
    if (parsed.hash && !parsed.hash.startsWith("#/")) {
      const destination = documents.get(target);
      const id = decodeURIComponent(parsed.hash.slice(1));
      if (
        destination &&
        !destination("[id]")
          .toArray()
          .some((node) => destination(node).attr("id") === id)
      )
        errors.push(`${file}: missing anchor ${source}`);
    }
  }
}
if (errors.length) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`Checked links and assets across ${pages.length} HTML pages.`);

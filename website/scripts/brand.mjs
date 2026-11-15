import { readFile, writeFile, mkdir, cp, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import sharp from "sharp";

const root = fileURLToPath(new URL("../../", import.meta.url));
const output = path.join(root, "website/public/brand");
await mkdir(output, { recursive: true });
await mkdir(path.join(root, "website/public/images"), { recursive: true });
// Remove obsolete documentation screenshots from previous local builds.
await rm(path.join(root, "website/public/images/docs"), {
  recursive: true,
  force: true,
});
await cp(
  path.join(root, "brand/screenshots"),
  path.join(root, "website/public/images"),
  { recursive: true },
);
const master = await readFile(path.join(root, "brand/mark.svg"), "utf8");
const paths = master.slice(
  master.indexOf("<path"),
  master.lastIndexOf("</svg>"),
);
const variants = {
  mark: master,
  "mark-dark": master
    .replaceAll("#e9eddf", "#141915")
    .replaceAll("#d5f36b", "#536b23"),
  "mark-mono": master.replaceAll("#d5f36b", "#e9eddf"),
  "mark-mono-dark": master
    .replaceAll("#e9eddf", "#141915")
    .replaceAll("#d5f36b", "#141915"),
};
for (const [name, svg] of Object.entries(variants)) {
  await writeFile(path.join(output, `${name}.svg`), svg);
  await sharp(Buffer.from(svg))
    .resize(512, 512)
    .png()
    .toFile(path.join(output, `${name}.png`));
}
const icon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120"><rect width="120" height="120" rx="24" fill="#141915"/><g transform="translate(12 12)">${paths}</g></svg>`;
await writeFile(path.join(output, "favicon.svg"), icon);
await writeFile(
  path.join(root, "src/Wafish.ReactUi/ClientApp/src/assets/wafish-favicon.svg"),
  icon,
);
const png = await sharp(Buffer.from(icon)).resize(512).png().toBuffer();
await writeFile(path.join(root, "logo.png"), png);
await sharp(Buffer.from(icon))
  .resize(180)
  .png()
  .toFile(path.join(output, "apple-touch-icon.png"));
// The wordmark is outlined geometry, with no external font dependency.
const letters = `<g fill="none" stroke="currentColor" stroke-width="8" stroke-linejoin="round" stroke-linecap="square"><path d="M132 32l12 40 13-28 13 28 12-40M197 50c0-16 26-16 26 0v21m0-20c-35-15-35 34 0 14M242 72V32q0-14 19-10m-27 22h27M276 43v29M296 65q22 17 29 0c5-16-28-8-28-23 0-13 24-14 29-4M344 22v50m0-22c0-17 28-18 28 0v22"/><path d="M276 25v1" stroke-width="9"/></g>`;
for (const [name, color, mark] of [
  ["wordmark", "#e9eddf", paths],
  [
    "wordmark-dark",
    "#141915",
    paths.replaceAll("#e9eddf", "#141915").replaceAll("#d5f36b", "#536b23"),
  ],
]) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 392 96" style="color:${color}">${mark}${letters}</svg>`;
  await writeFile(path.join(output, `${name}.svg`), svg);
}
const cover = `<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="800" viewBox="0 0 1600 800"><rect width="1600" height="800" fill="#141915"/><circle cx="1285" cy="494" r="246" stroke="#394333" fill="none"/><circle cx="1285" cy="494" r="290" stroke="#283122" fill="none"/><g transform="translate(1024 245) scale(5.2)">${paths}</g><g transform="translate(90 68) scale(1.1)">${paths}</g><text x="220" y="148" font-family="sans-serif" font-size="62" letter-spacing="-3" fill="#f1f0e8">Wafish</text><text x="94" y="366" font-family="sans-serif" font-weight="bold" font-size="98" letter-spacing="-5" fill="#f1f0e8">An API gateway.</text><text x="94" y="480" font-family="sans-serif" font-weight="bold" font-size="98" letter-spacing="-5" fill="#d5f36b">Built for .NET.</text><path d="M96 566h700" stroke="#44503d"/><text x="96" y="618" font-family="monospace" font-size="24" fill="#b4bead">REVERSE PROXY + REQUEST POLICIES</text><text x="96" y="726" font-family="monospace" font-size="18" fill="#b4bead">WAF / RATE LIMITS / CACHE / LIVE CONFIGURATION</text></svg>`;
await writeFile(path.join(output, "cover.svg"), cover);
await sharp(Buffer.from(cover)).png().toFile(path.join(root, "cover-en.png"));
await sharp(Buffer.from(cover))
  .resize(1200, 630, { fit: "cover" })
  .png()
  .toFile(path.join(root, "og-image.png"));
await cp(path.join(root, "og-image.png"), path.join(output, "social.png"));
await cp(path.join(root, "brand/README.md"), path.join(output, "README.md"));
await cp(path.join(root, "LICENSE"), path.join(output, "LICENSE"));
for (const font of [
  "@fontsource-variable/space-grotesk",
  "@fontsource/ibm-plex-sans",
  "@fontsource/ibm-plex-mono",
]) {
  await cp(
    path.join(root, "website/node_modules", font, "LICENSE"),
    path.join(output, font.split("/").at(-1) + "-LICENSE.txt"),
  );
}
console.log("Brand exports ready.");

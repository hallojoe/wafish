import http from "node:http";
import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

export function startServer({
  root = path.resolve("dist"),
  port = 4321,
  prefix = "/wafish",
} = {}) {
  const mime = {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css",
    ".js": "text/javascript",
    ".mjs": "text/javascript",
    ".json": "application/json",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    ".webp": "image/webp",
    ".woff2": "font/woff2",
    ".woff": "font/woff",
    ".wasm": "application/wasm",
    ".pdf": "application/pdf",
    ".zip": "application/zip",
    ".xml": "application/xml",
    ".txt": "text/plain",
    ".md": "text/plain; charset=utf-8",
  };
  const server = http.createServer(async (request, response) => {
    if (!["GET", "HEAD"].includes(request.method)) {
      response.writeHead(405).end();
      return;
    }
    try {
      const pathname = decodeURIComponent(
        new URL(request.url, "http://localhost").pathname,
      );
      if (pathname === prefix || pathname === "/") {
        response.writeHead(302, { Location: `${prefix}/` }).end();
        return;
      }
      if (!pathname.startsWith(prefix + "/")) {
        response.writeHead(404).end();
        return;
      }
      let file = path.resolve(root, "." + pathname.slice(prefix.length));
      if (
        file !== path.resolve(root) &&
        !file.startsWith(path.resolve(root) + path.sep)
      ) {
        response.writeHead(404).end();
        return;
      }
      try {
        if ((await stat(file)).isDirectory())
          file = path.join(file, "index.html");
        await stat(file);
      } catch {
        file = path.join(root, "404.html");
        response.statusCode = 404;
      }
      response.setHeader(
        "Content-Type",
        mime[path.extname(file)] || "application/octet-stream",
      );
      if (request.method === "HEAD") response.end();
      else
        createReadStream(file)
          .on("error", () => response.destroy())
          .pipe(response);
    } catch {
      response.writeHead(400).end();
    }
  });
  return new Promise((resolve) =>
    server.listen(port, "127.0.0.1", () => resolve(server)),
  );
}
if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  const port = Number(process.env.PORT || 4321);
  const server = await startServer({
    port,
    root: process.env.SITE_ROOT
      ? path.resolve(process.env.SITE_ROOT)
      : path.resolve("dist"),
  });
  console.log(
    `Wafish preview: http://127.0.0.1:${server.address().port}/wafish/`,
  );
}

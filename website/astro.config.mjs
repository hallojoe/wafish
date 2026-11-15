import { defineConfig } from "astro/config";

export default defineConfig({
  site: "https://hallojoe.github.io",
  base: "/wafish",
  output: "static",
  trailingSlash: "always",
  build: { format: "directory" },
  vite: { build: { chunkSizeWarningLimit: 700 } },
});

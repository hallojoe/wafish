import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": path.resolve(import.meta.dirname, "./src") } },
  base: "./",
  build: { manifest: true, outDir: "../wwwroot", emptyOutDir: true },
  server: {
    host: "127.0.0.1",
    proxy: {
      "/aneiang": {
        target: process.env.GATEWAY_URL ?? "http://127.0.0.1:5200",
      },
    },
  },
});

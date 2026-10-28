import { resolve } from "node:path";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { defineConfig } from "vite";

// One page per Tauri window: the notch, Settings, and the installer's window.
export default defineConfig({
  plugins: [svelte()],
  clearScreen: false,
  server: { port: 5173, strictPort: true },
  build: {
    target: "es2022",
    rollupOptions: {
      input: {
        notch: resolve(import.meta.dirname, "notch.html"),
        settings: resolve(import.meta.dirname, "settings.html"),
        setup: resolve(import.meta.dirname, "setup.html"),
      },
    },
  },
});

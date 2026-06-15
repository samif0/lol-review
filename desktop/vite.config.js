import { defineConfig } from 'vite';

// Serve the static UI in desktop/ui/ with hot reload. Tauri's devUrl points at
// this server during `tauri dev`; for production `tauri build` uses frontendDist
// (../ui) directly.
export default defineConfig({
  root: 'ui',
  // Fixed port so tauri.conf.json devUrl can reference it.
  server: {
    port: 1420,
    strictPort: true,
  },
  // Don't try to bundle ./app.js as a module graph entry differently than served.
  clearScreen: false,
});

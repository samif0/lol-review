import { defineConfig } from 'vite';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readdirSync } from 'node:fs';

const uiDir = resolve(dirname(fileURLToPath(import.meta.url)), 'ui');

// Every page is its own rollup input so `npm run build` (the CI compile gate)
// actually parses EVERY page's module graph. With vite's default single input
// (index.html only), a syntax error in review.js / games.js / vodplayer.js
// sailed through CI and into a signed release — the gate covered ~2 of 17
// scripts. Discovered dynamically so a new page can't be forgotten here.
const pageInputs = Object.fromEntries(
  readdirSync(uiDir)
    .filter((f) => f.endsWith('.html'))
    .map((f) => [f.replace(/\.html$/, ''), resolve(uiDir, f)]),
);

// Serve the static UI in desktop/ui/. NOTE: `tauri dev` serves frontendDist
// (../ui) statically — tauri.conf.json has no devUrl, so this dev server is a
// browser-preview convenience only, not wired into `tauri dev`. Production
// builds embed ../ui directly (frontendDist is read at compile time).
export default defineConfig({
  root: 'ui',
  server: {
    port: 1420,
    strictPort: true,
  },
  build: {
    rollupOptions: {
      input: pageInputs,
    },
  },
  clearScreen: false,
});

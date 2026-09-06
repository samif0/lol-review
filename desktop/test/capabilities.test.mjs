// Least-privilege gate for the Tauri capability file (src-tauri/capabilities/*.json).
//
// The webview runs with withGlobalTauri, so every permission granted here is
// callable from any script that runs in the page. The app drives its native ops
// (folder/save dialogs, Markdown export write, shell-open of the log folder)
// from Rust commands via invoke(); the plugins' Rust APIs need no capability
// grant. Granting the plugins' JS surface as well would hand page scripts
// shell-open / dialog / fs access the app itself never uses, so this test pins
// the grant list to the core + window-control permissions the UI actually calls.
//
// Runs under the Node built-in test runner (`node --test test/`) as part of
// `npm run build`, so CI's desktop compile gate enforces it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const capabilitiesDir = join(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "src-tauri",
  "capabilities",
);

// Plugin permission namespaces the webview must never be granted. Each one
// exposes a JS API (window.__TAURI__.shell / .dialog / .fs) the UI does not use.
const forbiddenPrefixes = ["shell:", "fs:", "dialog:"];

// The grants the frontend genuinely relies on: core defaults for invoke()/events
// and the frameless title bar's custom window controls (shell-outer.js).
const requiredPermissions = [
  "core:default",
  "core:window:allow-minimize",
  "core:window:allow-toggle-maximize",
  "core:window:allow-close",
  "core:window:allow-start-dragging",
];

// Permissions are either bare identifier strings or { identifier, allow, deny }
// objects (scoped grants). Normalise to the identifier.
function permissionIdentifier(entry) {
  if (typeof entry === "string") return entry;
  if (entry && typeof entry === "object" && typeof entry.identifier === "string") {
    return entry.identifier;
  }
  throw new Error(`Unrecognised permission entry: ${JSON.stringify(entry)}`);
}

function loadCapabilityFiles() {
  const files = readdirSync(capabilitiesDir).filter((f) => f.endsWith(".json"));
  assert.ok(files.length > 0, `no capability files found in ${capabilitiesDir}`);
  return files.map((file) => ({
    file,
    capability: JSON.parse(readFileSync(join(capabilitiesDir, file), "utf8")),
  }));
}

test("capabilities grant no shell/fs/dialog plugin permissions to the webview", () => {
  for (const { file, capability } of loadCapabilityFiles()) {
    const identifiers = (capability.permissions ?? []).map(permissionIdentifier);
    const forbidden = identifiers.filter((id) =>
      forbiddenPrefixes.some((prefix) => id.startsWith(prefix)),
    );
    assert.deepEqual(
      forbidden,
      [],
      `${file} grants plugin JS permissions the app never calls: ${forbidden.join(", ")}`,
    );
  }
});

test("default capability keeps the core + window-control grants the UI uses", () => {
  const { capability } = loadCapabilityFiles().find(({ file }) => file === "default.json");
  const identifiers = (capability.permissions ?? []).map(permissionIdentifier);
  for (const required of requiredPermissions) {
    assert.ok(identifiers.includes(required), `default.json is missing ${required}`);
  }
  assert.deepEqual(capability.windows, ["main"]);
});

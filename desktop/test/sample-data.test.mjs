// Guards the browser-preview demo fixtures (desktop/ui/sample-*.json) against
// leaking a real Windows account name. Every file path in these fixtures must
// use the neutral placeholder profile `C:\Users\you\...`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const uiDir = join(dirname(fileURLToPath(import.meta.url)), "..", "ui");
const sampleFiles = readdirSync(uiDir)
  .filter((name) => /^sample-.*\.json$/.test(name))
  .sort();

// A Windows profile path for any account other than the placeholder `you`.
const REAL_PROFILE_PATH = /[A-Za-z]:\\Users\\(?!you\\)[^\\"]+/i;

function collectStrings(value, path, out) {
  if (typeof value === "string") {
    out.push({ path, value });
  } else if (Array.isArray(value)) {
    value.forEach((item, i) => collectStrings(item, `${path}[${i}]`, out));
  } else if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      collectStrings(child, path ? `${path}.${key}` : key, out);
    }
  }
}

test("sample fixtures exist", () => {
  assert.ok(sampleFiles.length > 0, `no sample-*.json files found in ${uiDir}`);
});

for (const name of sampleFiles) {
  test(`${name} contains no real Windows profile paths`, () => {
    const parsed = JSON.parse(readFileSync(join(uiDir, name), "utf8"));
    const strings = [];
    collectStrings(parsed, "", strings);
    const leaks = strings.filter(({ value }) => REAL_PROFILE_PATH.test(value));
    assert.deepEqual(
      leaks.map(({ path, value }) => `${path}: ${value}`),
      [],
      `${name} has file paths outside the C:\\Users\\you placeholder profile`
    );
  });
}

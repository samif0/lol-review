// Decides which Windows CI jobs a pull request needs from the paths it changes.
// Fails open: a path no rule recognizes, an empty list, or a change to CI itself
// runs everything. The Linux jobs (proxy, site-and-scripts) always run.
//
//   git diff --name-only HEAD^1 HEAD | node scripts/ci-changes.mjs
//
// prints `dotnet=<bool>` and `desktop=<bool>` lines for $GITHUB_OUTPUT.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// Anything here changes what every job builds or how CI decides.
const everything = [
  /^\.github\/workflows\/ci\.yml$/,
  /^scripts\/ci-changes\.mjs$/,
  /^global\.json$/,
  /^Directory\.Build\.props$/,
  /^Revu\.sln$/,
  /^nuget\.config$/i,
];

// [pattern, dotnet, desktop]. First match wins.
const rules = [
  // The sidecar endpoint contract test reads desktop/ui/platform/commands.mjs.
  [/^desktop\/ui\/platform\//, true, true],
  [/^desktop\//, false, true],
  // The desktop package builds the sidecar, and desktop tests read Core sources.
  [/^src\//, true, true],
  // Packaging and the packaged smoke run these.
  [/^scripts\/(Get-PinnedFfmpeg|Test-PackagedHost|Test-ElectronHost)\.ps1$/, false, true],
  [/^licenses\//, false, true],
  [/^LICENSE$/, false, true],
  // Covered by the Linux jobs or not built at all.
  [/^site\//, false, false],
  [/^proxy\//, false, false],
  [/^scripts\//, false, false],
  [/^\.github\/(workflows\/(release|tag-release)\.yml|dependabot\.yml|ISSUE_TEMPLATE\/)/, false, false],
  [/^[^/]+\.md$/, false, false],
];

export function classify(files) {
  const paths = files.map(file => file.trim().replace(/\\/g, '/')).filter(Boolean);
  const all = { dotnet: true, desktop: true };
  if (paths.length === 0) return all;
  const needs = { dotnet: false, desktop: false };
  for (const file of paths) {
    if (everything.some(pattern => pattern.test(file))) return all;
    const rule = rules.find(([pattern]) => pattern.test(file));
    if (!rule) return all;
    needs.dotnet ||= rule[1];
    needs.desktop ||= rule[2];
  }
  return needs;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const needs = classify(readFileSync(0, 'utf8').split('\n'));
  process.stdout.write(`dotnet=${needs.dotnet}\ndesktop=${needs.desktop}\n`);
}

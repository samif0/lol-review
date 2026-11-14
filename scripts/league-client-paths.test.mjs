import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

// The app talks to the League client and to the game's Live Client Data API from C#
// only, and src/Revu.Sidecar.Tests/LeagueClientEndpointContractTests.cs pins that list.
// This test keeps the rest of the repository honest: the desktop host and UI, the site
// and the proxy make no such request, and the one developer script that does is listed
// here path by path. It runs on every pull request, including ones that touch no C#.
const root = fileURLToPath(new URL('..', import.meta.url));
const self = 'scripts/league-client-paths.test.mjs';

// A developer runs this by hand to look at field names. It is not packaged with the app.
const DEVELOPER_PROBE = 'scripts/Inspect-EventSources.ps1';
const DEVELOPER_PROBE_PATHS = [
  '/liveclientdata/allgamedata',
  '/lol-gameflow/v1/gameflow-phase',
  '/lol-match-history/v1/game-timelines/',
  '/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=1',
];

// /lol-<plugin>/ is how the League client names its routes. The repository's own name
// on GitHub (lol-review) has the same shape and is not one.
const LEAGUE_PATH = /\/(?:lol-(?!review\b)[a-z0-9-]+|liveclientdata|riotclient|riot-messaging-service|process-control)\/[^\s'"`)]*/gi;
// A root name on its own (a base address kept in a constant), the client's process and
// token, and the game's local port count as signs too.
const ROOT_NAME = /(?<![\w-])(?:lol-(?!review(?![\w-]))[a-z0-9-]+|liveclientdata|riotclient|riot-messaging-service|process-control)(?![\w-])/i;
const OTHER_SIGNS = /remoting-auth-token|ready-check|LeagueClientUx|(?:127\.0\.0\.1|localhost):2999\b/i;

function trackedFiles() {
  const listed = spawnSync('git', ['ls-files', '-z', '--', 'desktop', 'scripts', 'site', 'proxy'], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.equal(listed.status, 0, listed.stderr);
  return listed.stdout.split('\0').filter(Boolean).filter(file => file !== self);
}

function scan() {
  const hits = new Map();
  const files = trackedFiles();
  for (const file of files) {
    let text;
    try { text = readFileSync(join(root, file), 'utf8'); }
    catch (error) { if (error.code === 'ENOENT') continue; throw error; } // tracked, deleted in this checkout
    const found = [...text.matchAll(LEAGUE_PATH)].map(match => match[0]);
    if (found.length || ROOT_NAME.test(text) || OTHER_SIGNS.test(text)) hits.set(file, { paths: [...new Set(found)].sort(), text });
  }
  return { files, hits };
}

test('outside the C# clients, only the developer probe names a League client path', () => {
  const { files, hits } = scan();
  // The scan must have read the tree (an empty listing would otherwise pass).
  assert.ok(files.length > 200, `only ${files.length} tracked files were scanned`);
  assert.ok(files.includes('desktop/electron/main.mjs') && files.includes('desktop/ui/shell-outer.js'));
  assert.deepEqual([...hits.keys()], [DEVELOPER_PROBE]);
});

test('the developer probe calls exactly its listed paths, all read-only', () => {
  const { hits } = scan();
  const probe = hits.get(DEVELOPER_PROBE);
  assert.ok(probe, 'the developer probe was not found');
  assert.deepEqual(probe.paths, [...DEVELOPER_PROBE_PATHS].sort());
  assert.doesNotMatch(probe.text, /-Method\s+(?!Get\b)\w+/i, 'the probe only reads');
});

test('nothing outside the C# clients mentions answering a found match', () => {
  const { hits } = scan();
  for (const [file, { text }] of hits) assert.doesNotMatch(text, /ready-check/i, file);
});

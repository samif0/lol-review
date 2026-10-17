// Picks the next release tag from the existing tags: the highest stable
// vMAJOR.MINOR.PATCH, bumped. Prerelease tags never count as the base.
//
//   git tag -l 'v*' | node scripts/next-version.mjs <patch|minor|major>

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const stable = /^v(\d+)\.(\d+)\.(\d+)$/;

export function nextVersion(tags, bump) {
  const versions = tags.map(tag => stable.exec(tag.trim())).filter(Boolean).map(match => match.slice(1).map(Number));
  if (versions.length === 0) throw new Error('no stable vMAJOR.MINOR.PATCH tag to bump');
  const [major, minor, patch] = versions.sort((a, b) => b[0] - a[0] || b[1] - a[1] || b[2] - a[2])[0];
  switch (bump) {
    case 'major': return `v${major + 1}.0.0`;
    case 'minor': return `v${major}.${minor + 1}.0`;
    case 'patch': return `v${major}.${minor}.${patch + 1}`;
    default: throw new Error(`bump must be patch, minor or major, not ${JSON.stringify(bump)}`);
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    process.stdout.write(`${nextVersion(readFileSync(0, 'utf8').split('\n'), process.argv[2])}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exit(1);
  }
}

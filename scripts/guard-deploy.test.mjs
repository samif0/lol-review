import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { deployBlockers, guardDeploy, packageChecks, repositoryRoot } from './guard-deploy.mjs';

const main = 'a'.repeat(40);

test('a clean checkout of origin/main may deploy', () => {
  assert.deepEqual(deployBlockers({ status: '', head: main, mainHead: main }), []);
});

test('dirty trees, other commits and an unresolved main are refused', () => {
  assert.match(deployBlockers({ status: ' M site/index.html\n', head: main, mainHead: main }).join('\n'), /uncommitted or untracked/);
  assert.match(deployBlockers({ status: '?? proxy/src/new.ts\n', head: main, mainHead: main }).join('\n'), /uncommitted or untracked/);
  assert.match(deployBlockers({ status: '', head: 'b'.repeat(40), mainHead: main }).join('\n'), /is not origin\/main/);
  assert.match(deployBlockers({ status: '', head: main, mainHead: '' }).join('\n'), /origin\/main could not be resolved/);
  assert.equal(deployBlockers({ status: ' M x\n', head: 'b'.repeat(40), mainHead: main }).length, 2);
});

test('unknown deploy targets are refused before touching git', () => {
  assert.throws(() => guardDeploy('desktop'), /Unknown deploy target "desktop"/);
  assert.throws(() => guardDeploy('__proto__'), /Unknown deploy target/);
  assert.throws(() => guardDeploy(undefined), /Unknown deploy target/);
});

test('each deploy runs its package checks, and the proxy deploy runs the guard first', async () => {
  assert.deepEqual(packageChecks.proxy.commands, [['npm', 'run', 'typecheck'], ['npm', 'test']]);
  assert.deepEqual(packageChecks.site.commands.map(command => command.slice(1)), [['--test', 'site/tests']]);
  const proxy = JSON.parse(await readFile(path.join(repositoryRoot, 'proxy', 'package.json'), 'utf8'));
  assert.equal(proxy.scripts.typecheck, 'tsc --noEmit');
  assert.match(proxy.scripts.deploy, /^node \.\.\/scripts\/guard-deploy\.mjs proxy && wrangler deploy$/);
});

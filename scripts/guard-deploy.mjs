// Pre-deploy gate for the manually deployed parts of Revu (proxy/ Worker, site/ Pages).
// Deploys are not triggered by git, so this refuses unless the checkout IS reviewed
// main: a clean tree, HEAD equal to origin/main after a fetch, and the package's own
// checks passing. Usage: node scripts/guard-deploy.mjs <proxy|site>
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** Each package's checks, run from `cwd` (relative to the repository). Commands are fixed strings. */
export const packageChecks = Object.freeze({
  proxy: { cwd: 'proxy', commands: [['npm', 'run', 'typecheck'], ['npm', 'test']] },
  site: { cwd: '.', commands: [[process.execPath, '--test', 'site/tests']] },
});

const short = sha => (sha ? sha.slice(0, 12) : '(unknown)');

/** Reasons the checkout must not be deployed; empty when it is reviewed main. */
export function deployBlockers({ status, head, mainHead }) {
  const blockers = [];
  if (status.trim()) {
    blockers.push('The working tree has uncommitted or untracked changes. Commit (and merge to main) or remove them first.');
  }
  if (!mainHead) blockers.push('origin/main could not be resolved after fetching.');
  else if (head !== mainHead) {
    blockers.push(`HEAD ${short(head)} is not origin/main ${short(mainHead)}. Deploy only from an up-to-date main checkout.`);
  }
  return blockers;
}

function git(args) {
  const result = spawnSync('git', args, { cwd: repositoryRoot, encoding: 'utf8', windowsHide: true });
  if (result.error || result.status !== 0) {
    throw new Error(`git ${args.join(' ')} failed: ${(result.stderr || result.error?.message || '').trim()}`);
  }
  return result.stdout.trim();
}

function runCheck([command, ...args], cwd) {
  // npm is a .cmd shim on Windows, which Node only spawns through a shell. The
  // arguments are the fixed strings above, never user input.
  const shell = command === 'npm' && process.platform === 'win32';
  console.log(`> ${[command === process.execPath ? 'node' : command, ...args].join(' ')}`);
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell, windowsHide: true });
  return !result.error && result.status === 0;
}

/** Throws unless `name` may be deployed from this checkout; returns the deployed commit. */
export function guardDeploy(name) {
  const checks = Object.hasOwn(packageChecks, name) ? packageChecks[name] : null;
  if (!checks) throw new Error(`Unknown deploy target "${name}". Use one of: ${Object.keys(packageChecks).join(', ')}.`);
  git(['fetch', '--quiet', 'origin', 'main']);
  const state = {
    status: git(['status', '--porcelain', '--untracked-files=normal']),
    head: git(['rev-parse', 'HEAD']),
    mainHead: git(['rev-parse', '--verify', '--quiet', 'refs/remotes/origin/main']),
  };
  const blockers = deployBlockers(state);
  if (blockers.length) throw new Error(`Refusing to deploy ${name}:\n- ${blockers.join('\n- ')}`);
  const cwd = path.join(repositoryRoot, checks.cwd);
  for (const command of checks.commands) {
    if (!runCheck(command, cwd)) throw new Error(`Refusing to deploy ${name}: its checks failed.`);
  }
  console.log(`Deploy guard passed for ${name} at ${short(state.head)} (origin/main).`);
  return state.head;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { guardDeploy(process.argv[2]); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}

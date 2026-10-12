// Guarded Cloudflare Pages deploy for revu.lol. Pages serves every uploaded file,
// so site/ is staged through an allowlist first: tests, README.md and wrangler.toml
// never reach the production domain. The upload runs the wrangler version pinned
// in proxy/package-lock.json. Usage (from the repository root):
//   node scripts/deploy-site.mjs               guard, stage, deploy, remove the stage
//   node scripts/deploy-site.mjs --stage-only  stage for a local `wrangler pages dev` preview
import { spawnSync } from 'node:child_process';
import { copyFile, lstat, mkdir, readFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { guardDeploy, repositoryRoot } from './guard-deploy.mjs';

export const siteRoot = path.join(repositoryRoot, 'site');
// site/wrangler.toml's pages_build_output_dir points here.
export const stageRoot = path.join(repositoryRoot, 'artifacts', 'site-deploy');

const publicExtensions = new Set(['.html', '.css', '.js', '.png', '.jpg', '.jpeg', '.webp', '.gif',
  '.ico', '.svg', '.txt', '.xml', '.webmanifest', '.woff2']);
const pagesConfigFiles = new Set(['_headers', '_redirects']);
const privateRootFiles = new Set(['README.md', 'wrangler.toml', 'package.json', 'package-lock.json']);
const privateDirectories = new Set(['tests', 'node_modules']);
// Hidden entries and repo-internal directories never ship.
const isPrivatePath = parts => parts.some(part => part.startsWith('.')) || privateDirectories.has(parts[0]);

/** 'publish', 'skip' (repo-internal), or 'unknown' for a file under site/ (POSIX relative path). */
export function classifySiteFile(relative) {
  const parts = relative.split('/');
  if (relative.includes('\\') || parts.some(part => !part || part === '..')) return 'unknown';
  if (isPrivatePath(parts)) return 'skip';
  if (parts.length === 1 && privateRootFiles.has(relative)) return 'skip';
  if (parts.length === 1 && pagesConfigFiles.has(relative)) return 'publish';
  return publicExtensions.has(path.posix.extname(relative).toLowerCase()) ? 'publish' : 'unknown';
}

async function listFiles(root, relative = '') {
  const files = [];
  for (const entry of await readdir(path.join(root, relative), { withFileTypes: true })) {
    const child = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isSymbolicLink()) throw new Error(`site/ must not contain links: ${child}`);
    if (entry.isDirectory()) {
      if (isPrivatePath(child.split('/'))) continue;
      files.push(...await listFiles(root, child));
    } else if (entry.isFile()) files.push(child);
    else throw new Error(`Unsupported entry in site/: ${child}`);
  }
  return files;
}

/** Copies the public files of `source` into a fresh `destination`; returns their relative paths. */
export async function stageSite(source = siteRoot, destination = stageRoot) {
  const files = await listFiles(source);
  const unknown = files.filter(file => classifySiteFile(file) === 'unknown');
  if (unknown.length) {
    throw new Error(`Unrecognized files in site/ (add them to the allowlist in scripts/deploy-site.mjs, or keep them out of site/): ${unknown.join(', ')}`);
  }
  const published = files.filter(file => classifySiteFile(file) === 'publish').sort();
  for (const required of ['index.html', '_headers']) {
    if (!published.includes(required)) throw new Error(`site/${required} is missing.`);
  }
  await rm(destination, { recursive: true, force: true });
  for (const file of published) {
    const from = path.join(source, file), to = path.join(destination, file);
    if (!(await lstat(from)).isFile()) throw new Error(`Expected a regular file: site/${file}`);
    await mkdir(path.dirname(to), { recursive: true });
    await copyFile(from, to);
  }
  return published;
}

/** The exact wrangler version proxy/package-lock.json resolves, so both deploys use the same tool. */
export async function pinnedWranglerVersion(lockPath = path.join(repositoryRoot, 'proxy', 'package-lock.json')) {
  const lock = JSON.parse(await readFile(lockPath, 'utf8'));
  const version = lock.packages?.['node_modules/wrangler']?.version;
  if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) throw new Error('proxy/package-lock.json does not pin an exact wrangler version.');
  return version;
}

async function deploySite({ stageOnly }) {
  if (!stageOnly) guardDeploy('site');
  const published = await stageSite();
  console.log(`Staged ${published.length} public files in ${path.relative(repositoryRoot, stageRoot)}.`);
  if (stageOnly) return;
  const version = await pinnedWranglerVersion();
  try {
    // cwd site/ so wrangler reads site/wrangler.toml (project name, compatibility date).
    // npx is a .cmd shim on Windows, which Node only spawns through a shell; every
    // argument is a fixed string or the validated version.
    const result = spawnSync('npx', ['--yes', `wrangler@${version}`, 'pages', 'deploy', '../artifacts/site-deploy',
      '--project-name=revu-site', '--branch=main'],
    { cwd: siteRoot, stdio: 'inherit', windowsHide: true, shell: process.platform === 'win32' });
    if (result.error || result.status !== 0) throw new Error('wrangler pages deploy failed.');
  } finally {
    // A later bare `wrangler pages deploy` then fails instead of shipping a stale stage.
    await rm(stageRoot, { recursive: true, force: true });
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.some(arg => arg !== '--stage-only')) {
    console.error('Usage: node scripts/deploy-site.mjs [--stage-only]');
    process.exitCode = 1;
  } else {
    deploySite({ stageOnly: args.includes('--stage-only') })
      .catch(error => { console.error(error.message); process.exitCode = 1; });
  }
}

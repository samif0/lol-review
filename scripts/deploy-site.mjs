// Guarded Cloudflare Pages deploy for revu.lol. Pages serves every uploaded file,
// so site/ is staged through an allowlist first: tests, README.md and wrangler.toml
// never reach the production domain. The upload runs the wrangler version pinned
// in proxy/package-lock.json. Usage (from the repository root):
//   node scripts/deploy-site.mjs               guard, stage, deploy, remove the stage
//   node scripts/deploy-site.mjs --stage-only  stage without deploying, to inspect the upload
// Local preview commands are in site/README.md.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFile, lstat, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
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
// RFC 8615: site/.well-known/ (security.txt, assetlinks.json, ...) exists only to be
// served, so every regular file in it ships whatever its extension.
const wellKnown = '.well-known';
const isWellKnown = parts => parts.length > 1 && parts[0] === wellKnown;
// Hidden entries (local tool state, OS metadata, env files) and repo-internal
// directories never ship; only a top-level .well-known/ is exempt.
const isPrivatePath = parts => parts.some((part, index) => part.startsWith('.') && !(index === 0 && part === wellKnown))
  || privateDirectories.has(parts[0]);
// Self-hosted third-party code (site/vendor/hls-1.7.3/ ...). _headers caches /vendor/* for a
// year as immutable, so only a folder named for its version ships, and in it only scripts and
// licence texts. A licence needs its extension (LICENSE.txt): a bare LICENSE is refused.
const vendorFile = /^vendor\/[a-z0-9.-]+-\d+\.\d+\.\d+\/[A-Za-z0-9._-]+\.(?:js|txt)$/;

/** 'publish', 'skip' (repo-internal), or 'unknown' for a file under site/ (POSIX relative path). */
export function classifySiteFile(relative) {
  const parts = relative.split('/');
  if (relative.includes('\\') || parts.some(part => !part || part === '..')) return 'unknown';
  if (isPrivatePath(parts)) return 'skip';
  if (parts.length === 1 && privateRootFiles.has(relative)) return 'skip';
  if (parts.length === 1 && pagesConfigFiles.has(relative)) return 'publish';
  if (isWellKnown(parts)) return 'publish';
  if (parts[0] === 'vendor') return vendorFile.test(relative) ? 'publish' : 'unknown';
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

// Cloudflare keeps a page's stylesheets and scripts in the browser for hours, while the pages
// themselves are checked on every load, so after a deploy a viewer could meet a new page with
// the CSS or JS it replaced. Each staged page therefore asks for the exact files it was staged
// with: href="/vod.css" becomes href="/vod.css?v=<12 hex of its SHA-256>". Only <link> and
// <script> tags (and the preview image tags below) are touched, never inline script text
// (clip.html's is pinned by hash in _headers), and only root-relative .css and .js files
// that are staged themselves.
const assetTag = /<(?:link|script)\b[^>]*>/gi;
const assetAttribute = /(\s(?:href|src)=")(\/[A-Za-z0-9._/-]+\.(?:css|js))(")/;
const inlineScript = /<script\b(?![^>]*\ssrc=)[^>]*>[\s\S]*?<\/script>/gi;
// Link unfurlers cache a preview image by its URL far longer than any header asks: Discord
// kept showing a card two redesigns old. So an og:image or twitter:image tag that names a
// staged image on https://revu.lol gets a ?v= too. Shared clip cards, which the proxy
// renders, name the same version (OG_IMAGE_VERSION in proxy/src/clips.ts).
const previewTag = /<meta\b[^>]*\s(?:property|name)="(?:og:image|twitter:image)"[^>]*>/gi;
const previewAttribute = /(\scontent="https:\/\/revu\.lol)(\/[A-Za-z0-9._/-]+\.(?:jpe?g|png|webp|gif))(")/;

/** The tags of `html` that name a file in `versions` (path -> version), with ?v=<version> added. */
export function fingerprintHtml(html, versions) {
  const version = (match, before, url, after) =>
    (versions.has(url) ? `${before}${url}?v=${versions.get(url)}${after}` : match);
  const tags = text => text.replace(assetTag, tag => tag.replace(assetAttribute, version))
    .replace(previewTag, tag => tag.replace(previewAttribute, version));
  // The text of an inline script is never touched, whatever it says.
  let out = '', last = 0;
  for (const block of html.matchAll(inlineScript)) {
    out += tags(html.slice(last, block.index)) + block[0];
    last = block.index + block[0].length;
  }
  return out + tags(html.slice(last));
}

/** The first 12 hex digits of the SHA-256 of `content`. */
export function contentVersion(content) {
  return createHash('sha256').update(content).digest('hex').slice(0, 12);
}

/** Copies the public files of `source` into a fresh `destination`; returns their relative paths. */
export async function stageSite(source = siteRoot, destination = stageRoot) {
  const files = await listFiles(source);
  const unknown = files.filter(file => classifySiteFile(file) === 'unknown');
  if (unknown.length) {
    const licence = unknown.some(file => /(^|\/)LICENSE$/.test(file)) ? ' A licence file needs its extension: LICENSE.txt.' : '';
    throw new Error(`Unrecognized files in site/ (add them to the allowlist in scripts/deploy-site.mjs, or keep them out of site/): ${unknown.join(', ')}${licence}`);
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
  const versions = new Map();
  for (const file of published.filter(name => /\.(?:css|js|jpe?g|png|webp|gif)$/.test(name))) {
    versions.set(`/${file}`, contentVersion(await readFile(path.join(destination, file))));
  }
  for (const page of published.filter(name => name.endsWith('.html'))) {
    const staged = path.join(destination, page);
    const html = await readFile(staged, 'utf8');
    const versioned = fingerprintHtml(html, versions);
    if (versioned !== html) await writeFile(staged, versioned);
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

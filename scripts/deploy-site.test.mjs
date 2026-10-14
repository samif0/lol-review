import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { classifySiteFile, pinnedWranglerVersion, siteRoot, stageRoot, stageSite } from './deploy-site.mjs';

async function scratch(t) {
  const base = path.resolve(os.tmpdir());
  const root = await mkdtemp(path.join(base, 'Revu.SiteDeploy.Tests.'));
  t.after(async () => {
    assert.equal(path.dirname(root), base);
    await rm(root, { recursive: true, force: true });
  });
  return root;
}
async function file(root, name, value = 'fixture') {
  await mkdir(path.dirname(path.join(root, name)), { recursive: true });
  await writeFile(path.join(root, name), value);
}
async function tree(root, relative = '') {
  const names = [];
  for (const entry of await readdir(path.join(root, relative), { withFileTypes: true })) {
    const child = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) names.push(...await tree(root, child)); else names.push(child);
  }
  return names.sort();
}

test('only public site files are classified for publishing', () => {
  for (const name of ['index.html', 'discord/index.html', 'styles.css', 'app.js', 'og-image.jpg', 'favicon.ico',
    'revu.svg', 'robots.txt', 'sitemap.xml', '_headers', '_redirects', 'shot-home.webp', 'fonts/geist.woff2',
    'fonts/OFL-Geist.txt', '.well-known/security.txt',
    '.well-known/assetlinks.json', '.well-known/apple-app-site-association'])
    assert.equal(classifySiteFile(name), 'publish', name);
  for (const name of ['README.md', 'wrangler.toml', 'package.json', 'tests/index.js', 'tests/headers.test.mjs',
    '.DS_Store', '.wrangler/state.json', 'node_modules/x/index.js', 'ui/.env', '.well-known/.DS_Store',
    'nested/.well-known/security.txt']) assert.equal(classifySiteFile(name), 'skip', name);
  for (const name of ['notes.md', 'data.json', 'docs/README.md', 'nested/_headers', '../index.html', 'a\\b.html', 'a//b.html',
    '.well-known']) assert.equal(classifySiteFile(name), 'unknown', name);
});

test('staging copies public files only and refuses unrecognized files', async t => {
  const root = await scratch(t), source = path.join(root, 'site'), destination = path.join(root, 'stage');
  for (const name of ['index.html', '_headers', 'discord/index.html', 'styles.css', 'README.md', 'wrangler.toml',
    'tests/index.js', 'tests/page.test.mjs', '.DS_Store', '.wrangler/tmp/state.json', '.well-known/security.txt',
    '.well-known/.DS_Store']) await file(source, name);
  await file(destination, 'stale.html', 'from an earlier stage');
  const published = await stageSite(source, destination);
  assert.deepEqual(published, ['.well-known/security.txt', '_headers', 'discord/index.html', 'index.html', 'styles.css']);
  assert.deepEqual(await tree(destination), published);
  await file(source, 'notes.md');
  await assert.rejects(stageSite(source, destination), /Unrecognized files in site\/.*: notes\.md$/);
  await rm(path.join(source, 'notes.md'));
  await rm(path.join(source, '_headers'));
  await assert.rejects(stageSite(source, destination), /site\/_headers is missing/);
});

const isLocal = value => value && !/^(?:[a-z][a-z0-9+.-]*:|\/\/|#|\$\{)/i.test(value);

function localReferences(html) {
  const references = [];
  for (const match of html.matchAll(/\s(?:href|src)\s*=\s*"([^"]*)"/g)) {
    const value = match[1].trim();
    if (isLocal(value)) references.push(value.split(/[?#]/)[0]);
  }
  // srcset lists "url width" pairs separated by commas.
  for (const match of html.matchAll(/\ssrcset\s*=\s*"([^"]*)"/g)) {
    for (const candidate of match[1].split(',')) {
      const value = candidate.trim().split(/\s+/)[0];
      if (isLocal(value)) references.push(value.split(/[?#]/)[0]);
    }
  }
  return references;
}

function cssReferences(css) {
  return [...css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/url\(\s*['"]?([^'")]+)['"]?\s*\)/g)]
    .map(match => match[1].trim()).filter(isLocal).map(value => value.split(/[?#]/)[0]);
}

// Cloudflare Pages serves /name from name.html and /dir/ from dir/index.html.
function servedBy(published, page, reference) {
  const target = reference.startsWith('/') ? reference.slice(1)
    : path.posix.normalize(path.posix.join(path.posix.dirname(page), reference));
  const base = target.replace(/\/$/, '');
  const candidates = base === '' || base === '.' ? ['index.html'] : [base, `${base}.html`, `${base}/index.html`];
  return candidates.some(candidate => published.has(candidate));
}

test('the staged production site keeps every page and asset its HTML and CSS reference, and every redirect target', async t => {
  const root = await scratch(t), destination = path.join(root, 'stage');
  const published = new Set(await stageSite(siteRoot, destination));
  for (const name of ['README.md', 'wrangler.toml']) assert.equal(published.has(name), false, name);
  assert.equal([...published].some(name => name.startsWith('tests/')), false);
  for (const name of ['index.html', 'clip.html', 'sign-in.html', 'my-clips.html', 'account.html', '_headers'])
    assert.ok(published.has(name), name);
  for (const page of [...published].filter(name => name.endsWith('.html'))) {
    const html = await readFile(path.join(destination, page), 'utf8');
    for (const reference of localReferences(html)) {
      if (reference) assert.ok(servedBy(published, page, reference), `${page} references missing ${reference}`);
    }
  }
  for (const sheet of [...published].filter(name => name.endsWith('.css'))) {
    for (const reference of cssReferences(await readFile(path.join(destination, sheet), 'utf8'))) {
      assert.ok(servedBy(published, sheet, reference), `${sheet} references missing ${reference}`);
    }
  }
  // Every redirect lands on a published page, and none hides a published file. A 200 is a
  // rewrite (/v/:id serves /vod), so it may only match placeholder paths.
  const redirects = (await readFile(path.join(destination, '_redirects'), 'utf8')).split(/\r?\n/)
    .map(line => line.trim()).filter(line => line && !line.startsWith('#')).map(line => line.split(/\s+/));
  assert.ok(redirects.length > 0);
  for (const [from, to, status] of redirects) {
    assert.match(status, /^(?:30[1278]|200)$/, from);
    if (status === '200') assert.match(from, /\/:[a-z]+$/, `${from} rewrites a fixed path`);
    assert.ok(servedBy(published, 'index.html', to), `_redirects sends ${from} to missing ${to}`);
    assert.equal(servedBy(published, 'index.html', from), false, `_redirects hides the published ${from}`);
  }
});

test('wrangler.toml points Pages at the stage and the deploy pins the proxy wrangler', async () => {
  const config = await readFile(path.join(siteRoot, 'wrangler.toml'), 'utf8');
  const output = /^pages_build_output_dir\s*=\s*"([^"]+)"/m.exec(config)?.[1];
  assert.equal(path.resolve(siteRoot, output), stageRoot);
  assert.match(await pinnedWranglerVersion(), /^\d+\.\d+\.\d+$/);
});

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const siteDir = fileURLToPath(new URL('..', import.meta.url));
const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const headers = read('_headers').replace(/\r\n/g, '\n');

// Parse _headers into { path: [header lines] }. Comments and blank lines
// are skipped; indented lines belong to the path above them.
function parseHeaders(source) {
  const blocks = new Map();
  let current = null;
  for (const line of source.split('\n')) {
    if (!line.trim() || line.trim().startsWith('#')) continue;
    if (/^\s/.test(line)) {
      assert.ok(current, `header line without a path: ${line}`);
      blocks.get(current).push(line.trim());
    } else {
      current = line.trim();
      assert.ok(!blocks.has(current), `duplicate block ${current}`);
      blocks.set(current, []);
    }
  }
  return blocks;
}

const blocks = parseHeaders(headers);

function csp(path) {
  const lines = blocks.get(path);
  assert.ok(lines, `missing block ${path}`);
  const policy = lines.find(line => line.startsWith('Content-Security-Policy:'));
  assert.ok(policy, `${path} has a CSP`);
  return Object.fromEntries(policy.slice('Content-Security-Policy:'.length).split(';').map(part => {
    const [name, ...values] = part.trim().split(/\s+/);
    return [name, values];
  }));
}

function clipInlineScript() {
  const html = read('clip.html');
  return html.match(/<script>([\s\S]*?)<\/script>/)[1];
}

const sha = text => `'sha256-${createHash('sha256').update(text, 'utf8').digest('base64')}'`;

test('Each account page detaches the global CSP and gets a strict one', () => {
  for (const path of ['/sign-in', '/sign-in.html', '/my-clips', '/my-clips.html', '/account', '/account.html']) {
    const lines = blocks.get(path);
    assert.ok(lines, `missing block ${path}`);
    assert.equal(lines[0], '! Content-Security-Policy', `${path} detaches first`);
    const policy = csp(path);
    assert.ok(!policy['script-src'].includes("'unsafe-inline'"), `${path} script-src`);
    assert.deepEqual(policy['default-src'], ["'self'"]);
    assert.ok(policy['connect-src'].includes('https://clips.revu.lol'), `${path} connect-src`);
    assert.ok(policy['connect-src'].includes('http://localhost:8787'), `${path} connect-src dev`);
    assert.deepEqual(policy['frame-ancestors'], ["'none'"]);
    assert.deepEqual(policy['object-src'], ["'none'"]);
    if (path.startsWith('/sign-in')) {
      assert.deepEqual(policy['script-src'], ["'self'", 'https://challenges.cloudflare.com']);
      assert.deepEqual(policy['frame-src'], ['https://challenges.cloudflare.com']);
    } else {
      assert.deepEqual(policy['script-src'], ["'self'"]);
      assert.deepEqual(policy['frame-src'], ["'none'"]);
      assert.ok(lines.includes('X-Robots-Tag: noindex'), `${path} noindex`);
    }
  }
});

test('The global CSP allows no inline script but still allows Turnstile', () => {
  const policy = csp('/*');
  assert.ok(!policy['script-src'].includes("'unsafe-inline'"));
  assert.deepEqual(policy['script-src'], ["'self'", 'https://challenges.cloudflare.com']);
  assert.ok(policy['frame-src'].includes('https://challenges.cloudflare.com'));
  assert.ok(policy['connect-src'].includes('https://api.github.com'), 'download.js still reaches GitHub');
});

test('The clip page allows only its own inline script, by LF and CRLF hash', () => {
  const script = clipInlineScript().replace(/\r\n/g, '\n');
  const lf = sha(script);
  const crlf = sha(script.replace(/\n/g, '\r\n'));
  for (const path of ['/clip', '/clip.html']) {
    assert.equal(blocks.get(path)[0], '! Content-Security-Policy', `${path} detaches first`);
    const policy = csp(path);
    assert.ok(!policy['script-src'].includes("'unsafe-inline'"), `${path} script-src`);
    assert.deepEqual(policy['script-src'], ["'self'", lf, crlf],
      `${path}: clip.html's inline script changed; update both hashes in _headers`);
  }
});

test('The account pages are not in the sitemap', () => {
  const sitemap = read('sitemap.xml');
  assert.doesNotMatch(sitemap, /sign-in|my-clips|\/account/);
});

function htmlFiles(dir) {
  return readdirSync(dir, { recursive: true })
    .map(file => String(file).replace(/\\/g, '/'))
    .filter(file => file.endsWith('.html'))
    .filter(file => !/(^|\/)(\.wrangler|\.claude-flow|node_modules)\//.test(file));
}

const EXECUTABLE_TYPES = new Set(['', 'text/javascript', 'application/javascript', 'module', 'text/ecmascript', 'application/ecmascript']);

test('No page has an executable inline script except clip.html, and none has inline handlers', () => {
  const files = htmlFiles(siteDir);
  for (const required of ['index.html', 'review.html', 'clip.html', 'sign-in.html', 'my-clips.html', 'account.html', 'discord/index.html']) {
    assert.ok(files.includes(required), `scanned ${required}`);
  }
  let clipScripts = 0;
  for (const file of files) {
    const html = read(file);
    for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
      const attrs = match[1];
      if (/\bsrc\s*=/i.test(attrs)) continue;
      const type = (attrs.match(/\btype\s*=\s*["']?([^"'\s>]*)/i)?.[1] || '').toLowerCase();
      if (!EXECUTABLE_TYPES.has(type)) continue;
      assert.equal(file, 'clip.html', `${file} has an executable inline script`);
      assert.equal(match[2], clipInlineScript(), 'the hashed script is the first inline script in clip.html');
      clipScripts += 1;
    }
    for (const tag of html.match(/<[a-z][^>]*>/gi) || []) {
      assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, `${file}: ${tag}`);
      assert.doesNotMatch(tag, /=\s*["']?\s*javascript:/i, `${file}: ${tag}`);
    }
  }
  assert.equal(clipScripts, 1);
});

test('The shared game page is unlisted, unframeable and reads media only from clips.revu.lol', () => {
  const paths = ['/v/*', '/vod', '/vod.html'];
  const first = blocks.get(paths[0]);
  for (const path of paths) {
    const lines = blocks.get(path);
    assert.ok(lines, `missing block ${path}`);
    assert.deepEqual(lines, first, `${path} matches /v/*`);
    assert.equal(lines[0], '! Content-Security-Policy', `${path} detaches first`);
    assert.ok(lines.includes('X-Robots-Tag: noindex, nofollow'), `${path} noindex`);
    const policy = csp(path);
    assert.deepEqual(policy['default-src'], ["'self'"]);
    assert.deepEqual(policy['script-src'], ["'self'"], `${path} script-src`);
    assert.deepEqual(policy['media-src'], ['https://clips.revu.lol', 'http://localhost:8787', 'http://127.0.0.1:8787']);
    assert.deepEqual(policy['connect-src'], ['https://clips.revu.lol', 'http://localhost:8787', 'http://127.0.0.1:8787']);
    assert.deepEqual(policy['frame-ancestors'], ["'none'"]);
    assert.deepEqual(policy['object-src'], ["'none'"]);
    assert.deepEqual(policy['base-uri'], ["'self'"]);
  }
  const rules = read('_redirects').replace(/\r\n/g, '\n').split('\n').map(line => line.trim())
    .filter(line => line && !line.startsWith('#')).map(line => line.split(/\s+/));
  // The only rewrite: /v/<id> serves the game page. (/learn's 301s are checked in landing.test.mjs.)
  assert.deepEqual(rules.filter(rule => rule[2] === '200'), [['/v/:id', '/vod', '200']]);
  assert.doesNotMatch(read('sitemap.xml'), /\/v\/|vod/);
});

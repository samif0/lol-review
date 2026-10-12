import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { documentFromHtml, memoryStorage, read as readRaw } from './fake-dom.mjs';

// Checkouts may use CRLF (core.autocrlf), so compare structure with LF.
const read = name => readRaw(name).replace(/\r\n/g, '\n');

const PAGES = ['index.html', 'learn.html', 'privacy.html', 'terms.html', 'discord.html', 'discord/index.html'];
const LINK = '<a class="header-account" href="/sign-in" data-header-account>Sign in</a>';
const headerScript = read('header-account.js');

test('Every marketing page has the header account link and its script, but not clip.html', () => {
  for (const page of PAGES) {
    const html = read(page);
    const header = html.match(/<header class="site-header">[\s\S]*?<\/header>/)[0];
    assert.ok(header.includes(`</nav>\n      ${LINK}\n`), `${page}: link right after the header nav`);
    assert.equal(html.split('data-header-account').length, 2, `${page}: exactly one link`);
    assert.match(html, /<script src="\/header-account\.js" defer><\/script>\n<\/body>/, page);
  }
  const clip = read('clip.html');
  assert.doesNotMatch(clip, /data-header-account|header-account\.js/);
});

test('discord/index.html stays identical to discord.html', () => {
  assert.equal(readRaw('discord/index.html'), readRaw('discord.html'));
});

test('index.html loads its former inline scripts from files', () => {
  const html = read('index.html');
  const ready = html.indexOf('<script src="/turnstile-ready.js"></script>');
  const api = html.indexOf('<script src="https://challenges.cloudflare.com/turnstile/v0/api.js?onload=onTurnstileReady" async defer></script>');
  assert.ok(ready > 0, 'turnstile-ready.js is a plain blocking script');
  assert.ok(api > ready, 'turnstile-ready.js runs before the async Turnstile loader');
  assert.match(html, /<script src="\/version-chip\.js" defer><\/script>/);
  assert.match(html, /<script type="application\/ld\+json">/);
  assert.doesNotMatch(html, /<script>/);
});

test('turnstile-ready.js reserves the onload callback', () => {
  const window = {};
  vm.runInNewContext(read('turnstile-ready.js'), { window });
  assert.equal(window.__turnstileLoaded, false);
  window.onTurnstileReady();
  assert.equal(window.__turnstileLoaded, true);
});

test('version-chip.js writes the latest tag with textContent', async () => {
  const label = { textContent: 'Windows' };
  const requests = [];
  vm.runInNewContext(read('version-chip.js'), {
    document: { querySelector: selector => (selector === '#download-chip-version' ? label : null) },
    fetch: async (url, init) => {
      requests.push({ url, init });
      return { ok: true, json: async () => ({ tag_name: 'v3.14.0' }) };
    },
  });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(requests[0].url, 'https://api.github.com/repos/samif0/lol-review/releases/latest');
  assert.equal(label.textContent, 'v3.14.0');
});

function runHeader(html, { pathname, storage }) {
  const document = documentFromHtml(html);
  const window = { location: { pathname } };
  Object.defineProperty(window, 'localStorage', {
    get() {
      if (storage === 'throw') throw new Error('SecurityError');
      return storage;
    },
  });
  vm.runInNewContext(headerScript, { document, window });
  return document.querySelectorAll('[data-header-account]')[0];
}

test('header-account.js: Sign in by default, My clips with the hint', () => {
  const html = read('privacy.html');
  const signedOut = runHeader(html, { pathname: '/privacy', storage: memoryStorage() });
  assert.equal(signedOut.getAttribute('href'), '/sign-in');
  assert.equal(signedOut.getAttribute('aria-current'), null);

  const signedIn = runHeader(html, { pathname: '/privacy', storage: memoryStorage({ 'revu.web.signedIn.v1': '1' }) });
  assert.equal(signedIn.textContent, 'My clips');
  assert.equal(signedIn.getAttribute('href'), '/my-clips');

  const blocked = runHeader(html, { pathname: '/privacy', storage: 'throw' });
  assert.equal(blocked.getAttribute('href'), '/sign-in');
});

test('header-account.js marks the current page', () => {
  const signIn = runHeader(read('sign-in.html'), { pathname: '/sign-in', storage: memoryStorage() });
  assert.equal(signIn.getAttribute('aria-current'), 'page');

  const myClips = runHeader(read('my-clips.html'), { pathname: '/my-clips', storage: memoryStorage({ 'revu.web.signedIn.v1': '1' }) });
  assert.equal(myClips.getAttribute('aria-current'), 'page');

  const account = runHeader(read('account.html'), { pathname: '/account', storage: memoryStorage({ 'revu.web.signedIn.v1': '1' }) });
  assert.equal(account.getAttribute('href'), '/my-clips');
  assert.equal(account.getAttribute('aria-current'), null);

  const home = runHeader(read('index.html'), { pathname: '/', storage: memoryStorage({ 'revu.web.signedIn.v1': '1' }) });
  assert.equal(home.getAttribute('aria-current'), null);
});

test('header-account.js never writes storage or calls the network', () => {
  assert.doesNotMatch(headerScript, /fetch|setItem|removeItem|innerHTML/);
});

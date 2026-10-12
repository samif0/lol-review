// The clip watch page: a reload in the same tab is not a new view, and the
// ?proxy= override only works while the page itself is served from localhost.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import { flush, memoryStorage } from './fake-dom.mjs';

const html = readFileSync(new URL('../clip.html', import.meta.url), 'utf8');
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];

function element(tag, attributes) {
  const classes = new Set((attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/));
  const attrs = new Map();
  return {
    tagName: tag.toUpperCase(),
    hidden: /\bhidden\b/.test(attributes),
    disabled: false,
    textContent: '',
    value: '',
    style: {},
    listeners: new Map(),
    classList: {
      add: (...values) => values.forEach(value => classes.add(value)),
      remove: (...values) => values.forEach(value => classes.delete(value)),
      contains: value => classes.has(value),
    },
    setAttribute: (key, value) => attrs.set(key, value),
    removeAttribute: key => attrs.delete(key),
    getAttribute: key => attrs.get(key),
    addEventListener(type, callback) { this.listeners.set(type, callback); },
    focus() {},
    select() {},
  };
}

function load({ hostname = 'revu.lol', search = '?id=Abc1234', sessionStorage } = {}) {
  const elements = new Map();
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    elements.set(match[3], element(match[1], match[2]));
  }
  const requests = [];
  const context = {
    document: { getElementById: id => elements.get(id), querySelector: () => ({}), addEventListener() {} },
    navigator: {},
    location: { hostname, search, pathname: '/clip' },
    URLSearchParams,
    fetch: (url, options) => {
      requests.push({ url, method: options?.method || 'GET' });
      return Promise.resolve({ ok: true, json: async () => ({ title: 'Clip', duration_s: 30, view_count: 3 }) });
    },
    setTimeout: () => 0,
    clearTimeout: () => {},
  };
  if (sessionStorage !== undefined) context.sessionStorage = sessionStorage;
  vm.runInNewContext(script, context);
  return { requests, video: elements.get('clip-video') };
}

const views = requests => requests.filter(r => r.method === 'POST' && r.url.includes('/clip-view/'));

test('a clip counts one view per tab; reloading the page does not count again', async () => {
  const tab = memoryStorage();
  const first = load({ sessionStorage: tab });
  await flush();
  assert.deepEqual(views(first.requests).map(r => r.url), ['https://clips.revu.lol/clip-view/Abc1234']);
  assert.equal(tab.getItem('revu.clip.viewed.Abc1234'), '1');

  const reload = load({ sessionStorage: tab });
  await flush();
  assert.equal(views(reload.requests).length, 0);

  const otherClip = load({ sessionStorage: tab, search: '?id=Xyz9876' });
  await flush();
  assert.equal(views(otherClip.requests).length, 1);
});

test('blocked or missing session storage still counts (the server dedupes)', async () => {
  const blocked = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); } };
  const a = load({ sessionStorage: blocked });
  const b = load();
  await flush();
  assert.equal(views(a.requests).length, 1);
  assert.equal(views(b.requests).length, 1);
});

test('?proxy= is ignored on revu.lol and honored only for a loopback proxy on localhost', async () => {
  const prod = load({ search: '?id=Abc1234&proxy=http://localhost:8787' });
  const local = load({ hostname: 'localhost', search: '?id=Abc1234&proxy=http://localhost:8787' });
  const localBad = load({ hostname: 'localhost', search: '?id=Abc1234&proxy=https://evil.example' });
  await flush();
  assert.equal(prod.requests[0].url, 'https://clips.revu.lol/clip-meta/Abc1234');
  assert.equal(local.requests[0].url, 'http://localhost:8787/clip-meta/Abc1234');
  assert.equal(localBad.requests[0].url, 'https://clips.revu.lol/clip-meta/Abc1234');
});

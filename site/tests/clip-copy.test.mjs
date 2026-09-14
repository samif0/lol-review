import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const html = readFileSync(new URL('../clip.html', import.meta.url), 'utf8');
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
const shareLink = 'https://clips.revu.lol/Abc1234';

// Run the actual page script with isolated DOM, network, timer and clipboard
// stubs. Nothing in this suite can access the browser or the OS clipboard.
function page(clipboard, location = { search: '?id=Abc1234', pathname: '/clip' }) {
  const elements = new Map();
  const timers = new Map();
  const requests = [];
  let timerId = 0;
  const document = {
    getElementById: id => elements.get(id),
    querySelector: () => ({}),
    addEventListener() {},
  };
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    const [, tag, attributes, id] = match;
    const classes = new Set((attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/));
    const attrs = new Map();
    elements.set(id, {
      tagName: tag.toUpperCase(),
      hidden: /\bhidden\b/.test(attributes),
      readOnly: /\breadonly\b/.test(attributes),
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
      focus() { document.activeElement = this; },
      select() { this.selection = [0, this.value.length]; },
    });
  }
  vm.runInNewContext(script, {
    document,
    navigator: clipboard === undefined ? {} : { clipboard },
    location,
    URLSearchParams,
    fetch: (url, options) => {
      requests.push({ url, method: options?.method || 'GET' });
      return Promise.resolve({ ok: true, json: async () => ({ title: 'Test clip', duration_s: 30 }) });
    },
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id),
  });
  return {
    button: elements.get('copy-btn'),
    status: elements.get('copied'),
    fallback: elements.get('copy-fallback'),
    input: elements.get('copy-link'),
    document,
    requests,
    timers,
    click: () => elements.get('copy-btn').listeners.get('click')(),
    flushTimers() {
      for (const callback of timers.values()) callback();
      timers.clear();
    },
  };
}

test('Copy link confirms success only after the clipboard write completes', async () => {
  const writes = [];
  let finish;
  const clip = page({ writeText: text => {
    writes.push(text);
    return new Promise(resolve => { finish = resolve; });
  } });
  const pending = clip.click();
  assert.deepEqual(writes, [shareLink]);
  assert.equal(clip.button.disabled, true);
  assert.equal(clip.button.getAttribute('aria-busy'), 'true');
  assert.equal(clip.button.textContent, 'Copying...');
  assert.equal(clip.status.classList.contains('show'), false);
  assert.equal(clip.status.textContent, '');
  await clip.click();
  assert.equal(writes.length, 1, 'Repeated clicks must not start another write');

  finish();
  await pending;
  assert.equal(clip.status.textContent, 'Link copied');
  assert.equal(clip.status.classList.contains('show'), true);
  assert.equal(clip.fallback.hidden, true);
  assert.equal(clip.button.disabled, false);
  assert.equal(clip.button.textContent, 'Copy link');
  assert.equal(clip.button.getAttribute('aria-busy'), undefined);
  clip.flushTimers();
  assert.equal(clip.status.textContent, '');
  assert.equal(clip.status.classList.contains('show'), false);
});

test('Denied clipboard access exposes one selected link and allows a successful retry', async () => {
  let denied = true;
  const writes = [];
  const clip = page({ writeText: async text => {
    writes.push(text);
    if (denied) throw new Error('Permission denied');
  } });
  await clip.click();
  assert.equal(clip.status.textContent, 'Copy unavailable. Copy the link below.');
  assert.equal(clip.status.classList.contains('is-error'), true);
  assert.equal(clip.fallback.hidden, false);
  assert.equal(clip.input.value, shareLink);
  assert.equal(clip.input.readOnly, true);
  assert.equal(clip.document.activeElement, clip.input);
  assert.deepEqual(clip.input.selection, [0, shareLink.length]);
  assert.equal(clip.button.disabled, false);
  assert.equal(clip.timers.size, 0, 'Failure instructions must remain visible');

  const originalInput = clip.input;
  await clip.click();
  assert.equal(clip.input, originalInput, 'Retries reuse the existing manual-copy field');
  denied = false;
  await clip.click();
  assert.deepEqual(writes, [shareLink, shareLink, shareLink]);
  assert.equal(clip.status.textContent, 'Link copied');
  assert.equal(clip.status.classList.contains('is-error'), false);
  assert.equal(clip.fallback.hidden, true);
});

test('Missing Clipboard API shows manual copy without claiming success', async () => {
  const clip = page(undefined);
  await clip.click();
  assert.equal(clip.fallback.hidden, false);
  assert.equal(clip.input.value, shareLink);
  assert.equal(clip.status.classList.contains('is-error'), true);
  assert.equal(clip.status.textContent.includes('copied'), false);
  assert.equal(clip.button.disabled, false);
});

test('A synchronous clipboard exception also restores the button and offers manual copy', async () => {
  const clip = page({ writeText() { throw new Error('Clipboard blocked'); } });
  await clip.click();
  assert.equal(clip.fallback.hidden, false);
  assert.equal(clip.input.value, shareLink);
  assert.equal(clip.status.textContent.includes('copied'), false);
  assert.equal(clip.button.disabled, false);
  assert.equal(clip.button.getAttribute('aria-busy'), undefined);
});

test('An earlier success timer cannot erase a later failure message', async () => {
  let reject = false;
  const clip = page({ writeText: async () => {
    if (reject) throw new Error('Permission revoked');
  } });
  await clip.click();
  assert.equal(clip.timers.size, 1);
  reject = true;
  await clip.click();
  assert.equal(clip.timers.size, 0);
  clip.flushTimers();
  assert.equal(clip.status.textContent, 'Copy unavailable. Copy the link below.');
  assert.equal(clip.status.classList.contains('show'), true);
});

test('Clean clip routes copy the canonical link without changing metadata requests', async () => {
  const writes = [];
  const clip = page({ writeText: async text => { writes.push(text); } }, {
    search: '', pathname: '/Abc1234',
  });
  await clip.click();
  await Promise.resolve();
  await Promise.resolve();
  assert.deepEqual(writes, [shareLink]);
  assert.deepEqual(clip.requests, [
    { url: 'https://clips.revu.lol/clip-meta/Abc1234', method: 'GET' },
    { url: 'https://clips.revu.lol/clip-view/Abc1234', method: 'POST' },
  ]);
});

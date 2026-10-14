// download.js on the home page: every a[data-download] keeps its static
// installer link unless the latest GitHub release names a better one, and the
// labels are written with textContent only.
import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { flush, read } from './fake-dom.mjs';

const source = read('download.js');
const html = read('index.html');
const INSTALLER = 'https://github.com/samif0/lol-review/releases/latest/download/Revu-Setup.exe';

// One stub per element of index.html that download.js looks at.
function page() {
  const elements = [...html.matchAll(/<([a-z]+)\b([^>]*)>/g)]
    .filter(m => /\bdata-download|\bid="footer-version"/.test(m[2]))
    .map(([, tag, attrs]) => ({
      tag,
      attrs,
      href: attrs.match(/href="([^"]*)"/)?.[1] ?? '',
      hidden: /\shidden(\s|$)/.test(attrs),
      textContent: tag === 'span' && /data-download-version/.test(attrs) ? 'Latest version' : '',
    }));
  const has = (el, name) => new RegExp(`\\s${name}(\\s|$|=)`).test(el.attrs);
  const document = {
    querySelectorAll(selector) {
      const name = { 'a[data-download]': 'data-download', '[data-download-note]': 'data-download-note',
        '[data-download-version]': 'data-download-version', '[data-download-size]': 'data-download-size' }[selector];
      assert.ok(name, `unexpected selector ${selector}`);
      return elements.filter(el => has(el, name) && (selector !== 'a[data-download]' || el.tag === 'a'));
    },
    querySelector(selector) {
      assert.equal(selector, '#footer-version');
      return elements.find(el => /id="footer-version"/.test(el.attrs)) || null;
    },
  };
  const pick = name => elements.filter(el => has(el, name));
  return { document, links: pick('data-download').filter(el => el.tag === 'a'), pick, footer: document.querySelector('#footer-version') };
}

async function run({ fetch, userAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)', userAgentData } = {}) {
  const view = page();
  const requests = [];
  vm.runInNewContext(source, {
    document: view.document,
    navigator: { userAgent, userAgentData },
    fetch: async (url, init) => { requests.push({ url, init }); return fetch(url, init); },
  });
  await flush();
  return { ...view, requests };
}

const release = assets => async () => ({ ok: true, json: async () => ({ tag_name: 'v4.0.1', assets }) });
const asset = (name, size = 157 * 1024 * 1024) => ({ name, size, browser_download_url: `https://github.com/samif0/lol-review/releases/download/v4.0.1/${name}` });

test('every download link starts on the static installer', () => {
  const { links } = page();
  assert.equal(links.length, 2);
  for (const link of links) assert.equal(link.href, INSTALLER);
});

test('the latest release names the installer, version and size', async () => {
  const view = await run({ fetch: release([asset('LoLReview-win-Setup.exe'), asset('Revu-Setup.exe')]) });
  assert.equal(view.requests[0].url, 'https://api.github.com/repos/samif0/lol-review/releases/latest');
  for (const link of view.links) assert.equal(link.href, 'https://github.com/samif0/lol-review/releases/download/v4.0.1/Revu-Setup.exe');
  assert.deepEqual(view.pick('data-download-version').map(el => el.textContent), ['v4.0.1', 'v4.0.1']);
  assert.deepEqual(view.pick('data-download-size').map(el => el.textContent), [' · ~157 MB', ' · ~157 MB']);
  assert.equal(view.footer.textContent, 'v4.0.1');
  assert.equal(view.pick('data-download-note')[0].hidden, true, 'no note on Windows');
});

test('older releases fall back to the Velopack installer name', async () => {
  const view = await run({ fetch: release([asset('LoLReview-win-Setup.exe'), asset('RELEASES', 1)]) });
  for (const link of view.links) assert.match(link.href, /LoLReview-win-Setup\.exe$/);
});

test('a failed or odd API answer keeps the static links', async () => {
  for (const fetch of [
    async () => { throw new Error('offline'); },
    async () => ({ ok: false, json: async () => ({}) }),
    release([]),
    release([{ name: 'Revu-Setup.exe', size: 1, browser_download_url: 'https://example.com/Revu-Setup.exe' }]),
  ]) {
    const view = await run({ fetch });
    for (const link of view.links) assert.equal(link.href, INSTALLER);
    assert.equal(view.pick('data-download-size')[0].textContent, '');
  }
});

test('phones and Macs are told Revu is a Windows app', async () => {
  for (const options of [{ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)' },
    { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', userAgentData: { platform: 'macOS' } }]) {
    const view = await run({ fetch: release([asset('Revu-Setup.exe')]), ...options });
    const [note] = view.pick('data-download-note');
    assert.equal(note.hidden, false);
    assert.match(note.textContent, /^Revu is a Windows app\. Open this page on your PC to download it, or review a game in your browser\.$/);
  }
});

test('download.js writes text only', () => {
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});

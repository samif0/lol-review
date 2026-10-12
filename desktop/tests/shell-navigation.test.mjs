import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import vm from 'node:vm';

const ui = new URL('../ui/', import.meta.url);
const read = file => readFile(new URL(file, ui), 'utf8');
const pages = (await readdir(ui)).filter(file => file.endsWith('.html') && file !== 'index.html');
const outer = await read('shell-outer.js');
const shell = await read('index.html');

test('the persistent shell holds the only navigation rail; content pages carry none', async () => {
  assert.ok(pages.length >= 16);
  assert.equal((shell.match(/<nav class="nav\b/g) || []).length, 1);
  for (const page of pages) {
    const html = await read(page);
    assert.doesNotMatch(html, /class="nav[ "]|class="nav-i\b|data-nav=/, `${page} ships its own rail`);
    assert.doesNotMatch(html, /shell\.js|class="aurora"|class="grain"/, `${page} keeps standalone shell markup`);
    assert.match(html, /<script src="\.\/framed\.js"><\/script>/, `${page} must reopen itself inside the shell`);
  }
  await assert.rejects(read('shell.js'), { code: 'ENOENT' });
  // One active-work list: the live auto-show exists only in the shell.
  assert.equal((outer.match(/const ACTIVE_WORK_PAGES/g) || []).length, 1);
});

function runFramed({ top, pathname, search = '', hash = '' }) {
  const classes = [], replaced = [];
  const self = {};
  const context = vm.createContext({
    window: { self, top: top ? self : {} },
    document: { documentElement: { classList: { add: name => classes.push(name) } } },
    location: { pathname, search, hash, replace: url => replaced.push(url) },
  });
  return read('framed.js').then(code => { vm.runInContext(code, context); return { classes, replaced }; });
}

test('a framed page is marked framed and a page opened on its own reopens inside the shell at the same page', async () => {
  const framed = await runFramed({ top: false, pathname: '/games.html' });
  assert.deepEqual(framed.classes, ['framed']);
  assert.deepEqual(framed.replaced, []);
  const alone = await runFramed({ top: true, pathname: '/ui/settings.html', search: '?x=1', hash: '#settings-data' });
  assert.deepEqual(alone.classes, []);
  assert.deepEqual(alone.replaced, ['index.html#settings.html?x=1#settings-data']);
});

test('rail labels and page titles follow the glossary in one place', () => {
  const rail = Object.fromEntries([...shell.matchAll(/data-nav="([a-z]+)"[^>]*aria-label="([^"]+)"[\s\S]*?<span class="tip">([^<]+)<\/span>/g)]
    .map(([, id, label, tip]) => { assert.equal(label, tip, id); return [id, label]; }));
  const labels = vm.runInNewContext(`(${outer.match(/const pageLabels = (\{[\s\S]*?\});/)[1]})`);
  for (const [id, label] of Object.entries(rail)) assert.equal(labels[id], label, `${id} rail label and page title differ`);
  assert.equal(rail.tiltcheck, 'Tilt check');
  assert.equal(rail.games, 'Match library');
  assert.equal(labels.vodplayer, 'VOD review');
  assert.equal(labels.manualentry, 'Add a game manually');
  for (const stale of ['Mental check', 'Recording', 'Add a match', 'Log in', 'Dashboard', 'Tilt Check']) {
    assert.ok(!Object.values(labels).includes(stale), stale);
  }
});

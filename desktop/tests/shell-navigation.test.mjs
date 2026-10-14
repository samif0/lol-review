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

test('every page applies its appearance before any stylesheet and links the shared sheets in order', async () => {
  const all = ['index.html', ...pages];
  assert.equal(all.length, 17);
  const cacheKey = '?v=appearance-20261012';
  for (const page of all) {
    const html = await read(page);
    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    const sheets = [...head.matchAll(/<link rel="stylesheet" href="\.\/([^"]+)">/g)].map(m => m[1]);
    const firstSheet = head.indexOf('<link rel="stylesheet"');
    const prepaint = head.indexOf('<script src="./nav-prepaint.js"></script>');
    assert.ok(prepaint >= 0 && prepaint < firstSheet, `${page}: nav-prepaint.js runs before the first stylesheet`);
    assert.equal(head.indexOf('<script'), prepaint, `${page}: nav-prepaint.js is the first script`);
    const bare = sheets.map(sheet => sheet.replace(/\?.*$/, ''));
    assert.equal(bare[0], 'styles.css', page);
    assert.equal(bare[1], 'kit.css', `${page}: kit.css comes right after styles.css`);
    assert.equal(bare.at(-1), 'appearance.css', `${page}: appearance.css is the last stylesheet`);
    assert.equal(new Set(bare).size, bare.length, `${page}: no stylesheet twice`);
    assert.doesNotMatch(html.slice(html.indexOf('</head>')), /<link rel="stylesheet"/, `${page}: stylesheets stay in the head`);
    assert.doesNotMatch(head, /match-navigation-20260912/, page);
    for (const sheet of bare) await assert.doesNotReject(read(sheet), `${page} links ${sheet}, which must exist`);
    if (page === 'review.html' || page === 'vodplayer.html') {
      const keyed = sheets.filter(sheet => sheet.endsWith(cacheKey)).map(sheet => sheet.replace(cacheKey, ''));
      const local = page === 'review.html' ? 'review-launchpad.css' : 'vod-review.css';
      assert.deepEqual(keyed, ['styles.css', 'kit.css', local, 'appearance.css'], page);
    }
  }
  const shellHead = shell.slice(0, shell.indexOf('</head>'));
  assert.match(shellHead, /<link rel="stylesheet" href="\.\/shell\.css">/);
  assert.match(shellHead, /<script type="module" src="\.\/appearance-native\.mjs"><\/script>/);
  for (const page of pages) assert.doesNotMatch(await read(page), /shell\.css|appearance-native\.mjs/, page);
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

test('the sidebar footer has one account control: the profile row opens Account and lights up there', async () => {
  const foot = shell.slice(shell.indexOf('<div class="side-foot">'), shell.indexOf('</aside>'));
  const links = [...foot.matchAll(/<a\b([^>]*)>/g)].map(([, attrs]) => attrs);
  assert.equal(links.length, 2, 'the account row, then Settings');
  assert.match(links[0], /^ class="nav-i side-acct" id="side-acct" data-nav="onboarding" href="onboarding\.html\?signin=1" aria-labelledby="side-acct-tip side-name"/);
  assert.match(links[1], /class="nav-i" data-nav="settings" href="settings\.html" aria-label="Settings"/);
  assert.equal((shell.match(/data-nav="onboarding"/g) || []).length, 1, 'no second Account item anywhere in the shell');
  assert.equal(shell.split('<circle cx="12" cy="8.5" r="3.5"/>').length - 1, 1, 'the person glyph appears once, in the avatar');
  const row = foot.slice(0, foot.indexOf('</a>'));
  for (const id of ['side-avatar', 'side-name', 'appbar-lcu']) assert.match(row, new RegExp(`id="${id}"`), `${id} is part of the row`);
  // The row is a rail item: markActive lights every .nav-i whose data-nav is the frame's page.
  const fileToNav = vm.runInNewContext(`(${outer.match(/function fileToNav\(pathname\) \{[\s\S]*?\n\}/)[0]})`);
  for (const path of ['/onboarding.html', 'onboarding.html']) assert.equal(fileToNav(path), 'onboarding', path);
  assert.match(outer, /querySelectorAll\('\.nav-i'\)\.forEach\(\(a\) => \{\s*const active = a\.dataset\.nav === navId;/);
  // Every other way in opens the same page, never the first-launch Welcome card.
  assert.match(await read('settings.html'), /href="onboarding\.html\?signin=1">Manage sign-in</);
  assert.match(await read('vodplayer.html'), /id="vp-signin-hint" href="onboarding\.html\?signin=1"/);
  // Long names truncate inside the row; Settings keeps its fixed size beside it.
  const css = await read('shell.css');
  assert.match(css, /\.side-foot \.nav-i\.side-acct\{flex:1;min-width:0;/);
  assert.match(css, /\.side-foot \.nav-i\{justify-content:center;flex:none;width:32px;height:32px;padding:0\}/);
  assert.match(css, /\.side-name\{min-width:0;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;/);
});

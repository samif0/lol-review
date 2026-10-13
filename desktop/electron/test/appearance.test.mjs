import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, unlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { APPEARANCE_FILE, createAppearanceController, DEFAULT_PALETTES, mixHex, readWindowAppearance, splashPalettes,
  validateAppearanceReport, validateWindowAppearance, windowPalette } from '../appearance.mjs';
import { commandRequest } from '../routing.mjs';
import { cssColorToHex, isPageTint, isUnsavedAppearance, MIRROR_DELAY_MS, readShownAppearance, startAppearanceMirror, WATCHED_ATTRIBUTES }
  from '../../ui/appearance-native.mjs';
import { DEFAULT_MODE, UNSAVED_ATTRIBUTE } from '../../ui/appearance.mjs';

const WARM_DARK = Object.freeze({ mode: 'dark', background: '#21201e', text: '#f3f0ea' });
const NEUTRAL_LIGHT = Object.freeze({ mode: 'light', background: '#ffffff', text: '#0f1012', pref: 'light' });
const source = name => readFile(new URL(`../${name}`, import.meta.url), 'utf8');

test('window appearance accepts only a dark or light mode and #rrggbb colors', () => {
  assert.deepEqual(validateWindowAppearance({ mode: 'light', background: '#F8F7F3', text: '#1C1B19', pref: 'system' }),
    { mode: 'light', background: '#f8f7f3', text: '#1c1b19', pref: 'system' });
  assert.deepEqual(validateWindowAppearance(WARM_DARK), WARM_DARK);
  for (const bad of [null, undefined, [], 'dark', 7,
    { ...WARM_DARK, mode: 'system' }, { ...WARM_DARK, mode: 'auto' }, { ...WARM_DARK, mode: undefined },
    { ...WARM_DARK, background: '#fff' }, { ...WARM_DARK, background: 'red' }, { ...WARM_DARK, background: '#12345g' },
    { ...WARM_DARK, background: 'rgb(33, 32, 30)' }, { ...WARM_DARK, background: '#21201e;}' }, { ...WARM_DARK, background: 0x21201e },
    { ...WARM_DARK, text: '#f3f0ea80' }, { ...WARM_DARK, text: undefined }, { mode: 'dark', background: '#21201e' },
    { ...WARM_DARK, pref: 'warm' }, { ...WARM_DARK, pref: null }, { ...WARM_DARK, theme: 'warm' }, { ...WARM_DARK, v: 1 }])
    assert.throws(() => validateWindowAppearance(bad), TypeError, JSON.stringify(bad));
});

test('the command is a host-only native call with one object payload', () => {
  const request = commandRequest('set_window_appearance', { payload: WARM_DARK });
  assert.equal(request.method, 'NATIVE');
  assert.equal(request.definition.route, null);
  assert.throws(() => commandRequest('set_window_appearance', { payload: WARM_DARK, mode: 'dark' }), /Unexpected argument/);
  assert.throws(() => commandRequest('set_window_appearance', {}), /Invalid argument: payload/);
  assert.throws(() => commandRequest('set_window_appearance_now', { payload: WARM_DARK }), /Unsupported desktop command/);
});

test('applying paints the window, round-trips appearance.json and skips unchanged writes', async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), 'Revu.Appearance.'));
  try {
    const painted = [];
    const window = { isDestroyed: () => false, setBackgroundColor: color => painted.push(color) };
    const controller = createAppearanceController({ window, profile });
    assert.equal(await readWindowAppearance(profile), null);
    assert.deepEqual(await controller.apply({ ...NEUTRAL_LIGHT, background: '#FFFFFF' }), { ok: true, saved: true });
    assert.deepEqual(painted, ['#ffffff']);
    const file = path.join(profile, APPEARANCE_FILE);
    assert.deepEqual(JSON.parse(await readFile(file, 'utf8')), { v: 1, ...NEUTRAL_LIGHT });
    assert.deepEqual(await readWindowAppearance(profile), NEUTRAL_LIGHT);
    // Every frame navigation reports again; an unchanged report repaints but never rewrites.
    await unlink(file);
    assert.deepEqual(await controller.apply(NEUTRAL_LIGHT), { ok: true, saved: true });
    assert.deepEqual(painted, ['#ffffff', '#ffffff']);
    assert.equal(await readWindowAppearance(profile), null);
    // A newer report is written; concurrent reports land in order, the last one wins.
    await Promise.all([controller.apply(WARM_DARK), controller.apply({ ...WARM_DARK, background: '#1b1b1b' })]);
    assert.deepEqual(await readWindowAppearance(profile), { ...WARM_DARK, background: '#1b1b1b' });
    assert.throws(() => controller.apply({ ...WARM_DARK, background: 'navy' }), TypeError);
    assert.equal(painted.at(-1), '#1b1b1b');
    // The launch's saved value counts as written; a destroyed window is left alone.
    const gone = { isDestroyed: () => true, setBackgroundColor: () => assert.fail('destroyed window painted') };
    const relaunched = createAppearanceController({ window: gone, profile, saved: await readWindowAppearance(profile) });
    await unlink(file);
    assert.deepEqual(await relaunched.apply({ ...WARM_DARK, background: '#1b1b1b' }), { ok: true, saved: true });
    assert.equal(await readWindowAppearance(profile), null);
  } finally { await rm(profile, { recursive: true, force: true }); }
});

test('a choice the shell could not save paints the window but never rewrites appearance.json', async () => {
  assert.deepEqual(validateAppearanceReport(WARM_DARK), { appearance: WARM_DARK, persist: true });
  assert.deepEqual(validateAppearanceReport({ ...NEUTRAL_LIGHT, persist: false }), { appearance: NEUTRAL_LIGHT, persist: false });
  for (const bad of [null, [], 'dark', { ...WARM_DARK, persist: 'no' }, { ...WARM_DARK, persist: 0 }, { ...WARM_DARK, persist: null },
    { ...WARM_DARK, persist: false, theme: 'warm' }])
    assert.throws(() => validateAppearanceReport(bad), TypeError, JSON.stringify(bad));
  assert.throws(() => validateWindowAppearance({ ...WARM_DARK, persist: true }), TypeError, 'the file never carries persist');
  const profile = await mkdtemp(path.join(os.tmpdir(), 'Revu.Appearance.'));
  try {
    const painted = [];
    const window = { isDestroyed: () => false, setBackgroundColor: color => painted.push(color) };
    const controller = createAppearanceController({ window, profile });
    assert.deepEqual(await controller.apply(WARM_DARK), { ok: true, saved: true });
    const file = path.join(profile, APPEARANCE_FILE);
    const before = await readFile(file, 'utf8');
    assert.deepEqual(await controller.apply({ ...NEUTRAL_LIGHT, persist: false }), { ok: true, saved: false });
    assert.deepEqual(painted, ['#21201e', '#ffffff'], 'the window shows the unsaved choice now');
    assert.equal(await readFile(file, 'utf8'), before, 'the next launch still opens in the saved look');
    assert.throws(() => controller.apply({ ...NEUTRAL_LIGHT, persist: 'no' }), TypeError);
    // Once the choice is saved, the same colors are written.
    assert.deepEqual(await controller.apply(NEUTRAL_LIGHT), { ok: true, saved: true });
    assert.deepEqual(await readWindowAppearance(profile), NEUTRAL_LIGHT);
  } finally { await rm(profile, { recursive: true, force: true }); }
});

test('an unwritable profile still paints the window and reports the save as failed', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.Appearance.'));
  try {
    const painted = [];
    const controller = createAppearanceController({ profile: path.join(root, 'missing', 'profile'),
      window: { isDestroyed: () => false, setBackgroundColor: color => painted.push(color) } });
    assert.deepEqual(await controller.apply(WARM_DARK), { ok: true, saved: false });
    assert.deepEqual(painted, ['#21201e']);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('a missing, corrupt, old or tampered appearance.json falls back to the defaults', async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), 'Revu.Appearance.'));
  try {
    const file = path.join(profile, APPEARANCE_FILE);
    for (const text of ['{', 'null', '[]', JSON.stringify(WARM_DARK), JSON.stringify({ v: 2, ...WARM_DARK }),
      JSON.stringify({ v: 1, ...WARM_DARK, background: '#21201e;}</style><script>' }),
      JSON.stringify({ v: 1, ...WARM_DARK, extra: true })]) {
      await writeFile(file, text);
      assert.equal(await readWindowAppearance(profile), null, text);
    }
    await writeFile(file, JSON.stringify({ v: 1, ...WARM_DARK, pref: 'system' }));
    assert.deepEqual(await readWindowAppearance(profile), { ...WARM_DARK, pref: 'system' });
  } finally { await rm(profile, { recursive: true, force: true }); }
});

test('the window opens in the saved colors, Warm dark before any, and follows Windows for System', () => {
  assert.equal(DEFAULT_MODE, 'dark');
  assert.equal(windowPalette(null, { dark: true }).background, '#21201e');
  assert.equal(windowPalette(null, { dark: false }).background, '#21201e', 'the app default mode is dark, not Windows');
  assert.equal(windowPalette(null, { dark: false, fallback: 'system' }).background, '#f8f7f3');
  assert.equal(windowPalette(NEUTRAL_LIGHT, { dark: true }).background, '#ffffff');
  const system = { ...WARM_DARK, background: '#1c1d1f', pref: 'system' };
  assert.equal(windowPalette(system, { dark: true }).background, '#1c1d1f');
  assert.deepEqual(windowPalette(system, { dark: false }), DEFAULT_PALETTES.light);
  assert.equal(mixHex('#ffffff', '#000000', 0.5), '#808080');
  assert.equal(windowPalette(WARM_DARK).muted, mixHex('#f3f0ea', '#21201e', 0.62));
});

test('the splash follows Windows only for a saved System choice, and opens in the app default before that', () => {
  assert.deepEqual(splashPalettes(null), { base: DEFAULT_PALETTES.dark, light: null }, 'the first launch matches the dark default window');
  assert.deepEqual(splashPalettes(NEUTRAL_LIGHT).light, null);
  assert.equal(splashPalettes(NEUTRAL_LIGHT).base.background, '#ffffff');
  const darkSystem = splashPalettes({ ...WARM_DARK, background: '#1c1d1f', pref: 'system' });
  assert.equal(darkSystem.base.background, '#1c1d1f');
  assert.deepEqual(darkSystem.light, DEFAULT_PALETTES.light);
  const lightSystem = splashPalettes({ ...NEUTRAL_LIGHT, pref: 'system' });
  assert.deepEqual(lightSystem.base, DEFAULT_PALETTES.dark);
  assert.equal(lightSystem.light.background, '#ffffff');
});

test('main reads the saved appearance before the splash and window, never sets themeSource, and keeps controls clickable', async () => {
  const main = await source('main.mjs');
  assert.doesNotMatch(main, /themeSource\s*=|setThemeSource/);
  assert.doesNotMatch(main + await source('splash.mjs'), /#0d101b/i);
  const read = main.indexOf('await readWindowAppearance(profile)');
  assert.ok(read > 0 && read < main.indexOf('createSplash({') && read < main.indexOf('new BrowserWindow('));
  assert.match(main, /backgroundColor: windowPalette\(savedAppearance, \{ dark: nativeTheme\.shouldUseDarkColors \}\)\.background/);
  assert.match(main, /command === 'set_window_appearance'\) value = await appearance\.apply\(args\.payload\)/);
  const drag = /const DRAG_CSS = ([^;]+);\r?\n/.exec(main)[1];
  assert.equal(Function(`return ${drag}`)(), '[data-window-drag]{-webkit-app-region:drag}'
    + '[data-window-drag] :is(a,button,input,select,textarea,[role="button"]),.appbar-controls,.appbar-controls *{-webkit-app-region:no-drag}');
});

test('the renderer reads computed colors as opaque #rrggbb only', () => {
  assert.equal(cssColorToHex('rgb(33, 32, 30)'), '#21201e');
  assert.equal(cssColorToHex('rgba(248, 247, 243, 1)'), '#f8f7f3');
  assert.equal(cssColorToHex('rgb(248 247 243 / 100%)'), '#f8f7f3');
  assert.equal(cssColorToHex('  #1C1D1F '), '#1c1d1f');
  assert.equal(cssColorToHex('color(srgb 1 1 1)'), '#ffffff');
  assert.equal(cssColorToHex('color(srgb 0.129412 0.12549 0.117647)'), '#21201e');
  for (const value of ['rgba(0, 0, 0, 0)', 'rgba(33, 32, 30, 0.5)', 'rgb(33 32 30 / 40%)', 'color(srgb 1 1 1 / 0.2)',
    'transparent', 'oklab(0.2 0 0)', '#fff', '', null, undefined, 42])
    assert.equal(cssColorToHex(value), null, String(value));
});

function fakeShell({ attributes = {}, background = 'rgb(33, 32, 30)', text = 'rgb(243, 240, 234)', rootBackground = 'rgba(0, 0, 0, 0)' } = {}) {
  const attrs = { 'data-theme': 'warm', 'data-mode': 'dark', 'data-mode-pref': 'dark', ...attributes };
  const root = { getAttribute: name => attrs[name] ?? null };
  const body = {};
  const style = { body: { backgroundColor: background, color: text }, root: { backgroundColor: rootBackground, color: text } };
  const timers = new Map();
  let nextTimer = 1;
  const view = {
    getComputedStyle: element => (element === body ? style.body : style.root),
    setTimeout: (callback, delay) => { const id = nextTimer++; timers.set(id, { callback, delay }); return id; },
    clearTimeout: id => { timers.delete(id); },
  };
  let observer = null;
  class Observer {
    constructor(callback) { this.callback = callback; observer = this; }
    observe(target, options) { this.target = target; this.options = options; }
    disconnect() { this.disconnected = true; }
  }
  return { doc: { documentElement: root, body }, view, attrs, style, timers, Observer, observer: () => observer,
    async flush() {
      const pending = [...timers.values()];
      timers.clear();
      for (const { callback } of pending) await callback();
    } };
}

test('the shell reports its body colors, the resolved mode and the saved preference', () => {
  const shell = fakeShell({ attributes: { 'data-mode-pref': 'system' } });
  assert.deepEqual(readShownAppearance(shell.doc, shell.view),
    { mode: 'dark', background: '#21201e', text: '#f3f0ea', pref: 'system' });
  shell.attrs['data-mode'] = 'light'; shell.attrs['data-mode-pref'] = 'bogus';
  shell.style.body.backgroundColor = 'rgba(0, 0, 0, 0)'; shell.style.root.backgroundColor = 'rgb(255, 255, 255)';
  shell.style.body.color = 'rgb(15, 16, 18)';
  assert.deepEqual(readShownAppearance(shell.doc, shell.view), { mode: 'light', background: '#ffffff', text: '#0f1012' });
  shell.style.root.backgroundColor = 'transparent';
  assert.equal(readShownAppearance(shell.doc, shell.view), null);
  assert.equal(readShownAppearance(null, shell.view), null);
});

test('the mirror sends once at start, then once per settled change, and only when colors change', async () => {
  const shell = fakeShell();
  const calls = [];
  let fail = false;
  const invoke = async (command, args) => { calls.push([command, args]); if (fail) throw new Error('The app is closing'); return { ok: true }; };
  const mirror = startAppearanceMirror({ invoke, doc: shell.doc, view: shell.view, Observer: shell.Observer });
  await mirror.ready;
  assert.deepEqual(calls, [['set_window_appearance', { payload: { mode: 'dark', background: '#21201e', text: '#f3f0ea', pref: 'dark' } }]]);
  assert.equal(shell.observer().target, shell.doc.documentElement);
  assert.deepEqual(shell.observer().options, { attributes: true, attributeFilter: [...WATCHED_ATTRIBUTES] });
  assert.deepEqual([...WATCHED_ATTRIBUTES], ['data-theme', 'data-mode', 'data-mode-pref', 'data-frame-page', UNSAVED_ATTRIBUTE]);
  assert.equal(UNSAVED_ATTRIBUTE, 'data-appearance-unsaved');

  // A burst of attribute changes is one timer; unchanged colors send nothing.
  shell.observer().callback(); shell.observer().callback(); shell.observer().callback();
  assert.equal(shell.timers.size, 1);
  assert.equal([...shell.timers.values()][0].delay, MIRROR_DELAY_MS);
  await shell.flush();
  assert.equal(calls.length, 1);

  // Neutral light: one call with the new colors.
  Object.assign(shell.attrs, { 'data-theme': 'neutral', 'data-mode': 'light', 'data-mode-pref': 'light' });
  Object.assign(shell.style.body, { backgroundColor: 'rgb(255, 255, 255)', color: 'rgb(15, 16, 18)' });
  shell.observer().callback();
  await shell.flush();
  assert.deepEqual(calls.at(-1)[1].payload, { mode: 'light', background: '#ffffff', text: '#0f1012', pref: 'light' });
  assert.equal(calls.length, 2);

  // A switch to System that keeps the colors is still sent, so the next launch follows Windows.
  shell.attrs['data-mode-pref'] = 'system';
  shell.observer().callback();
  await shell.flush();
  assert.equal(calls.at(-1)[1].payload.pref, 'system');

  // A failed call is retried on the next change even when the colors match.
  fail = true;
  shell.style.body.backgroundColor = 'rgb(27, 27, 27)';
  shell.observer().callback();
  await shell.flush();
  fail = false;
  shell.observer().callback();
  await shell.flush();
  assert.equal(calls.length, 5);
  assert.equal(calls.at(-1)[1].payload.background, '#1b1b1b');

  mirror.stop();
  assert.equal(shell.observer().disconnected, true);
  shell.observer().callback();
  assert.equal(shell.timers.size, 0);
});

test('a choice storage refused is mirrored with persist:false until a save succeeds', async () => {
  const shell = fakeShell();
  const calls = [];
  const invoke = async (command, args) => { calls.push([command, args]); return { ok: true }; };
  const mirror = startAppearanceMirror({ invoke, doc: shell.doc, view: shell.view, Observer: shell.Observer });
  await mirror.ready;
  assert.equal(isUnsavedAppearance(shell.doc), false);
  assert.equal(calls.at(-1)[1].payload.persist, undefined, 'a saved look is sent as before');

  // Settings could not save Neutral light: the window paints it, the file keeps the saved look.
  Object.assign(shell.attrs, { 'data-theme': 'neutral', 'data-mode': 'light', 'data-mode-pref': 'light', [UNSAVED_ATTRIBUTE]: '' });
  Object.assign(shell.style.body, { backgroundColor: 'rgb(255, 255, 255)', color: 'rgb(15, 16, 18)' });
  assert.equal(isUnsavedAppearance(shell.doc), true);
  shell.observer().callback();
  await shell.flush();
  assert.deepEqual(calls.at(-1)[1].payload, { ...NEUTRAL_LIGHT, persist: false });

  // The same choice saved later: same colors, sent again so the host writes them.
  delete shell.attrs[UNSAVED_ATTRIBUTE];
  shell.observer().callback();
  await shell.flush();
  assert.deepEqual(calls.at(-1)[1].payload, NEUTRAL_LIGHT);
  assert.equal(calls.length, 3);
  mirror.stop();
  assert.equal(isUnsavedAppearance(null), false);
});

test('Aura\'s dimmed VOD colors paint the window but are never saved for the next launch', async () => {
  const aura = { mode: 'dark', background: '#1f1e1c', text: '#f3efe8', pref: 'dark' };
  const shell = fakeShell({ attributes: { 'data-theme': 'aura' }, background: 'rgb(31, 30, 28)', text: 'rgb(243, 239, 232)' });
  const calls = [];
  const invoke = async (command, args) => { calls.push([command, args]); return { ok: true }; };
  const mirror = startAppearanceMirror({ invoke, doc: shell.doc, view: shell.view, Observer: shell.Observer });
  await mirror.ready;
  assert.deepEqual(calls.at(-1)[1].payload, aura);
  assert.equal(isPageTint(shell.doc), false);

  // The VOD page dims the shell: the window follows, the file keeps Aura's own colors.
  shell.attrs['data-frame-page'] = 'vodplayer';
  Object.assign(shell.style.body, { backgroundColor: 'rgb(27, 27, 27)', color: 'rgb(239, 239, 239)' });
  assert.equal(isPageTint(shell.doc), true);
  shell.observer().callback();
  await shell.flush();
  assert.deepEqual(calls.at(-1)[1].payload, { mode: 'dark', background: '#1b1b1b', text: '#efefef', pref: 'dark', persist: false });

  // Back on Home: Aura's colors are sent to be saved again.
  shell.attrs['data-frame-page'] = 'dashboard';
  Object.assign(shell.style.body, { backgroundColor: 'rgb(31, 30, 28)', color: 'rgb(243, 239, 232)' });
  shell.observer().callback();
  await shell.flush();
  assert.deepEqual(calls.at(-1)[1].payload, aura);

  // Another theme on the VOD page keeps its colors and saves them as usual.
  Object.assign(shell.attrs, { 'data-theme': 'warm', 'data-frame-page': 'vodplayer' });
  assert.equal(isPageTint(shell.doc), false);
  assert.equal(isPageTint(null), false);
  mirror.stop();
});

test('a System choice keeps its theme in the other mode, so a Windows switch opens in that theme', async () => {
  const neutralSystem = { mode: 'dark', background: '#1c1d1f', text: '#f3f4f6', pref: 'system', alt: { background: '#FFFFFF', text: '#0F1012' } };
  const valid = validateWindowAppearance(neutralSystem);
  assert.deepEqual(valid.alt, { background: '#ffffff', text: '#0f1012' });
  for (const alt of [null, [], 'light', { background: '#ffffff' }, { background: '#ffffff', text: 'red' },
    { background: '#ffffff', text: '#0f1012', mode: 'light' }])
    assert.throws(() => validateWindowAppearance({ ...neutralSystem, alt }), TypeError, JSON.stringify(alt));
  // Saved dark, Windows now light: Neutral's own light colors, not Warm's.
  assert.equal(windowPalette(valid, { dark: false }).background, '#ffffff');
  assert.equal(windowPalette(valid, { dark: true }).background, '#1c1d1f');
  assert.equal(splashPalettes(valid).light.background, '#ffffff');
  assert.equal(splashPalettes(valid).base.background, '#1c1d1f');
  // Saved light, Windows now dark: the theme's dark colors.
  const savedLight = validateWindowAppearance({ mode: 'light', background: '#ffffff', text: '#0f1012', pref: 'system',
    alt: { background: '#1c1d1f', text: '#f3f4f6' } });
  assert.equal(windowPalette(savedLight, { dark: true }).background, '#1c1d1f');
  assert.equal(splashPalettes(savedLight).base.background, '#1c1d1f');
  // An older file without alt still falls back to the default palettes.
  const { alt, ...older } = valid;
  assert.deepEqual(windowPalette(older, { dark: false }), DEFAULT_PALETTES.light);
  // The file round-trips alt.
  const profile = await mkdtemp(path.join(os.tmpdir(), 'Revu.Appearance.'));
  try {
    const controller = createAppearanceController({ window: null, profile });
    assert.deepEqual(await controller.apply(neutralSystem), { ok: true, saved: true });
    assert.deepEqual(await readWindowAppearance(profile), valid);
  } finally { await rm(profile, { recursive: true, force: true }); }
});

test('the mirror reads the theme in the other mode from a hidden probe only for a System choice', () => {
  const shell = fakeShell({ attributes: { 'data-theme': 'neutral', 'data-mode-pref': 'system' }, background: 'rgb(28, 29, 31)', text: 'rgb(243, 244, 246)' });
  const appended = [];
  const tokens = { light: { '--bg': ' #ffffff', '--t1': '#0f1012' }, dark: { '--bg': '#1c1d1f', '--t1': '#f3f4f6' } };
  shell.doc.createElement = () => {
    const attrs = {};
    const probe = { setAttribute: (k, v) => { attrs[k] = v; }, attrs, remove() { probe.removed = true; } };
    return probe;
  };
  shell.doc.body.appendChild = probe => { appended.push(probe); };
  const getComputedStyle = shell.view.getComputedStyle;
  shell.view.getComputedStyle = element => (element.attrs
    ? { getPropertyValue: name => tokens[element.attrs['data-mode']][name] || '' } : getComputedStyle(element));
  assert.deepEqual(readShownAppearance(shell.doc, shell.view),
    { mode: 'dark', background: '#1c1d1f', text: '#f3f4f6', pref: 'system', alt: { background: '#ffffff', text: '#0f1012' } });
  assert.equal(appended.length, 1);
  assert.deepEqual(appended[0].attrs, { 'aria-hidden': 'true', 'data-theme': 'neutral', 'data-mode': 'light' });
  assert.equal(appended[0].hidden, true);
  assert.equal(appended[0].removed, true, 'the probe never stays in the shell');
  // A fixed mode needs no probe.
  shell.attrs['data-mode-pref'] = 'dark';
  assert.equal('alt' in readShownAppearance(shell.doc, shell.view), false);
  assert.equal(appended.length, 1);
  // A token that is not a solid hex color leaves alt out.
  shell.attrs['data-mode-pref'] = 'system';
  tokens.light['--bg'] = 'rgba(0,0,0,.5)';
  assert.equal('alt' in readShownAppearance(shell.doc, shell.view), false);
});

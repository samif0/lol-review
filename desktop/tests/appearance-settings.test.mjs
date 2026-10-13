import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { APPEARANCE_KEY, MODES, THEMES } from '../ui/appearance.mjs';
import {
  APPEARANCE_SETTINGS_COPY, MODE_CHOICES, STATUS_CLEAR_MS, THEME_CHOICES, initializeAppearanceSettings,
} from '../ui/appearance-settings.mjs';

// The Settings "Look and feel" card. The behavior tests run the real nav-prepaint.js
// for the shell and for the Settings frame (as tests/appearance.test.mjs does), so a
// choice is checked end to end: storage, the shell, the forward to the frame, and the
// card reading the result back.
const read = async path => (await readFile(new URL(path, import.meta.url), 'utf8')).replace(/\r\n/g, '\n');
const html = await read('../ui/settings.html');
const css = await read('../ui/settings.css');
const settingsJs = await read('../ui/settings.js');
const moduleSource = await read('../ui/appearance-settings.mjs');
const PREPAINT = await read('../ui/nav-prepaint.js');
const visibleText = markup => markup.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();

const section = html.slice(html.indexOf('<section class="settings-section" id="settings-appearance"'),
  html.indexOf('</section>', html.indexOf('id="settings-appearance"')));
const card = section.slice(section.indexOf('<div class="set-card" id="theme-settings"'), section.indexOf('<div class="set-card" id="window-settings"'));
const cardOpen = card.slice(0, card.indexOf('>') + 1);

test('Look and feel is the first card under Appearance & startup, outside the saved config cards', () => {
  assert.ok(card.length > 0, 'the card sits before the Window card');
  assert.equal(section.indexOf('data-settings-card'), section.indexOf('id="theme-settings"') + 'id="theme-settings" '.length,
    'no other card comes first');
  assert.match(cardOpen, /data-settings-card/);
  // settings.js counts a card as unsaved only through these attributes on a field's ancestors.
  assert.doesNotMatch(cardOpen, /data-config-group|data-native-group/, 'the look saves on change and never joins dirty state');
  assert.doesNotMatch(section.slice(0, section.indexOf('>')), /data-config-group|data-native-group/);
  assert.doesNotMatch(card, /data-config-group|data-native-group|data-action=|set-toggle|aria-pressed|tabindex/,
    'no shared click handler, switch or pressed button inside the card');
  for (const word of 'theme appearance look color colour dark light mode system warm neutral journal aura serif'.split(' ')) {
    assert.match(cardOpen, new RegExp(`data-search="[^"]*\\b${word}\\b`), word);
  }
  assert.match(card, /<h3>Look and feel<\/h3><p class="settings-card-description">Changes apply right away on this computer\.<\/p>/);
  assert.match(card, /<div class="set-status" id="appearance-status" role="status" aria-live="polite"><\/div>/);
  assert.match(visibleText(section), /Choose how Revu looks, set your window size, and decide when Revu runs in the background\./);
  assert.doesNotMatch(visibleText(card), /[–—]/);
  assert.doesNotMatch(moduleSource, /[–—]/);
});

test('the theme is a radio group of four labelled cards, each with a preview in its own theme', () => {
  assert.deepEqual(THEME_CHOICES.map(choice => choice.id), [...THEMES]);
  const fieldset = card.slice(card.indexOf('<fieldset'), card.indexOf('</fieldset>'));
  assert.match(fieldset, /^<fieldset class="settings-appearance-field" role="radiogroup" aria-labelledby="appearance-theme-label"><legend class="settings-appearance-legend" id="appearance-theme-label">Theme<\/legend>/);
  const labels = [...fieldset.matchAll(/<label class="settings-theme-option">([\s\S]*?)<\/label>/g)].map(match => match[1]);
  assert.equal(labels.length, 4);
  labels.forEach((label, index) => {
    const { id, name, description } = THEME_CHOICES[index];
    // A native radio per card: arrow keys move between them and a label click checks one.
    assert.match(label, new RegExp(`^<input class="settings-theme-input" type="radio" name="appearanceTheme" value="${id}" `
      + `aria-labelledby="appearance-theme-${id}" aria-describedby="appearance-theme-${id}-desc"( checked)?>`));
    assert.match(label, new RegExp(`<span class="settings-theme-preview" data-theme="${id}" data-mode="dark" aria-hidden="true">`));
    assert.match(label, new RegExp(`<span class="settings-theme-name" id="appearance-theme-${id}">${name}</span>`));
    assert.match(label, new RegExp(`<span class="settings-theme-desc" id="appearance-theme-${id}-desc">${description}</span>`));
    // The preview draws with tokens: a sidebar, the display face, a note, and a win and loss dot.
    for (const part of ['stp-side', 'stp-face', 'stp-note', 'stp-card', 'data-result="win"', 'data-result="loss"']) {
      assert.ok(label.includes(part), `${id} preview has ${part}`);
    }
  });
  assert.equal((fieldset.match(/ checked/g) || []).length, 1, 'Warm is checked before the script runs');
  assert.deepEqual(THEME_CHOICES.map(choice => choice.description), ['Warm neutrals with a serif for titles.',
    'Cool greys and one clean sans.', 'Warm, with your own notes set in a serif.', 'Warm, with a soft light that follows the time of day.']);
});

test('the mode is a segmented radio group: Match Windows, Dark and Light', () => {
  assert.deepEqual(MODE_CHOICES.map(choice => choice.id), [...MODES]);
  const fieldset = card.slice(card.lastIndexOf('<fieldset'));
  assert.match(fieldset, /^<fieldset class="settings-appearance-field" role="radiogroup" aria-labelledby="appearance-mode-label"><legend class="settings-appearance-legend" id="appearance-mode-label">Mode<\/legend><div class="ui-seg settings-mode-seg">/);
  const options = [...fieldset.matchAll(/<label><input type="radio" name="appearanceMode" value="(\w+)"( checked)?><span>([^<]+)<\/span><\/label>/g)];
  assert.deepEqual(options.map(match => [match[1], match[3]]), MODE_CHOICES.map(choice => [choice.id, choice.name]));
  assert.deepEqual(options.filter(match => match[2]).map(match => match[1]), ['dark'], 'Dark is checked before the script runs');
});

test('Settings CSS keeps the kit and preview faces, lights the current category and colors status by tone', () => {
  assert.doesNotMatch(css, /\.settings-page \*,/, 'no blanket font forcing over kit classes and previews');
  assert.match(css, /\.settings-page :where\(\*:not\(\[class\*="ui-"\]\):not\(\.settings-theme-preview,\.settings-theme-preview \*\)\)\{font-family:var\(--fb\)/);
  assert.match(css, /button\.is-current:hover\{background:var\(--lit-fill\)!important;box-shadow:var\(--lit-shadow\)!important;/);
  assert.match(css, /\.settings-page \.set-status\[data-tone\]\{color:var\(--tone\)\}/);
  assert.match(css, /\.settings-theme-preview\[data-theme="journal"\]\{--stp-voice-face:var\(--f-voice\);--stp-voice:var\(--voice\)\}/);
  assert.match(css, /\.settings-theme-preview\[data-theme="aura"\]\{--stp-glow:var\(--aura-core\);--stp-glow-wide:var\(--aura-wide\)\}/);
  assert.match(html, /<h1 class="ui-title">Settings<\/h1>/, 'the page title uses the display face');
});

test('settings.js boots the card beside Audio devices and maps legacy status hex to a tone', () => {
  assert.match(settingsJs, /import \{ initializeAppearanceSettings \} from '\.\/appearance-settings\.mjs';/);
  assert.match(settingsJs, /try \{ initializeAppearanceSettings\(\); \} catch \(err\) \{[^}]*\}\n\s*try \{ initializeAudioSettings\(\); \}/);
  const source = settingsJs.replace(/^import .*?;\n/gm, '');
  const node = id => ({ id, textContent: '', style: { color: '#7EC9A0' }, dataset: {}, hidden: false, children: [],
    classList: { add() {}, remove() {}, toggle() {} } });
  const ids = new Map(['ffmpeg-status', 'clip-usage'].map(id => [id, node(id)]));
  const context = vm.createContext({
    document: { readyState: 'loading', addEventListener() {}, querySelectorAll: () => [], querySelector: () => null },
    window: {}, $: id => ids.get(id) || null, show() {}, clearEl() {}, toneFromHex: hex => `tone-of-${hex}`,
    console: { error() {}, warn() {}, info() {} }, setTimeout, clearTimeout,
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { renderStatus };`, context);
  context.hooks.renderStatus({ ffmpeg: { text: 'Available', colorHex: '#7EC9A0' }, clipUsage: { text: 'Using 640 MB' } });
  assert.equal(ids.get('ffmpeg-status').textContent, 'Available');
  assert.equal(ids.get('ffmpeg-status').dataset.tone, 'tone-of-#7EC9A0');
  assert.equal(ids.get('ffmpeg-status').style.color, '', 'no inline hex beats the theme');
  ids.get('clip-usage').dataset.tone = 'ok';
  context.hooks.renderStatus({ clipUsage: { text: 'Using 640 MB' } });
  assert.equal(ids.get('clip-usage').dataset.tone, undefined, 'a status without a color drops its old tone');
});

// ── behavior over the real prepaint ──────────────────────────────────────────

function fakeElement(tagName = 'html') {
  const attrs = new Map(), children = [];
  return {
    tagName, attrs, children, style: {}, classList: { add() {} },
    setAttribute(name, value) { attrs.set(name, String(value)); },
    getAttribute(name) { return attrs.has(name) ? attrs.get(name) : null; },
    appendChild(node) { children.push(node); return node; },
  };
}
function memoryStorage(initial) {
  const data = new Map(initial === undefined ? [] : [[APPEARANCE_KEY, JSON.stringify(initial)]]);
  return { data, getItem: key => (data.has(key) ? data.get(key) : null), setItem: (key, value) => data.set(key, String(value)) };
}
function fakeMedia(light) {
  const listeners = [], query = { matches: light, addEventListener: (_type, fn) => listeners.push(fn) };
  const matchMedia = () => query;
  matchMedia.flip = value => { query.matches = value; for (const fn of listeners) fn({ matches: value }); };
  return matchMedia;
}
/** A window (an EventTarget, so revu:appearance reaches listeners) running nav-prepaint.js. */
function prepaintWindow({ pathname, storage, matchMedia, top = null, frame = null }) {
  const win = new EventTarget();
  const root = fakeElement('html');
  const document = { documentElement: root, head: fakeElement('head'), location: { pathname },
    createElement: tag => fakeElement(tag), getElementById: id => (id === 'app-frame' ? frame?.() ?? null : null) };
  Object.assign(win, { document, localStorage: storage, matchMedia, CustomEvent, setInterval: () => 0 });
  win.self = win;
  win.top = top ?? win;
  vm.runInContext(PREPAINT, vm.createContext({ window: win, document, location: { pathname, search: '', hash: '' } }));
  return win;
}
function cardDom(document) {
  const input = (name, value) => ({ name, value, checked: false });
  const themes = THEME_CHOICES.map(choice => input('appearanceTheme', choice.id));
  const modes = MODE_CHOICES.map(choice => input('appearanceMode', choice.id));
  const previews = THEME_CHOICES.map(choice => { const element = fakeElement('span'); element.setAttribute('data-theme', choice.id); return element; });
  const listeners = [];
  const card = {
    dataset: {},
    querySelectorAll: selector => ({ 'input[name="appearanceTheme"]': themes, 'input[name="appearanceMode"]': modes,
      '.settings-theme-preview': previews })[selector] || [],
    addEventListener: (type, fn) => listeners.push([type, fn]),
    removeEventListener: (type, fn) => { const at = listeners.findIndex(entry => entry[0] === type && entry[1] === fn); if (at >= 0) listeners.splice(at, 1); },
  };
  const classes = new Set();
  const status = { textContent: '', classList: { add: (...names) => names.forEach(n => classes.add(n)),
    remove: (...names) => names.forEach(n => classes.delete(n)) }, get tone() { return [...classes].join(' '); } };
  const ids = { 'theme-settings': card, 'appearance-status': status };
  const frameLookup = document.getElementById;
  document.getElementById = id => ids[id] ?? frameLookup(id);
  // A change event bubbles from the radio the player picked up to the card.
  const pick = (list, value) => {
    for (const item of list) item.checked = item.value === value;
    const target = list.find(item => item.value === value);
    for (const [type, fn] of [...listeners]) if (type === 'change') fn({ target });
  };
  return {
    card, status, previews, listeners,
    theme: () => themes.filter(item => item.checked).map(item => item.value),
    mode: () => modes.filter(item => item.checked).map(item => item.value),
    pickTheme: value => pick(themes, value), pickMode: value => pick(modes, value),
  };
}
function fixture({ stored, light = false, storage = memoryStorage(stored), withCard = true } = {}) {
  const media = fakeMedia(light);
  let frameWin = null;
  const shell = prepaintWindow({ pathname: '/index.html', storage, matchMedia: media, frame: () => ({ contentWindow: frameWin }) });
  frameWin = prepaintWindow({ pathname: '/settings.html', storage, matchMedia: media, top: shell });
  const dom = withCard ? cardDom(frameWin.document) : null;
  const timers = [];
  const ui = initializeAppearanceSettings({
    document: frameWin.document, scope: frameWin,
    setTimer: (callback, ms) => { const timer = { callback, ms, cleared: false }; timers.push(timer); return timer; },
    clearTimer: timer => { if (timer) timer.cleared = true; },
  });
  const shown = win => ['data-theme', 'data-mode', 'data-mode-pref'].map(name => win.document.documentElement.getAttribute(name)).join('/');
  return { ui, dom, storage, media, shell, frameWin, timers, shell_: () => shown(shell), frame: () => shown(frameWin),
    saved: () => JSON.parse(storage.data.get(APPEARANCE_KEY) ?? 'null'), previewModes: () => [...new Set(dom.previews.map(p => p.getAttribute('data-mode')))] };
}

test('opening Settings checks what the window shows and gives every preview the resolved mode', () => {
  const f = fixture({ stored: { v: 1, theme: 'journal', mode: 'system' }, light: true });
  assert.equal(f.frame(), 'journal/light/system');
  assert.deepEqual(f.dom.theme(), ['journal']);
  assert.deepEqual(f.dom.mode(), ['system']);
  assert.deepEqual(f.previewModes(), ['light']);
  assert.deepEqual(f.dom.previews.map(preview => preview.getAttribute('data-theme')), ['warm', 'neutral', 'journal', 'aura'],
    'each preview keeps its own theme');
  assert.ok(f.dom.previews.every(preview => /^(morning|day|evening|night)$/.test(preview.getAttribute('data-daypart'))));
  assert.equal(f.dom.status.textContent, '');
});

test('picking a theme saves it and applies it to the shell and this frame at once', () => {
  const f = fixture();
  assert.equal(f.shell_(), 'warm/dark/dark');
  f.dom.pickTheme('neutral');
  assert.deepEqual(f.saved(), { v: 1, theme: 'neutral', mode: 'dark' });
  assert.equal(f.shell_(), 'neutral/dark/dark');
  assert.equal(f.frame(), 'neutral/dark/dark');
  assert.deepEqual(f.dom.theme(), ['neutral']);
  assert.equal(f.dom.status.textContent, APPEARANCE_SETTINGS_COPY.themeSaved);
  assert.equal(f.dom.status.tone, 'good');
  assert.equal(f.dom.card.dataset.dirty, undefined, 'never marks the card unsaved');
  // The confirmation clears itself like the Audio devices card.
  const timer = f.timers.at(-1);
  assert.equal(timer.ms, STATUS_CLEAR_MS);
  timer.callback();
  assert.equal(f.dom.status.textContent, '');
  assert.equal(f.dom.status.tone, '');
});

test('picking a mode saves it with the current theme and the previews follow the resolved mode', () => {
  const f = fixture({ stored: { v: 1, theme: 'aura', mode: 'dark' } });
  f.dom.pickMode('light');
  assert.deepEqual(f.saved(), { v: 1, theme: 'aura', mode: 'light' });
  assert.equal(f.shell_(), 'aura/light/light');
  assert.deepEqual(f.previewModes(), ['light']);
  assert.equal(f.dom.status.textContent, APPEARANCE_SETTINGS_COPY.modeSaved);
  assert.equal(f.timers.length, 1);
  f.dom.pickMode('dark');
  assert.ok(f.timers[0].cleared, 'a new confirmation replaces the pending clear');
  assert.deepEqual(f.previewModes(), ['dark']);
});

test('Match Windows follows Windows live through the shell, and the previews follow it', () => {
  const f = fixture({ light: false });
  f.dom.pickMode('system');
  assert.deepEqual(f.saved(), { v: 1, theme: 'warm', mode: 'system' });
  assert.equal(f.frame(), 'warm/dark/system');
  f.media.flip(true);
  assert.equal(f.shell_(), 'warm/light/system');
  assert.equal(f.frame(), 'warm/light/system');
  assert.deepEqual(f.previewModes(), ['light']);
  assert.deepEqual(f.dom.mode(), ['system']);
});

test('a storage failure keeps the new look for this session and says so', () => {
  const storage = { data: new Map(), getItem: () => null, setItem() { throw new Error('quota'); } };
  const f = fixture({ storage });
  f.dom.pickTheme('aura');
  assert.equal(f.shell_(), 'aura/dark/dark');
  assert.equal(f.frame(), 'aura/dark/dark');
  assert.deepEqual(f.dom.theme(), ['aura']);
  assert.equal(f.dom.status.textContent, 'Could not save. Revu uses this look until you close it.');
  assert.equal(f.dom.status.tone, 'bad');
  assert.equal(f.timers.length, 0, 'the failure stays on screen');
});

test('a choice saved in another window reaches the card through the shell', () => {
  const f = fixture();
  const newValue = JSON.stringify({ v: 1, theme: 'journal', mode: 'light' });
  f.storage.data.set(APPEARANCE_KEY, newValue);
  f.shell.dispatchEvent(Object.assign(new Event('storage'), { key: APPEARANCE_KEY, newValue }));
  assert.equal(f.frame(), 'journal/light/light');
  assert.deepEqual(f.dom.theme(), ['journal']);
  assert.deepEqual(f.dom.mode(), ['light']);
  assert.deepEqual(f.previewModes(), ['light']);
  assert.equal(f.dom.status.textContent, '', 'only the player\'s own choice reports a save');
});

test('without the prepaint API the card still shows the choice in this document', () => {
  const root = fakeElement('html');
  const document = { documentElement: root, location: { pathname: '/settings.html' }, getElementById: () => null };
  const dom = cardDom(document);
  const scope = new EventTarget();
  scope.top = scope;
  const storage = memoryStorage();
  initializeAppearanceSettings({ document, scope, storage, prefersLight: () => true, setTimer: () => 1, clearTimer() {} });
  assert.deepEqual(dom.theme(), ['warm'], 'missing attributes read as the defaults');
  dom.pickMode('system');
  assert.equal(root.getAttribute('data-mode'), 'light');
  assert.equal(root.getAttribute('data-mode-pref'), 'system');
  assert.equal(root.style.colorScheme, 'light');
  assert.deepEqual(JSON.parse(storage.data.get(APPEARANCE_KEY)), { v: 1, theme: 'warm', mode: 'system' });
  assert.deepEqual(dom.previews.map(preview => preview.getAttribute('data-mode')), ['light', 'light', 'light', 'light']);
});

test('a page without the card is left alone, and dispose stops listening', () => {
  const none = initializeAppearanceSettings({ document: { getElementById: () => null }, scope: new EventTarget() });
  assert.equal(none, null);
  const f = fixture();
  f.ui.dispose();
  assert.equal(f.dom.listeners.length, 0);
  f.shell.__revuAppearance.reapply({ theme: 'neutral', mode: 'light' });
  assert.equal(f.frame(), 'neutral/light/light');
  assert.deepEqual(f.dom.theme(), ['warm'], 'a disposed card no longer follows');
});

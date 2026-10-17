import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import { findSettings, preserveSettingsDraft } from '../ui/settings-navigation.mjs';

const source = (await readFile(new URL('../ui/settings.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');
const plain = value => JSON.parse(JSON.stringify(value));
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

function node(id = '', tag = 'DIV', action = '') {
  const names = new Set();
  return {
    id, tagName: tag, value: '', textContent: '', disabled: false, hidden: false,
    dataset: action ? { action } : {}, children: [], style: {},
    classList: {
      add: (...values) => values.forEach(value => names.add(value)),
      remove: (...values) => values.forEach(value => names.delete(value)),
      contains: value => names.has(value),
      toggle: (value, on) => on ? names.add(value) : names.delete(value),
    },
    reportValidity: () => true,
    contains(other) { return !!other && (other === this || this.children.includes(other)); },
    matches(selector) { return selector.split(',').some(part => part.toUpperCase() === tag); },
    closest(selector) {
      if (selector === '[data-action]') return this.dataset.action ? this : null;
      if (selector === '[data-config-group]') return this.group || null;
      if (selector === '[data-settings-section]') return this.section || null;
      if (selector === '.set-toggle[role="switch"]') return this.getAttribute('role') === 'switch' ? this : null;
      return null;
    },
    querySelectorAll(selector) {
      if (selector === '[data-save-status]') return this.children.filter(child => child.saveStatus);
      if (selector.startsWith('[data-action=')) return this.children.filter(child => child.dataset.action === selector.match(/"(.*?)"/)[1]);
      return this.children.filter(child => child.matches(selector));
    },
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
    setAttribute(name, value) { this[name] = value; },
    getAttribute(name) { return this[name]; },
    focus() { this.focused = true; },
  };
}

async function fixture({ native = true, recordingsMaxSizeGb = 0, handler, hash } = {}) {
  const ids = new Map(), groups = [], listeners = new Map(), requests = [], categoryClicks = [], shellEvents = [];
  const sections = { recordingStorage: 'recording', clips: 'playback', account: 'account', review: 'playback' };
  const add = (id, tag, action) => { const element = node(id, tag, action); ids.set(id, element); return element; };
  const group = (name, fieldNames) => {
    const element = add(`${name}-settings`);
    element.dataset.configGroup = name;
    element.section = { dataset: { settingsSection: sections[name] } };
    for (const id of fieldNames) element.children.push(add(id, 'INPUT'));
    element.children.push(add(`${name}-save`, 'BUTTON', 'save_config'), add(`${name}-discard`, 'BUTTON', 'discard_config'));
    const status = add(`${name}-save-status`); status.saveStatus = true; element.children.push(status);
    element.children.forEach(child => { child.group = element; }); groups.push(element);
    return element;
  };
  const recordingStorage = group('recordingStorage', ['recordingsMaxSizeGb']);
  const clips = group('clips', ['clipsFolder', 'clipsMaxSizeMb']);
  const account = group('account', ['riotId', 'region']);
  const review = group('review', ['autoMatchupNotesEnabled']);
  ids.get('autoMatchupNotesEnabled').setAttribute('role', 'switch');
  // Number fields carry their own range, like settings.html.
  Object.assign(ids.get('clipsMaxSizeMb'), { min: '100', max: '50000' });
  Object.assign(ids.get('recordingsMaxSizeGb'), { min: '0', max: '2000' });
  add('recover-games', 'BUTTON', 'recover_missing_games');
  add('recovery-status');
  const config = { recordingsMaxSizeGb, clipsFolder: 'D:\\Clips', clipsMaxSizeMb: 2048, riotId: 'player#NA1', region: 'na1' };
  let failRead = false, reads = 0;
  const invoke = async (command, args) => {
    requests.push({ command, args: args === undefined ? undefined : plain(args) });
    const response = await handler?.(command, args);
    if (response !== undefined) return response;
    if (command === 'save_config') {
      for (const [key, value] of Object.entries(args.payload)) config[key] = value === ' __REVU_CLEAR__ ' ? '' : value;
      return { ok: true };
    }
    return command === 'get_settings_status' ? { backups: [] } : null;
  };
  const document = {
    readyState: 'loading',
    addEventListener(type, callback) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(callback);
    },
    querySelectorAll: selector => selector === '[data-config-group]' ? groups
      : selector === '[data-config-group][data-dirty="true"]' ? groups.filter(group => group.dataset.dirty === 'true') : [],
    querySelector: selector => selector.startsWith('[data-config-group][data-dirty="true"]')
      ? groups.find(group => group.dataset.dirty === 'true') || null
      : selector.startsWith('[data-settings-category=')
        ? { click: () => categoryClicks.push(selector.match(/"(.*?)"/)[1]) } : null,
  };
  const context = vm.createContext({
    document, $: id => ids.get(id) || null,
    window: { top: { dispatchEvent: event => { shellEvents.push(event.type); return true; } } },
    CustomEvent: class { constructor(type) { this.type = type; } },
    show: (element, visible) => { if (element) element.hidden = !visible; },
    clearEl: element => { element.children = []; },
    getInvoke: async () => native ? invoke : null,
    readSnapshot: async () => { reads++; if (failRead) throw new Error(failRead); return plain(config); },
    preserveSettingsDraft, setTimeout, clearTimeout,
    console: { error() {}, warn() {}, info() {} },
    ...(hash === undefined ? {} : { location: { hash } }),
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, loadRecoveryAccess, collectPayload, settingsDirty, saveDirtySettings, loadConfig };`, context);
  context.hooks.render(plain(config));
  await context.hooks.loadRecoveryAccess();
  const emit = async (type, target) => {
    for (const listener of listeners.get(type) || []) await listener({ target, preventDefault() {} });
  };
  return {
    ids, groups: { recordingStorage, clips, account, review }, config, requests, categoryClicks, shellEvents,
    $: id => ids.get(id),
    click: id => emit('click', ids.get(id)),
    edit: async (id, value) => { ids.get(id).value = value; await emit('input', ids.get(id)); },
    change: async (id, value) => { ids.get(id).value = value; await emit('change', ids.get(id)); },
    render: () => context.hooks.render(plain(config)),
    payload: element => plain(context.hooks.collectPayload(element)),
    failRead: (message = 'Read interrupted') => { failRead = message; },
    get reads() { return reads; },
    load: () => context.hooks.loadConfig(),
    dirty: () => context.hooks.settingsDirty(),
    saveBeforeLeaving: () => context.hooks.saveDirtySettings(),
  };
}

test('leaving Settings sees unsaved cards in hidden categories and saves each one as its own button would', async () => {
  const f = await fixture();
  assert.equal(f.dirty(), false);
  await f.edit('clipsFolder', 'E:\\Clips'); await f.edit('region', 'euw1');
  assert.equal(f.dirty(), true);
  assert.equal(await f.saveBeforeLeaving(), true);
  assert.deepEqual(f.requests.filter(request => request.command === 'save_config').map(request => request.args.payload),
    [{ clipsFolder: 'E:\\Clips', clipsMaxSizeMb: 2048 }, { riotId: 'player#NA1', region: 'euw1' }]);
  assert.equal(f.dirty(), false);
  assert.deepEqual(f.categoryClicks, [], 'a successful save leaves the category as it was');

  const failing = await fixture({ handler: command => command === 'save_config' ? { ok: false, message: 'Disk is full.' } : undefined });
  await failing.edit('clipsFolder', 'F:\\Clips');
  assert.equal(await failing.saveBeforeLeaving(), false, 'a card that cannot save keeps the player on Settings');
  assert.equal(failing.$('clipsFolder').value, 'F:\\Clips');
  assert.equal(failing.$('clips-save-status').textContent, 'Disk is full.');
  assert.deepEqual(failing.categoryClicks, ['playback'], 'Settings opens the category of the card that could not save');
});

test('an audio device choice never marks Settings dirty or reaches the leave guard', async () => {
  const f = await fixture();
  // Like settings.html, the audio selects sit outside every data-config-group and data-native-group card.
  for (const id of ['audioInput', 'audioOutput']) f.ids.set(id, node(id, 'SELECT'));
  await f.edit('audioInput', 'mic-desk'); await f.change('audioInput', 'mic-desk');
  await f.change('audioOutput', 'phones');
  assert.equal(f.dirty(), false);
  assert.ok(Object.values(f.groups).every(group => group.dataset.dirty !== 'true'));
  assert.equal(await f.saveBeforeLeaving(), true, 'leaving Settings needs no prompt');
  assert.deepEqual(f.requests.filter(request => request.command === 'save_config'), []);
  assert.deepEqual(f.categoryClicks, []);
});

test('automatic matchup notes default off, save only review preferences and keep unrelated drafts', async () => {
  const f = await fixture();
  assert.equal(f.$('autoMatchupNotesEnabled').getAttribute('aria-checked'), 'false');
  await f.edit('clipsFolder', 'unfinished folder draft');
  await f.click('autoMatchupNotesEnabled');
  assert.equal(f.$('review-save').disabled, false);
  await f.click('review-save');
  assert.deepEqual(f.requests.find(request => request.command === 'save_config').args,
    { payload: { autoMatchupNotesEnabled: true } });
  assert.equal(f.$('autoMatchupNotesEnabled').getAttribute('aria-checked'), 'true');
  assert.equal(f.$('clipsFolder').value, 'unfinished folder draft');
  await f.click('autoMatchupNotesEnabled');
  await f.click('review-discard');
  assert.equal(f.$('autoMatchupNotesEnabled').getAttribute('aria-checked'), 'true');
  await f.click('autoMatchupNotesEnabled');
  await f.click('review-save');
  assert.equal(f.config.autoMatchupNotesEnabled, false);
});

test('a saved Riot account updates the sidebar account row at once; other cards do not', async () => {
  const f = await fixture();
  await f.click('autoMatchupNotesEnabled');
  await f.click('review-save');
  assert.deepEqual(f.shellEvents, [], 'a review preference is not the account');
  await f.edit('riotId', 'bye#NA1');
  await f.click('account-save');
  assert.deepEqual(f.requests.at(-2).args, { payload: { riotId: 'bye#NA1', region: 'na1' } });
  assert.deepEqual(f.shellEvents, ['revu:account-changed']);
});

test('signed in with no Riot ID, the Riot account card says so instead of a blank attached account', async () => {
  const f = await fixture();
  for (const id of ['acct-state', 'acct-email']) f.ids.set(id, node(id));
  Object.assign(f.config, { riotId: '', region: 'na1', riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com' });
  f.render();
  assert.equal(f.$('acct-state').textContent, 'No Riot ID linked yet', 'Home and the Account page send this player here');
  assert.equal(f.$('acct-email').textContent, 'Signed in as you@example.com');
  Object.assign(f.config, { riotId: 'bye#NA1' });
  f.render();
  assert.equal(f.$('acct-state').textContent, 'Account attached: bye#NA1 · NA1');
  Object.assign(f.config, { riotId: '', riotAuthState: 'loggedOut', riotSessionEmail: '' });
  f.render();
  assert.equal(f.$('acct-state').textContent, 'No account attached');
  assert.equal(f.$('acct-email').hidden, true);
});

test('a prompt to link a Riot ID (#settings-account) puts the cursor in the blank Riot ID field once it loads', async () => {
  const f = await fixture({ hash: '#settings-account' });
  f.config.riotId = '';
  await f.load();
  assert.equal(f.$('riotId').focused, true);
  f.$('riotId').focused = false;
  await f.load();
  assert.equal(f.$('riotId').focused, false, 'only on arrival, never on a later reload');
  const linked = await fixture({ hash: '#settings-account' });
  await linked.load();
  assert.equal(linked.$('riotId').focused, undefined, 'a linked Riot ID is left alone');
  const elsewhere = await fixture({ hash: '#settings-recording' });
  elsewhere.config.riotId = '';
  await elsewhere.load();
  assert.equal(elsewhere.$('riotId').focused, undefined, 'another category never takes the cursor');
});

test('browser preview cannot recover matches and says why', async () => {
  const f = await fixture({ native: false });
  assert.equal(f.$('recover-games').disabled, true);
  assert.match(f.$('recovery-status').textContent, /Browser preview cannot reach the League client/);
  await f.click('recover-games');
  assert.deepEqual(f.requests, []);
});

test('recovery runs once at a time and explains disconnected League', async () => {
  const recovery = deferred();
  let response = recovery.promise;
  const f = await fixture({ handler: command => command === 'recover_missing_games' ? response : undefined });
  assert.equal(f.$('recover-games').disabled, false);
  const pending = f.click('recover-games');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.$('recover-games').disabled, true);
  assert.match(f.$('recovery-status').textContent, /Recovering recent matches from League/);
  await f.click('recover-games');
  assert.deepEqual(f.requests, [{ command: 'recover_missing_games', args: undefined }]);
  recovery.resolve({ ok: true, recovered: 2, message: 'Recovered 2 ranked games.' });
  await pending;
  assert.equal(f.$('recovery-status').textContent, 'Recovered 2 ranked games.');
  assert.equal(f.$('recover-games').disabled, false);
  response = { ok: true, recovered: 1 };
  await f.click('recover-games');
  assert.equal(f.$('recovery-status').textContent, '1 match recovered.');
  response = { ok: false, recovered: 0, message: 'Open League of Legends, sign in, and wait for Revu to connect, then try again.' };
  await f.click('recover-games');
  assert.match(f.$('recovery-status').textContent, /Open League of Legends/);
  assert.equal(f.$('recovery-status').classList.contains('bad'), true);
});

test('the recordings size limit saves on its own card, 0 means no limit, and an out-of-range value is not sent', async () => {
  const f = await fixture({ recordingsMaxSizeGb: 0 });
  assert.equal(f.$('recordingsMaxSizeGb').value, '0');
  await f.edit('clipsFolder', 'unfinished clip draft');
  await f.edit('recordingsMaxSizeGb', '150');
  assert.equal(f.groups.recordingStorage.dataset.dirty, 'true');
  await f.click('recordingStorage-save');
  assert.deepEqual(f.requests.find(request => request.command === 'save_config').args, { payload: { recordingsMaxSizeGb: 150 } });
  assert.equal(f.config.recordingsMaxSizeGb, 150);
  assert.equal(f.$('clipsFolder').value, 'unfinished clip draft', 'other cards keep their drafts');

  await f.edit('recordingsMaxSizeGb', '5000');
  assert.deepEqual(f.payload(f.groups.recordingStorage), {}, 'above the field max is never sent');
  await f.edit('recordingsMaxSizeGb', '0');
  assert.deepEqual(f.payload(f.groups.recordingStorage), { recordingsMaxSizeGb: 0 });
  await f.edit('clipsMaxSizeMb', '50');
  assert.deepEqual(f.payload(f.groups.clips), { clipsFolder: 'unfinished clip draft' }, 'the clips limit keeps its own 100 MB floor');
});

test('a failed Settings read reports at once instead of retrying for ten seconds', async () => {
  const f = await fixture();
  f.failRead('Failed to fetch');
  const before = f.reads, started = Date.now();
  await f.load();
  assert.equal(f.reads - before, 1);
  assert.ok(Date.now() - started < 1000);
});

test('a rejected save shows the sidecar sentence without the Electron IPC prefix', async () => {
  const f = await fixture({ handler: command => {
    if (command === 'save_config') throw new Error("Error invoking remote method 'revu:command': Error: Folder is locked.");
  } });
  await f.edit('clipsFolder', 'E:\\Clips'); await f.click('clips-save');
  assert.equal(f.$('clips-save-status').textContent, 'Folder is locked.');
  assert.equal(f.$('clipsFolder').value, 'E:\\Clips');
});

test('the Recording category finds the storage limit and match recovery, and Ascent is gone', async () => {
  const html = await readFile(new URL('../ui/settings.html', import.meta.url), 'utf8');
  assert.doesNotMatch(html, /ascent/i);
  const card = id => ({ id, text: html.match(new RegExp(`id="${id}"[^>]*data-search="([^"]+)"`))[1] });
  const categories = [{ id: 'recording', title: 'Recording', cards: [card('recording-storage-settings'), card('recovery-settings')] }];
  for (const query of ['size limit', 'delete oldest', 'disk space']) {
    assert.deepEqual(findSettings(categories, query)[0].cards, ['recording-storage-settings'], query);
  }
  assert.deepEqual(findSettings(categories, 'missing matches')[0].cards, ['recovery-settings']);
});

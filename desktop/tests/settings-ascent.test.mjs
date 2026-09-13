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
  };
}

async function fixture({ native = true, ascentFolder = '', handler } = {}) {
  const ids = new Map(), groups = [], listeners = new Map(), requests = [];
  const add = (id, tag, action) => { const element = node(id, tag, action); ids.set(id, element); return element; };
  const group = (name, fieldNames) => {
    const element = add(`${name}-settings`);
    element.dataset.configGroup = name;
    for (const id of fieldNames) element.children.push(add(id, 'INPUT'));
    element.children.push(add(`${name}-save`, 'BUTTON', 'save_config'), add(`${name}-discard`, 'BUTTON', 'discard_config'));
    const status = add(`${name}-save-status`); status.saveStatus = true; element.children.push(status);
    element.children.forEach(child => { child.group = element; }); groups.push(element);
    return element;
  };
  const ascent = group('ascent', ['ascentFolder']);
  const clips = group('clips', ['clipsFolder', 'clipsMaxSizeMb']);
  const account = group('account', ['riotId', 'region']);
  for (const [id, action] of [['ascent-browse', 'pick_ascent'], ['ascent-disconnect', 'disconnect_ascent'], ['ascent-scan', 'scan_vods']]) {
    const button = add(id, 'BUTTON', action); button.group = ascent; ascent.children.push(button);
  }
  add('ascent-status');
  const config = { ascentFolder, clipsFolder: 'D:\\Clips', clipsMaxSizeMb: 2048, riotId: 'player#NA1', region: 'na1' };
  let failRead = false;
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
    querySelectorAll: selector => selector === '[data-config-group]' ? groups : [],
    querySelector: () => null,
  };
  const context = vm.createContext({
    document, window: {}, $: id => ids.get(id) || null,
    show: (element, visible) => { if (element) element.hidden = !visible; },
    clearEl: element => { element.children = []; },
    getInvoke: async () => native ? invoke : null,
    readSnapshot: async () => { if (failRead) throw new Error('Read interrupted'); return plain(config); },
    preserveSettingsDraft, setTimeout, clearTimeout,
    console: { error() {}, warn() {}, info() {} },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, loadAscentAccess, collectPayload };`, context);
  context.hooks.render(plain(config));
  await context.hooks.loadAscentAccess();
  const emit = async (type, target) => {
    for (const listener of listeners.get(type) || []) await listener({ target, preventDefault() {} });
  };
  return {
    ids, groups: { ascent, clips, account }, config, requests,
    $: id => ids.get(id),
    click: id => emit('click', ids.get(id)),
    edit: async (id, value) => { ids.get(id).value = value; await emit('input', ids.get(id)); },
    render: () => context.hooks.render(plain(config)),
    payload: element => plain(context.hooks.collectPayload(element)),
    failRead: () => { failRead = true; },
  };
}

test('browser preview disables folder access, scanning and connection writes without implying local availability', async () => {
  const f = await fixture({ native: false, ascentFolder: 'D:\\Ascent' });
  for (const id of ['ascentFolder', 'ascent-browse', 'ascent-save', 'ascent-disconnect', 'ascent-scan']) assert.equal(f.$(id).disabled, true, id);
  assert.match(f.$('ascent-status').textContent, /Browser preview cannot access local recordings/);
  await f.click('ascent-scan'); await f.click('ascent-disconnect');
  assert.deepEqual(f.requests, []);
});

test('picker cancellation is inert; connecting saves only its folder and preserves other category drafts', async () => {
  let picked = null;
  const saved = deferred();
  let blockSave = true;
  const f = await fixture({ handler: async command => {
    if (command === 'pick_folder') return picked;
    if (command === 'save_config' && blockSave) { await saved.promise; blockSave = false; }
  } });
  await f.click('ascent-browse');
  assert.equal(f.groups.ascent.dataset.dirty, 'false');
  picked = ' E:\\Ascent recordings ';
  await f.click('ascent-browse');
  assert.equal(f.$('ascentFolder').value, picked);
  assert.equal(f.$('ascent-save').disabled, false);
  assert.equal(f.$('ascent-scan').disabled, true);
  const pending = f.click('ascent-save');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.$('ascentFolder').disabled, true);
  await f.edit('clipsFolder', '  unfinished clip path  ');
  await f.edit('clipsMaxSizeMb', '');
  saved.resolve(); await pending;
  assert.deepEqual(f.requests.find(request => request.command === 'save_config').args, { payload: { ascentFolder: 'E:\\Ascent recordings' } });
  assert.equal(f.$('clipsFolder').value, '  unfinished clip path  ');
  assert.equal(f.$('clipsMaxSizeMb').value, '');
  assert.equal(f.groups.clips.dataset.dirty, 'true');
  assert.equal(f.$('ascentFolder').value, 'E:\\Ascent recordings');
  assert.equal(f.$('ascent-save').textContent, 'Save folder');
  assert.equal(f.$('ascent-scan').disabled, false);

  await f.edit('ascentFolder', ' raw path in hidden recording category ');
  await f.edit('region', 'euw1'); await f.click('account-save');
  assert.equal(f.$('ascentFolder').value, ' raw path in hidden recording category ');
  assert.equal(f.groups.ascent.dataset.dirty, 'true');
  await f.click('ascent-discard');
  assert.equal(f.$('ascentFolder').value, 'E:\\Ascent recordings');
});

test('empty input cannot clear a connection; explicit disconnect sends the sentinel and safely handles a failed refetch', async () => {
  const f = await fixture({ ascentFolder: 'D:\\Ascent' });
  await f.edit('ascentFolder', '  ');
  assert.deepEqual(f.payload(f.groups.ascent), {});
  assert.equal(f.$('ascent-save').disabled, true);
  await f.click('ascent-save');
  assert.equal(f.requests.length, 0);
  await f.edit('clipsFolder', 'keep this draft');
  f.failRead();
  await f.click('ascent-disconnect');
  assert.deepEqual(f.requests.find(request => request.command === 'save_config').args,
    { payload: { ascentFolder: ' __REVU_CLEAR__ ' } });
  assert.equal(f.$('ascentFolder').value, '');
  assert.equal(f.$('clipsFolder').value, 'keep this draft');
  assert.equal(f.$('ascent-scan').disabled, true);
  assert.equal(f.$('ascent-disconnect').disabled, true);
  assert.match(f.$('ascent-save-status').textContent, /Existing videos and match links are kept/);
});

test('scan uses only the saved folder, serializes actions and reports actual counts and safe error text', async () => {
  const scan = deferred();
  let response = scan.promise;
  const f = await fixture({ ascentFolder: 'D:\\Ascent', handler: command => command === 'scan_vods' ? response : undefined });
  await f.edit('ascentFolder', 'E:\\Unsaved'); await f.click('ascent-scan');
  assert.equal(f.requests.length, 0);
  await f.click('ascent-discard');
  const pending = f.click('ascent-scan');
  await new Promise(resolve => setImmediate(resolve));
  for (const id of ['ascentFolder', 'ascent-browse', 'ascent-disconnect', 'ascent-scan']) assert.equal(f.$(id).disabled, true, id);
  await f.click('ascent-scan'); await f.click('ascent-disconnect');
  assert.deepEqual(f.requests, [{ command: 'scan_vods', args: undefined }]);
  scan.resolve({ ok: true, matched: 1, recordingCount: 3, message: 'Already linked videos were preserved.' });
  await pending;
  assert.equal(f.$('ascent-status').textContent, 'Already linked videos were preserved.');
  assert.equal(f.$('ascent-scan').disabled, false);
  response = { ok: true, matched: 1, recordingCount: 3 };
  await f.click('ascent-scan');
  assert.match(f.$('ascent-status').textContent, /1 match linked · 3 recordings found/);
  response = { ok: false, message: '<img src=x onerror=alert(1)> cannot be read' };
  await f.click('ascent-scan');
  assert.equal(f.$('ascent-status').textContent, '<img src=x onerror=alert(1)> cannot be read');
  assert.equal(f.$('ascent-status').children.length, 0);
  assert.equal(f.$('ascent-status').classList.contains('bad'), true);
  assert.equal(f.$('ascentFolder').value, 'D:\\Ascent');
});

test('failed connection saves keep the draft and allow retry without touching the existing folder', async () => {
  const f = await fixture({ ascentFolder: 'D:\\Ascent', handler: command => command === 'save_config' ? { ok: false, message: 'Folder cannot be accessed.' } : undefined });
  await f.edit('ascentFolder', 'E:\\New folder'); await f.click('ascent-save');
  assert.equal(f.config.ascentFolder, 'D:\\Ascent');
  assert.equal(f.$('ascentFolder').value, 'E:\\New folder');
  assert.equal(f.$('ascent-save').disabled, false);
  assert.equal(f.$('ascent-scan').disabled, true);
  assert.equal(f.$('ascent-save-status').textContent, 'Folder cannot be accessed.');
});

test('Ascent is discoverable as external recording in the Recording category', async () => {
  const html = await readFile(new URL('../ui/settings.html', import.meta.url), 'utf8');
  const search = html.match(/id="ascent-settings"[^>]*data-search="([^"]+)"/)[1];
  const categories = [{ id: 'recording', title: 'Recording', cards: [{ id: 'ascent-settings', text: search }] }];
  for (const query of ['Ascent', 'external recorder', 'link recordings', 'scan folder']) {
    assert.deepEqual(findSettings(categories, query)[0].cards, ['ascent-settings']);
  }
});

import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

// "Export review data": the sidecar saves the Markdown into Documents\Revu and answers with
// the path; the page never carries the export itself (it used to hit the 4 MiB IPC cap).
const source = (await readFile(new URL('../ui/settings.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');

function element(id, action = '') {
  return {
    id, hidden: false, disabled: false, textContent: '', type: '', className: '',
    dataset: action ? { action } : {},
    classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
    closest(selector) { return selector === '[data-action]' && this.dataset.action ? this : null; },
    after(node) { created.push(node); ids.set(node.id, node); },
  };
}
let ids, created;

function fixture(handler) {
  ids = new Map(); created = [];
  for (const [id, action] of [['export-btn', 'export_data'], ['export-status', '']]) ids.set(id, element(id, action));
  const listeners = new Map(), requests = [];
  const document = {
    readyState: 'loading',
    addEventListener(type, callback) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(callback); },
    querySelectorAll: () => [], querySelector: () => null,
    createElement: () => element(''),
  };
  const invoke = async (command, args) => { requests.push(command); return handler(command, args); };
  const context = vm.createContext({
    document, window: {}, $: id => ids.get(id) || null, show() {}, clearEl() {},
    getInvoke: async () => invoke, readSnapshot: async () => ({}), preserveSettingsDraft() {},
    initializeRecordingSettings() {}, initializeSettingsNavigation() {},
    setTimeout, clearTimeout, console: { error() {}, warn() {}, info() {} },
  });
  vm.runInContext(source, context);
  const click = async id => {
    for (const listener of listeners.get('click') || []) await listener({ target: ids.get(id), preventDefault() {} });
  };
  return { requests, click, $: id => ids.get(id) };
}

test('a saved export names the file and offers to open its folder', async () => {
  const f = fixture(command => command === 'export_review_data'
    ? { ok: true, path: 'C:\\Users\\me\\Documents\\Revu\\revu-review-export-20261012-0930.md', fileName: 'revu-review-export-20261012-0930.md', games: 4200 }
    : null);
  await f.click('export-btn');
  assert.deepEqual(f.requests, ['export_review_data']);
  assert.equal(f.$('export-status').textContent, 'Saved to C:\\Users\\me\\Documents\\Revu\\revu-review-export-20261012-0930.md');
  assert.equal(f.$('export-status').hidden, false);
  const open = f.$('export-open-btn');
  assert.ok(open);
  assert.equal(open.hidden, false);
  assert.equal(open.textContent, 'Open folder');
  assert.equal(f.$('export-btn').disabled, false);
  await f.click('export-open-btn');
  assert.deepEqual(f.requests, ['export_review_data', 'open_export_folder']);
  // The Markdown never comes back through the page.
  assert.ok(!f.requests.includes('save_export_file'));
});

test('a failed export says so and hides the folder action', async () => {
  let fail = false;
  const f = fixture(command => {
    if (command !== 'export_review_data') return null;
    if (fail) throw new Error("Couldn't save the export to C:\\Users\\me\\Documents\\Revu. Check that the folder can be written to.");
    return { ok: true, path: 'C:\\Users\\me\\Documents\\Revu\\a.md' };
  });
  await f.click('export-btn');
  assert.equal(f.$('export-open-btn').hidden, false);
  fail = true;
  await f.click('export-btn');
  assert.match(f.$('export-status').textContent, /^Export failed: Couldn't save the export to /);
  assert.equal(f.$('export-open-btn').hidden, true);
  assert.equal(f.$('export-btn').disabled, false);
});

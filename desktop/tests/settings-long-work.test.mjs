import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

// Long Settings work. "Export review data": the sidecar saves the Markdown into
// Documents\Revu and answers with the path; the page never carries the export itself (it
// used to hit the 4 MiB IPC cap). "Update & restart": the download is accepted sidecar work,
// so a request deadline means "still downloading", never "failed".
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

function fixture(handler, scope = {}) {
  ids = new Map(); created = [];
  for (const [id, action] of [['export-btn', 'export_data'], ['export-status', ''],
    ['install-update-btn', 'install_update'], ['update-status', '']]) ids.set(id, element(id, action));
  const listeners = new Map(), requests = [];
  const document = {
    readyState: 'loading',
    addEventListener(type, callback) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(callback); },
    querySelectorAll: () => [], querySelector: () => null,
    createElement: () => element(''),
  };
  const invoke = async (command, args) => { requests.push(command); return handler(command, args); };
  const context = vm.createContext({
    document, window: scope, $: id => ids.get(id) || null, show() {}, clearEl() {},
    getInvoke: async () => invoke, readSnapshot: async () => ({}), preserveSettingsDraft() {},
    initializeRecordingSettings() {}, initializeSettingsNavigation() {},
    setTimeout, clearTimeout, console: { error() {}, warn() {}, info() {} },
  });
  vm.runInContext(source, context);
  const click = async id => {
    for (const listener of listeners.get('click') || []) await listener({ target: ids.get(id), preventDefault() {} });
  };
  return { requests, click, $: id => ids.get(id), progress: value => context.showUpdateDownloadProgress(value) };
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

const timeout = () => new Error("Error invoking remote method 'revu:command': TimeoutError: The operation was aborted due to timeout");

test('an update download that outlives the request deadline is joined, not reported as failed', async () => {
  let downloads = 0;
  const seen = [];
  const f = fixture(command => {
    if (command === 'download_update') {
      seen.push(f.$('update-status').textContent);
      if (++downloads < 3) throw timeout();
      return { ok: true, version: '3.15.0', message: 'Downloaded.' };
    }
    return null;
  });
  await f.click('install-update-btn');
  assert.deepEqual(f.requests, ['download_update', 'download_update', 'download_update', 'apply_update']);
  assert.deepEqual(seen, ['Downloading update…', 'Still downloading the update…', 'Still downloading the update…']);
  assert.equal(f.$('update-status').textContent, 'Installing… the app will restart.');
});

test('a real update failure still says so, and the retries are bounded', async () => {
  const failed = fixture(command => { if (command === 'download_update') throw new Error('Backend HTTP 500'); return null; });
  await failed.click('install-update-btn');
  assert.deepEqual(failed.requests, ['download_update']);
  assert.equal(failed.$('update-status').textContent, 'Update failed. Try again later.');
  assert.equal(failed.$('install-update-btn').disabled, false);

  const stuck = fixture(command => { if (command === 'download_update') throw timeout(); return null; });
  await stuck.click('install-update-btn');
  assert.equal(stuck.requests.filter(command => command === 'download_update').length, 6);
  assert.equal(stuck.$('update-status').textContent, 'Update failed. Try again later.');
});

test('Install update asks the page before restarting, as the update banner does', async () => {
  const stay = fixture(command => command === 'download_update' ? { ok: true, version: '3.15.0' } : null,
    { revuBeforeNavigate: async () => false });
  await stay.click('install-update-btn');
  assert.deepEqual(stay.requests, ['download_update']);
  assert.equal(stay.$('update-status').textContent, 'Update ready. Save or discard your changes, then install it.');
  assert.equal(stay.$('install-update-btn').disabled, false);

  let seenWhenAsked;
  const leave = fixture(command => command === 'download_update' ? { ok: true, version: '3.15.0' } : null,
    { revuBeforeNavigate: async () => { seenWhenAsked = [...leave.requests]; return true; } });
  await leave.click('install-update-btn');
  assert.deepEqual(seenWhenAsked, ['download_update']);
  assert.deepEqual(leave.requests, ['download_update', 'apply_update']);
  assert.equal(leave.$('update-status').textContent, 'Installing… the app will restart.');
});

test('forwarded download progress shows a percent only while Settings waits for its own install', async () => {
  let finish;
  const f = fixture(command => command === 'download_update' ? new Promise(resolve => { finish = resolve; }) : null);
  f.progress({ percent: 30, finished: false });
  assert.equal(f.$('update-status').textContent, '');
  const install = f.click('install-update-btn');
  while (!f.requests.includes('download_update')) await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.$('update-status').textContent, 'Downloading update…');
  f.progress({ percent: 41.6, finished: false });
  assert.equal(f.$('update-status').textContent, 'Downloading update… 42%');
  for (const ignored of [{ percent: 'soon' }, { percent: 100, finished: true, ok: true }, null])
    f.progress(ignored);
  assert.equal(f.$('update-status').textContent, 'Downloading update… 42%');
  finish({ ok: true, version: '3.15.0' });
  await install;
  f.progress({ percent: 100, finished: false });
  assert.equal(f.$('update-status').textContent, 'Installing… the app will restart.');
  assert.match(source, /window\.addEventListener\('revu:update-download-progress', event => showUpdateDownloadProgress\(event\.detail\)\);/);
});

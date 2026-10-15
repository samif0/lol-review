import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createVodShare, formatShareSize, formatShareTime, safeShareUrl, shareQualityLabel, vodShareErrorText, vodSharePhaseLabel, vodSharePlan,
  vodShareProgressLabel, vodShareTtlText, CHECKING, KEEP_REVIEWING, LINK_CHANGED, LINK_CHANGED_SHORT, LINK_EARLY, LINK_GONE,
  LINK_LIVE, LINK_READY, LINK_REPLACING, POOL_720_HINT, SIZE_720_NOTE, STOP_FAILED, STOPPED, STOPPED_QUEUED, CONFIRM_STOP,
  CONFIRM_STOP_LIVE, ENDED_EARLY, LINK_WATCH_NOW, LINK_WATCH_SOON, PAUSED_GAME, SWITCHED_720, UPLINK_720_HINT, UPLINK_SLOW,
  vodShareLiveNote } from '../ui/vod-share.mjs';

const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
const flat = text => String(text).replace(/[  ]/g, ' ');
const flush = async () => { for (let i = 0; i < 6; i++) await new Promise(resolve => setImmediate(resolve)); };
const NOW = Date.UTC(2026, 9, 14, 12, 0); // Wednesday 2026-10-14 12:00 UTC
const SHARE = { id: 'Abc1234', url: 'https://revu.lol/v/Abc1234', quality: '1080p', includeNotes: false,
  sizeBytes: 1315583333, createdAt: NOW / 1000, expiresAt: NOW / 1000 + 259200, markersAttached: true };

function status(over = {}) {
  return { gameId: 123, signedIn: true, allowed: true, sharingOn: true, reachable: true,
    recording: { available: true, builtIn: true, durationSeconds: 2255, tooLong: false },
    estimates: { p1080Bytes: 1315650000, p720Bytes: 789390000 },
    pool: { freeBytes: 5684416667, fits1080: true, fits720: true, fits1080At: null, fits720At: null },
    limits: { live: 1, maxLive: 3, pending: 0, startsToday: 2, maxStartsPerDay: 6, dayResetsAt: 1760486400 },
    ttlSeconds: 259200, job: null, share: null, busyGameId: null, ...over };
}

// Minimal DOM: the module only reads [data-vs] and [data-vs-view] hooks, toggles hidden,
// text, disabled and checked, and listens for clicks, changes, keydown and close.
function element(name, doc) {
  const listeners = new Map(), classes = new Set();
  return { name, hidden: false, textContent: '', disabled: false, checked: false, value: '', dataset: {}, style: {},
    classList: { toggle(c, on) { if (on) classes.add(c); else classes.delete(c); }, contains: c => classes.has(c) },
    addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); },
    removeEventListener(type, fn) { listeners.set(type, (listeners.get(type) || []).filter(f => f !== fn)); },
    dispatch(type, extra = {}) {
      const event = { type, target: this, stopped: false, stopPropagation() { this.stopped = true; }, ...extra };
      for (const fn of listeners.get(type) || []) fn(event);
      return event;
    },
    click() { if (!this.disabled) this.dispatch('click'); },
    focus() { doc.active = this; },
    select() { this.selected = true; } };
}
function container(doc, hooks, views = []) {
  const map = new Map(hooks.map(h => [`[data-vs="${h}"]`, element(h, doc)]));
  for (const v of views) map.set(`[data-vs-view="${v}"]`, element(v, doc));
  const root = element('root', doc);
  return Object.assign(root, { querySelector: selector => map.get(selector) || null,
    el: name => map.get(`[data-vs="${name}"]`), view: name => map.get(`[data-vs-view="${name}"]`) });
}
const ROW_HOOKS = ['row-label', 'row-url', 'row-detail', 'row-live', 'row-actions', 'row-copy', 'row-open', 'row-stop',
  'row-cancel', 'row-retry', 'row-confirm', 'row-confirm-text', 'row-confirm-stop', 'row-keep'];
const SHEET_HOOKS = ['close', 'size', 'q1080', 'q720', 'q1080-label', 'q720-label', 'hint', 'ttl', 'imported', 'notes', 'progress-label', 'progress-live', 'progress-bar',
  'progress-link', 'progress-url', 'progress-copy', 'progress-open', 'progress-link-note',
  'progress-note', 'url', 'copy', 'open', 'expiry', 'markers', 'message-box', 'message', 'signin', 'folder', 'confirm',
  'confirm-text', 'confirm-stop', 'keep', 'actions', 'stop', 'cancel', 'share', 'done'];

function setup({ replies = {}, caps = { externalLinks: true } } = {}) {
  const doc = { active: null };
  const openButton = element('open', doc);
  const row = container(doc, ROW_HOOKS);
  const sheet = container(doc, SHEET_HOOKS, ['start', 'progress', 'done']);
  Object.assign(sheet, { open: false, showModal() { this.open = true; },
    close() { if (!this.open) return; this.open = false; this.dispatch('close'); } });
  const target = new EventTarget(), calls = [], opened = [], timers = { intervals: [], timeouts: [] };
  Object.assign(timers, {
    setInterval: (fn, ms) => { timers.intervals.push({ fn, ms, live: true }); return timers.intervals.length; },
    clearInterval: id => { if (timers.intervals[id - 1]) timers.intervals[id - 1].live = false; },
    setTimeout: (fn, ms) => { timers.timeouts.push({ fn, ms, live: true }); return timers.timeouts.length; },
    clearTimeout: id => { if (timers.timeouts[id - 1]) timers.timeouts[id - 1].live = false; },
  });
  const invoke = async (command, args) => {
    calls.push([command, args]);
    const reply = replies[command];
    return typeof reply === 'function' ? reply(args) : reply;
  };
  const platform = { capabilities: async () => caps, openExternal: async url => { opened.push(url); } };
  const share = createVodShare({ openButton, row, sheet, invoke, platform, target, timers, now: () => NOW,
    locale: 'en-US', timeZone: 'UTC' });
  const emit = detail => target.dispatchEvent(Object.assign(new Event('revu:vod-share-progress'), { detail }));
  const tick = async ms => {
    for (const t of timers.intervals.filter(t => t.live && t.ms === ms)) t.fn();
    for (const t of timers.timeouts.filter(t => t.live && t.ms === ms)) { t.live = false; t.fn(); }
    await flush();
  };
  const sheetView = () => ['start', 'progress', 'done'].find(v => !sheet.view(v).hidden);
  const sheetMessage = () => (sheet.el('message-box').hidden ? '' : flat(sheet.el('message').textContent));
  return { share, openButton, row, sheet, calls, opened, timers, emit, tick, doc, sheetView, sheetMessage,
    named: command => calls.filter(([name]) => name === command) };
}

test('sizes, times, progress labels and links follow the contract', () => {
  assert.equal(formatShareSize(1315650000), '1.3 GB');
  assert.equal(formatShareSize(789390000), '0.8 GB');
  assert.equal(formatShareSize(30_000_000), '0.1 GB');
  for (const bad of [0, -1, NaN, null, 'x']) assert.equal(formatShareSize(bad), '');
  const options = { now: NOW, locale: 'en-US', timeZone: 'UTC' };
  assert.equal(flat(formatShareTime(NOW / 1000 + 259200, options)), 'Saturday 12:00 PM');
  assert.match(flat(formatShareTime(NOW / 1000 + 7 * 86400, options)), /^October 21(,| at) 12:00 PM$/);
  assert.equal(formatShareTime(0, options), '');
  assert.equal(vodShareProgressLabel({ state: 'running', phase: 'queued', percent: 0 }), 'Preparing video 0%');
  assert.equal(vodShareProgressLabel({ state: 'running', phase: 'preparing', percent: 34.9 }), 'Preparing video 34%');
  assert.equal(vodShareProgressLabel({ state: 'running', phase: 'uploading', percent: 61 }), 'Uploading 61%');
  assert.equal(vodShareProgressLabel({ state: 'running', phase: 'uploading', percent: 140 }), 'Uploading 100%');
  assert.equal(vodShareProgressLabel({ state: 'running', phase: 'finishing', percent: 100 }), 'Finishing');
  assert.equal(vodShareProgressLabel({ state: 'done', phase: 'done' }), '');
  assert.equal(vodSharePhaseLabel({ state: 'running', phase: 'preparing', percent: 34 }), 'Preparing video');
  assert.equal(vodSharePhaseLabel({ state: 'running', phase: 'uploading', percent: 61 }), 'Uploading');
  assert.equal(vodSharePhaseLabel({ state: 'done', phase: 'done' }), '');
  assert.equal(safeShareUrl('https://revu.lol/v/Abc1234'), 'https://revu.lol/v/Abc1234');
  assert.equal(safeShareUrl('http://localhost:8788/v/Abc1234'), 'http://localhost:8788/v/Abc1234');
  for (const bad of ['https://evil.example/v/Abc1234', 'https://revu.lol/v/Abc1234/../x', 'javascript:alert(1)',
    'http://revu.lol/v/Abc1234', 'https://revu.lol.evil.example/v/Abc1234', 'https://clips.revu.lol/Abc1234', null]) {
    assert.equal(safeShareUrl(bad), '', String(bad));
  }
  assert.equal(vodShareTtlText(259200), 'Anyone with the link can watch for 3 days.');
  assert.equal(vodShareTtlText(86400), 'Anyone with the link can watch for 1 day.');
});

test('every errorCode has the agreed copy, and unknown codes fall back to the sidecar sentence', () => {
  const time = () => 'Thursday 9:41 PM';
  const expected = {
    pool_full: 'Sharing space is full until Thursday 9:41 PM. Try 720p, or save the file instead.',
    live_limit: 'You already share 3 games. Stop sharing one to share this game.',
    daily_limit: "You've started 6 shares today. You can share again after Thursday 9:41 PM.",
    busy: 'Another game is still being shared. Try again when it finishes.',
    starting: 'Revu is still starting. Try again in a moment.',
    closing: 'Revu is closing. Share again after it restarts.',
    stopping: 'Stopping the previous share. Try again in a moment.',
    network: "Couldn't reach Revu. Check your connection and try again.",
    sharing_paused: 'Full-game sharing is paused right now. Try again later.',
    too_long: "Games longer than 2 hours can't be shared.",
    too_large: 'This game is too large to share, even at 720p.',
    no_recording: "This game's recording isn't on this PC.",
    disk_full: "There isn't enough free disk space to prepare the video.",
    encode_failed: "Revu couldn't prepare the video. Try again.",
    game_in_progress: 'Finish your game first, then share.',
    game_started: 'Sharing stopped because a game started. Share again after the game.',
    upload_failed: "The upload didn't finish. Check your connection and try again.",
    needs_login: 'Sign in to share this game.',
    not_allowed: '', cancelled: '',
  };
  for (const [code, text] of Object.entries(expected)) {
    assert.equal(vodShareErrorText(code, { retryAt: 1760400000, time, error: 'sidecar words' }), text, code);
  }
  assert.equal(vodShareErrorText('pool_full', { time }), 'Sharing space is full right now. Try 720p, or save the file instead.');
  // "Try 720p" only when a 1080p attempt would fit at 720p; otherwise the honest advice is to wait.
  assert.equal(vodShareErrorText('pool_full', { time, retryAt: 1, can720: false }),
    'Sharing space is full until Thursday 9:41 PM. Try again then, or save the file instead.');
  assert.equal(vodShareErrorText('pool_full', { time, can720: false }), 'Sharing space is full right now. Try again later, or save the file instead.');
  assert.equal(vodShareErrorText('live_limit', { limits: { maxLive: 5 } }), 'You already share 5 games. Stop sharing one to share this game.');
  assert.equal(vodShareErrorText('daily_limit', { time }), "You've started 6 shares today. You can share again tomorrow.");
  assert.equal(vodShareErrorText('mystery', { error: ' Proxy said no. ' }), 'Proxy said no.');
  assert.equal(vodShareErrorText('mystery'), "Sharing didn't finish. Try again.");
  for (const text of Object.values(expected)) assert.doesNotMatch(text, /[–—]/);
});

test('the plan preselects 720p when 1080p does not fit and blocks what cannot be shared', () => {
  assert.deepEqual(vodSharePlan(status()), { quality: '1080p', disable1080: false, block: null, canShare: true, hint: '',
    notice: null, sizeText: 'About 1.3 GB at 1080p.', labels: { p1080: '1080p', p720: '720p' }, builtIn: true, imported: false });
  assert.equal(vodSharePlan(status(), '720p').sizeText, 'About 0.8 GB at 720p.');
  const tight = vodSharePlan(status({ pool: { freeBytes: 900e6, fits1080: false, fits720: true, fits1080At: 1760403600 } }), '1080p');
  assert.equal(tight.quality, '720p');
  assert.equal(tight.disable1080, true);
  assert.equal(tight.hint, POOL_720_HINT);
  assert.equal(tight.canShare, true);
  const full = vodSharePlan(status({ pool: { freeBytes: 0, fits1080: false, fits720: false, fits720At: 1760403600 } }));
  assert.deepEqual(full.block, { code: 'pool_full', retryAt: 1760403600 });
  assert.equal(full.canShare, false);
  assert.equal(vodSharePlan(status({ sharingOn: false })).block.code, 'sharing_paused');
  assert.equal(vodSharePlan(status({ recording: { available: true, builtIn: false, tooLong: true } })).block.code, 'too_long');
  assert.equal(vodSharePlan(status({ recording: { available: true, builtIn: false } })).imported, true);
  assert.equal(vodSharePlan(status({ busyGameId: 77 })).block.code, 'busy');
  assert.equal(vodSharePlan(status({ limits: { live: 3, maxLive: 3, startsToday: 0, maxStartsPerDay: 6 } })).block.code, 'live_limit');
  assert.deepEqual(vodSharePlan(status({ limits: { live: 0, maxLive: 3, startsToday: 6, maxStartsPerDay: 6, dayResetsAt: 9 } })).block,
    { code: 'daily_limit', retryAt: 9 });
  // The sidecar's real shape when the proxy can't be reached: sharingOn false, reachable false.
  const offline = vodSharePlan(status({ reachable: false, sharingOn: false, pool: null, limits: null }));
  assert.equal(offline.block, null, 'an unreachable proxy is not "paused"');
  assert.equal(offline.canShare, true);
  assert.equal(offline.notice, 'network');
});

test('the action shows only for a signed-in, allowed account with a recording, or for a link or running share', async () => {
  const cases = [
    [status(), true], [status({ signedIn: false, allowed: false, pool: null, limits: null }), false],
    [status({ allowed: false }), false], [status({ recording: { available: false } }), false],
    [status({ signedIn: false, allowed: false, share: SHARE }), true],
    [status({ allowed: false, job: { state: 'running', phase: 'uploading', percent: 12 } }), true],
  ];
  for (const [reply, visible] of cases) {
    const f = setup({ replies: { get_vod_share_status: reply } });
    await f.share.check({ gameId: 123, hasVod: true, filePath: 'capture.mp4' });
    await flush();
    assert.equal(f.openButton.hidden, !visible, JSON.stringify(reply).slice(0, 90));
  }
  const failing = setup({ replies: { get_vod_share_status: () => { throw new Error('sidecar closing'); } } });
  await failing.share.check({ gameId: 123 });
  assert.equal(failing.openButton.hidden, true);
  assert.equal(failing.row.hidden, true);
  const preview = setup({ replies: { get_vod_share_status: status() } });
  await preview.share.check(null);
  assert.deepEqual(preview.calls, []);
  assert.equal(preview.openButton.hidden, true);
});

test('an existing link shows in the row with Copy, Open, expiry and a confirmed Stop sharing', async () => {
  let stopReply = { ok: false };
  const f = setup({ replies: { get_vod_share_status: () => status({ share: stopReply.ok ? null : SHARE }),
    copy_text_to_clipboard: { ok: true }, stop_vod_share: () => stopReply } });
  await f.share.check({ gameId: 123 });
  await flush();
  assert.equal(f.row.hidden, false);
  assert.equal(f.row.el('row-label').textContent, 'Full game link');
  assert.equal(f.row.el('row-url').textContent, 'https://revu.lol/v/Abc1234');
  assert.equal(flat(f.row.el('row-detail').textContent), 'Link expires Saturday 12:00 PM.');
  assert.equal(f.row.el('row-open').hidden, false);
  f.row.el('row-copy').click();
  await flush();
  assert.deepEqual(f.named('copy_text_to_clipboard'), [['copy_text_to_clipboard', { text: 'https://revu.lol/v/Abc1234' }]]);
  assert.equal(f.row.el('row-copy').textContent, 'Copied');
  await f.tick(2000);
  assert.equal(f.row.el('row-copy').textContent, 'Copy');
  f.row.el('row-open').click();
  await flush();
  assert.deepEqual(f.opened, ['https://revu.lol/v/Abc1234']);

  f.row.el('row-stop').click();
  assert.equal(f.row.el('row-confirm').hidden, false);
  assert.equal(f.row.el('row-actions').hidden, true);
  f.row.el('row-keep').click();
  assert.equal(f.row.el('row-confirm').hidden, true);
  assert.equal(f.named('stop_vod_share').length, 0);
  f.row.el('row-stop').click();
  f.row.el('row-confirm-stop').click();
  await flush();
  assert.deepEqual(f.named('stop_vod_share'), [['stop_vod_share', { payload: { gameId: 123 } }]]);
  assert.equal(f.row.el('row-detail').textContent, STOP_FAILED, 'a failed stop keeps the link and its actions');
  assert.equal(f.row.classList.contains('is-error'), true);
  assert.equal(f.row.el('row-stop').hidden, false);
  // Revu couldn't reach its server: the link still works, so it stays, with an error that stays too.
  stopReply = { ok: true, remoteDeleted: false, queued: true };
  f.row.el('row-stop').click();
  f.row.el('row-confirm-stop').click();
  await flush();
  assert.equal(f.row.el('row-detail').textContent, STOPPED_QUEUED);
  assert.equal(f.row.classList.contains('is-error'), true);
  assert.equal(f.row.el('row-url').textContent, 'https://revu.lol/v/Abc1234');
  assert.equal(f.row.el('row-stop').hidden, false, 'Stop sharing stays available');
  assert.equal(f.row.el('row-live').textContent, STOPPED_QUEUED);
  await f.tick(6000);
  assert.equal(f.row.el('row-detail').textContent, STOPPED_QUEUED, 'the warning does not fade');
  stopReply = { ok: true, remoteDeleted: true, queued: false };
  f.row.el('row-stop').click();
  f.row.el('row-confirm-stop').click();
  await flush();
  assert.equal(f.row.el('row-detail').textContent, STOPPED);
  assert.equal(f.row.el('row-url').hidden, true);
  await f.tick(6000);
  assert.equal(f.row.hidden, true, 'the calm result fades');
});

test('the quality choices carry the frame rate the recording is shared at', async () => {
  assert.equal(shareQualityLabel('1080p', 60), '1080p60');
  assert.equal(shareQualityLabel('720p', 30), '720p30');
  for (const unknown of [undefined, null, 0, 48, 'x']) assert.equal(shareQualityLabel('1080p', unknown), '1080p');
  const sixty = status({ recording: { available: true, builtIn: true, durationSeconds: 2255, tooLong: false, fps: 60 },
    estimates: { p1080Bytes: 1502096400, p720Bytes: 915692400 } });
  assert.deepEqual(vodSharePlan(sixty).labels, { p1080: '1080p60', p720: '720p60' });
  assert.equal(vodSharePlan(sixty).sizeText, 'About 1.5 GB at 1080p60.');
  assert.equal(vodSharePlan(sixty, '720p').sizeText, 'About 0.9 GB at 720p60.');

  const f = setup({ replies: { get_vod_share_status: sixty } });
  await f.share.check({ gameId: 123 });
  f.openButton.click();
  await flush();
  assert.equal(f.sheet.el('q1080-label').textContent, '1080p60');
  assert.equal(f.sheet.el('q720-label').textContent, '720p60');
  assert.equal(f.sheet.el('size').textContent, 'About 1.5 GB at 1080p60.');
});

test('a full share runs from the sheet: fresh status, start payload, progress, done and copy', async () => {
  const f = setup({ replies: {
    get_vod_share_status: status({ recording: { available: true, builtIn: false, durationSeconds: 2255 } }),
    start_vod_share: { ok: true, accepted: true, quality: '1080p', qualityReason: null },
    copy_text_to_clipboard: { ok: false },
  } });
  await f.share.check({ gameId: 123 });
  await flush();
  f.openButton.click();
  assert.equal(f.sheet.open, true);
  assert.equal(f.sheet.el('size').textContent, CHECKING, 'the sheet waits for a fresh pool');
  assert.equal(f.sheet.el('share').disabled, true);
  await flush();
  assert.equal(f.named('get_vod_share_status').length, 2, 'opening the sheet reads the status again');
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheet.el('size').textContent, 'About 1.3 GB at 1080p.');
  assert.equal(f.sheet.el('ttl').textContent, 'Anyone with the link can watch for 3 days.');
  assert.equal(f.sheet.el('imported').hidden, false, 'an imported recording warns about voice and desktop audio');
  assert.equal(f.sheet.el('notes').checked, false);
  assert.equal(f.sheet.el('share').disabled, false);
  assert.equal(f.doc.active, f.sheet.el('share'));
  f.sheet.el('q720').checked = true;
  f.sheet.el('q720').dispatch('change');
  assert.equal(f.sheet.el('size').textContent, 'About 0.8 GB at 720p.');
  f.sheet.el('notes').checked = true;
  f.sheet.el('notes').dispatch('change');
  f.sheet.el('share').click();
  await flush();
  assert.deepEqual(f.named('start_vod_share'), [['start_vod_share', { payload: { gameId: 123, quality: '720p', includeNotes: true } }]]);
  assert.equal(f.sheetView(), 'progress');
  assert.equal(f.sheet.el('progress-label').textContent, 'Preparing video 0%');
  assert.equal(f.sheet.el('progress-note').textContent, KEEP_REVIEWING);
  assert.equal(f.doc.active, f.sheet.el('cancel'));
  assert.equal(f.sheet.el('cancel').textContent, 'Cancel sharing');
  assert.equal(f.row.el('row-detail').textContent, 'Preparing video 0%');
  assert.equal(f.row.el('row-live').textContent, 'Preparing video');
  assert.equal(f.sheet.el('progress-live').textContent, 'Preparing video');

  f.emit({ gameId: 999, state: 'running', phase: 'uploading', percent: 90 });
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 34 });
  assert.equal(f.sheet.el('progress-label').textContent, 'Preparing video 34%');
  assert.equal(f.row.el('row-detail').textContent, 'Preparing video 34%');
  assert.equal(f.row.el('row-live').textContent, 'Preparing video', 'screen readers hear phases, not every percent');
  assert.equal(f.sheet.el('progress-bar').style.width, '34%');
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 61 });
  assert.equal(f.sheet.el('progress-label').textContent, 'Uploading 61%');
  assert.equal(f.sheet.el('progress-live').textContent, 'Uploading');
  assert.equal(f.row.el('row-live').textContent, 'Uploading');
  assert.equal(f.row.el('row-label').textContent, 'Sharing full game');
  f.emit({ gameId: 123, state: 'running', phase: 'finishing', percent: 100 });
  assert.equal(f.sheet.el('progress-label').textContent, 'Finishing');
  f.emit({ gameId: 123, state: 'done', phase: 'done', percent: 100, url: 'https://revu.lol/v/Abc1234',
    expiresAt: SHARE.expiresAt, markersAttached: false });
  assert.equal(f.sheetView(), 'done');
  assert.equal(f.sheet.el('url').value, 'https://revu.lol/v/Abc1234');
  assert.equal(flat(f.sheet.el('expiry').textContent), 'Link expires Saturday 12:00 PM.');
  assert.equal(f.sheet.el('markers').hidden, false, 'missing markers are explained');
  assert.equal(f.row.el('row-live').textContent, LINK_READY);
  assert.equal(f.sheet.el('progress-live').textContent, '');
  assert.equal(f.sheet.el('stop').hidden, false);
  assert.equal(f.sheet.el('done').hidden, false);
  assert.equal(f.sheet.el('share').hidden, true);
  assert.equal(f.doc.active, f.sheet.el('copy'));
  assert.equal(f.timers.intervals.filter(t => t.live).length, 0, 'the fallback poll stops with the job');
  f.sheet.el('copy').click();
  await flush();
  assert.equal(f.sheet.el('copy').textContent, "Couldn't copy");
  assert.equal(f.sheet.el('url').selected, true, 'a failed copy selects the link');
  await flush();
  assert.equal(f.sheetView(), 'done', 'the read right after done keeps the new link even if the store lags');
  f.sheet.el('done').click();
  assert.equal(f.sheet.open, false);
  await f.share.refresh();
  assert.equal(f.row.hidden, true, 'a later read without the link (stopped on the web) clears it');
});

test('a downgrade by the sidecar is explained, and the sheet keeps quality choices honest', async () => {
  const f = setup({ replies: {
    get_vod_share_status: status({ pool: { freeBytes: 900e6, fits1080: false, fits720: true, fits1080At: 1760403600 } }),
    start_vod_share: { ok: true, accepted: true, quality: '720p', qualityReason: 'pool' },
  } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  assert.equal(f.sheet.el('q1080').disabled, true);
  assert.equal(f.sheet.el('q720').checked, true);
  assert.equal(f.sheet.el('hint').textContent, POOL_720_HINT);
  assert.equal(f.sheet.el('hint').hidden, false);
  f.sheet.el('share').click();
  await flush();
  assert.equal(f.named('start_vod_share')[0][1].payload.quality, '720p');
  assert.equal(f.sheet.el('progress-note').textContent, `${POOL_720_HINT} ${KEEP_REVIEWING}`);
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 5, qualityReason: 'size' });
  assert.equal(f.sheet.el('progress-note').textContent, `${SIZE_720_NOTE} ${KEEP_REVIEWING}`);
});

test('refusals show their copy; not_allowed hides the action; sign-in and the recordings folder are offered', async () => {
  for (const code of ['live_limit', 'daily_limit', 'busy', 'network', 'disk_full', 'game_in_progress', 'too_large', 'mystery']) {
    const f = setup({ replies: { get_vod_share_status: status(),
      start_vod_share: { ok: false, errorCode: code, error: 'The sidecar said so.', retryAt: 1760486400 } } });
    await f.share.check({ gameId: 123 });
    await f.share.open();
    await f.share.start();
    const expected = vodShareErrorText(code, { retryAt: 1760486400, error: 'The sidecar said so.',
      time: unix => formatShareTime(unix, { now: NOW, locale: 'en-US', timeZone: 'UTC' }) });
    assert.equal(f.sheetMessage(), flat(expected), code);
    assert.equal(f.sheet.el('message-box').classList.contains('is-error'), true, code);
    assert.equal(f.sheetView(), 'start', code);
  }
  const denied = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: false, errorCode: 'not_allowed' } } });
  await denied.share.check({ gameId: 123 });
  await denied.share.open();
  await denied.share.start();
  assert.equal(denied.sheet.open, false);
  assert.equal(denied.openButton.hidden, true);
  assert.equal(denied.row.hidden, true);

  const login = setup({ replies: { get_vod_share_status: status(),
    start_vod_share: { ok: false, errorCode: 'needs_login', needsLogin: true } } });
  await login.share.check({ gameId: 123 });
  await login.share.open();
  await login.share.start();
  assert.equal(login.sheetMessage(), 'Sign in to share this game.');
  assert.equal(login.sheet.el('signin').hidden, false);

  const full = setup({ replies: { get_vod_share_status: status({ pool: { freeBytes: 0, fits1080: false, fits720: false,
    fits720At: NOW / 1000 + 3600 } }), open_recordings_folder: { ok: true } } });
  await full.share.check({ gameId: 123 });
  await full.share.open();
  assert.equal(full.sheetMessage(), 'Sharing space is full until Wednesday 1:00 PM. Try again then, or save the file instead.');
  assert.equal(full.sheet.el('share').disabled, true);
  assert.equal(full.sheet.el('folder').hidden, false, 'a built-in recording can be saved instead');
  full.sheet.el('folder').click();
  await flush();
  assert.deepEqual(full.named('open_recordings_folder'), [['open_recordings_folder', undefined]]);

  // The proxy refused a 1080p upload that 720p would still fit: here "Try 720p" is honest.
  const raced = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true } } });
  await raced.share.check({ gameId: 123 });
  await raced.share.open();
  await raced.share.start();
  raced.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'pool_full', retryAt: null, quality: '1080p' });
  assert.equal(raced.sheetMessage(), 'Sharing space is full right now. Try 720p, or save the file instead.');

  const paused = setup({ replies: { get_vod_share_status: status({ sharingOn: false }) } });
  await paused.share.check({ gameId: 123 });
  await paused.share.open();
  assert.equal(paused.sheetMessage(), 'Full-game sharing is paused right now. Try again later.');
  assert.equal(paused.sheet.el('share').disabled, true);
  assert.equal(paused.sheet.el('folder').hidden, true);
  await paused.share.start();
  assert.equal(paused.named('start_vod_share').length, 0);

  const long = setup({ replies: { get_vod_share_status: status({ recording: { available: true, builtIn: true, tooLong: true } }) } });
  await long.share.check({ gameId: 123 });
  await long.share.open();
  assert.equal(long.sheetMessage(), "Games longer than 2 hours can't be shared.");
});

test('a job error shows in the sheet, or in the row with Try again when the sheet is closed', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'encode_failed', error: 'ffmpeg failed' });
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), "Revu couldn't prepare the video. Try again.");
  assert.equal(f.doc.active, f.sheet.el('share'));

  await f.share.start();
  f.sheet.close();
  assert.equal(f.row.el('row-cancel').hidden, false, 'a running share stays visible above the player');
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'game_started' });
  assert.equal(f.row.hidden, false);
  assert.equal(f.row.el('row-detail').textContent, 'Sharing stopped because a game started. Share again after the game.');
  assert.equal(f.row.classList.contains('is-error'), true);
  f.row.el('row-retry').click();
  await flush();
  assert.equal(f.sheet.open, true);
  assert.equal(f.row.hidden, true);
  assert.equal(f.sheetMessage(), '', 'reopening starts clean');
});

test('cancel returns to the start state, and late events from the cancelled job are ignored', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true },
    cancel_vod_share: { ok: true, cancelled: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 40 });
  f.sheet.el('cancel').click();
  await flush();
  assert.deepEqual(f.named('cancel_vod_share'), [['cancel_vod_share', { payload: { gameId: 123 } }]]);
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), '');
  assert.equal(f.sheet.open, true);
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 41 });
  f.emit({ gameId: 123, state: 'cancelled', phase: 'cancelled' });
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), '');
  assert.equal(f.row.hidden, true);
  f.sheet.el('cancel').click();
  assert.equal(f.sheet.open, false, 'Cancel in the start state only closes the sheet');
  assert.equal(f.named('cancel_vod_share').length, 1);
});

test('the 15 s status poll covers missed events: progress, a vanished job, and done', async () => {
  const replies = [status({ job: { state: 'running', phase: 'uploading', percent: 50 } }),
    status({ job: null }), status({ job: { state: 'done', phase: 'done' }, share: SHARE })];
  let next = status({ job: { state: 'running', phase: 'preparing', percent: 10 } });
  const f = setup({ replies: { get_vod_share_status: () => next } });
  await f.share.check({ gameId: 123 });
  await flush();
  assert.equal(f.row.el('row-detail').textContent, 'Preparing video 10%', 'a share started on another visit is picked up');
  assert.equal(f.timers.intervals.filter(t => t.live && t.ms === 15000).length, 1);
  next = replies[0];
  await f.tick(15000);
  assert.equal(f.row.el('row-detail').textContent, 'Uploading 50%');
  next = replies[1];
  await f.tick(15000);
  assert.equal(f.row.el('row-detail').textContent, "The upload didn't finish. Check your connection and try again.");
  assert.equal(f.timers.intervals.filter(t => t.live).length, 0);

  const g = setup({ replies: { get_vod_share_status: () => next } });
  next = status({ job: { state: 'running', phase: 'uploading', percent: 90 } });
  await g.share.check({ gameId: 123 });
  next = replies[2];
  await g.tick(15000);
  assert.equal(g.row.el('row-url').textContent, 'https://revu.lol/v/Abc1234');
  assert.equal(g.row.classList.contains('is-error'), false);
});

test('stop from the sheet confirms first and says whether the link already stopped', async () => {
  let stopped = false;
  const f = setup({ replies: { get_vod_share_status: () => status({ share: stopped ? null : SHARE }),
    stop_vod_share: () => { stopped = true; return { ok: true, remoteDeleted: true, queued: false }; } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  assert.equal(f.sheetView(), 'done');
  f.sheet.el('stop').click();
  assert.equal(f.sheet.el('confirm').hidden, false);
  assert.equal(f.sheet.el('actions').hidden, true);
  assert.equal(f.doc.active, f.sheet.el('keep'));
  f.sheet.el('keep').click();
  assert.equal(f.sheet.el('confirm').hidden, true);
  f.sheet.el('stop').click();
  f.sheet.el('confirm-stop').click();
  await flush();
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), STOPPED);
  assert.equal(f.sheet.el('message-box').classList.contains('is-error'), false);
  assert.equal(f.row.hidden, true);
});

test('a stop that could not reach Revu keeps the link in the sheet and says it still works', async () => {
  const f = setup({ replies: { get_vod_share_status: () => status({ share: SHARE }),
    stop_vod_share: { ok: true, remoteDeleted: false, queued: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  f.sheet.el('stop').click();
  f.sheet.el('confirm-stop').click();
  await flush();
  assert.equal(f.sheetView(), 'done', 'the link and Stop sharing stay');
  assert.equal(f.sheetMessage(), STOPPED_QUEUED);
  assert.equal(f.sheet.el('message-box').classList.contains('is-error'), true);
  assert.equal(f.sheet.el('stop').hidden, false);
  assert.doesNotMatch(STOPPED_QUEUED, /Sharing stopped/);
  f.sheet.close();
  assert.equal(f.row.hidden, false);
  assert.equal(f.row.el('row-url').textContent, 'https://revu.lol/v/Abc1234');
});

test('"Include my notes and bookmarks" starts off for every new share; Try again keeps it', async () => {
  let shared = false;
  const f = setup({ replies: {
    get_vod_share_status: () => status({ share: shared ? SHARE : null }),
    start_vod_share: { ok: true, accepted: true, quality: '1080p' },
    stop_vod_share: () => { shared = false; return { ok: true, remoteDeleted: true, queued: false }; },
  } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  f.sheet.el('notes').checked = true;
  f.sheet.el('notes').dispatch('change');
  // Closing without sharing, then reopening: off again.
  f.sheet.close();
  await f.share.open();
  assert.equal(f.sheet.el('notes').checked, false);
  f.sheet.el('notes').checked = true;
  f.sheet.el('notes').dispatch('change');
  await f.share.start();
  assert.equal(f.named('start_vod_share')[0][1].payload.includeNotes, true);
  shared = true;
  f.emit({ gameId: 123, state: 'done', phase: 'done', url: SHARE.url, expiresAt: SHARE.expiresAt, markersAttached: true });
  await flush();
  f.sheet.el('stop').click();
  f.sheet.el('confirm-stop').click();
  await flush();
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheet.el('notes').checked, false, 'stopping and sharing again asks again');
  await f.share.start();
  assert.equal(f.named('start_vod_share')[1][1].payload.includeNotes, false);

  // A failed share retried from the row keeps the player's choice.
  f.sheet.el('notes').checked = true;
  f.sheet.el('notes').dispatch('change');
  f.sheet.close();
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'upload_failed' });
  f.row.el('row-retry').click();
  await flush();
  assert.equal(f.sheet.el('notes').checked, true);
});

test('a new share after a finished or stopped one starts at 1080p again; Try again keeps 720p', async () => {
  let shared = false;
  const f = setup({ replies: {
    get_vod_share_status: () => status({ share: shared ? SHARE : null }),
    start_vod_share: { ok: true, accepted: true, quality: '720p' },
    stop_vod_share: () => { shared = false; return { ok: true, remoteDeleted: true, queued: false }; },
  } });
  const choose720 = () => { f.sheet.el('q720').checked = true; f.sheet.el('q720').dispatch('change'); };
  await f.share.check({ gameId: 123 });
  await f.share.open();
  choose720();
  await f.share.start();
  assert.equal(f.named('start_vod_share')[0][1].payload.quality, '720p');
  shared = true;
  f.emit({ gameId: 123, state: 'done', phase: 'done', url: SHARE.url, expiresAt: SHARE.expiresAt, markersAttached: true });
  await flush();
  f.sheet.el('stop').click();
  f.sheet.el('confirm-stop').click();
  await flush();
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheet.el('q1080').checked, true, 'stopping and sharing again starts from the default');
  assert.equal(f.sheet.el('size').textContent, 'About 1.3 GB at 1080p.');
  await f.share.start();
  assert.equal(f.named('start_vod_share')[1][1].payload.quality, '1080p');

  // A failed share retried from the row keeps the player's choice.
  choose720();
  f.sheet.close();
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'upload_failed' });
  f.row.el('row-retry').click();
  await flush();
  assert.equal(f.sheet.el('q720').checked, true);
});

test('the progress bar starts each phase over instead of sliding backwards', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true, quality: '1080p' } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  const bar = f.sheet.el('progress-bar');
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 99 });
  assert.equal(bar.style.width, '99%');
  assert.equal(bar.style.transition, '');
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 0 });
  assert.equal(bar.style.width, '0%');
  assert.equal(bar.style.transition, 'none', 'uploading starts at 0 without animating down from 99%');
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 33 });
  assert.equal(bar.style.width, '33%');
  assert.equal(bar.style.transition, '', 'progress within a phase still animates');
  f.emit({ gameId: 123, state: 'running', phase: 'finishing', percent: 100 });
  assert.equal(bar.style.width, '100%');
  assert.equal(bar.style.transition, '');
});

test('a running share shows its link as soon as the upload starts, in the sheet and the row', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true, quality: '1080p' },
    copy_text_to_clipboard: { ok: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  assert.equal(f.sheet.el('progress-link').hidden, true, 'no link before the upload starts');
  assert.equal(f.sheet.el('progress-link-note').hidden, true);
  assert.equal(f.row.el('row-url').hidden, true);
  assert.equal(f.row.el('row-copy').hidden, true);

  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 3, url: SHARE.url, expiresAt: SHARE.expiresAt,
    linkChanged: false });
  assert.equal(f.sheetView(), 'progress');
  assert.equal(f.sheet.el('progress-label').textContent, 'Preparing video 3%');
  assert.equal(f.sheet.el('progress-link').hidden, false);
  assert.equal(f.sheet.el('progress-url').value, SHARE.url);
  assert.equal(f.sheet.el('progress-open').hidden, false);
  assert.equal(f.sheet.el('progress-link-note').textContent, LINK_EARLY);
  assert.equal(f.sheet.el('progress-link-note').hidden, false);
  assert.equal(f.sheet.el('progress-note').textContent, KEEP_REVIEWING);
  assert.equal(f.sheet.el('progress-live').textContent, `Preparing video. ${LINK_LIVE}`, 'the link is announced once');
  assert.equal(f.row.el('row-label').textContent, 'Sharing full game');
  assert.equal(f.row.el('row-url').textContent, SHARE.url);
  assert.equal(f.row.el('row-detail').textContent, 'Preparing video 3%');
  assert.equal(f.row.el('row-live').textContent, `Preparing video. ${LINK_LIVE}`);
  assert.deepEqual(['row-copy', 'row-open', 'row-cancel', 'row-stop'].map(name => f.row.el(name).hidden), [false, false, false, true],
    'a running share is cancelled, not stopped');

  f.sheet.el('progress-copy').click();
  await flush();
  assert.deepEqual(f.named('copy_text_to_clipboard'), [['copy_text_to_clipboard', { text: SHARE.url }]]);
  assert.equal(f.sheet.el('progress-copy').textContent, 'Copied');
  f.row.el('row-copy').click();
  f.sheet.el('progress-open').click();
  await flush();
  assert.equal(f.named('copy_text_to_clipboard').length, 2);
  assert.deepEqual(f.opened, [SHARE.url]);

  // A later event keeps the link; the finished share has the same one.
  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 80, url: SHARE.url, expiresAt: SHARE.expiresAt });
  assert.equal(f.sheet.el('progress-live').textContent, `Uploading. ${LINK_LIVE}`);
  assert.equal(f.sheet.el('progress-url').value, SHARE.url);
  f.emit({ gameId: 123, state: 'done', phase: 'done', percent: 100, url: SHARE.url, expiresAt: SHARE.expiresAt,
    markersAttached: true, linkChanged: false });
  assert.equal(f.sheetView(), 'done');
  assert.equal(f.sheet.el('url').value, SHARE.url);
  assert.equal(f.sheetMessage(), '');
});

test('a link picked up by the status poll shows too, and only revu.lol watch links are ever shown', async () => {
  let next = status({ job: { state: 'running', phase: 'preparing', percent: 20, url: SHARE.url, expiresAt: SHARE.expiresAt,
    linkChanged: false } });
  const f = setup({ replies: { get_vod_share_status: () => next } });
  await f.share.check({ gameId: 123 });
  await flush();
  assert.equal(f.row.el('row-url').textContent, SHARE.url, 'a share started on another visit shows its link');
  assert.equal(f.row.el('row-copy').hidden, false);
  next = status({ job: { state: 'running', phase: 'preparing', percent: 25, url: 'https://evil.example/v/Abc1234' } });
  await f.tick(15000);
  assert.equal(f.row.el('row-url').hidden, true);
  assert.equal(f.row.el('row-copy').hidden, true);
  assert.equal(f.row.el('row-detail').textContent, 'Preparing video 25%');
});

test('an upload that starts over takes its link down and says the new link changed', async () => {
  const B = 'https://revu.lol/v/Xyz9876';
  const f = setup({ replies: { get_vod_share_status: () => status({ share: null }), start_vod_share: { ok: true, accepted: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 30, url: SHARE.url, expiresAt: SHARE.expiresAt });
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 30, url: null, expiresAt: null, linkChanged: true });
  assert.equal(f.sheet.el('progress-link').hidden, true);
  assert.equal(f.sheet.el('progress-link-note').textContent, LINK_REPLACING);
  assert.equal(f.row.el('row-url').hidden, true);
  assert.equal(f.row.el('row-copy').hidden, true);
  assert.equal(f.row.el('row-detail').textContent, `Preparing video 30%. ${LINK_CHANGED_SHORT}`);

  f.emit({ gameId: 123, state: 'running', phase: 'uploading', percent: 0, url: B, expiresAt: SHARE.expiresAt, linkChanged: true });
  assert.equal(f.sheet.el('progress-url').value, B);
  assert.equal(f.sheet.el('progress-link-note').textContent, LINK_CHANGED);
  assert.equal(f.row.el('row-url').textContent, B);
  assert.equal(f.row.el('row-detail').textContent, `Uploading 0%. ${LINK_CHANGED_SHORT}`);

  f.emit({ gameId: 123, state: 'done', phase: 'done', url: B, expiresAt: SHARE.expiresAt, markersAttached: true, linkChanged: true });
  assert.equal(f.sheetView(), 'done');
  assert.equal(f.sheet.el('url').value, B);
  assert.equal(f.sheetMessage(), LINK_CHANGED, 'a link copied before it changed no longer works');
  assert.equal(f.sheet.el('message-box').classList.contains('is-error'), false);
});

test('a failed or cancelled share whose link was shown says the link stopped working', async () => {
  assert.equal(vodShareErrorText('upload_failed', { linkShown: true }),
    `The upload didn't finish. Check your connection and try again. ${LINK_GONE}`);
  assert.equal(vodShareErrorText('upload_failed', { linkShown: false }), "The upload didn't finish. Check your connection and try again.");
  assert.equal(vodShareErrorText('cancelled', { linkShown: true }), '');

  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true },
    cancel_vod_share: { ok: true, cancelled: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 40, url: SHARE.url, expiresAt: SHARE.expiresAt });
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'game_started', url: null });
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), `Sharing stopped because a game started. Share again after the game. ${LINK_GONE}`);

  // A link taken down while the upload started over is just as gone when the share then fails.
  await f.share.start();
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 40, url: SHARE.url, expiresAt: SHARE.expiresAt });
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 40, url: null, linkChanged: true });
  f.emit({ gameId: 123, state: 'error', phase: 'error', errorCode: 'upload_failed', url: null, linkChanged: true });
  assert.equal(f.sheetMessage(), `The upload didn't finish. Check your connection and try again. ${LINK_GONE}`);

  // Cancelled from the row with the sheet closed: a calm note that fades.
  await f.share.start();
  f.sheet.close();
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 10, url: SHARE.url, expiresAt: SHARE.expiresAt });
  f.row.el('row-cancel').click();
  await flush();
  assert.equal(f.row.hidden, false);
  assert.equal(f.row.el('row-detail').textContent, STOPPED);
  assert.equal(f.row.el('row-url').hidden, true);
  assert.equal(f.row.classList.contains('is-error'), false);
  await f.tick(6000);
  assert.equal(f.row.hidden, true);

  // Cancelled from the sheet: the start view says so.
  await f.share.open();
  await f.share.start();
  f.emit({ gameId: 123, state: 'running', phase: 'preparing', percent: 10, url: SHARE.url, expiresAt: SHARE.expiresAt });
  f.sheet.el('cancel').click();
  await flush();
  assert.equal(f.sheetView(), 'start');
  assert.equal(f.sheetMessage(), STOPPED);
});

test('a share made before access was removed still opens its sheet with Stop sharing', async () => {
  const f = setup({ replies: {
    get_vod_share_status: ({ gameId }) => status({ gameId, allowed: gameId === 124 ? false : true, share: gameId === 123 ? SHARE : null }),
    start_vod_share: { ok: false, errorCode: 'not_allowed' },
  } });
  await f.share.check({ gameId: 124 });
  await f.share.open();
  await f.share.start();
  assert.equal(f.share.state.denied, true);
  await f.share.check({ gameId: 123 });
  await flush();
  assert.equal(f.openButton.hidden, false);
  f.openButton.click();
  await flush();
  assert.equal(f.sheet.open, true);
  assert.equal(f.sheetView(), 'done');
  assert.equal(f.sheet.el('stop').hidden, false);
});

test('a new game resets everything, and keys typed in the sheet never reach the player', async () => {
  const f = setup({ replies: { get_vod_share_status: ({ gameId }) => status({ gameId, share: gameId === 123 ? SHARE : null }) } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  assert.equal(f.sheet.dispatch('keydown', { key: ' ' }).stopped, true);
  await f.share.check({ gameId: 124 });
  await flush();
  assert.equal(f.sheet.open, false);
  assert.equal(f.row.hidden, true);
  assert.equal(f.share.state.gameId, 124);
  const reads = f.named('get_vod_share_status').length;
  await f.share.check({ gameId: 124 });
  assert.equal(f.named('get_vod_share_status').length, reads, 'a re-render of the same game reuses a fresh status');
});

test('the VOD page carries every hook, the agreed copy, and only minimal wiring', async () => {
  const [html, js, module] = await Promise.all([read('vodplayer.html'), read('vodplayer.js'), read('vod-share.mjs')]);
  const visible = html.replace(/<!--[\s\S]*?-->/g, '');
  const hooks = new Set([...module.matchAll(/hook\((?:row|sheet), '([a-z0-9-]+)'\)/g), ...module.matchAll(/on\((?:row|sheet), '([a-z0-9-]+)'/g)]
    .map(match => match[1]));
  for (const name of ['q1080', 'q720', 'notes']) hooks.add(name);
  assert.ok(hooks.size >= 35, `found ${hooks.size} hooks`);
  for (const name of hooks) assert.match(html, new RegExp(`data-vs="${name}"`), name);
  for (const view of ['start', 'progress', 'done']) assert.match(html, new RegExp(`data-vs-view="${view}"`));
  for (const id of ['vp-vodshare-open', 'vp-vodshare', 'vp-vodshare-sheet']) assert.match(html, new RegExp(`id="${id}"`));
  assert.match(html, /<dialog id="vp-vodshare-sheet" class="vp-vs-sheet" aria-labelledby="vp-vs-title">/);
  assert.match(html, /<button class="ui-btn ui-btn-quiet vp-vs-open" id="vp-vodshare-open"[^>]*hidden>/);
  for (const text of ['Share full game', 'Anyone with the link can watch for 3 days.',
    "The video shows other players' names and in-game chat.", 'Include my notes and bookmarks',
    'Imported recordings may include voice or desktop audio.', 'Link ready',
    "The timeline markers couldn't be added. The video still plays.",
    'Stop sharing this game? The link will stop working.', 'Keep sharing', 'Open recordings folder']) {
    assert.ok(visible.includes(text), text);
  }
  assert.match(html, /<input type="checkbox" data-vs="notes">/, 'notes are off by default');
  assert.match(html, /<link rel="stylesheet" href=".\/vod-share.css">\r?\n<link rel="stylesheet" href=".\/appearance.css/);
  assert.equal(visible.includes('—'), false);
  assert.match(js, /^import \{ createVodShare \} from '\.\/vod-share\.mjs';$/m);
  assert.equal((js.match(/_vodShare\?\.check\(core \? data : null\)/g) || []).length, 2);
  assert.doesNotMatch(module, /filePath|vodPath/, 'the page never names a file');
});

// ── 4.4: a game shared as segments plays at its link while it uploads ──

const HLS = { gameId: 123, state: 'running', format: 'hls', url: SHARE.url, expiresAt: SHARE.expiresAt, linkChanged: false };

test('the segmented share notes follow its state, most urgent first, with no dashes', () => {
  const job = over => ({ ...HLS, phase: 'uploading', percent: 12, watchable: false, uplinkSlow: false, paused: false, ...over });
  assert.equal(vodShareLiveNote(job()), LINK_WATCH_SOON);
  assert.equal(vodShareLiveNote(job({ url: '' })), '');
  assert.equal(vodShareLiveNote(job({ watchable: true })), LINK_WATCH_NOW);
  assert.equal(vodShareLiveNote(job({ watchable: true, uplinkSlow: true })), UPLINK_SLOW);
  assert.equal(vodShareLiveNote(job({ watchable: true, qualityReason: 'uplink' })), SWITCHED_720);
  assert.equal(vodShareLiveNote(job({ watchable: true, uplinkSlow: true, paused: true })), PAUSED_GAME);
  assert.equal(vodShareLiveNote(job({ format: 'mp4', watchable: true })), '');
  assert.equal(vodShareProgressLabel(job({ phase: 'queued', percent: 0 })), 'Uploading 0%');
  assert.equal(vodShareProgressLabel(job({ phase: 'preparing', percent: 7 })), 'Uploading 7%');
  assert.equal(vodSharePhaseLabel(job()), 'Uploading');
  assert.equal(vodShareErrorText('upload_failed', { linkShown: true, kept: true }),
    `The upload didn't finish. Check your connection and try again. ${ENDED_EARLY}`);
  for (const text of [LINK_WATCH_SOON, LINK_WATCH_NOW, UPLINK_SLOW, SWITCHED_720, PAUSED_GAME, CONFIRM_STOP_LIVE, CONFIRM_STOP,
    ENDED_EARLY, UPLINK_720_HINT]) {
    assert.doesNotMatch(text, /[–—]/, text);
    assert.match(text, /[.?]$/, text);
  }
});

test('a segmented share shows one Uploading phase and says when people can watch, pause or see pauses', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true, quality: '1080p' } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 0, watchable: false });
  assert.equal(f.sheet.el('progress-label').textContent, 'Uploading 0%');
  assert.equal(f.sheet.el('progress-link-note').textContent, LINK_WATCH_SOON);
  f.emit({ ...HLS, phase: 'uploading', percent: 3, watchable: true });
  assert.equal(f.sheet.el('progress-link-note').textContent, LINK_WATCH_NOW);
  f.emit({ ...HLS, phase: 'uploading', percent: 4, watchable: true, uplinkSlow: true });
  assert.equal(f.sheet.el('progress-link-note').textContent, UPLINK_SLOW);
  f.emit({ ...HLS, phase: 'uploading', percent: 5, watchable: true, paused: true });
  assert.equal(f.sheet.el('progress-link-note').textContent, PAUSED_GAME);
  assert.equal(f.row.el('row-detail').textContent, `Uploading 5%. ${PAUSED_GAME}`);
  f.emit({ ...HLS, phase: 'uploading', percent: 6, watchable: true, paused: false, uplinkSlow: false, quality: '720p',
    qualityReason: 'uplink' });
  assert.equal(f.sheet.el('progress-link-note').textContent, SWITCHED_720);
  assert.equal(f.row.el('row-detail').textContent, 'Uploading 6%');
});

test('Cancel asks first once people can watch a segmented share, in the sheet and in the row', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true, quality: '1080p' },
    cancel_vod_share: { ok: true } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 2, watchable: false });
  f.sheet.el('cancel').click();
  await flush();
  assert.equal(f.named('cancel_vod_share').length, 1, 'nobody could watch yet: no question');

  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 20, watchable: true });
  f.sheet.el('cancel').click();
  await flush();
  assert.equal(f.named('cancel_vod_share').length, 1);
  assert.equal(f.sheet.el('confirm').hidden, false);
  assert.equal(f.sheet.el('confirm-text').textContent, CONFIRM_STOP_LIVE);
  assert.equal(f.sheet.el('actions').hidden, true);
  f.sheet.el('keep').click();
  assert.equal(f.sheet.el('confirm').hidden, true);
  assert.equal(f.doc.active, f.sheet.el('cancel'));
  f.sheet.el('cancel').click();
  f.sheet.el('confirm-stop').click();
  await flush();
  assert.equal(f.named('cancel_vod_share').length, 2);
  assert.equal(f.sheet.el('confirm').hidden, true);

  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 30, watchable: true });
  f.sheet.close();
  f.row.el('row-cancel').click();
  assert.equal(f.row.el('row-confirm').hidden, false);
  assert.equal(f.row.el('row-confirm-text').textContent, CONFIRM_STOP_LIVE);
  f.row.el('row-confirm-stop').click();
  await flush();
  assert.equal(f.named('cancel_vod_share').length, 3);
});

test('a slow last upload preselects 720p with its reason until the player picks a quality', async () => {
  const slow = status({ preferredQuality: '720p' });
  assert.equal(vodSharePlan(slow, 'auto').quality, '720p');
  assert.equal(vodSharePlan(slow, 'auto').hint, UPLINK_720_HINT);
  assert.equal(vodSharePlan(slow, '1080p').quality, '1080p');
  assert.equal(vodSharePlan(slow, '1080p').hint, '');
  assert.equal(vodSharePlan(status(), 'auto').quality, '1080p');

  const f = setup({ replies: { get_vod_share_status: slow, start_vod_share: { ok: true, accepted: true, quality: '1080p' } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  assert.equal(f.sheet.el('q720').checked, true);
  assert.equal(f.sheet.el('hint').textContent, UPLINK_720_HINT);
  f.sheet.el('q1080').checked = true;
  f.sheet.el('q1080').dispatch('change');
  assert.equal(f.sheet.el('hint').hidden, true);
  await f.share.start();
  assert.equal(f.named('start_vod_share')[0][1].payload.quality, '1080p');
});

test('a segmented share people could watch keeps its link when it ends early', async () => {
  const f = setup({ replies: { get_vod_share_status: status(), start_vod_share: { ok: true, accepted: true, quality: '1080p' } } });
  await f.share.check({ gameId: 123 });
  await f.share.open();
  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 40, watchable: true });
  f.emit({ ...HLS, state: 'error', phase: 'error', errorCode: 'upload_failed', watchable: true });
  assert.equal(f.sheetMessage(), `The upload didn't finish. Check your connection and try again. ${ENDED_EARLY}`);

  await f.share.start();
  f.emit({ ...HLS, phase: 'uploading', percent: 50, watchable: true });
  f.emit({ ...HLS, state: 'done', phase: 'done', percent: 100, truncated: true, markersAttached: true });
  assert.equal(f.sheetView(), 'done');
  assert.equal(f.sheetMessage(), ENDED_EARLY);
});

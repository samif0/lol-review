// "Speed up slow recordings" in Settings > Recording. A built-in recording saved before Revu
// indexed recordings, or interrupted before it finished saving, opens slowly. The host lists
// them by game and rewrites one file at a time in place; the page only ever names a game.
export const SPEED_UP_WORKING = 'Speeding up this recording. This can take a minute; keep Revu open.';
export const SPEED_UP_DONE = 'This recording now opens instantly.';
export const SPEED_UP_LOADING = 'Checking your recordings…';
export const SPEED_UP_EMPTY = 'Every recording opens quickly. Nothing to speed up.';
export const SPEED_UP_UNCHECKED = 'Your recordings could not be checked right now. Reopen Settings to try again.';
// The VOD page cannot stream a file while the host rewrites it.
export const SPEED_UP_HELD = 'This recording is being sped up in Settings. Open it again when that finishes.';
const KEPT = 'The original recording was kept.';
const WAITING = {
  'game-running': 'You can speed it up after your League game ends.',
  'recording-active': 'You can speed it up after the current recording finishes.',
  busy: 'Another recording is being sped up. Try again when it finishes.',
  unavailable: 'Speeding up is unavailable right now.',
};
// The same lines above a list of several recordings.
const WAITING_MANY = {
  'game-running': 'You can speed them up after your League game ends.',
  'recording-active': 'You can speed them up after the current recording finishes.',
};
const FAILED = {
  'recording-in-use': 'The recording was in use, for example by a clip being saved. Try again in a moment.',
  'game-running': 'A League game started, so Revu stopped speeding up this recording.',
  'recording-active': 'A recording started, so Revu stopped speeding up this recording.',
  'not-enough-space': 'There is not enough free space on this drive.',
  'verify-failed': 'The faster copy did not match the original exactly, so Revu discarded it.',
  'remux-failed': 'Revu could not rewrite this recording.',
  'not-eligible': 'This recording cannot be sped up.',
  busy: 'Another recording is being sped up. Try again when it finishes.',
};
// 'stopped': a speed-up started on an earlier visit ended without rewriting the file; its cause is unknown here.
const RETRY = new Set(['recording-in-use', 'game-running', 'recording-active', 'busy', 'not-enough-space', 'remux-failed', 'stopped']);
// A row explains only what is about that file. A game, a recording or an unreadable drive holds
// every row at once, so the card says it once.
const ROW_REASONS = new Set(['not-enough-space']);

export function speedUpWaitingText(status) {
  if (status?.reason === 'not-enough-space') {
    const gigabytes = Math.ceil(Number(status.requiredBytes) / 1024 ** 3);
    return Number.isFinite(gigabytes) && gigabytes > 0
      ? `It needs about ${gigabytes} GB of free space on this drive.` : 'It needs more free space on this drive.';
  }
  return WAITING[status?.reason] || WAITING.unavailable;
}
export function speedUpFailureText(code) { return `${FAILED[code] || 'Speeding up did not finish.'} ${KEPT}`; }

/** "Oct 13, 21:40 · Ahri vs Zed · 32 min · 1.4 GB"; parts the snapshot lacks are left out. */
export function speedUpRowLabel(item) {
  const champion = String(item?.championName || '').trim(), enemy = String(item?.enemyChampion || '').trim();
  const minutes = Math.round(Number(item?.durationSeconds) / 60), bytes = Number(item?.fileSize);
  const size = !(bytes > 0) ? '' : bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1024 ** 2))} MB`;
  return [String(item?.datePlayed || '').trim(), champion && enemy ? `${champion} vs ${enemy}` : champion || enemy || 'Match',
    minutes > 0 ? `${minutes} min` : '', size].filter(Boolean).join(' · ');
}

/** The VOD page: true only when a running speed-up holds this game's file. */
export async function speedUpHoldsRecording(core, gameId) {
  if (typeof core?.invoke !== 'function' || !Number.isSafeInteger(gameId) || gameId <= 0) return false;
  try { return (await core.invoke('get_recording_optimization', { gameId }))?.reason === 'working'; } catch (_) { return false; }
}

const validItem = item => Number.isSafeInteger(item?.gameId) && item.gameId > 0;

/** card shows only where built-in recording works; status is its live line; list holds one row per
 * recording. Rows are keyed by game, so a re-check never re-announces or steals focus. While a
 * block, a job or a full drive is showing, the host is asked again every pollMs the page is visible. */
export function createRecordingSpeedUp({ card, status, list, invoke, document, onAvailability = () => {}, pollMs = 10_000,
  setTimer = (callback, ms) => setTimeout(callback, ms), clearTimer = id => clearTimeout(id) }) {
  const rows = new Map(), labels = new Map(), results = new Map();
  let last = null, running = null, seenWorking = null, order = [], generation = 0, pending = 0, timer = null, disposed = false;
  const setVisible = on => {
    const value = on ? 'false' : 'true';
    if (card.dataset.unavailable === value) return;
    card.dataset.unavailable = value;
    onAvailability(on);
  };
  // Writing the same words again would make a screen reader repeat them.
  const say = text => { if (status.textContent !== text) status.textContent = text; };
  const busy = () => running !== null || last?.working != null;
  const following = () => running !== null || (last?.available === true
    && (last.working != null || !!last.blocked || last.items.some(item => !item.ready)));
  function schedule() {
    if (timer !== null) clearTimer(timer);
    timer = null;
    if (disposed || !following()) return;
    timer = setTimer(() => { timer = null; if (!document.hidden) void refresh(); }, pollMs);
  }

  function row(id) {
    let entry = rows.get(id);
    if (entry) return entry;
    const make = (tag, className) => { const node = document.createElement(tag); node.className = className; return node; };
    const li = make('li', 'set-row settings-speedup-row'), text = make('div', 'set-row-txt');
    const label = make('div', 'set-row-h'), note = make('div', 'set-status settings-speedup-note'), button = make('button', 'rule-act');
    li.tabIndex = -1; li.dataset.gameId = String(id);
    label.id = `speedup-label-${id}`; note.id = `speedup-note-${id}`; note.setAttribute('role', 'status');
    // Named "Speed up Oct 13, 21:40 · Ahri vs Zed · …", so a list of buttons tells the rows apart.
    button.type = 'button'; button.id = `speedup-action-${id}`;
    button.setAttribute('aria-labelledby', `${button.id} ${label.id}`); button.setAttribute('aria-describedby', note.id);
    button.addEventListener('click', () => { void act(id); });
    text.appendChild(label); text.appendChild(note); li.appendChild(text); li.appendChild(button);
    entry = { li, label, note, button };
    rows.set(id, entry);
    return entry;
  }
  function paintRow(id, index, item) {
    const entry = row(id), result = results.get(id), held = busy() || !!last?.blocked;
    let note = '', action = '', disabled = false, bad = false;
    if (id === running || result?.state === 'checking' || (running === null && item?.reason === 'working')) note = SPEED_UP_WORKING;
    else if (result?.state === 'done') note = SPEED_UP_DONE;
    else if (result?.state === 'failed') {
      note = speedUpFailureText(result.code); bad = true;
      if (item && RETRY.has(result.code)) { action = 'Try again'; disabled = held; }
    } else if (item) {
      action = 'Speed up'; disabled = held || !item.ready;
      if (!item.ready && ROW_REASONS.has(item.reason)) note = speedUpWaitingText(item);
    }
    // The button can vanish under the keyboard; keep focus on its row instead of the page.
    if (document.activeElement === entry.button && (!action || disabled)) entry.li.focus({ preventScroll: true });
    if (entry.label.textContent !== labels.get(id)) entry.label.textContent = labels.get(id) || '';
    if (entry.note.textContent !== note) entry.note.textContent = note;
    entry.note.classList.toggle('bad', bad);
    entry.button.hidden = !action;
    if (action && entry.button.textContent !== action) entry.button.textContent = action;
    entry.button.disabled = disabled;
    // Move a row only when it is out of place, so a focused row keeps its focus.
    if (list.children[index] !== entry.li) list.insertBefore(entry.li, list.children[index] ?? null);
  }
  function render() {
    if (!last?.available) return;
    const ids = last.items.map(item => item.gameId);
    // A finished or failed row stays where it was until the page closes.
    for (const id of results.keys()) {
      if (!ids.includes(id) && labels.has(id)) ids.splice(Math.min(Math.max(order.indexOf(id), 0), ids.length), 0, id);
    }
    ids.forEach((id, index) => paintRow(id, index, last.items.find(item => item.gameId === id)));
    for (const [id, entry] of rows) if (!ids.includes(id)) { entry.li.remove(); rows.delete(id); }
    order = ids;
    list.hidden = ids.length === 0;
    const blocked = last.blocked ?? (last.items.some(item => item.reason === 'unavailable') ? 'unavailable' : null);
    say(!ids.length ? SPEED_UP_EMPTY : blocked ? (last.items.length > 1 && WAITING_MANY[blocked]) || speedUpWaitingText({ reason: blocked })
      : last.working != null && !ids.includes(last.working) ? WAITING.busy : '');
  }
  // A speed-up this page did not start has ended and its game left the list. A game whose check
  // failed is left out of an answer too, so the host confirms the file now opens instantly first.
  async function confirm(id) {
    results.set(id, { state: 'checking' });
    let status = null;
    try { status = await invoke('get_recording_optimization', { gameId: id }); } catch (_) { status = null; }
    if (disposed || results.get(id)?.state !== 'checking') return;
    if (status?.reason === 'already-optimized') results.set(id, { state: 'done' });
    else if (status?.eligible === true && status.reason !== 'working') results.set(id, { state: 'failed', code: 'stopped' });
    else results.delete(id);
    render();
    if (status?.eligible === true) void refresh(); // bring back its row and Try again
  }
  function apply(answer) {
    if (!answer || typeof answer !== 'object') {
      if (!last) { say(SPEED_UP_UNCHECKED); list.hidden = true; }
      return; // a missed re-check keeps the rows already on screen
    }
    if (answer.available !== true) { last = null; setVisible(false); return; }
    const items = (Array.isArray(answer.items) ? answer.items : []).filter(validItem);
    const working = Number.isSafeInteger(answer.working) ? answer.working : null;
    // A listed recording still opens slowly, whatever an earlier answer suggested.
    for (const item of items) if (results.get(item.gameId)?.state === 'done') results.delete(item.gameId);
    // Work this page did not start has ended. Still listed, it did not finish and the original was kept.
    if (seenWorking !== null && seenWorking !== running && working !== seenWorking && !results.has(seenWorking)
        && labels.has(seenWorking)) {
      if (items.some(item => item.gameId === seenWorking)) results.set(seenWorking, { state: 'failed', code: 'stopped' });
      else void confirm(seenWorking);
    }
    seenWorking = working;
    for (const item of items) labels.set(item.gameId, speedUpRowLabel(item));
    last = { available: true, blocked: typeof answer.blocked === 'string' ? answer.blocked : null, working, items };
    setVisible(true);
    render();
  }
  async function refresh() {
    if (disposed) return;
    const current = ++generation;
    let answer = null;
    pending++;
    try { answer = await invoke('list_recording_optimizations', {}); } catch (_) { answer = null; } finally { pending--; }
    if (disposed || current !== generation) return;
    apply(answer);
    schedule();
  }

  async function act(id) {
    if (disposed || busy()) return;
    if (results.get(id)?.state === 'failed') {
      // Try again asks the host first: the reason it stopped may still apply.
      results.delete(id);
      render();
      await refresh();
    }
    const item = last?.items.find(value => value.gameId === id);
    if (disposed || busy() || !item?.ready || last.blocked) return;
    running = id;
    render(); schedule();
    let result = null;
    try { result = await invoke('optimize_recording', { gameId: id }); } catch (_) { result = null; }
    running = null;
    if (disposed) return;
    results.set(id, result?.ok ? { state: 'done' } : { state: 'failed', code: result?.code });
    render();
    await refresh();
  }

  async function start() {
    let recording = null;
    try { recording = await invoke('get_recording_status'); } catch (_) { recording = null; }
    if (disposed) return;
    if (recording?.available !== true) { setVisible(false); return; }
    say(SPEED_UP_LOADING); list.hidden = true;
    setVisible(true);
    await refresh();
  }
  function visible() { if (!disposed && !document.hidden && timer === null && pending === 0 && following()) void refresh(); }
  function dispose() { disposed = true; ++generation; if (timer !== null) clearTimer(timer); timer = null; }
  return { start, refresh, visible, dispose };
}

/** Settings boot: hidden in a browser preview, an isolated preview, or wherever recording is unavailable. */
export async function initializeRecordingSpeedUp({ document = globalThis.document, scope = globalThis.window, getInvoke,
  onAvailability } = {}) {
  const card = document.getElementById('speedup-settings'), status = document.getElementById('speedup-status');
  const list = document.getElementById('speedup-list');
  if (!card || !status || !list) return null;
  let invoke = null;
  try { invoke = await getInvoke?.(); } catch (_) { invoke = null; }
  if (!invoke) return null;
  const speedUp = createRecordingSpeedUp({ card, status, list, invoke, document, onAvailability,
    setTimer: (callback, ms) => scope.setTimeout(callback, ms), clearTimer: id => scope.clearTimeout(id) });
  const onVisibility = () => speedUp.visible();
  document.addEventListener('visibilitychange', onVisibility);
  scope.addEventListener('pagehide', () => { speedUp.dispose(); document.removeEventListener('visibilitychange', onVisibility); },
    { once: true });
  await speedUp.start();
  return speedUp;
}

// "Speed up" for a built-in recording saved before Revu indexed recordings: one quiet row
// above the player. The host decides eligibility from the game's own snapshot and rewrites
// the file in place; the page lets go of the video first and reloads it afterwards.
export const SPEED_UP_OFFER = 'This recording opens slowly because it was saved before Revu indexed recordings. '
  + 'Speed it up once to make it open instantly.';
export const SPEED_UP_WORKING = 'Speeding up this recording. This can take a minute; keep Revu open.';
export const SPEED_UP_DONE = 'This recording now opens instantly.';
const KEPT = 'The original recording was kept.';
const WAITING = {
  'game-running': 'You can speed it up after your League game ends.',
  'recording-active': 'You can speed it up after the current recording finishes.',
  busy: 'Another recording is being sped up. Try again when it finishes.',
  unavailable: 'Speeding up is unavailable right now.',
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
const RETRY = new Set(['recording-in-use', 'game-running', 'recording-active', 'busy', 'not-enough-space', 'remux-failed']);

export function speedUpWaitingText(status) {
  if (status?.reason === 'not-enough-space') {
    const gigabytes = Math.ceil(Number(status.requiredBytes) / 1024 ** 3);
    return Number.isFinite(gigabytes) && gigabytes > 0
      ? `It needs about ${gigabytes} GB of free space on this drive.` : 'It needs more free space on this drive.';
  }
  return WAITING[status?.reason] || WAITING.unavailable;
}
export function speedUpFailureText(code) { return `${FAILED[code] || 'Speeding up did not finish.'} ${KEPT}`; }

/** root holds [data-speedup-text] and [data-speedup-action]. release() unloads the page's video
 * and returns its view state; reload(state) fetches the snapshot again and restores that view.
 * While the row waits, or shows work another page started, it asks the host again every pollMs. */
export function createRecordingSpeedUp({ root, invoke, release, reload, pollMs = 5000 }) {
  const text = root?.querySelector('[data-speedup-text]'), button = root?.querySelector('[data-speedup-action]');
  let state = 'hidden', gameId = 0, generation = 0, last = null, running = false, timer = null;
  const paint = (next, message = '', { action = '', disabled = false, error = false } = {}) => {
    const hadFocus = !!button && root.ownerDocument?.activeElement === button;
    state = next;
    root.hidden = next === 'hidden';
    root.classList.toggle('is-error', error);
    text.textContent = message;
    button.hidden = !action;
    button.textContent = action;
    button.disabled = disabled;
    // The button can vanish under the keyboard; keep focus on this row instead of the page.
    if (hadFocus && (button.hidden || disabled)) root.focus({ preventScroll: true });
  };
  const following = () => !running && ['waiting', 'working'].includes(state);
  function watch() {
    clearTimeout(timer); timer = null;
    if (following()) timer = setTimeout(() => { void recheck(); }, pollMs);
  }
  function apply(status, before = state) {
    if (status?.reason === 'working') paint('working', SPEED_UP_WORKING);
    else if (status?.eligible && status.ready) paint('offer', SPEED_UP_OFFER, { action: 'Speed up' });
    else if (status?.eligible) paint('waiting', `${SPEED_UP_OFFER} ${speedUpWaitingText(status)}`, { action: 'Speed up', disabled: true });
    else if (before === 'working' && status?.reason === 'already-optimized') paint('done', SPEED_UP_DONE);
    else paint('hidden');
    watch();
  }
  // A block can clear, and work started on another visit can finish, without this page acting.
  async function recheck() {
    const id = gameId, current = generation, before = state;
    if (!following() || !(id > 0)) return;
    if (root.ownerDocument?.hidden) { watch(); return; }
    let status = null;
    try { status = await invoke('get_recording_optimization', { gameId: id }); } catch (_) { status = null; }
    if (current !== generation || id !== gameId || !following()) return;
    if (!status && before === 'working') { watch(); return; } // no answer is not the end of the work
    apply(status, before);
    // That page's swap ended any stream here; load the video again now that the file is settled.
    if (before === 'working' && state !== 'working') { try { await reload?.(null); } catch (_) { /* reload shows its own error */ } }
  }

  /** status: an answer read alongside the page's snapshot, painted at once so the row is
   * in place before a resumed view restores (undefined asks the host). */
  async function check(vod, { force = false, status } = {}) {
    const id = Number(vod?.gameId);
    if (!root || !text || !button) return;
    if (!vod?.hasVod || !vod.filePath || !(id > 0) || typeof invoke !== 'function') {
      ++generation; gameId = 0; last = null; paint('hidden'); watch(); return;
    }
    last = vod;
    // A reload of the same game keeps a running job or its result on screen.
    if (!force && id === gameId && (running || ['working', 'done', 'failed'].includes(state))) return;
    const current = ++generation;
    gameId = id;
    if (status === undefined) {
      try { status = await invoke('get_recording_optimization', { gameId: id }); } catch (_) { status = null; }
      if (current !== generation) return;
    }
    apply(status);
  }

  async function run() {
    // Try again asks the host first: the reason it stopped may still apply.
    if (state === 'failed') { await check(last, { force: true }); if (state !== 'offer') return; }
    if (state !== 'offer' || !(gameId > 0) || running) return;
    const id = gameId;
    ++generation; running = true;
    paint('working', SPEED_UP_WORKING); watch();
    let view = null, result = null;
    try {
      view = release?.() ?? null; // No stream may hold the file while it is replaced.
      result = await invoke('optimize_recording', { gameId: id });
    } catch (_) { result = null; }
    running = false;
    if (id !== gameId) return;
    if (result?.ok) paint('done', SPEED_UP_DONE);
    else paint('failed', speedUpFailureText(result?.code), { error: true, action: RETRY.has(result?.code) ? 'Try again' : '' });
    try { await reload?.(view); } catch (_) { /* reload shows its own load error */ }
  }

  button?.addEventListener('click', () => { void run(); });
  return { check, run, get state() { return state; } };
}

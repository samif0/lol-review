// Back-to-back review of a mistake trend that is ready (Patterns page). The sidecar
// marks a trend ready when it has at least four watchable moments across three games
// since its last review, and lists those moments (reviewMomentOrdinals, oldest first).
// "Watch all" plays only those, skipping any that cannot play; when a moment's window
// ends the next one starts after a short beat, and Pause stops that advance. After the
// last moment the page asks what keeps repeating and offers Finish review. Nothing is
// marked reviewed until Finish: leaving midway changes nothing.
//
// This module holds the queue, the copy, the run's position and timer, and the painters.
// ./pattern-bulk-review.mjs connects them to the page's player and finish controls.

export const BULK_ADVANCE_MS = 1000;

export const BULK_COPY = Object.freeze({
  start: n => `Watch all ${n} back to back`,
  ready: (n, games) => `${n} mistakes across ${games} games are ready to review together.`,
  running: n => `Watching all ${n} back to back`,
  progress: (position, n) => `Moment ${position} of ${n}`,
  playing: 'The next moment starts on its own. Pause to stay on this one.',
  writing: 'The next moment waits while you write.',
  paused: 'Paused. Press Play to keep going.',
  // Moments that could not play at all are not counted as watched.
  done: (n, watched = n) => watched >= n ? `You watched all ${n}. What keeps repeating?`
    : `You watched ${watched} of ${n}. What keeps repeating?`,
  doneHelp: 'Write it as your takeaway below, then finish the review.',
  question: 'What keeps repeating?',
  finishLater: 'Finish review appears after the last moment.',
  finish: 'Finish review',
  finishPattern: 'Finish reviewing this pattern',
  takeaway: 'What would you do differently in this moment?',
  takeawayName: 'Your takeaway for this moment',
  reviewed: 'Reviewed. Revu will bring this back if it keeps happening.',
  next: title => `Another pattern is ready: ${title}`,
  nextButton: 'Watch it next',
});

/**
 * Indexes into a ready trend's moments that "Watch all" plays: the moments the sidecar
 * listed for review that can still play, oldest first. [] for anything else (a saved
 * collection, a trend that is not ready, nothing playable).
 */
export function bulkQueue(pattern, canPlay = () => true) {
  if (!pattern || pattern.reviewMode === 'saved' || pattern.readyToReview !== true) return [];
  const moments = Array.isArray(pattern.moments) ? pattern.moments : [];
  const ordinals = Array.isArray(pattern.reviewMomentOrdinals) ? pattern.reviewMomentOrdinals : [];
  const queue = new Set();
  for (const ordinal of ordinals) {
    // Ordinals are 1-based positions in the playlist; trust the moment's own ordinal.
    const n = Number(ordinal);
    if (!Number.isSafeInteger(n) || n < 1) continue;
    const index = moments[n - 1]?.ordinal === n ? n - 1 : moments.findIndex(m => m?.ordinal === n);
    if (index >= 0 && canPlay(moments[index])) queue.add(index);
  }
  return [...queue].sort((a, b) => a - b);
}

/**
 * The bar under the review heading: what is ready and its Watch all button, then the
 * run's state, then (after the last moment) the review's question. It keeps its place
 * until Finish so the player does not jump. Its copy sits in one live region, with the
 * run's position for screen readers. dom is the page's { $, show }; text only.
 */
export function paintBulkBar({ $, show }, { visible, run = null, done = null, writing = false,
  queueLength = 0, games = 0, disabled = false }) {
  const bar = $('pat-bulk');
  if (!bar) return;
  show(bar, visible);
  if (!visible) return;
  const text = $('pat-bulk-text'), help = $('pat-bulk-help'), start = $('pat-bulk-start'), progress = $('pat-bulk-progress');
  if (progress) progress.textContent = run ? BULK_COPY.progress(run.position + 1, run.length) : '';
  if (run || done) {
    // The player's own label also counts the run (Moment 2 of 5).
    text.textContent = run ? BULK_COPY.running(run.length) : BULK_COPY.done(done.length, done.watched);
    help.textContent = done ? BULK_COPY.doneHelp : run.held ? BULK_COPY.paused : writing ? BULK_COPY.writing : BULK_COPY.playing;
    show(help, true);
    show(start, false);
    return;
  }
  text.textContent = BULK_COPY.ready(queueLength, games);
  show(help, false);
  start.textContent = BULK_COPY.start(queueLength);
  start.disabled = disabled;
  show(start, true);
}

/** After Finish review: one confirmation, and the next ready trend when there is one. */
export function paintReviewDone({ $, show }, { done, following = null }) {
  const status = $('pat-review-status');
  if (done && status) {
    status.textContent = following ? `${BULK_COPY.reviewed} ${BULK_COPY.next(following.title || 'Next pattern')}.` : BULK_COPY.reviewed;
    show(status, true);
  }
  const next = $('pat-bulk-next');
  if (next) {
    next.textContent = BULK_COPY.nextButton;
    next.dataset.patternKey = following ? following.patternKey : '';
    show(next, !!following);
  }
}

/** Distinct games among the queued moments. */
export function queueGames(pattern, queue) {
  const moments = Array.isArray(pattern?.moments) ? pattern.moments : [];
  return new Set(queue.map(index => moments[index]?.gameId)).size;
}

/**
 * One run through a queue. show(index) opens and plays that moment; onChange() repaints.
 * schedule/cancel default to timers; tests pass their own.
 */
export function createBulkRun({ key, queue, show, onChange = () => {},
  schedule = (fn, ms) => setTimeout(fn, ms), cancel = id => clearTimeout(id), delayMs = BULK_ADVANCE_MS }) {
  const order = [...queue];
  let position = 0, timer = null, held = false, done = false;

  function clearTimer() { if (timer !== null) { cancel(timer); timer = null; } }
  function advance() {
    clearTimer();
    held = false;
    if (position + 1 >= order.length) { done = true; onChange(); return; }
    position += 1;
    show(order[position]);
    onChange();
  }

  return {
    key,
    get queue() { return order.slice(); },
    get position() { return position; },
    get index() { return order[position]; },
    get length() { return order.length; },
    get done() { return done; },
    get held() { return held; },
    get waiting() { return timer !== null; },
    start() { position = 0; held = false; done = false; show(order[0]); onChange(); },
    /** The moment finished (its window, its clip or its recording ended) or cannot play. */
    ended() {
      if (done || held || timer !== null) return;
      timer = schedule(() => { timer = null; advance(); }, delayMs);
    },
    /** Playback moved back inside the moment: no advance is due. */
    rewound() { clearTimer(); },
    /** The player paused: stay on this moment until Play. */
    paused() {
      if (done) return;
      clearTimer();
      if (!held) { held = true; onChange(); }
    },
    resumed() { if (held) { held = false; onChange(); } },
    /** Move on now (Play pressed after the moment ended). */
    next() { if (!done) advance(); },
    /** Manual navigation is opening `index` (the page repaints); false when it leaves the queue. */
    sync(index) {
      const at = order.indexOf(index);
      if (at < 0) return false;
      if (at !== position) { clearTimer(); held = false; position = at; }
      return true;
    },
    /** The queued moment before (-1) or after (1) the current one, or null. */
    neighbor(step) { const at = position + (step < 0 ? -1 : 1); return at >= 0 && at < order.length ? order[at] : null; },
    stop() { clearTimer(); },
  };
}

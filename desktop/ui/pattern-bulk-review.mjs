import { BULK_COPY, bulkQueue, createBulkRun, paintBulkBar, paintReviewDone, queueGames } from './pattern-bulk.mjs';

// The Patterns page's back-to-back review (./pattern-bulk.mjs has the queue, the run and
// the copy). The page owns the player, the snapshot and the writes; it hands this
// controller small accessors and calls it from its renders, its media events and its
// actions, so patterns.js keeps one-line hooks.
//
// States, per trend: ready (the bar offers Watch all), running (the run's position and
// timer), watched (the last moment ended: the review's question and Finish review),
// and reviewed (Finish went through: one confirmation, then the next ready trend).
// Watched survives a trend refresh, so rating a moment during the run never loses the
// end of the review. Leaving the trend clears all of it and marks nothing.

const isSaved = p => p?.reviewMode === 'saved';

/**
 * page: { $, show, pattern(), patterns(), canPlay(moment), saving(), stale(),
 *   open(index), pause(), hasMedia(), flushNote(), focusReview(), repaint() }.
 * schedule/cancel reach the run's timer (tests pass their own).
 */
export function createBulkReview({ $, show, pattern, patterns, canPlay, saving, stale, open, pause, hasMedia,
  flushNote, focusReview, repaint, schedule, cancel }) {
  let run = null;          // the run in progress
  let watched = null;      // { key, length, watched }: a run that reached its last moment
  let reviewed = null;     // { key }: Finish review went through
  let writing = false;     // the takeaway field has focus: the run never moves on by itself
  const skipped = new Set();
  const queueFor = p => bulkQueue(p, canPlay);

  function running() { return !!run && !run.done && run.key === pattern()?.patternKey; }
  /** The active trend was watched to its last moment and is not reviewed yet. */
  function finished(p = pattern()) { return !!p && !p.isReviewed && watched?.key === p.patternKey; }
  function stop() { if (run) run.stop(); run = null; }

  function changed() {
    if (run?.done) { settle(); return; }
    paintBar();
    repaint();
  }

  // The last moment ended: stay on it and ask the review's question.
  function settle() {
    watched = { key: run.key, length: run.length, watched: run.length - skipped.size };
    stop();
    pause();
    paintBar();
    repaint();
    $('m-note')?.focus?.();
  }

  function start() {
    const p = pattern();
    if (!p || saving() || stale()) return;
    const queue = queueFor(p);
    if (!queue.length) return;
    flushNote();
    stop();
    watched = null;
    reviewed = null;
    writing = false;
    skipped.clear();
    run = createBulkRun({ key: p.patternKey, queue, show: open, onChange: changed, schedule, cancel });
    run.start();
    focusReview();
  }

  function paintBar() {
    const p = pattern();
    const live = running() ? run : null;
    const done = !live && finished(p) ? watched : null;
    const queue = live || done ? [] : queueFor(p);
    paintBulkBar({ $, show }, {
      visible: !!p && !isSaved(p) && (!!live || !!done || (queue.length > 0 && !p.isReviewed)),
      run: live, done, writing, queueLength: queue.length, games: queueGames(p, queue), disabled: saving() || stale(),
    });
  }

  // Finish controls after the page's own: hidden midway through a run, "Finish review"
  // once it is watched (a rating made during the run does not block it), and after
  // Finish the confirmation with the next ready trend.
  function paintClosure(p) {
    if (!p) return;
    const live = running(), done = finished(p);
    const btn = $('pat-markrev');
    if (btn) {
      btn.textContent = done ? BULK_COPY.finish : BULK_COPY.finishPattern;
      if (done) btn.disabled = saving();
      if (live) show(btn, false);
    }
    const pending = $('pat-pending-text');
    if (live && pending) pending.textContent = BULK_COPY.finishLater;
    const confirmed = !isSaved(p) && reviewed?.key === p.patternKey;
    // The bar carries a watched run's help; the confirmation replaces the general line.
    show(pending, !done && !confirmed);
    const label = $('m-note-label');
    if (label) label.textContent = done ? BULK_COPY.question : BULK_COPY.takeaway;
    $('m-note')?.setAttribute('aria-label', done ? BULK_COPY.done(watched.length, watched.watched) : BULK_COPY.takeawayName);
    paintReviewDone({ $, show }, { done: confirmed, following: confirmed ? nextReady(p.patternKey) : null });
  }

  function nextReady(afterKey) {
    return patterns().find(q => q.patternKey !== afterKey && !q.isReviewed && queueFor(q).length > 0) || null;
  }

  return {
    running,
    finished,
    start,
    paintBar,
    paintClosure,
    /** Snapshot for tests and diagnostics. */
    get state() {
      return { key: run?.key ?? null, running: running(), held: !!run?.held, waiting: !!run?.waiting,
        position: run?.position ?? null, watched: watched ? { ...watched } : null, writing };
    },
    /** A new snapshot can reorder moments under a run: it ends. A watched run keeps its question. */
    snapshot() { stop(); },
    /** Leaving the trend or the view: nothing is marked. */
    leave() { stop(); watched = null; reviewed = null; writing = false; },
    /** Navigation is opening `index`; a step outside the run's queue ends the run. */
    sync(index) {
      if (run && !run.done && !run.sync(index)) { stop(); paintBar(); repaint(); }
    },
    /** The run's position for the player label, or null outside a run. */
    progress() {
      if (!running()) return null;
      return { label: BULK_COPY.progress(run.position + 1, run.length), prev: run.neighbor(-1), next: run.neighbor(1) };
    },
    /** Previous or Next inside a run steps through its queue; false outside a run. */
    step(direction) {
      if (!running()) return false;
      const index = run.neighbor(direction);
      if (index !== null) open(index);
      return true;
    },
    /** Finish review went through for p: stop, then confirm and offer the next ready trend. */
    confirm(p) { stop(); watched = null; reviewed = { key: p.patternKey }; },
    focusAfterReview() {
      const next = $('pat-bulk-next');
      if (next && !next.hidden) next.focus();
      else $('pat-review-title')?.focus({ preventScroll: true });
    },
    /** This moment cannot play (no file, no URL, a load error, outside the recording). */
    skip() {
      if (!running()) return;
      skipped.add(run.index);
      if (writing) run.paused(); else run.ended();
    },
    /**
     * The moment's window ended (replay: Play pressed at its end). True when the run
     * handled it: moves on a beat later, or at once on Play. False leaves the page's
     * own window rule (stop at the end): no run, a held run, or a takeaway being written.
     */
    windowEnded(replay) {
      if (!running()) return false;
      // A rating is saving and the step it holds back is pending: no second step.
      if (saving()) return true;
      if (writing && !replay) { run.paused(); return false; }
      if (run.held) return false;
      if (replay) run.next(); else run.ended();
      return true;
    },
    rewound() { if (running()) run.rewound(); },
    /** Play: the run continues. A kept clip has no window: Play replays it and stays. */
    onPlay(windowed) {
      if (!running()) return;
      run.resumed();
      if (!windowed) run.rewound();
    },
    /** A pause the player chose holds the run (not the end of the media, not a source switch). */
    onPause(video) {
      if (running() && !video.ended && video.readyState >= 1) run.paused();
    },
    onEnded() {
      if (!running()) return;
      if (writing) run.paused(); else run.ended();
    },
    /** Writing a takeaway keeps the run on this moment; a pending advance stops with the video. */
    noteFocus() {
      if (!running()) return;
      writing = true;
      if (run.waiting) { run.paused(); pause(); } else paintBar();
    },
    noteBlur() {
      if (!writing) return;
      writing = false;
      if (!running()) return;
      // Nothing is loaded to press Play on (the moment could not play): carry on.
      if (run.held && !hasMedia()) { run.resumed(); run.ended(); } else paintBar();
    },
  };
}

// Match library: Skip and Unskip for one game or a selection, as pure helpers for games.js.
// A skipped game left the review queue without a review: it no longer counts as waiting
// for review anywhere (To review, the sidebar, Home), keeps everything saved with it, and
// can still be reviewed. Unskip makes it wait for review again.

// One set_review_skipped request covers at most this many games (the sidecar's limit).
export const SKIP_BATCH = 500;

export const SKIPPED_TITLE = 'You skipped this review, so it no longer waits for review. You can still review it.';

/**
 * What Skip can do for a row: 'pending' (waiting for review, so Skip), 'skipped' (Unskip)
 * or null (reviewed, a remake, or a review state the row does not say). `reviewed` is the
 * row's known review state: true, false or null.
 */
export function skipState(game, reviewed) {
  if (!game || game.isRemake === true || reviewed === true) return null;
  if (game.isSkipped === true) return 'skipped';
  return reviewed === false ? 'pending' : null;
}

/** The ids in groups of at most `size`, in order. */
export function batches(ids, size = SKIP_BATCH) {
  const groups = [];
  for (let at = 0; at < ids.length; at += size) groups.push(ids.slice(at, at + size));
  return groups;
}

/** The toast after a skip or an unskip: the match by name when there is one, else the count. */
export function skipToast(names, skipped) {
  const one = names.length === 1;
  if (skipped) return one ? `${names[0]} was skipped. It no longer waits for review.`
    : `${names.length} games were skipped. They no longer wait for review.`;
  return one ? `${names[0]} is waiting for review again.` : `${names.length} games are waiting for review again.`;
}

/**
 * The toast when a skip or an unskip failed for `failed` of `total` games (a large
 * selection is written in parts); `reason` is the sidecar's sentence.
 */
export function skipFailedToast(failed, total, skipped, reason) {
  const what = skipped ? 'skip' : 'unskip';
  const which = failed < total ? `${failed} of these games` : total === 1 ? 'this game' : 'these games';
  return `Couldn't ${what} ${which}. ${reason || ''}`.trim();
}

/**
 * The selected games, by id. toggle() with `extend` (Shift) selects or clears the run of
 * `order` (the selectable ids in display order) from the last toggled id to this one.
 */
export function createPicks() {
  const ids = new Set();
  let anchor = null;
  return {
    get size() { return ids.size; },
    has: id => ids.has(String(id)),
    list: () => [...ids],
    toggle(id, { extend = false, order = [] } = {}) {
      const key = String(id);
      const on = !ids.has(key);
      const from = extend && anchor !== null ? order.indexOf(anchor) : -1;
      const to = order.indexOf(key);
      const run = from >= 0 && to >= 0 ? order.slice(Math.min(from, to), Math.max(from, to) + 1) : [key];
      for (const each of run) {
        if (on) ids.add(each); else ids.delete(each);
      }
      anchor = key;
      return on;
    },
    addAll(list) { for (const id of list) ids.add(String(id)); },
    /** Drop every id not in `list` (the selectable rows still shown). */
    keepOnly(list) {
      const keep = new Set(list.map(String));
      for (const id of [...ids]) if (!keep.has(id)) ids.delete(id);
      if (anchor !== null && !keep.has(anchor)) anchor = null;
    },
    clear() { ids.clear(); anchor = null; },
  };
}

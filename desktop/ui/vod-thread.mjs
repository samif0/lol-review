// Journal VOD thread: the side panel as one list in game time. Focus events, the
// player's notes and clips, a "You are here" marker and the review takeaway read top
// to bottom. Read and jump only: every write stays in Saved moments.
//
// buildThread is pure. renderThread and moveNow touch only the list they are given
// and use a small DOM surface (createElement, appendChild, insertBefore, removeChild,
// classList, dataset, setAttribute, textContent) so the VOD test fixture can run them.
//
// Inputs (vodplayer.js maps its own rows into these):
//   events:   [{ t, text, key, tone }]          key = reviewEventKey(mark)
//   notes:    [{ t, text, key?, own? }]
//   clips:    [{ t, end?, text, key?, narrated?, own? }]
//   takeaway: string | { text } | null          the review's takeaway (reviewNotes)
//   now:      current game time in seconds
// `own: false` marks a note or clip whose text is a generated title, not the player's
// words; anything else with text counts as the player's own words.
//
// DOM per entry (styled by the VOD page CSS):
//   li.vp-th-item.vp-th-{kind}[data-kind][data-t][data-tone]
//     button.ui-row.vp-th-row[data-action][data-thread-key]   (div.vp-th-row when not clickable;
//     the ui- class makes the styles.css button reset skip it, and kit.css gives it a clean slate)
//       span.vp-th-t   i.vp-th-dot   span.vp-th-body > span.vp-th-kind + span.vp-th-text
//   events:  data-action="review_event" data-review-key, .is-on + aria-current when selected
//   notes and clips: data-action="jump" data-seconds
//   now:     li.vp-th-item.vp-th-now[aria-hidden] > div.vp-th-row > span.vp-th-t + i.vp-th-dot + span.vp-th-nowline
//   takeaway: li.vp-th-takeaway, time cell "End"; .is-missing with a button.ui-link.vp-th-link
//             when there is no takeaway and the caller passes onReview
//   empty:   li.vp-th-empty

import { TONES } from './meaning-tone.mjs';

/** Same tolerance as the event stepper: an entry within a quarter second counts as reached. */
export const NOW_TOLERANCE = 0.25;

const KIND_ORDER = Object.freeze({ event: 0, clip: 1, note: 2 });
const NOW_TEXT = 'You are here';
const END_LABEL = 'End';
const EMPTY_TEXT = 'Notes and clips you save will show here, in game time.';
const MISSING_TAKEAWAY_TEXT = 'Write your takeaway in the review';
const KNOWN_TONES = new Set(TONES);
const renderOptions = new WeakMap();

const finite = value => typeof value === 'number' && Number.isFinite(value);
const toSeconds = value => {
  if (value === null || value === undefined || value === '') return NaN;
  const number = Number(value);
  return Number.isFinite(number) ? number : NaN;
};
const textOf = value => (value === null || value === undefined ? '' : String(value).trim());
const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;

/** m:ss game clock, matching vodplayer.js clock(). */
export function clock(seconds) {
  const s = Math.max(0, Math.floor(Number(seconds) || 0));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

function takeawayText(takeaway) {
  if (takeaway && typeof takeaway === 'object') return textOf(takeaway.text);
  return textOf(takeaway);
}

function entriesOf(list, kind) {
  if (!Array.isArray(list)) return [];
  const entries = [];
  list.forEach((raw, index) => {
    if (!raw || typeof raw !== 'object') return;
    const t = toSeconds(raw.t ?? raw.seconds);
    if (!Number.isFinite(t)) return;
    const item = { kind, t, text: textOf(raw.text) };
    const key = textOf(raw.key);
    if (key) item.key = key;
    if (kind === 'event') {
      if (!item.text) item.text = 'Game event';
      item.tone = KNOWN_TONES.has(raw.tone) ? raw.tone : 'neutral';
    } else {
      item.own = raw.own !== false && item.text !== '';
    }
    if (kind === 'clip') {
      const end = toSeconds(raw.end);
      if (Number.isFinite(end) && end > t) item.end = end;
      item.narrated = !!raw.narrated;
    }
    entries.push({ item, index });
  });
  return entries;
}

/** Count line under the heading: "2 notes · 1 clip", zero parts left out. */
export function threadCountText(notes, clips) {
  const parts = [];
  if (notes > 0) parts.push(plural(notes, 'note'));
  if (clips > 0) parts.push(plural(clips, 'clip'));
  return parts.length ? parts.join(' · ') : 'No notes yet';
}

/**
 * Merge events, notes and clips into one list sorted by game time. On a tie the event
 * comes first, then the clip, then the note; equal entries keep their input order. The
 * "now" item sits before the first entry later than now + 0.25 s, and the takeaway is
 * always last with the label "End". Nothing to show gives an empty list.
 */
export function buildThread({ events = [], notes = [], clips = [], takeaway = null, now = 0 } = {}) {
  const entries = [...entriesOf(events, 'event'), ...entriesOf(clips, 'clip'), ...entriesOf(notes, 'note')]
    .sort((a, b) => (a.item.t - b.item.t) || (KIND_ORDER[a.item.kind] - KIND_ORDER[b.item.kind]) || (a.index - b.index))
    .map(entry => entry.item);
  const noteCount = entries.filter(item => item.kind === 'note').length;
  const clipCount = entries.filter(item => item.kind === 'clip').length;
  const countText = threadCountText(noteCount, clipCount);
  const take = takeawayText(takeaway);
  if (!entries.length && !take) return { items: [], countText };
  const at = finite(now) ? Math.max(0, now) : 0;
  const nowItem = { kind: 'now', t: at, text: NOW_TEXT };
  const split = entries.findIndex(item => item.t > at + NOW_TOLERANCE);
  const items = split < 0 ? [...entries, nowItem] : [...entries.slice(0, split), nowItem, ...entries.slice(split)];
  if (take) items.push({ kind: 'takeaway', t: null, text: take, label: END_LABEL });
  return { items, countText };
}

function threadKey(item) {
  if (item.kind === 'event') return `event:${item.key || item.t}`;
  if (item.kind === 'note' || item.kind === 'clip') return `${item.kind}:${item.key || item.t}`;
  return '';
}

function kindLabel(item, formatTime) {
  if (item.kind === 'note') return 'Note';
  if (item.kind === 'takeaway') return 'Takeaway';
  if (item.kind !== 'clip') return '';
  let label = finite(item.end) ? `Clip · ${formatTime(item.t)} to ${formatTime(item.end)}` : 'Clip';
  if (item.narrated) label += ' · Narrated';
  return label;
}

function element(doc, tag, className, text) {
  const el = doc.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

function dot(doc) {
  const el = element(doc, 'i', 'vp-th-dot');
  el.setAttribute('aria-hidden', 'true');
  return el;
}

function entryRow(doc, item, formatTime, selectedKey) {
  const li = element(doc, 'li', `vp-th-item vp-th-${item.kind}`);
  li.dataset.kind = item.kind;
  if (finite(item.t)) li.dataset.t = String(item.t);
  if (item.kind === 'event') li.dataset.tone = item.tone || 'neutral';
  else if (item.kind === 'note' || item.kind === 'clip') li.dataset.tone = item.kind;

  if (item.kind === 'now') {
    li.setAttribute('aria-hidden', 'true');
    const row = li.appendChild(element(doc, 'div', 'vp-th-row'));
    row.appendChild(element(doc, 'span', 'vp-th-t', formatTime(item.t)));
    row.appendChild(dot(doc));
    row.appendChild(element(doc, 'span', 'vp-th-nowline', item.text || NOW_TEXT));
    return li;
  }

  const clickable = item.kind !== 'takeaway';
  const row = li.appendChild(element(doc, clickable ? 'button' : 'div', clickable ? 'ui-row vp-th-row' : 'vp-th-row'));
  if (clickable) {
    row.type = 'button';
    row.dataset.threadKey = threadKey(item);
    const at = formatTime(item.t);
    if (item.kind === 'event') {
      row.dataset.action = 'review_event';
      row.dataset.reviewKey = item.key || '';
      row.setAttribute('aria-label', `Watch ${item.text} at ${at}`);
      if (selectedKey && item.key === selectedKey) {
        row.classList.add('is-on');
        row.setAttribute('aria-current', 'true');
      }
    } else {
      row.dataset.action = 'jump';
      row.dataset.seconds = String(item.t);
      row.setAttribute('aria-label', `Watch your ${item.kind} at ${at}${item.text ? `: ${item.text}` : ''}`);
    }
  }
  row.appendChild(element(doc, 'span', 'vp-th-t', item.kind === 'takeaway' ? (item.label || END_LABEL) : formatTime(item.t)));
  row.appendChild(dot(doc));
  const body = row.appendChild(element(doc, 'span', 'vp-th-body'));
  const kind = kindLabel(item, formatTime);
  if (kind) body.appendChild(element(doc, 'span', 'vp-th-kind', kind));
  if (item.text) {
    const own = item.kind === 'takeaway' || (item.kind !== 'event' && item.own !== false);
    body.appendChild(element(doc, 'span', own ? 'vp-th-text own-words' : 'vp-th-text', item.text));
  }
  return li;
}

function missingTakeawayRow(doc, onReview) {
  const li = element(doc, 'li', 'vp-th-item vp-th-takeaway is-missing');
  li.dataset.kind = 'takeaway';
  const row = li.appendChild(element(doc, 'div', 'vp-th-row'));
  row.appendChild(element(doc, 'span', 'vp-th-t', END_LABEL));
  row.appendChild(dot(doc));
  const body = row.appendChild(element(doc, 'span', 'vp-th-body'));
  body.appendChild(element(doc, 'span', 'vp-th-kind', 'Takeaway'));
  const link = body.appendChild(element(doc, 'button', 'ui-link vp-th-link', MISSING_TAKEAWAY_TEXT));
  link.type = 'button';
  link.dataset.threadKey = 'takeaway:missing';
  link.addEventListener('click', event => onReview(event));
  return li;
}

/**
 * Replace the list's rows with the thread. Keeps keyboard focus on the same entry across
 * rebuilds and never scrolls. Options: formatTime (default m:ss), doc (default the
 * list's document), selectedKey (the selected review event key), onReview (renders a
 * "write your takeaway" button at the end when there is no takeaway).
 */
export function renderThread(listEl, items, { formatTime = clock, doc, selectedKey = '', onReview } = {}) {
  if (!listEl) return;
  const document = doc || listEl.ownerDocument || globalThis.document;
  const format = typeof formatTime === 'function' ? formatTime : clock;
  renderOptions.set(listEl, { formatTime: format });
  const active = document?.activeElement;
  const focusKey = active && listEl.contains?.(active) ? active.closest?.('[data-thread-key]')?.dataset.threadKey || '' : '';
  while (listEl.firstChild) listEl.removeChild(listEl.firstChild);

  const list = Array.isArray(items) ? items.filter(item => item && typeof item === 'object') : [];
  const focusable = [];
  if (!list.length) listEl.appendChild(element(document, 'li', 'vp-th-item vp-th-empty', EMPTY_TEXT));
  for (const item of list) {
    const li = listEl.appendChild(entryRow(document, item, format, selectedKey));
    const target = li.querySelector?.('[data-thread-key]');
    if (target) focusable.push(target);
  }
  if (typeof onReview === 'function' && !list.some(item => item.kind === 'takeaway')) {
    const li = listEl.appendChild(missingTakeawayRow(document, onReview));
    const target = li.querySelector?.('[data-thread-key]');
    if (target) focusable.push(target);
  }
  if (focusKey) focusable.find(el => el.dataset.threadKey === focusKey)?.focus?.({ preventScroll: true });
}

function rowTime(row) {
  const kind = row?.dataset?.kind;
  if (!kind || kind === 'now') return NaN;
  if (kind === 'takeaway') return Infinity;
  return toSeconds(row.dataset.t);
}

/**
 * Move the single "You are here" row to the current game time without rebuilding the
 * list, scrolling or announcing anything. Returns true when the row moved.
 */
export function moveNow(listEl, now, { formatTime } = {}) {
  if (!listEl) return false;
  const rows = Array.from(listEl.children || []);
  const nowEl = rows.find(row => row.classList?.contains('vp-th-now'));
  if (!nowEl) return false;
  const at = finite(now) ? Math.max(0, now) : 0;
  const format = typeof formatTime === 'function' ? formatTime : renderOptions.get(listEl)?.formatTime || clock;
  nowEl.dataset.t = String(at);
  const time = nowEl.querySelector?.('.vp-th-t');
  const label = format(at);
  if (time && time.textContent !== label) time.textContent = label;

  const ref = rows.find(row => row !== nowEl && rowTime(row) > at + NOW_TOLERANCE) || null;
  const next = rows[rows.indexOf(nowEl) + 1] || null;
  if (next === ref) return false;
  if (ref) listEl.insertBefore(nowEl, ref);
  else listEl.appendChild(nowEl);
  return true;
}

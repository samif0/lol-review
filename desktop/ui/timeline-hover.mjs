// Hover preview for the VOD event timeline. One resolver pass (timeline-event-hit.mjs)
// decides what a click here does; this module rings that event (.evbar-hit-hover) and
// writes a small chip in the clock row: "14:32" over empty space, "Kill at 14:27"
// over an event, "Bookmark at 5:23" or "Clip at 12:41" over a diamond. Over empty
// space close to an event, below 4x, the chip also says "Ctrl+scroll to zoom in",
// until this viewer zooms once (remembered in localStorage). The chip never takes
// the pointer and lives in the clock row, which holds no marks, so it covers nothing;
// a clock tick it would touch hides while it shows, and the rest keep full contrast.
// When a short timeline puts the bookmark diamonds in that row, it stays hidden.
// The preview follows the geometry as well as the pointer: a page scroll, a zoom, or
// markers rebuilt or nudged under a resting pointer resolve it again at the last
// pointer position, so the chip and ring always describe what a click there does.
import { resolveTimelinePoint } from './timeline-event-hit.mjs';

export const ZOOM_LEARNED_KEY = 'revu.vod.timelineZoomLearned.v1';
export const HINT_NEAR_PX = 16;
export const HINT_MAX_ZOOM = 4;
// The chip is 16px tall at the bottom; the bookmark diamonds reach 15px under the track.
const CHIP_ROW_PX = 17, MARK_LANE_PX = 15;
// A clock tick within this many px of the chip hides, so no fragment reads as part of it.
const TICK_GAP_PX = 4, UNDER = 'is-under-hover';
const CONTROLS = '#vp-tl-zoom, button, input, select, option, textarea, a[href], [contenteditable]:not([contenteditable="false"])';
const MISS = Object.freeze({ bar: null, gap: Infinity, x: NaN });

const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };

export function zoomHintLearned(storage) {
  return attempt(() => storage?.getItem(ZOOM_LEARNED_KEY) === '1', false);
}

export function rememberZoomLearned(storage) {
  attempt(() => storage?.setItem(ZOOM_LEARNED_KEY, '1'));
}

// The chip's words for a marker. Ghosts are events the user removed; selecting one
// is how it is restored, so the chip says so.
export function eventChipText(bar, clock) {
  const d = bar?.dataset || {};
  const label = String(d.label || d.code || bar?.querySelector?.('.evbar-code')?.textContent || 'Event').trim() || 'Event';
  return `${d.ghost === '1' ? `Removed ${label}` : label} at ${clock(Number(d.seconds) || 0)}`;
}

export function bookmarkChipText(mark, clock) {
  return `${mark?.dataset?.tone === 'clip' ? 'Clip' : 'Bookmark'} at ${clock(Number(mark?.dataset?.seconds) || 0)}`;
}

export function createTimelineHover({ seek, markers, chip, text, hint, clock, secondsAt, zoom,
  storage = attempt(() => globalThis.localStorage),
  requestFrame = callback => (typeof globalThis.requestAnimationFrame === 'function'
    ? globalThis.requestAnimationFrame(callback) : (callback(), 0)),
  Observer = globalThis.MutationObserver }) {
  const drawn = !!(seek && chip && text && hint);
  let hovered = null, pending = null, frame = 0, learned = zoomHintLearned(storage);
  let measuredKey = '', width = 0, point = null, inside = false, covered = [];

  // Mouse events (click, contextmenu, wheel) report whole CSS pixels, while pointer
  // events keep the fraction a scaled display gives them. At 1x one pixel can be
  // several seconds, so a mouse event on the pixel of the latest pointer position is
  // read at that precise point: a click selects and seeks exactly what the chip showed.
  const remember = event => {
    inside = true;
    if (Number.isFinite(event.clientX) && Number.isFinite(event.clientY)) point = { x: event.clientX, y: event.clientY };
  };
  for (const type of ['pointerdown', 'pointermove', 'pointerup']) seek?.addEventListener?.(type, remember, true);
  seek?.addEventListener?.('pointerleave', () => { inside = false; });
  function precise(event) {
    if (!point || String(event?.type).startsWith('pointer')
      || !(Math.abs(event.clientX - point.x) < 1 && Math.abs(event.clientY - point.y) < 1)) return event;
    return { type: event.type, detail: event.detail, target: event.target, clientX: point.x, clientY: point.y };
  }

  function ring(bar) {
    if (bar === hovered) return;
    hovered?.classList.remove('evbar-hit-hover');
    hovered = bar;
    hovered?.classList.add('evbar-hit-hover');
    seek?.classList.toggle('is-event-hover', !!bar);
  }

  // Room for the chip: the clock row must sit clear of the bookmark lane under the
  // track. Without a track there are no diamonds to cover.
  function roomForChip(box) {
    const track = seek.querySelector?.('.track')?.getBoundingClientRect?.();
    if (!track || !(track.height >= 0)) return true;
    return box.bottom - CHIP_ROW_PX >= (track.top + track.bottom) / 2 + MARK_LANE_PX;
  }

  // Hides exactly the clock ticks in `next` (the ones the chip would touch).
  function cover(next) {
    for (const tick of covered) if (!next.includes(tick)) tick.classList.remove(UNDER);
    for (const tick of next) tick.classList.add(UNDER);
    covered = next;
  }

  // Writes text, visibility and one CSS variable. The chip is measured only when its
  // shape changes (digits share one width under tabular figures), so a moving
  // pointer costs at most one extra layout read. The few clock ticks are read before
  // any write; the ones the chip would touch hide while it shows.
  function paint(model) {
    if (!drawn) return;
    const box = model && seek.getBoundingClientRect();
    const shown = !!(model && box && roomForChip(box));
    if (!shown) { chip.hidden = true; measuredKey = ''; cover([]); return; }
    const ticks = [...(seek.querySelectorAll?.('.vp-tl-tick') || [])].map(tick => [tick, tick.getBoundingClientRect?.()]);
    text.textContent = model.text;
    hint.hidden = !model.hint;
    chip.hidden = false;
    const key = `${model.text.replace(/\d/g, '0')}|${model.hint}`;
    if (key !== measuredKey) { measuredKey = key; width = chip.offsetWidth || 0; }
    const room = Math.max(0, (seek.clientWidth || box.width || 0) - width);
    const left = Math.min(room, Math.max(0, model.anchor - box.left - width / 2));
    const from = box.left + left - TICK_GAP_PX, to = box.left + left + width + TICK_GAP_PX;
    cover(ticks.filter(([, r]) => r && r.right > from && r.left < to).map(([tick]) => tick));
    chip.style.setProperty('--hover-x', `${Math.round(left)}px`);
  }

  const showHint = r => !learned && r.gap <= HINT_NEAR_PX && Number(zoom?.()) < HINT_MAX_ZOOM;

  // Resolve, ring and paint for one pointer position; returns the resolution so a
  // click or context menu acts on exactly what the preview shows.
  function render(event) {
    pending = null;
    event = precise(event);
    if (!seek || seek.classList.contains('is-panning')) { ring(null); paint(null); return MISS; }
    const target = event.target?.closest ? event.target : event.target?.parentElement;
    const mark = target?.closest?.('.ev-bm');
    if (mark && seek.contains(mark)) {
      ring(null);
      const box = mark.getBoundingClientRect();
      paint({ text: bookmarkChipText(mark, clock), anchor: (box.left + box.right) / 2, hint: false });
      return MISS;
    }
    if (target?.closest?.(CONTROLS)) { ring(null); paint(null); return MISS; }
    const r = resolveTimelinePoint(seek, markers, event, { sticky: hovered });
    ring(r.bar);
    if (r.bar) {
      paint({ text: eventChipText(r.bar, clock), anchor: r.x, hint: false });
    } else {
      const seconds = Number.isFinite(r.x) ? secondsAt(r.x) : null;
      paint(Number.isFinite(seconds) ? { text: clock(seconds), anchor: r.x, hint: showHint(r) } : null);
    }
    return r;
  }

  function hide() { pending = null; ring(null); paint(null); }

  // The timeline moved under a pointer that did not: resolve again at the last pointer
  // position. When something else now sits under the pointer, the preview hides.
  const AGAIN = Object.freeze({});
  function again() {
    if (!inside || !point) return null;
    const target = attempt(() => seek.ownerDocument?.elementFromPoint?.(point.x, point.y));
    if (target && !seek.contains(target)) { hide(); return null; }
    return { type: 'pointermove', detail: 0, clientX: point.x, clientY: point.y, target };
  }

  function flush() {
    frame = 0;
    const next = pending;
    pending = null;
    const event = next === AGAIN ? again() : next;
    if (event) render(event);
  }

  // Resolving reads every marker's rect, so it runs at most once a frame.
  function queue(event) {
    pending = event;
    if (!frame) frame = requestFrame(flush) || 0;
  }

  function refresh() { if (inside && point) queue(AGAIN); }

  // What can move the timeline under a resting pointer: a scroll anywhere in the page,
  // markers rebuilt, nudged or hidden, and a zoom or pan of the content.
  seek?.ownerDocument?.addEventListener?.('scroll', refresh, { capture: true, passive: true });
  if (typeof Observer === 'function' && markers) attempt(() => {
    const watch = new Observer(refresh);
    watch.observe(markers, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'hidden'] });
    const content = markers.parentElement;
    if (content && content !== seek) watch.observe(content, { attributes: true, attributeFilter: ['style'] });
  });

  return {
    resolve: render,
    // The pointer x a click or context menu happened at, to the fraction (see precise).
    clientX: event => precise(event).clientX,
    queue,
    // Resolve again at the last pointer position (see again).
    refresh,
    hide,
    // The viewer found zoom: the hint is retired for good on this device.
    noteZoomed() {
      if (learned) return;
      learned = true;
      rememberZoomLearned(storage);
      if (drawn) hint.hidden = true;
    },
    get hovered() { return hovered; },
    get learned() { return learned; },
  };
}

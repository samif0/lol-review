// Resolve in viewport coordinates so CSS transforms, timeline zoom and pan all
// use the geometry the user can see. No event selection or mutation happens here.
//
// An event is the target only where it is drawn: its stem padded by HIT_SLOP_PX
// (from HIT_SLOP_PX above its top, or its cap, down to BASELINE_SLOP_PX under its
// baseline) or its visible label padded by HIT_SLOP_PX. Everywhere else is a time,
// so a click there seeks to the exact pointer time. There is no magnet. The same
// pass reports how far the pointer is from the nearest drawn event (gap, in CSS
// px), so the hover preview reuses one resolution for its ring, chip and hint.
export const HIT_SLOP_PX = 3;
export const BASELINE_SLOP_PX = 4;
// Objective and corrected bars draw a small cap above the stem (styles.css ::before).
const CAP_PX = 6;
// A hovered label-less bar reveals its code 4px above the stem (an 11px code with
// 2px padding, vod-review.css). Pseudo-elements have no rect, so its box is derived.
const CODE_GAP_PX = 4, CODE_HEIGHT_PX = 15, CODE_CHAR_PX = 7, CODE_PAD_X_PX = 4;
const PROTECTED = '#vp-tl-zoom, .ev-bm, button, input, select, option, textarea, a[href], [contenteditable]:not([contenteditable="false"])';
const MISS = Object.freeze({ bar: null, gap: Infinity, x: NaN });

// Returns {bar, gap, x}: bar is the event a click here selects (or null for a time),
// gap is the distance to the nearest visible stem or label, and x is the stem
// center for an event or the pointer for a time. `sticky` is the bar currently
// ringed by hover: a label-less bar keeps its revealed code clickable.
export function resolveTimelinePoint(seek, markers, event, { slop = HIT_SLOP_PX, sticky = null } = {}) {
  if (!seek || !markers || !event || !seek.contains(markers)) return MISS;
  const view = seek.ownerDocument?.defaultView;
  const visibility = new Map();
  const visible = node => {
    if (!node) return true;
    if (visibility.has(node)) return visibility.get(node);
    const style = view?.getComputedStyle(node);
    const result = node.isConnected !== false && !node.hidden && style?.display !== 'none'
      && style?.visibility !== 'hidden' && style?.visibility !== 'collapse'
      && Number(style?.opacity) !== 0 && visible(node.parentElement);
    visibility.set(node, result);
    return result;
  };
  const rect = node => {
    if (!node || !visible(node)) return null;
    const box = node.getBoundingClientRect();
    return [box.left, box.top, box.right, box.bottom, box.width, box.height].every(Number.isFinite)
      && box.width > 0 && box.height > 0 ? box : null;
  };
  const intersect = (a, b) => {
    if (!a || !b) return null;
    const left = Math.max(a.left, b.left), top = Math.max(a.top, b.top);
    const right = Math.min(a.right, b.right), bottom = Math.min(a.bottom, b.bottom);
    return right > left && bottom > top ? { left, top, right, bottom } : null;
  };
  let viewport = rect(seek);
  if (view?.innerWidth > 0 && view?.innerHeight > 0) viewport = intersect(viewport,
    { left: 0, top: 0, right: view.innerWidth, bottom: view.innerHeight });
  if (!viewport) return MISS;
  const target = event.target?.closest ? event.target : event.target?.parentElement;
  if (target?.closest(PROTECTED)) return MISS;
  const candidate = bar => {
    if (!bar || !markers.contains(bar)) return null;
    const raw = rect(bar);
    if (!raw) return null;
    const stem = intersect(raw, viewport);
    const labels = [...bar.querySelectorAll('.evbar-code')].map(label => intersect(rect(label), viewport)).filter(Boolean);
    const cap = bar.matches?.('.evbar-objective') || bar.matches?.('.evbar-fixed') ? CAP_PX : 0;
    return stem || labels.length ? { bar, raw, stem, labels, cap, cx: (raw.left + raw.right) / 2 } : null;
  };
  const direct = target?.closest('.evbar');
  // Programmatic/keyboard clicks have no meaningful coordinates. Pointer hover
  // and context menus also use detail=0, but retain their actual point geometry.
  if (event.type === 'click' && event.detail === 0) {
    const exact = candidate(direct);
    return exact ? { bar: exact.bar, gap: 0, x: exact.cx } : MISS;
  }
  const x = event.clientX, y = event.clientY;
  const within = (box, pad = 0, below = pad) =>
    x >= box.left - pad && x <= box.right + pad && y >= box.top - pad && y <= box.bottom + below;
  if (!Number.isFinite(x) || !Number.isFinite(y) || !within(viewport)) return MISS;
  const items = [...markers.querySelectorAll('.evbar')].map(candidate).filter(Boolean);
  const distance = box => Math.hypot(Math.max(box.left - x, 0, x - box.right), Math.max(box.top - y, 0, y - box.bottom));
  let gap = Infinity;
  for (const item of items) for (const box of item.stem ? [item.stem, ...item.labels] : item.labels) gap = Math.min(gap, distance(box));
  const hit = item => ({ bar: item.bar, gap: 0, x: item.cx });
  const exact = items.find(item => item.bar === direct);
  if (exact) return hit(exact);
  // The code a hovered label-less bar reveals is drawn over its neighbors, so while
  // it shows it stays that bar's target and moving onto it never flickers.
  const held = sticky?.matches?.('.evbar-nolabel') ? items.find(item => item.bar === sticky && item.stem) : null;
  const pad = Number.isFinite(slop) ? Math.max(0, slop) : HIT_SLOP_PX;
  if (held) {
    const half = (String(held.bar.dataset?.code || '').length * CODE_CHAR_PX) / 2 + CODE_PAD_X_PX;
    const code = { left: held.cx - half, right: held.cx + half, top: held.raw.top - CODE_GAP_PX - CODE_HEIGHT_PX, bottom: held.raw.top };
    if (within(code, pad, 0)) return hit(held);
  }
  // Labels can sit away from their thin stem, including where pointer-events lets
  // the click reach the track underneath. Exact label coverage wins first.
  for (const item of items) if (item.labels.some(box => within(box))) return hit(item);
  // Otherwise the nearest padded stem or label; equal distances keep DOM order.
  let best = null, nearest = Infinity;
  for (const item of items) {
    const boxes = item.labels.filter(box => within(box, pad));
    if (item.stem && within({ ...item.stem, top: item.stem.top - item.cap }, pad, BASELINE_SLOP_PX)) boxes.push(item.stem);
    for (const box of boxes) {
      const away = distance(box);
      if (away < nearest) { best = item; nearest = away; }
    }
  }
  return best ? hit(best) : { bar: null, gap, x };
}

export function findTimelineEventAtPoint(seek, markers, event, options) {
  return resolveTimelinePoint(seek, markers, event, options).bar;
}

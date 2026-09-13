// Resolve in viewport coordinates so CSS transforms, timeline zoom and pan all
// use the geometry the user can see. No event selection or mutation happens here.
export function findTimelineEventAtPoint(seek, markers, event, radius = 14) {
  if (!seek || !markers || !event || !seek.contains(markers)) return null;
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
  if (!viewport) return null;
  const target = event.target?.closest ? event.target : event.target?.parentElement;
  if (target?.closest('#vp-tl-zoom, .ev-bm, button, input, select, option, textarea, a[href], [contenteditable]:not([contenteditable="false"])')) return null;
  const candidate = bar => {
    if (!bar || !markers.contains(bar)) return null;
    const rawStem = rect(bar);
    if (!rawStem) return null;
    const stem = intersect(rawStem, viewport);
    const labels = [...bar.querySelectorAll('.evbar-code')].map(label => intersect(rect(label), viewport)).filter(Boolean);
    return stem || labels.length ? { bar, stem, labels, baseline: rawStem.bottom } : null;
  };
  const direct = target?.closest('.evbar');
  // Programmatic/keyboard clicks have no meaningful coordinates. Pointer hover
  // and context menus also use detail=0, but retain their actual point geometry.
  if (event.type === 'click' && event.detail === 0) return candidate(direct)?.bar || null;
  const x = event.clientX, y = event.clientY;
  const contains = box => x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
  if (!Number.isFinite(x) || !Number.isFinite(y) || !contains(viewport)) return null;
  const exact = candidate(direct);
  if (exact) return exact.bar;
  const candidates = [...markers.querySelectorAll('.evbar')].map(candidate).filter(Boolean);
  // Labels can extend well away from a thin stem, including when pointer-events
  // lets the click reach the scrub track underneath. Exact label coverage wins.
  for (const item of candidates) if (item.labels.some(contains)) return item.bar;
  const distance = box => Math.hypot(Math.max(box.left - x, 0, x - box.right), Math.max(box.top - y, 0, y - box.bottom));
  const limit = Number.isFinite(radius) ? Math.max(0, radius) : 14;
  let nearest = null, best = limit;
  for (const item of candidates) {
    const boxes = item.labels.slice();
    // Leave the bookmark/scrub lane beneath the baseline available to its owner.
    if (item.stem && y <= item.baseline + 4) boxes.push(item.stem);
    const gap = Math.min(...boxes.map(distance));
    if (gap <= limit && (!nearest || gap < best)) { nearest = item.bar; best = gap; }
  }
  return nearest;
}

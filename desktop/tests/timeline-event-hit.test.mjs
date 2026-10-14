import test from 'node:test';
import assert from 'node:assert/strict';
import { BASELINE_SLOP_PX, findTimelineEventAtPoint, HIT_SLOP_PX, resolveTimelinePoint } from '../ui/timeline-event-hit.mjs';

const rect = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height });

function fixture() {
  const view = { innerWidth: 800, innerHeight: 600,
    getComputedStyle: node => ({ display: 'block', visibility: 'visible', opacity: '1', ...node.style }) };
  const document = { defaultView: view };
  function node({ classes = [], tag = 'span', id = '', box = rect(0, 0, 1, 1), style = {}, hidden = false } = {}) {
    const element = { classes, tagName: tag.toUpperCase(), id, box, style, hidden,
      children: [], parentElement: null, ownerDocument: document, isConnected: true, reads: 0,
      getBoundingClientRect() { this.reads++; return this.box; },
      append(child) { child.parentElement = this; this.children.push(child); return child; },
      contains(other) { return this === other || this.children.some(child => child.contains(other)); },
      matches(selector) {
        if (selector.startsWith('.')) return this.classes.includes(selector.slice(1));
        if (selector.startsWith('#')) return this.id === selector.slice(1);
        if (selector === 'a[href]') return this.tagName === 'A' && !!this.href;
        if (selector.startsWith('[contenteditable]')) return this.contentEditable === 'true';
        return this.tagName === selector.toUpperCase();
      },
      closest(selector) {
        return selector.split(',').some(part => this.matches(part.trim())) ? this : this.parentElement?.closest(selector) || null;
      },
      querySelectorAll(selector) {
        return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
      },
    };
    return element;
  }
  const seek = node({ tag: 'div', box: rect(100, 100, 400, 160) });
  const markers = seek.append(node({ tag: 'div', box: seek.box }));
  function bar(stem, label, { ghost = false, extra = [], ...options } = {}) {
    const element = markers.append(node({ classes: ['evbar', ...(ghost ? ['evbar-ghost'] : []), ...extra], box: stem, ...options }));
    element.dataset = {};
    if (label) element.append(node({ classes: ['evbar-code'], box: label, style: { pointerEvents: 'none' } }));
    return element;
  }
  const event = (x, y, { target = seek, type = 'click', detail = 1 } = {}) => ({ clientX: x, clientY: y, target, type, detail });
  const hit = (x, y, { slop, sticky, ...options } = {}) =>
    findTimelineEventAtPoint(seek, markers, event(x, y, options), { slop, sticky });
  const resolve = (x, y, { sticky, ...options } = {}) => resolveTimelinePoint(seek, markers, event(x, y, options), { sticky });
  return { node, seek, markers, bar, hit, resolve, view };
}

test('visible seek edges constrain hits and the slop measures rendered rectangles in CSS pixels', () => {
  const f = fixture();
  const edge = f.bar(rect(100, 175, 2, 45));
  assert.equal(HIT_SLOP_PX, 3);
  assert.equal(f.hit(100, 200), edge);
  assert.equal(f.hit(99.99, 200, { target: edge }), null, 'an exact bar target cannot reach outside the viewport');
  assert.equal(f.hit(105, 200), edge, '3px from the stem edge is included');
  assert.equal(f.hit(105.01, 200), null, 'there is no magnet beyond the drawn mark');
  assert.equal(f.hit(107, 200), null, '5px away is a time, not the event');
  assert.equal(f.hit(116, 200), null, 'the old 14px magnet is gone');
  assert.equal(f.hit(102, 200, { slop: 0 }), edge);
  assert.equal(f.hit(102.5, 200, { slop: 0 }), null);
  assert.equal(f.hit(100, 99.99), null); assert.equal(f.hit(100, 260.01), null);
  assert.equal(f.hit(NaN, 200), null); assert.equal(f.hit(101, Infinity), null);
});

test('a stem reaches from three pixels above its top to four pixels under its baseline', () => {
  const f = fixture();
  const event = f.bar(rect(200, 175, 2, 45));
  assert.equal(BASELINE_SLOP_PX, 4);
  assert.equal(f.hit(201, 224), event);
  assert.equal(f.hit(201, 224.01), null);
  assert.equal(f.hit(205, 229), null, 'nearby bookmark-lane space remains a scrub target');
  assert.equal(f.hit(201, 172), event);
  assert.equal(f.hit(201, 171.99), null, 'above the stem is a time');
  assert.equal(f.hit(197, 172), event, 'the padded corner is still the drawn mark');
  assert.equal(f.hit(196.99, 190), null);
});

test('objective and corrected stems include the cap drawn above them', () => {
  const f = fixture();
  const objective = f.bar(rect(200, 175, 3, 45), null, { extra: ['evbar-objective'] });
  const fixed = f.bar(rect(300, 175, 2, 45), null, { extra: ['evbar-fixed'] });
  f.bar(rect(400, 175, 2, 45));
  assert.equal(f.hit(201, 166), objective);
  assert.equal(f.hit(201, 165.99), null);
  assert.equal(f.hit(301, 166), fixed);
  assert.equal(f.hit(401, 171.99), null, 'a plain stem has no cap allowance');
});

test('the resolution reports the gap to the nearest drawn event and the stem center for a hit', () => {
  const f = fixture();
  const first = f.bar(rect(200, 175, 2, 45), rect(185, 145, 32, 20));
  const second = f.bar(rect(300, 175, 2, 45));
  assert.deepEqual(f.resolve(201, 190), { bar: first, gap: 0, x: 201 });
  assert.deepEqual(f.resolve(310, 190), { bar: null, gap: 8, x: 310 }, 'an off-mark point is a time at the pointer');
  assert.deepEqual(f.resolve(250, 160), { bar: null, gap: 33, x: 250 }, 'labels count toward the gap');
  assert.equal(f.resolve(303, 190).bar, second);
  assert.equal(f.resolve(450, 190).gap, 148);
  assert.deepEqual(f.resolve(450, 50), { bar: null, gap: Infinity, x: NaN }, 'outside the viewport resolves to nothing');
});

test('a hovered label-less bar keeps its revealed code as a target; other bars do not', () => {
  const f = fixture();
  const quiet = f.bar(rect(200, 190, 2, 30), null, { extra: ['evbar-nolabel'] });
  quiet.dataset.code = 'RCL';
  // The code is 7px per letter plus 4px padding a side: 14.5px each way from x 201,
  // from 19px above the stem top (171) down to the stem, padded by three pixels.
  assert.equal(f.hit(201, 180), null, 'without hover the code is not drawn');
  assert.equal(f.hit(201, 180, { sticky: quiet }), quiet);
  assert.equal(f.hit(201 + 14.5 + 3, 172, { sticky: quiet }), quiet);
  assert.equal(f.hit(201 + 14.5 + 3.01, 172, { sticky: quiet }), null);
  assert.equal(f.hit(201, 168, { sticky: quiet }), quiet);
  assert.equal(f.hit(201, 167.99, { sticky: quiet }), null);
  const labeled = f.bar(rect(260, 190, 2, 30));
  assert.equal(f.hit(261, 180, { sticky: labeled }), null, 'only label-less bars reveal a code');
});

test('exact labels beat closer stems even when the visible label is far from its own stem', () => {
  const f = fixture();
  const stemUnderLabel = f.bar(rect(269, 120, 2, 30));
  const labeled = f.bar(rect(300, 190, 2, 30), rect(260, 110, 80, 24));
  assert.equal(f.hit(270, 125), labeled, 'a pointer-events:none label remains the intended target');
  assert.equal(f.hit(257, 120), labeled, 'a label is padded by three pixels on every side');
  assert.equal(f.hit(256.99, 120), null);
  assert.equal(f.hit(250, 120), null, 'beyond the padded label is a time');
  assert.equal(f.hit(270, 125, { target: stemUnderLabel }), stemUnderLabel, 'an actual direct bar hit is authoritative');
  const label = labeled.children[0];
  const child = label.append(f.node({ box: rect(265, 115, 5, 5) }));
  assert.equal(f.hit(266, 116, { target: child }), labeled);
});

test('nearest clustered events win and equal distances use stable DOM order rather than z-index', () => {
  const f = fixture();
  const first = f.bar(rect(200, 175, 2, 45), null, { style: { zIndex: '1' } });
  const second = f.bar(rect(206, 175, 2, 45), null, { style: { zIndex: '999' } });
  assert.equal(f.hit(205, 190), second);
  assert.equal(f.hit(203, 190), first);
  assert.equal(f.hit(204, 190), first, 'equal 2px gaps retain the first event');
  assert.equal(f.hit(211.01, 190), null);
  const one = first.append(f.node({ classes: ['evbar-code'], box: rect(190, 145, 50, 20) }));
  second.append(f.node({ classes: ['evbar-code'], box: one.box }));
  assert.equal(f.hit(215, 150), first, 'overlapping exact labels keep stable ordering');
});

test('fresh transformed bounds follow zoom and pan; clipped stems and labels cannot attract offscreen hits', () => {
  const f = fixture();
  const moved = f.bar(rect(200, 175, 2, 45), rect(185, 145, 32, 20));
  assert.equal(f.hit(203, 190), moved);
  moved.box = rect(360, 155, 4, 65); moved.children[0].box = rect(334, 118, 56, 30);
  assert.equal(f.hit(203, 190), null, 'old untransformed bounds are not cached');
  assert.equal(f.hit(350, 130), moved);
  assert.equal(f.hit(367, 190), moved, 'a zoomed stem keeps the same three-pixel reach');
  assert.equal(f.hit(367.01, 190), null);
  moved.box = rect(98, 175, 2, 45); moved.children[0].box = rect(50, 145, 40, 20);
  assert.equal(f.hit(100, 190), null, 'fully panned-away events are ignored even within the slop');
  const partlyVisible = f.bar(rect(497, 175, 4, 45));
  assert.equal(f.hit(499, 190), partlyVisible);
  assert.equal(f.hit(501, 190), null, 'the seek viewport clips an event spanning its right edge');
  const labelOnly = f.bar(rect(90, 175, 2, 45), rect(85, 145, 30, 20));
  assert.equal(f.hit(105, 150), labelOnly, 'a still-visible code remains clickable when its stem is panned out');
  f.view.innerWidth = 490;
  assert.equal(f.hit(497, 190), null, 'the visible viewport also excludes pixels beyond the window');
});

test('hidden, transparent, detached and zero-sized events are skipped but visible ghost bars participate', () => {
  const f = fixture();
  const ghost = f.bar(rect(200, 175, 2, 45), null, { ghost: true });
  const hidden = f.bar(rect(204, 175, 2, 45), rect(190, 145, 40, 20), { hidden: true });
  f.bar(rect(204, 175, 0, 45));
  f.bar(rect(204, 175, 2, 45), null, { style: { visibility: 'hidden' } });
  f.bar(rect(204, 175, 2, 45), null, { style: { opacity: '0' } });
  const detached = f.bar(rect(204, 175, 2, 45)); detached.isConnected = false;
  assert.equal(f.hit(205, 190), ghost);
  assert.equal(f.hit(205, 150), null, 'a hidden parent also hides its code');
  assert.equal(f.hit(0, 0, { type: 'click', detail: 0, target: hidden }), null);
  ghost.hidden = true;
  assert.equal(f.hit(205, 190), null);
  ghost.hidden = false; f.markers.style.display = 'none';
  assert.equal(f.hit(201, 190), null, 'hidden marker containers suppress all descendants');
});

test('bookmark diamonds, zoom badges and native editing controls retain their own clicks, including descendants', () => {
  const f = fixture(); f.bar(rect(200, 175, 2, 45));
  const protectedNodes = [
    f.node({ id: 'vp-tl-zoom' }), f.node({ classes: ['ev-bm'] }),
    ...['button', 'input', 'select', 'option', 'textarea'].map(tag => f.node({ tag })),
  ];
  const link = f.node({ tag: 'a' }); link.href = '#'; protectedNodes.push(link);
  const editor = f.node(); editor.contentEditable = 'true'; protectedNodes.push(editor);
  for (const control of protectedNodes) {
    f.seek.append(control);
    assert.equal(f.hit(201, 190, { target: control }), null);
    const child = control.append(f.node());
    assert.equal(f.hit(201, 190, { target: child }), null);
  }
});

test('detail-zero clicks use only valid direct bars while hover and contextmenu still use nearby geometry', () => {
  const f = fixture(); const event = f.bar(rect(200, 175, 2, 45), rect(185, 145, 32, 20));
  assert.equal(f.hit(0, 0, { detail: 0, target: event }), event);
  assert.equal(f.hit(0, 0, { detail: 0, target: event.children[0] }), event);
  assert.equal(f.hit(201, 190, { detail: 0 }), null, 'a synthetic scrub click has no inferred event target');
  assert.equal(f.hit(203, 190, { type: 'pointermove', detail: 0 }), event);
  assert.equal(f.hit(203, 190, { type: 'contextmenu', detail: 0 }), event);
  assert.equal(f.hit(208, 190, { type: 'contextmenu', detail: 0 }), null, 'a right-click off the mark opens nothing');
  event.box = rect(30, 175, 2, 45); event.children[0].box = rect(15, 145, 32, 20);
  assert.equal(f.hit(0, 0, { detail: 0, target: event }), null, 'direct synthetic clicks still require a visible event');
  assert.equal(findTimelineEventAtPoint(f.seek, f.node(), { type: 'click', detail: 0, target: event }), null);
});

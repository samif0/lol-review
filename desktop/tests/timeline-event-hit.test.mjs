import test from 'node:test';
import assert from 'node:assert/strict';
import { findTimelineEventAtPoint } from '../ui/timeline-event-hit.mjs';

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
  function bar(stem, label, options = {}) {
    const element = markers.append(node({ classes: ['evbar', ...(options.ghost ? ['evbar-ghost'] : [])], box: stem, ...options }));
    if (label) element.append(node({ classes: ['evbar-code'], box: label, style: { pointerEvents: 'none' } }));
    return element;
  }
  const hit = (x, y, { target = seek, type = 'click', detail = 1, radius = 14 } = {}) =>
    findTimelineEventAtPoint(seek, markers, { clientX: x, clientY: y, target, type, detail }, radius);
  return { node, seek, markers, bar, hit, view };
}

test('visible seek edges constrain hits and the radius measures rendered rectangles in CSS pixels', () => {
  const f = fixture();
  const edge = f.bar(rect(100, 175, 2, 45));
  assert.equal(f.hit(100, 200), edge);
  assert.equal(f.hit(99.99, 200, { target: edge }), null, 'an exact bar target cannot reach outside the viewport');
  assert.equal(f.hit(116, 200), edge, '14px from the stem edge is included');
  assert.equal(f.hit(116.01, 200), null);
  assert.equal(f.hit(106, 200, { radius: 3 }), null);
  assert.equal(f.hit(102, 200, { radius: 0 }), edge);
  assert.equal(f.hit(100, 99.99), null); assert.equal(f.hit(100, 260.01), null);
  assert.equal(f.hit(NaN, 200), null); assert.equal(f.hit(101, Infinity), null);
});

test('near stems extend only four pixels below their baseline so the bookmark and scrub lane stays available', () => {
  const f = fixture();
  const event = f.bar(rect(200, 175, 2, 45));
  assert.equal(f.hit(201, 224), event);
  assert.equal(f.hit(201, 224.01), null);
  assert.equal(f.hit(205, 229), null, 'nearby bookmark-lane space remains a scrub target');
  assert.equal(f.hit(201, 165), event, 'the normal near radius still applies above the stem');
});

test('exact labels beat closer stems even when the visible label is far from its own stem', () => {
  const f = fixture();
  const stemUnderLabel = f.bar(rect(269, 120, 2, 30));
  const labeled = f.bar(rect(300, 190, 2, 30), rect(260, 110, 80, 24));
  assert.equal(f.hit(270, 125), labeled, 'a pointer-events:none label remains the intended target');
  assert.equal(f.hit(250, 120), labeled, 'near-label distance uses its full rectangle, not the stem x position');
  assert.equal(f.hit(270, 125, { target: stemUnderLabel }), stemUnderLabel, 'an actual direct bar hit is authoritative');
  const label = labeled.children[0];
  const child = label.append(f.node({ box: rect(265, 115, 5, 5) }));
  assert.equal(f.hit(266, 116, { target: child }), labeled);
});

test('nearest clustered events win and equal distances use stable DOM order rather than z-index', () => {
  const f = fixture();
  const first = f.bar(rect(200, 175, 2, 45), null, { style: { zIndex: '1' } });
  const second = f.bar(rect(220, 175, 2, 45), null, { style: { zIndex: '999' } });
  assert.equal(f.hit(214, 190), second);
  assert.equal(f.hit(207, 190), first);
  assert.equal(f.hit(211, 190), first, 'equal 9px gaps retain the first event');
  const one = first.append(f.node({ classes: ['evbar-code'], box: rect(190, 145, 50, 20) }));
  second.append(f.node({ classes: ['evbar-code'], box: one.box }));
  assert.equal(f.hit(215, 150), first, 'overlapping exact labels keep stable ordering');
});

test('fresh transformed bounds follow zoom and pan; clipped stems and labels cannot attract offscreen hits', () => {
  const f = fixture();
  const moved = f.bar(rect(200, 175, 2, 45), rect(185, 145, 32, 20));
  assert.equal(f.hit(210, 190), moved);
  moved.box = rect(360, 155, 4, 65); moved.children[0].box = rect(334, 118, 56, 30);
  assert.equal(f.hit(210, 190), null, 'old untransformed bounds are not cached');
  assert.equal(f.hit(350, 130), moved);
  moved.box = rect(70, 175, 2, 45); moved.children[0].box = rect(50, 145, 40, 20);
  assert.equal(f.hit(100, 190), null, 'fully panned-away events are ignored even within the radius');
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
  assert.equal(f.hit(210, 190, { type: 'pointermove', detail: 0 }), event);
  assert.equal(f.hit(210, 190, { type: 'contextmenu', detail: 0 }), event);
  event.box = rect(30, 175, 2, 45); event.children[0].box = rect(15, 145, 32, 20);
  assert.equal(f.hit(0, 0, { detail: 0, target: event }), null, 'direct synthetic clicks still require a visible event');
  assert.equal(findTimelineEventAtPoint(f.seek, f.node(), { type: 'click', detail: 0, target: event }), null);
});

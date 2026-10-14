import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { bookmarkChipText, createTimelineHover, eventChipText, rememberZoomLearned, ZOOM_LEARNED_KEY,
  zoomHintLearned } from '../ui/timeline-hover.mjs';

const clock = seconds => { const s = Math.max(0, Math.floor(seconds || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
const rect = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height });

// A minimal element: enough for the hover to ring, resolve and paint.
function node(box = rect(0, 0, 0, 0), classes = []) {
  const el = { box, classes: new Set(classes), children: [], parentElement: null, hidden: false, isConnected: true,
    dataset: {}, textContent: '', offsetWidth: 0, clientWidth: box.width, style: { setProperty(name, value) { this[name] = value; } },
    classList: {
      contains: name => el.classes.has(name), add: name => el.classes.add(name), remove: name => el.classes.delete(name),
      toggle: (name, force) => ((force ?? !el.classes.has(name)) ? el.classes.add(name) : el.classes.delete(name)),
    },
    getBoundingClientRect: () => el.box,
    append(child) { child.parentElement = el; el.children.push(child); return child; },
    contains: other => other === el || el.children.some(child => child.contains(other)),
    matches: selector => selector.split(',').some(part => part.trim().startsWith('.') && el.classes.has(part.trim().slice(1))),
    closest(selector) { return el.matches(selector) ? el : el.parentElement?.closest(selector) || null; },
    querySelectorAll: selector => el.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]),
    querySelector: selector => el.querySelectorAll(selector)[0] || null,
  };
  return el;
}

function fixture({ height = 124, storage } = {}) {
  const view = { innerWidth: 1200, innerHeight: 800, getComputedStyle: () => ({ display: 'block', visibility: 'visible', opacity: '1' }) };
  const seek = node(rect(100, 100, 600, height));
  seek.ownerDocument = { defaultView: view };
  const mid = 100 + height * 0.72;
  seek.append(node(rect(100, mid - 1, 600, 2), ['track']));
  const markers = seek.append(node(rect(100, 100, 600, height)));
  const bar = markers.append(node(rect(299, mid - 30, 2, 30), ['evbar']));
  bar.dataset = { seconds: '123', label: 'Kill' };
  const chip = seek.append(node()), text = chip.append(node()), hint = chip.append(node());
  chip.hidden = true; hint.hidden = true; chip.offsetWidth = 60;
  const hover = createTimelineHover({ seek, markers, chip, text, hint, clock, zoom: () => 1, storage,
    secondsAt: x => ((x - 100) / 600) * 1800, requestFrame: fn => { fn(); return 0; } });
  const move = (x, y = mid - 10, target = markers) => hover.resolve({ type: 'pointermove', detail: 0, clientX: x, clientY: y, target });
  return { seek, bar, chip, text, hint, hover, move, mid };
}

test('chip words: event label, removed events, bookmarks and clips, with safe fallbacks', () => {
  assert.equal(eventChipText({ dataset: { label: 'Kill', seconds: '867' } }, clock), 'Kill at 14:27');
  assert.equal(eventChipText({ dataset: { label: 'Death', seconds: '260', ghost: '1' } }, clock), 'Removed Death at 4:20');
  assert.equal(eventChipText({ dataset: { code: 'RCL', seconds: '61' } }, clock), 'RCL at 1:01');
  assert.equal(eventChipText({ dataset: {} }, clock), 'Event at 0:00');
  assert.equal(bookmarkChipText({ dataset: { seconds: '323', tone: 'note' } }, clock), 'Bookmark at 5:23');
  assert.equal(bookmarkChipText({ dataset: { seconds: '761', tone: 'clip' } }, clock), 'Clip at 12:41');
});

test('the learned flag survives missing, throwing and full storage without throwing', () => {
  const throwing = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('quota'); } };
  assert.equal(zoomHintLearned(null), false);
  assert.equal(zoomHintLearned(throwing), false);
  assert.doesNotThrow(() => rememberZoomLearned(throwing));
  assert.doesNotThrow(() => rememberZoomLearned(undefined));
  const items = new Map();
  const storage = { getItem: key => items.get(key) ?? null, setItem: (key, value) => items.set(key, value) };
  rememberZoomLearned(storage);
  assert.equal(items.get(ZOOM_LEARNED_KEY), '1');
  assert.equal(zoomHintLearned(storage), true);
  const f = fixture({ storage: throwing });
  f.move(310);
  assert.equal(f.hint.hidden, false, 'unreadable storage still shows the hint');
  assert.doesNotThrow(() => f.hover.noteZoomed());
  assert.equal(f.hint.hidden, true);
  f.move(310);
  assert.equal(f.hint.hidden, true, 'retired in memory even when it cannot be saved');
});

test('the chip shows the exact time and centers on the pointer or the stem', () => {
  const f = fixture({ storage: null });
  f.move(400);
  assert.deepEqual([f.chip.hidden, f.text.textContent, f.chip.style['--hover-x']], [false, '15:00', '270px']);
  f.move(301);
  assert.deepEqual([f.text.textContent, f.chip.style['--hover-x']], ['Kill at 2:03', '170px']);
  assert.equal(f.bar.classes.has('evbar-hit-hover'), true);
  assert.equal(f.seek.classes.has('is-event-hover'), true);
  f.hover.hide();
  assert.deepEqual([f.chip.hidden, f.bar.classes.has('evbar-hit-hover'), f.seek.classes.has('is-event-hover')], [true, false, false]);
});

test('only the clock ticks the chip would touch hide; the others keep full contrast', () => {
  const f = fixture({ storage: null });
  const axis = f.seek.append(node(rect(100, 206, 600, 14)));
  const tick = (left, width) => axis.append(node(rect(left, 206, width, 14), ['vp-tl-tick']));
  const ticks = [tick(100, 26), tick(385, 30), tick(670, 30)];
  const under = () => ticks.map(t => t.classes.has('is-under-hover'));
  f.move(400);
  assert.deepEqual(under(), [false, true, false], 'the chip spans 370 to 430');
  f.move(600);
  assert.deepEqual(under(), [false, false, false]);
  f.move(640);
  assert.deepEqual(under(), [false, false, true], 'a tick within 4px of the chip (610 to 670) hides too');
  assert.equal(f.seek.classes.has('is-hover-chip'), false, 'the clock row never fades as a whole');
  f.hover.hide();
  assert.deepEqual(under(), [false, false, false]);
});

test('on a timeline too short to keep the clock row clear of bookmarks, the chip stays hidden', () => {
  const tall = fixture({ height: 124, storage: null });
  tall.move(400);
  assert.equal(tall.chip.hidden, false);
  const short = fixture({ height: 80, storage: null });
  short.move(400);
  assert.equal(short.chip.hidden, true);
  short.move(301);
  assert.equal(short.bar.classes.has('evbar-hit-hover'), true, 'the ring still shows what a click selects');
});

test('the page carries the chip, its hint copy and the plain seek instructions', async () => {
  const html = await readFile(new URL('../ui/vodplayer.html', import.meta.url), 'utf8');
  assert.match(html, /id="vp-seek" title="Click an event to select it\. Click anywhere else to jump to that time\. Ctrl\+scroll to zoom, drag to pan\."/);
  assert.match(html, /<span class="vp-tl-hover" id="vp-tl-hover" aria-hidden="true" hidden>/);
  assert.match(html, /id="vp-tl-hover-hint" hidden>Ctrl\+scroll to zoom in</);
  const css = await readFile(new URL('../ui/vod-review.css', import.meta.url), 'utf8');
  assert.match(css, /\.vp-tl-hover\{[^}]*pointer-events:none/);
  assert.match(css, /\.vp-tl-tick\.is-under-hover\{visibility:hidden\}/);
  assert.doesNotMatch(css, /\.vp-tl-axis\{[^}]*opacity/, 'the clock row keeps its contrast while the chip shows');
});

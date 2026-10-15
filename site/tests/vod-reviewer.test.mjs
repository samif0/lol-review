// The shared game page's reviewer chrome, shaped like the desktop app's VOD reviewer: the ink
// (vod-ink.js), the transport and seek bar (vod-controls.js), and the timeline's stems and clock
// ticks (vod-lib.js, vod-reviewer.css). The page around them is in the vod-page and vod-live tests.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import { read } from './vod-harness.mjs';

const app = name => readFileSync(new URL(`../../desktop/ui/${name}`, import.meta.url), 'utf8');
// Values made in the scripts' own realm, as plain data for deepEqual.
const plain = value => JSON.parse(JSON.stringify(value));
// Rounded, and -0 (a letterbox offset of -0.00000000000005) is 0.
const round = (n, places = 2) => Math.round(n * 10 ** places) / 10 ** places + 0;
const text = node => [node.textContent || '', ...(node.children || []).map(text)].join('');

const context = { URLSearchParams };
vm.createContext(context);
for (const name of ['vod-lib.js', 'vod-ink.js', 'vod-controls.js']) vm.runInContext(read(name), context);
const { RevuVod: V, RevuVodInk: Ink, RevuVodControls: Controls } = context;

// A small DOM for attach(): elements with classes, attributes, children and listeners, a 2D
// canvas that records what it draws, a video, full screen, and animation frames that run on frame().
function fakePage({ width = 1600, height = 900 } = {}) {
  const frames = [], calls = [], docListeners = new Map();
  const listen = (map, type, fn) => { if (!map.has(type)) map.set(type, []); map.get(type).push(fn); };
  const fire = (map, type, init = {}) => {
    const event = { type, button: 0, pointerId: 1, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...init };
    for (const fn of [...(map.get(type) || [])]) fn(event);
    return event;
  };
  const ctx = { strokeStyle: '', fillStyle: '', lineWidth: 1, globalAlpha: 1, stack: [] };
  for (const name of ['beginPath', 'moveTo', 'lineTo', 'quadraticCurveTo', 'arc', 'rect', 'ellipse', 'fill', 'clearRect']) {
    ctx[name] = (...args) => calls.push([name, ...args]);
  }
  ctx.save = () => ctx.stack.push({ strokeStyle: ctx.strokeStyle, lineWidth: ctx.lineWidth, globalAlpha: ctx.globalAlpha });
  ctx.restore = () => Object.assign(ctx, ctx.stack.pop());
  ctx.stroke = () => calls.push(['stroke', { color: ctx.strokeStyle, width: round(ctx.lineWidth, 1), alpha: ctx.globalAlpha }]);

  function element(tag) {
    const classes = new Set(), attrs = new Map(), listeners = new Map();
    const el = {
      tagName: tag.toUpperCase(), children: [], parentNode: null, hidden: false, disabled: false, title: '', type: '', value: '',
      textContent: '', style: {}, dataset: {}, clientWidth: 0, clientHeight: 0,
      get className() { return [...classes].join(' '); },
      set className(value) { classes.clear(); String(value).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
      classList: {
        add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c),
        toggle: (c, on) => { if (on === undefined ? !classes.has(c) : on) classes.add(c); else classes.delete(c); },
      },
      get firstChild() { return el.children[0] || null; },
      appendChild: child => el.insertBefore(child, null),
      insertBefore(child, before) {
        if (child.parentNode) child.parentNode.removeChild(child);
        const at = el.children.indexOf(before);
        el.children.splice(at < 0 ? el.children.length : at, 0, child);
        child.parentNode = el;
        return child;
      },
      replaceChild(child, old) { el.insertBefore(child, old); return el.removeChild(old); },
      removeChild(child) { el.children = el.children.filter(c => c !== child); child.parentNode = null; return child; },
      querySelector: name => el.children.find(c => c.tagName === name.toUpperCase()) || null,
      closest(selector) { for (let n = el; n; n = n.parentNode) if (n.classList && n.classList.contains(selector.slice(1))) return n; return null; },
      setAttribute: (key, value) => { attrs.set(key, String(value)); },
      getAttribute: key => (attrs.has(key) ? attrs.get(key) : null),
      removeAttribute: key => { attrs.delete(key); },
      addEventListener: (type, fn) => listen(listeners, type, fn),
      dispatch: (type, init = {}) => fire(listeners, type, { target: el, ...init }),
      click: () => el.dispatch('click'),
      getBoundingClientRect: () => ({ left: 0, top: 0, width: el.clientWidth, height: el.clientHeight }),
      setPointerCapture() {},
    };
    if (tag === 'canvas') el.getContext = () => ctx;
    return el;
  }

  const tokens = { '--ink-red': ' #ff5a5f', '--ink-yellow': '#ffd54a' };
  const win = { devicePixelRatio: 2, requestAnimationFrame: fn => frames.push(fn), addEventListener() {},
    getComputedStyle: () => ({ getPropertyValue: name => tokens[name] || '' }) };
  const doc = {
    defaultView: win, documentElement: element('html'), body: element('body'), fullscreenElement: null,
    createElement: element, createElementNS: (ns, tag) => element(tag),
    createTextNode: value => ({ textContent: String(value), children: [], parentNode: null }),
    addEventListener: (type, fn) => listen(docListeners, type, fn),
    exitFullscreen() { doc.fullscreenElement = null; fire(docListeners, 'fullscreenchange'); return Promise.resolve(); },
  };

  const video = element('video');
  let muted = false, rate = 1;
  Object.assign(video, { paused: true, currentTime: 0, duration: 1000, videoWidth: 1920, videoHeight: 1080,
    play() { if (video.paused) { video.paused = false; video.dispatch('play'); } return Promise.resolve(); },
    pause() { if (!video.paused) { video.paused = true; video.dispatch('pause'); } } });
  Object.defineProperty(video, 'muted', { get: () => muted, set: on => { muted = !!on; video.dispatch('volumechange'); } });
  Object.defineProperty(video, 'playbackRate', { get: () => rate, set: r => { rate = r; video.dispatch('ratechange'); } });

  const deck = element('div');
  deck.requestFullscreen = () => { doc.fullscreenElement = deck; fire(docListeners, 'fullscreenchange'); return Promise.resolve(); };
  const stage = Object.assign(element('div'), { clientWidth: width, clientHeight: height });
  const track = Object.assign(element('div'), { clientWidth: 1000, clientHeight: 108 });
  const tip = Object.assign(element('span'), { hidden: true });
  const pointer = (el, type, x, y = 0, init = {}) => el.dispatch(type, { clientX: x, clientY: y, ...init });
  return {
    doc, win, video, deck, stage, track, tip, calls, bar: element('div'), fill: element('span'), playhead: element('span'),
    press: (key, init = {}) => fire(docListeners, 'keydown', { key, target: doc.body, ...init }),
    pointer,
    // pointerdown at the first point, a move to each of the others, pointerup at the last.
    drag(el, points) {
      points.forEach(([x, y], i) => pointer(el, i ? 'pointermove' : 'pointerdown', x, y));
      const [x, y] = points[points.length - 1];
      pointer(el, 'pointerup', x, y);
    },
    frame() { calls.length = 0; frames.splice(0).forEach(fn => fn()); },
  };
}

function transport({ ink = null, started = true } = {}) {
  const run = fakePage();
  Object.assign(run, { started, edge: null, starts: 0 });
  run.handle = Controls.attach({ doc: run.doc, video: run.video, deck: run.deck, bar: run.bar, track: run.track, fill: run.fill,
    playhead: run.playhead, tip: run.tip, ink, length: () => 1000, edge: () => run.edge, started: () => run.started, start: () => { run.starts += 1; } });
  return run;
}

function inkPage(size) {
  const run = fakePage(size);
  run.changes = [];
  run.ink = Ink.attach({ doc: run.doc, video: run.video, stage: run.stage, onChange: s => run.changes.push(plain(s)) });
  [run.canvas, run.inkBar] = run.stage.children;
  Object.assign(run.canvas, { clientWidth: run.stage.clientWidth, clientHeight: run.stage.clientHeight });
  return run;
}

test('ink geometry: strokes live in the picture\'s own coordinates, letterbox and all', () => {
  const box = r => Object.values(plain(r)).map(n => round(n));
  assert.deepEqual(box(Ink.frameRect(1000, 1000, 1920, 1080)), [0, 218.75, 1000, 562.5], 'bars above and below');
  assert.deepEqual(box(Ink.frameRect(1600, 600, 1920, 1080)), [266.67, 0, 1066.67, 600], 'bars left and right');
  assert.deepEqual(box(Ink.frameRect(800, 450, 0, 0)), [0, 0, 800, 450], 'before the video size is known: the whole element');
  assert.deepEqual(box(Ink.frameRect(0, 450, 1920, 1080)), [0, 0, 0, 0]);
  const rect = Ink.frameRect(1000, 1000, 1920, 1080);
  assert.deepEqual(plain(Ink.toFrame(500, 500, rect)).map(n => round(n, 4)), [0.5, 0.5]);
  assert.deepEqual(plain(Ink.toFrame(-20, 100, rect)), [0, 0], 'a point on a bar lands on the picture\'s edge');
  assert.deepEqual(plain(Ink.toFrame(1200, 900, rect)), [1, 1]);
  assert.equal(Ink.toFrame(5, 5, { x: 0, y: 0, w: 0, h: 0 }), null);
});

test('the eraser takes a stroke it touches: along a pen line, the arrow, a box or circle outline, a dot', () => {
  const stroke = (tool, points) => ({ tool, size: 0.008, points });
  const hit = (s, p) => Ink.hitStroke(s, p, 0.02);
  const pen = stroke('pen', [[0.1, 0.1], [0.3, 0.1], [0.5, 0.2]]);
  assert.deepEqual([hit(pen, [0.3, 0.115]), hit(pen, [0.45, 0.18]), hit(pen, [0.3, 0.2])], [true, true, false]);
  const arrow = stroke('arrow', [[0, 0.5], [1, 0.5]]);
  assert.deepEqual([hit(arrow, [0.7, 0.51]), hit(arrow, [0.7, 0.6])], [true, false]);
  const box = stroke('rect', [[0.2, 0.2], [0.6, 0.6]]);
  assert.deepEqual([hit(box, [0.4, 0.21]), hit(box, [0.59, 0.4]), hit(box, [0.4, 0.4])], [true, true, false], 'the outline, not the inside');
  const circle = stroke('ellipse', [[0.2, 0.2], [0.6, 0.6]]);
  assert.deepEqual([hit(circle, [0.6, 0.4]), hit(circle, [0.4, 0.4]), hit(circle, [0.2, 0.2])], [true, false, false], 'not its box\'s corner');
  const dot = stroke('pen', [[0.5, 0.5]]);
  assert.deepEqual([hit(dot, [0.51, 0.5]), hit(dot, [0.6, 0.5])], [true, false]);
});

test('an arrowhead\'s two corners sit the head\'s length back from the tip, mirrored across the shaft', () => {
  const [left, right] = plain(Ink.arrowHead([0, 0], [100, 0], 10));
  assert.deepEqual([left, right].map(([x, y]) => round(Math.hypot(x - 100, y))), [10, 10]);
  assert.ok(left[0] < 100 && right[0] < 100, 'behind the tip');
  assert.equal(round(left[1] + right[1], 9), 0);
  assert.ok(Math.abs(left[1]) > 3, 'spread apart');
});

test('transport math: the clock, the app\'s step and speed lists, and seeks that stop at the uploaded edge', () => {
  assert.deepEqual([0, 59.9, 61, 3725, -5, 'x'].map(s => Controls.clock(s)), ['0:00', '0:59', '1:01', '1:02:05', '0:00', '0:00']);
  const transportJs = app('vodtransport.js');
  const list = name => JSON.parse(transportJs.match(new RegExp(`export const ${name} = (\\[[^\\]]*\\])`))[1]);
  assert.deepEqual(plain(Controls.STEP_CHOICES), list('STEP_CHOICES'));
  assert.deepEqual(plain(Controls.RATE_CHOICES), list('RATE_CHOICES'));
  assert.deepEqual([[2, 1], [4, 1], [0, -1], [3, -1]].map(([i, d]) => Controls.nextIndex(i, d, 5)), [3, 4, 0, 2]);
  assert.equal(Controls.seekTarget(500, 1000, 120), 119.5, 'half a second short of the uploaded edge');
  assert.equal(Controls.seekTarget(500, 1000, null), 500);
  assert.equal(Controls.seekTarget(2000, 1000, null), 1000);
  assert.equal(Controls.seekTarget(-3, 1000, 120), 0);
  assert.equal(Controls.seekTarget(100, 1000, 0.2), 0, 'nothing uploaded yet: the start');
  assert.equal(Controls.seekTarget(42, 0, null), 42, 'no length yet: only the start bounds it');
});

test('timeline stems take the app\'s colors, codes and heights by kind; a code too close to the last one waits for the hover chip', () => {
  const kinds = ['KILL', 'DEATH', 'ASSIST', 'FIRST_BLOOD', 'MULTI_KILL', 'DRAGON', 'baron', 'HERALD', 'TURRET', 'INHIBITOR',
    'FLASH', 'SUMMONER_SPELL', 'LEVEL_UP', 'RECALL'];
  assert.deepEqual(kinds.map(kind => Object.values(plain(V.eventStyle(kind))).join(' ')), [
    'kill KIL kill', 'death DTH kill', 'assist AST kill', 'kill FB kill', 'kill MK kill', 'dragon DRG major', 'void BAR major',
    'void HLD major', 'structure TWR major', 'structure INH major', 'summoner FLS summoner', 'summoner SUM summoner',
    'structure LVL minor', 'recall RCL recall']);
  assert.deepEqual(plain(V.eventStyle('WARD_KILL', 'gold')), { tone: 'dragon', code: 'WAR', tier: 'minor' }, 'an unknown kind keeps the sharer\'s tone');
  assert.deepEqual(plain(V.eventStyle('', 'nope')), { tone: 'structure', code: '', tier: 'minor' });

  const doc = { version: 1, gameTimeAtVideoStart: 0, notes: [{ t: 300, kind: 'clip', text: 'Back here' }],
    markers: [{ t: 100, kind: 'KILL', label: 'Kill' }, { t: 120, kind: 'DEATH', label: 'Death' }, { t: 200, kind: 'DRAGON', label: 'Dragon' }] };
  assert.deepEqual(plain(V.laneItems(doc, 1000)).map(i => [i.code, i.tier, i.labeled]),
    [['KIL', 'kill', true], ['DTH', 'kill', false], ['DRG', 'major', true], ['', 'note', false]]);
  const rows = { version: 1, gameTimeAtVideoStart: 0, markers: [{ t: 100, kind: 'KILL' }, { t: 110, kind: 'DRAGON' },
    { t: 150, kind: 'TURRET' }, { t: 210, kind: 'DEATH' }, { t: 300, kind: 'LEVEL_UP' }] };
  assert.deepEqual(plain(V.laneItems(rows, 1000)).map(i => i.code + (i.labeled ? '' : '-')), ['KIL', 'DRG', 'TWR-', 'DTH', 'LVL-'],
    'objectives have a row of their own above the rest; minor stems carry no code');
  assert.deepEqual(plain(V.laneItems(rows, 1000, null, 12)).map(i => i.labeled), [true, true, false, false, false], 'a narrow lane needs more room');

  const css = read('vod-reviewer.css');
  const styles = kinds.map(kind => V.eventStyle(kind));
  for (const tone of new Set([...styles.map(s => s.tone), 'clip', 'note'])) {
    if (tone !== 'structure') assert.match(css, new RegExp(`\\.vod-mark\\.is-${tone}\\b[^{]*\\{ --evc: var\\(--`), tone);
  }
  assert.match(css, /\.vod-mark,\s*\.vod-event-dot \{\s*--evc: var\(--ev-structure, var\(--t3\)\);/, 'structure is the default color');
  // Every kind's color falls back to a token tokens.css had before the timeline's own, so a page
  // that meets a cached tokens.css still draws its stems in color.
  for (const rule of css.matchAll(/--evc: (var\([^;]*\));/g)) assert.match(rule[1], /^var\(--(?:ev|mk)-[a-z]+, var\(--(?:t1|t2|t3|win|loss|pending|focus)\)\)$/, rule[1]);
  assert.match(css, /\.vod-mark::before \{[^}]*background: var\(--evc, var\(--t3\)\);/, 'a stem never goes transparent');
  for (const tier of new Set([...styles.map(s => s.tier), 'note'])) assert.match(css, new RegExp(`\\.vod-mark\\.tier-${tier} \\{`), tier);
  assert.match(css, /\.vod-mark\.is-unlabeled:hover::after/, 'a code with no room shows on hover');
});

test('the timeline colors are the app\'s Neutral dark ones, value for value', () => {
  const pairs = block => Object.fromEntries([...block.matchAll(/(--[\w-]+)\s*:\s*([^;}]+)/g)].map(m => [m[1], m[2].trim()]));
  const neutral = pairs(app('appearance.css').replace(/\/\*[\s\S]*?\*\//g, '').match(/\[data-theme="neutral"\]\{([^}]*)\}/)[1]);
  const ours = pairs(read('tokens.css').match(/:root\{([^}]*--ev-kill[^}]*)\}/)[1]);
  assert.deepEqual(Object.keys(ours), ['--ev-kill', '--ev-assist', '--ev-death', '--ev-dragon', '--ev-void', '--ev-structure',
    '--ev-recall', '--ev-summoner', '--mk-clip', '--mk-note', '--playhead', '--track']);
  // The app's summoner color is its calm blue.
  for (const [name, value] of Object.entries(ours)) assert.equal(value, neutral[name === '--ev-summoner' ? '--calm' : name], name);
});

test('axis ticks under the lane: every 5 minutes, every 10 from a 20-minute game on, in game time', () => {
  const ticks = (length, offset) => plain(V.axisTicks(length, offset)).map(t => `${t.label}@${round(t.left)}`);
  assert.deepEqual(ticks(1000, 0), ['0:00@0', '5:00@30', '10:00@60', '15:00@90']);
  assert.deepEqual(ticks(1800, 0), ['0:00@0', '10:00@33.33', '20:00@66.67', '30:00@100']);
  assert.deepEqual(ticks(1000, 75), ['5:00@22.5', '10:00@52.5', '15:00@82.5'], 'the video starts 1:15 into the game');
  assert.deepEqual(ticks(1000, -30), ['0:00@3', '5:00@33', '10:00@63', '15:00@93'], 'the video starts before the game clock');
  assert.deepEqual(ticks(0, 0), []);
});

test('the transport is the app\'s: step back, play, step forward, the time with the step, speed, mute, the pen and full screen', () => {
  const run = transport();
  const bar = run.bar.children;
  assert.deepEqual(bar.map(el => el.className), ['tbtn', 'tbtn play', 'tbtn', 'ttime', 'tspring', 'vp-rate-sel', 'tbtn', 'tbtn tpen', 'tbtn']);
  assert.deepEqual(bar.map(el => el.getAttribute('aria-label')),
    ['Back one step', 'Play', 'Forward one step', null, null, 'Playback speed', 'Mute', 'Draw on the video', 'Full screen']);
  assert.deepEqual(bar.map(el => el.children.find(c => c.tagName === 'KBD')?.textContent ?? null),
    ['←', null, '→', null, null, null, null, 'D', 'F'], 'key hints, as in the app');
  assert.ok(bar.filter(el => el.className.startsWith('tbtn')).every(b => b.children[0].tagName === 'SVG'), 'each button leads with its icon');
  assert.equal(bar[7].hidden, true, 'no pen without the ink');
  assert.deepEqual(bar[5].children.map(o => o.textContent), ['0.25', '0.5', '1', '1.5', '2', '3'].map(v => `${v}×`));
  assert.equal(text(bar[3]), '0:00 / 16:40step 5s');
  assert.equal(bar[3].children[1].className, 'tstep');
  assert.ok(bar.filter(el => el.className.startsWith('tbtn')).every(b => b.dispatch('mousedown').defaultPrevented),
    'a click leaves the keyboard where it was, so Space still plays');
});

test('the app\'s keys: Space and K play, the arrows step, Up and Down change the step, - and + the speed, M, F and frame steps', () => {
  const run = transport();
  const [, play, , time, , rate, mute, , full] = run.bar.children;
  assert.equal(run.press(' ').defaultPrevented, true);
  assert.deepEqual([run.video.paused, play.getAttribute('aria-label')], [false, 'Pause']);
  run.press('k');
  assert.equal(run.video.paused, true);
  run.press('ArrowRight');
  assert.equal(run.video.currentTime, 5);
  run.press('ArrowUp');
  assert.equal(time.children[1].textContent, 'step 10s');
  run.press('ArrowRight');
  run.press('ArrowRight');
  run.press('ArrowLeft');
  assert.equal(run.video.currentTime, 15);
  for (let i = 0; i < 6; i += 1) run.press('ArrowDown');
  assert.equal(time.children[1].textContent, 'step 1s', 'the smallest step');
  run.press('=');
  assert.deepEqual([run.video.playbackRate, rate.value], [1.5, '1.5']);
  for (let i = 0; i < 6; i += 1) run.press('-');
  assert.deepEqual([run.video.playbackRate, rate.value], [0.25, '0.25'], 'the slowest');
  run.press('m');
  assert.deepEqual([run.video.muted, mute.getAttribute('aria-label')], [true, 'Unmute']);
  run.video.play();
  run.press('.');
  assert.equal(run.video.paused, true, 'a frame step pauses');
  assert.equal(round(run.video.currentTime, 4), round(15 + 1 / 60, 4));
  run.press('f');
  assert.equal(run.doc.fullscreenElement, run.deck, 'the deck goes full screen as one');
  assert.deepEqual([run.deck.classList.contains('is-full'), full.getAttribute('aria-label')], [true, 'Exit full screen']);
  run.press('F');
  assert.deepEqual([run.deck.classList.contains('is-full'), full.getAttribute('aria-label')], [false, 'Full screen']);
  assert.equal(run.press('x').defaultPrevented, false, 'other keys are left alone');
});

test('typing in a field, a modifier, or Space on a focused button is not a transport key', () => {
  const run = transport();
  for (const tag of ['input', 'select', 'textarea']) run.press('ArrowRight', { target: run.doc.createElement(tag) });
  run.press('ArrowRight', { ctrlKey: true });
  assert.equal(run.video.currentTime, 0);
  assert.equal(run.press(' ', { target: run.doc.createElement('button') }).defaultPrevented, false, 'the button takes its own Space');
  assert.equal(run.video.paused, true);
});

test('before the first play the transport presses the page\'s Play; the lane seeks and drags, short of the uploaded edge', () => {
  const run = transport({ started: false });
  run.bar.children[1].click();
  run.press(' ');
  assert.equal(run.starts, 2, 'the page\'s own Play mints the token and counts the play');
  assert.equal(run.video.paused, true);
  run.press('ArrowRight');
  run.pointer(run.track, 'pointerdown', 500);
  assert.equal(run.video.currentTime, 0, 'nothing to seek yet');

  Object.assign(run, { started: true, edge: 120 });
  run.pointer(run.track, 'pointerdown', 500);
  assert.equal(run.video.currentTime, 119.5, 'the middle of the game has not uploaded: as far as it goes');
  assert.deepEqual([run.tip.hidden, run.tip.textContent, run.tip.style.left], [false, '8:20', '50%']);
  run.pointer(run.track, 'pointermove', 100);
  assert.deepEqual([run.video.currentTime, run.fill.style.width, run.playhead.style.left], [100, '10%', '10%'], 'a drag seeks as it goes');
  run.pointer(run.track, 'pointerup', 100);
  run.pointer(run.track, 'pointermove', 900);
  assert.deepEqual([run.video.currentTime, run.tip.textContent], [100, '15:00'], 'a hover only moves the chip');
  run.pointer(run.track, 'pointerleave', 900);
  assert.equal(run.tip.hidden, true);

  const mark = run.doc.createElement('button');
  mark.className = 'vod-mark';
  mark.setAttribute('aria-label', 'Dragon at 5:12');
  run.track.appendChild(mark);
  run.pointer(run.track, 'pointermove', 300, 0, { target: mark });
  assert.deepEqual([run.tip.hidden, run.tip.textContent], [false, 'Dragon at 5:12'], 'on a moment the chip names it, as in the app');
  run.pointer(run.track, 'pointerdown', 300, 0, { target: mark });
  assert.equal(run.video.currentTime, 100, 'a moment\'s own button jumps, not the bar');
  run.edge = null;
  run.press('ArrowUp');
  run.video.currentTime = 998;
  run.press('ArrowRight');
  assert.equal(run.video.currentTime, 1000, 'the end of the game');
});

test('a click on the picture plays and pauses unless the pen is out; D and the pen button take the pen out', () => {
  const pen = { out: false, toggles: 0, layouts: 0, active() { return this.out; }, toggle() { this.toggles += 1; },
    key: ev => ev.key === 'e', layout() { this.layouts += 1; } };
  const run = transport({ ink: pen });
  run.video.click();
  assert.equal(run.video.paused, false);
  run.video.click();
  assert.equal(run.video.paused, true);
  pen.out = true;
  run.video.click();
  assert.equal(run.video.paused, true, 'a click while drawing is a stroke, not play');
  const button = run.bar.children[7];
  assert.equal(button.hidden, false);
  button.click();
  run.press('d');
  assert.equal(pen.toggles, 2);
  assert.equal(run.press('e').defaultPrevented, true, 'the pen\'s own keys go to the pen first');
  run.handle.penPressed(true);
  assert.deepEqual([button.getAttribute('aria-pressed'), run.deck.classList.contains('is-drawing')], ['true', true]);
  run.handle.penPressed(false);
  assert.deepEqual([button.getAttribute('aria-pressed'), run.deck.classList.contains('is-drawing')], ['false', false]);
  run.press('f');
  assert.equal(pen.layouts, 1, 'full screen redraws the ink at its new size');
});

test('the pen\'s palette: six tools, five colors, three sizes, undo, clear, keep and done, hidden until the pen is out', () => {
  const run = inkPage();
  assert.deepEqual([run.canvas.className, run.canvas.getAttribute('aria-hidden')], ['vod-ink', 'true']);
  assert.deepEqual([run.inkBar.getAttribute('role'), run.inkBar.hidden], ['toolbar', true]);
  const [tools, colors, sizes, actions, done] = run.inkBar.children;
  const labels = el => el.children.map(b => b.getAttribute('aria-label'));
  assert.deepEqual(labels(tools), ['Pen', 'Highlighter', 'Arrow', 'Box', 'Circle', 'Eraser']);
  assert.deepEqual(labels(colors), ['Red', 'Yellow', 'Green', 'Blue', 'White']);
  assert.deepEqual(labels(sizes), ['Thin', 'Medium', 'Thick']);
  assert.deepEqual(labels(actions), ['Undo', 'Clear ink', 'Keep ink while the video plays']);
  assert.equal(done.getAttribute('aria-label'), 'Stop drawing');
  assert.ok([...tools.children, ...colors.children, ...sizes.children, ...actions.children, done].every(b => b.dispatch('mousedown').defaultPrevented),
    'a click leaves the keyboard where it was, so Space still plays');
  const pressed = el => el.children.map(b => b.getAttribute('aria-pressed'));
  assert.deepEqual(pressed(tools), ['true', 'false', 'false', 'false', 'false', 'false']);
  assert.deepEqual(pressed(colors), ['true', 'false', 'false', 'false', 'false']);
  assert.deepEqual(pressed(sizes), ['false', 'true', 'false']);
  assert.deepEqual([actions.children[0].disabled, actions.children[1].disabled], [true, true], 'nothing to undo or clear');
  run.ink.setActive(true);
  assert.deepEqual([run.inkBar.hidden, run.canvas.classList.contains('is-active')], [false, true]);
  assert.deepEqual(run.changes.at(-1), { active: true, hasInk: false });
  assert.deepEqual([run.canvas.width, run.canvas.height], [3200, 1800], 'sharp on a 2x screen');
  done.click();
  assert.deepEqual([run.inkBar.hidden, run.ink.active()], [true, false]);
});

test('drawing pauses the game; undo, the eraser, clear; playing on clears the ink unless it is kept', () => {
  const run = inkPage();
  const { ink, canvas, video } = run;
  const [undo, clear, keep] = run.inkBar.children[3].children;
  run.drag(canvas, [[400, 450], [1200, 450]]);
  assert.equal(ink.state().strokes, 0, 'the canvas takes no strokes while the pen is away');
  ink.setActive(true);
  video.play();
  run.drag(canvas, [[400, 450], [800, 450], [800.5, 450], [1200, 450]]);
  assert.equal(video.paused, true, 'a stroke pauses the game');
  assert.equal(ink.state().strokes, 1);
  assert.deepEqual(run.changes.at(-1), { active: true, hasInk: true });
  run.drag(canvas, [[400, 200], [1200, 200]]);
  undo.click();
  assert.equal(ink.state().strokes, 1);
  ink.key({ key: 'e' });
  run.drag(canvas, [[800, 455]]);
  assert.equal(ink.state().strokes, 0, 'the eraser takes the stroke it touches');
  ink.key({ key: 'z', ctrlKey: true });
  assert.equal(ink.state().strokes, 1, 'undo brings it back');
  clear.click();
  assert.equal(ink.state().strokes, 0);
  undo.click();
  assert.equal(ink.state().strokes, 1);

  video.play();
  assert.equal(ink.state().strokes, 0, 'playing on clears the ink');
  video.pause();
  undo.click();
  keep.click();
  assert.equal(keep.getAttribute('aria-pressed'), 'true');
  video.play();
  assert.equal(ink.state().strokes, 1, 'kept while the game plays');
});

test('shapes need a drag; the keys pick tools, colors and sizes, and Escape puts the pen away', () => {
  const run = inkPage();
  const { ink, canvas } = run;
  assert.equal(ink.key({ key: 'r' }), false, 'no pen keys while the pen is away');
  ink.setActive(true);
  assert.equal(ink.key({ key: 'r' }), true);
  run.drag(canvas, [[500, 300]]);
  assert.equal(ink.state().strokes, 0, 'a click is not a box');
  run.drag(canvas, [[500, 300], [700, 400], [900, 600]]);
  assert.equal(ink.state().strokes, 1);
  for (const [key, field, value] of [['h', 'tool', 'highlighter'], ['a', 'tool', 'arrow'], ['o', 'tool', 'ellipse'], ['p', 'tool', 'pen'],
    ['3', 'color', 'green'], ['5', 'color', 'white'], [']', 'size', 2], [']', 'size', 2], ['[', 'size', 1]]) {
    assert.equal(ink.key({ key }), true, key);
    assert.equal(ink.state()[field], value, key);
  }
  ink.key({ key: 'e' });
  ink.key({ key: '1' });
  assert.deepEqual([ink.state().tool, ink.state().color], ['pen', 'red'], 'picking a color puts the eraser down');
  assert.equal(ink.key({ key: 'c' }), true);
  assert.equal(ink.state().strokes, 0);
  assert.equal(ink.key({ key: 'k' }), false, 'not a pen key: the transport has it');
  assert.equal(ink.key({ key: 'Escape' }), true);
  assert.equal(ink.active(), false);
});

test('ink is drawn on the picture, not its bars: sharp, in the ink colors, the highlighter three times wide and see-through', () => {
  const run = inkPage({ width: 1600, height: 1200 });
  const { ink, canvas } = run;
  ink.setActive(true);
  run.drag(canvas, [[0, 0], [1600, 1200]]);
  run.frame();
  // A 16:9 picture in a 4:3 box: 150 px bars, so the corners of the box are the picture's corners.
  assert.deepEqual(plain(run.calls.filter(c => ['moveTo', 'lineTo', 'stroke'].includes(c[0]))),
    [['moveTo', 0, 300], ['lineTo', 3200, 2100], ['stroke', { color: '#ff5a5f', width: 14.4, alpha: 1 }]]);
  ink.key({ key: 'h' });
  ink.key({ key: '2' });
  run.drag(canvas, [[0, 600], [1600, 600]]);
  run.frame();
  assert.deepEqual(plain(run.calls.filter(c => c[0] === 'stroke')).map(c => c[1]),
    [{ color: '#ff5a5f', width: 14.4, alpha: 1 }, { color: '#ffd54a', width: 43.2, alpha: 0.35 }]);
});

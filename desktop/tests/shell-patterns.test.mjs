import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createPatternNudge, nudgeCount, nudgeLabel, nudgeLiveChange, NUDGE_THROTTLE_MS, NUDGE_POLL_MS } from '../ui/shell-patterns.mjs';

// The Patterns count in the sidebar: a quiet nudge that the sidecar decides. It shows
// the ready count with screen-reader text, hides on any failure, hides at once in champ
// select or a game, and clears without a reload when a review is finished.

const ui = new URL('../ui/', import.meta.url);
const shell = await readFile(new URL('index.html', ui), 'utf8');
const outer = await readFile(new URL('shell-outer.js', ui), 'utf8');
const ready = { notify: true, readyCount: 1, patternKey: 'bad_objective_evidence:obj3', title: 'Track the jungler',
  momentCount: 4, gameCount: 3, quietReason: '', errorText: '' };

function el() { return { textContent: '', hidden: true }; }
function harness(reply = ready) {
  const els = { 'nav-patterns-count': el(), 'nav-patterns-count-text': el() };
  const docListeners = {}, winListeners = {}, timers = [], reads = [];
  let clock = 1_000_000, live = null;
  const source = { reply };
  const doc = { visibilityState: 'visible', getElementById: id => els[id] || null,
    addEventListener: (type, fn) => { docListeners[type] = fn; } };
  const nudge = createPatternNudge({
    doc, win: { addEventListener: (type, fn) => { winListeners[type] = fn; } },
    read: async (command, sample) => {
      reads.push([command, sample]);
      if (source.reply instanceof Error) throw source.reply;
      return structuredClone(source.reply);
    },
    getListen: async () => async (name, fn) => { assert.equal(name, 'lcu-event'); live = fn; },
    now: () => clock,
    schedule: (fn, ms) => timers.push({ fn, ms }),
    cancel: id => { timers[id - 1].fn = null; },
  });
  const settle = async () => { for (let i = 0; i < 4; i++) await new Promise(resolve => setImmediate(resolve)); };
  return {
    els, nudge, reads, timers, source, settle, docListeners, winListeners,
    tick: ms => { clock += ms; },
    emit: (type, payload = {}) => live({ payload: { type, payload } }),
    due: async ms => { timers.filter(t => t.fn && t.ms === ms).forEach(t => { const fn = t.fn; t.fn = null; fn(); }); await settle(); },
    shown: () => (els['nav-patterns-count'].hidden ? '' : els['nav-patterns-count'].textContent),
  };
}

test('the reply decides the count; a held, empty or failed check shows none', () => {
  assert.equal(nudgeCount(ready), 1);
  assert.equal(nudgeCount({ ...ready, readyCount: 2 }), 2);
  assert.equal(nudgeCount({ ...ready, notify: false, quietReason: 'recent_review' }), 0);
  assert.equal(nudgeCount({ ...ready, errorText: "Couldn't check your patterns." }), 0);
  assert.equal(nudgeCount({ ...ready, readyCount: -1 }), 0);
  assert.equal(nudgeCount(null), 0);
  assert.equal(nudgeLabel(1), '1 pattern ready to review');
  assert.equal(nudgeLabel(3), '3 patterns ready to review');
});

test('the count shows with screen-reader text, and finishing a review clears it without a reload', async () => {
  const h = harness();
  h.nudge.start();
  await h.settle();
  assert.deepEqual(h.reads, [['get_pattern_nudge', 'sample-pattern-nudge.json']]);
  assert.equal(h.shown(), '1');
  assert.equal(h.els['nav-patterns-count-text'].textContent, '1 pattern ready to review');

  // The Patterns page announces a finished review; the sidecar is now in its quiet period.
  h.source.reply = { ...ready, notify: false, quietReason: 'recent_review' };
  h.winListeners['revu:patterns-changed']();
  await h.settle();
  assert.equal(h.reads.length, 2, 'asks again at once, without waiting for the throttle');
  assert.equal(h.shown(), '');
  assert.equal(h.els['nav-patterns-count-text'].textContent, '');
});

test('a failed read hides the count instead of keeping a stale one', async () => {
  const h = harness();
  await h.nudge.refresh();
  assert.equal(h.shown(), '1');
  h.source.reply = new Error('sidecar restarting');
  await h.nudge.refresh();
  assert.equal(h.shown(), '');
});

test('champ select and games hide the count at once; their end asks again', async () => {
  const h = harness();
  h.nudge.start();
  await h.settle();
  assert.equal(h.shown(), '1');

  h.emit('champSelectStarted');
  assert.equal(h.shown(), '', 'hidden before any read');
  // A read that was already in flight cannot bring it back mid-lobby.
  await h.nudge.refresh();
  assert.equal(h.shown(), '');

  h.emit('champSelectCancelled');
  await h.due(600);
  assert.equal(h.shown(), '1');

  h.emit('gameInProgress');
  assert.equal(h.shown(), '');
  h.emit('liveState', { isGameInProgress: true });
  await h.due(600);
  assert.equal(h.shown(), '');
  h.emit('gameEnded', { saved: true });
  await h.due(600);
  assert.equal(h.shown(), '1');
});

test('closing the League client or a recovered game end clears a lobby the count was waiting on', async () => {
  const h = harness();
  h.nudge.start();
  await h.settle();
  h.emit('champSelectStarted');
  assert.equal(h.shown(), '');
  // Dodging by closing League sends no cancel; the client disconnecting ends the lobby.
  h.emit('lcuConnection', { connected: false });
  await h.due(600);
  assert.equal(h.shown(), '1', 'the sidecar decides again');
  h.emit('lcuConnection', { connected: true });
  await h.settle();
  assert.equal(h.reads.length, 2, 'a connection alone asks nothing');

  h.emit('gameInProgress');
  assert.equal(h.shown(), '');
  h.emit('gameRecovered', { saved: true });
  await h.due(600);
  assert.equal(h.shown(), '1');
  // Held back by the sidecar (the player is in the next lobby): still hidden.
  h.source.reply = { ...ready, notify: false, quietReason: 'live' };
  h.emit('gameRecovered', { saved: true });
  await h.due(600);
  assert.equal(h.shown(), '');
});

test('live events map to hide, ask or nothing', () => {
  const change = (type, payload) => nudgeLiveChange({ type, payload });
  assert.equal(change('champSelectStarted'), 'hide');
  assert.equal(change('gameInProgress'), 'hide');
  assert.equal(change('liveState', { isGameInProgress: true }), 'hide');
  assert.equal(change('liveState', { isGameInProgress: false }), 'ask');
  for (const type of ['champSelectCancelled', 'gameEnded', 'gameRecovered']) assert.equal(change(type), 'ask', type);
  assert.equal(change('lcuConnection', { connected: false }), 'ask');
  assert.equal(change('lcuConnection', { connected: true }), '');
  assert.equal(change('champSelectUpdated'), '');
  assert.equal(nudgeLiveChange(null), '');
});

test('navigation asks at most once per throttle window, with one trailing read', async () => {
  const h = harness();
  await h.nudge.refresh();
  h.nudge.frameLoaded();
  await h.settle();
  assert.equal(h.reads.length, 1, 'within the window: no new read');
  const trailing = h.timers.filter(t => t.fn);
  assert.equal(trailing.length, 1);
  assert.equal(trailing[0].ms, NUDGE_THROTTLE_MS);
  h.tick(NUDGE_THROTTLE_MS);
  h.nudge.frameLoaded();
  await h.settle();
  assert.equal(h.reads.length, 2, 'after the window: reads right away');
});

test('a slow re-check lets the quiet period end on its own', async () => {
  const h = harness({ ...ready, notify: false, quietReason: 'recent_review' });
  h.nudge.start();
  await h.settle();
  assert.equal(h.shown(), '');
  h.source.reply = ready;
  await h.due(NUDGE_POLL_MS);
  assert.equal(h.shown(), '1');
  assert.equal(h.timers.filter(t => t.fn && t.ms === NUDGE_POLL_MS).length, 1, 'keeps polling');
});

test('the shell markup carries the Patterns count like the Match library count', () => {
  const link = shell.match(/<a class="nav-i" data-nav="patterns"[^>]*>[\s\S]*?<\/a>/)[0];
  assert.match(link, /aria-label="Patterns" aria-describedby="nav-patterns-count-text"/);
  assert.match(link, /<span class="nav-count" id="nav-patterns-count" aria-hidden="true" hidden><\/span>/);
  assert.match(link, /<span class="side-sr" id="nav-patterns-count-text"><\/span>/);
  assert.match(outer, /import \{ createPatternNudge \} from '\.\/shell-patterns\.mjs';/);
  assert.match(outer, /patternNudge\.frameLoaded\(\);/);
  assert.match(outer, /patternNudge\.start\(\);/);
  return readFile(new URL('shell-patterns.mjs', ui), 'utf8').then(code => {
    const source = code.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
    // Reads only: no navigation, no frame access, no notifications of any kind.
    assert.doesNotMatch(source, /__revuActiveWork|contentWindow|location\.|Notification|showNotification|tray/i);
    assert.doesNotMatch(code, /[–—]/);
  });
});

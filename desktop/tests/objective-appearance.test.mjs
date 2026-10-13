import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { TRACKABLE_EVENT_TOKENS } from '../ui/objective-labels.mjs';
import { polarityTone, tokenTone, toneFromHex } from '../ui/meaning-tone.mjs';

// The learning pages under the appearance themes: meaning colors travel as data-tone
// (the theme maps them), progress stays monochrome, and the player's own words carry
// .own-words so the Journal theme can set them in its serif voice.
const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
const template = (html, id) => html.match(new RegExp(`<template id="${id}">([\\s\\S]*?)</template>`))[1];

// A minimal element: every selector resolves to its own child node.
function node(tag = 'div') {
  const children = {};
  const classes = new Set();
  const el = { tagName: tag.toUpperCase(), dataset: {}, style: { setProperty(k, v) { this[k] = v; } }, attributes: {},
    hidden: false, textContent: '', title: '', className: '', kids: [],
    classList: { add: (...names) => names.forEach(c => classes.add(c)), remove: (...names) => names.forEach(c => classes.delete(c)),
      toggle: (c, on) => ((on ?? !classes.has(c)) ? classes.add(c) : classes.delete(c)), contains: c => classes.has(c) },
    setAttribute(k, v) { el.attributes[k] = String(v); }, addEventListener() {}, appendChild(child) { el.kids.push(child); return child; },
    querySelector: sel => (children[sel] ||= node()), querySelectorAll: () => [] };
  return el;
}

async function objectivesPage(extra = {}) {
  const source = (await read('objectives.js')).replace(/^import .*?;\r?\n/gm, '');
  const els = {};
  const context = vm.createContext({
    document: { readyState: 'complete', addEventListener() {}, querySelector: () => null, createElement: tag => node(tag) },
    window: { addEventListener() {}, location: {} }, console,
    requestAnimationFrame: fn => fn(),
    readSnapshot: () => new Promise(() => {}), getInvoke: async () => null,
    $: id => (els[id] ||= node()), show: (el, on) => { if (el) el.hidden = !on; }, clear: el => { if (el) el.kids = []; }, tpl: () => node(),
    objectiveDisplayText: v => String(v ?? ''), objectiveTypeLabel: () => '', objectivePhaseLabel: v => String(v ?? ''),
    registerLeaveGuard: () => () => {}, trackForm: () => ({ isDirty: () => false, mark() {} }),
    renderObjectiveProgress: () => ({ score: 0, ready: false }), TRACKABLE_EVENT_TOKENS, toneFromHex, tokenTone,
    ...extra,
  });
  vm.runInContext(source, context);
  return { context, els, source };
}

test('the hit rate carries a tone, never the sidecar hex', async () => {
  const { context, source } = await objectivesPage();
  assert.doesNotMatch(source, /style\.color|setAttribute\('stroke'|--evt'|levelColorHex|levelDimColorHex/);
  const card = node();
  const crit = card.querySelector('.obj-card-crit');
  context.fillCriteria(card, { hasStructuredCriteria: true, criteriaHitRateText: 'Hit 6/8 games', criteriaHitRateHex: '#8EE7BA' });
  const hit = crit.querySelector('.obj-crit-hit');
  assert.equal(hit.dataset.tone, 'ok');
  assert.equal(hit.style.color, undefined);
  context.fillCriteria(card, { hasStructuredCriteria: true, criteriaHitRateText: 'Hit 1/8 games', criteriaHitRateHex: '#f3a3a8' });
  assert.equal(hit.dataset.tone, 'loss');
  context.fillCriteria(card, { hasStructuredCriteria: true, criteriaHitRateText: 'No data yet', criteriaHitRateHex: '#a79ec2' });
  assert.equal(hit.dataset.tone, 'neutral', 'no data stays quiet');
  assert.equal(crit.hidden, false);
});

test('progress rings and drill bars stay monochrome: JS draws only the arc length', async () => {
  const { context } = await objectivesPage();
  const scope = node();
  context.drawRing(scope, 0.5, 150.8);
  assert.deepEqual(scope.querySelector('.ring-prog').attributes, { 'stroke-dashoffset': '75.4' });
  assert.deepEqual(scope.querySelector('.ring-track').attributes, {}, 'the track keeps its CSS color');

  const focusList = [];
  const drill = node();
  const fill = drill.querySelector('.obj-mini-fill');
  const page = await objectivesPage({ tpl: () => drill });
  page.els['focus-list'] = { kids: focusList, appendChild: el => focusList.push(el) };
  page.context.renderFocus({ focusObjectives: [{ id: 3, title: 'Hold the wave', gameCount: 2, targetGameCount: 3,
    progress: 0.66, levelColorHex: '#f3c794' }] });
  assert.equal(fill.style.width, '66%');
  assert.equal(fill.style.background, undefined, 'the drill bar takes its color from CSS');
});

test('tracked-event pills carry the event tone the VOD timeline uses', async () => {
  const { context, els } = await objectivesPage();
  context.renderEventPicker();
  const pills = els['f-events'].kids.flatMap(group => group.kids[1].kids);
  assert.equal(pills.length, TRACKABLE_EVENT_TOKENS.length);
  for (const pill of pills) {
    assert.equal(pill.dataset.tone, tokenTone(pill.dataset.token), pill.dataset.token);
    assert.equal(pill.style['--evt'], undefined, 'no catalog hex on the pill');
  }
  const tone = token => pills.find(p => p.dataset.token === token).dataset.tone;
  assert.equal(tone('KILL'), 'kill');
  assert.equal(tone('JUNGLE_GANK'), 'death');
  assert.equal(tone('BARON'), 'void');
  assert.equal(tone('ENEMY_JUNGLE_PROXIMITY'), 'loss');
});

test('evidence chips carry the polarity tone', async () => {
  const source = (await read('objectivegames.js')).replace(/^import .*?;\r?\n/gm, '');
  assert.doesNotMatch(source, /\.(polarityColorHex|resultHex)\b|style\.color/);
  const host = { kids: [], appendChild(el) { this.kids.push(el); } };
  const els = { 'ev-list': host };
  const context = vm.createContext({ URLSearchParams, console,
    document: { readyState: 'complete', addEventListener() {}, querySelector: () => null },
    window: { location: { search: '?id=7' }, history: { length: 1, back() {} } },
    readSnapshot: () => new Promise(() => {}), $: id => (els[id] ||= node()), show: (el, on) => { if (el) el.hidden = !on; },
    clear: el => { el.kids = []; }, tpl: () => node(), objectiveDisplayText: v => String(v ?? ''),
    objectivePhaseLabel: v => String(v ?? ''), polarityTone });
  vm.runInContext(source, context);
  const sample = JSON.parse(await read('sample-objective-games.json'));
  context.renderEvidence(sample);
  const chips = host.kids.map(el => el.querySelector('.og-ev-chip'));
  assert.deepEqual(chips.map(c => c.dataset.tone), sample.evidence.map(e => polarityTone(e.polarity)));
  assert.deepEqual(chips.map(c => c.dataset.tone), ['ok', 'loss', 'neutral']);
});

test('the player\'s own words carry .own-words on every learning page', async () => {
  const objectives = await read('objectives.html');
  assert.match(template(objectives, 'tpl-active'), /class="oname obj-card-name own-words"/);
  assert.match(template(objectives, 'tpl-active'), /class="obj-crit-text own-words"/);
  assert.match(template(objectives, 'tpl-focus'), /class="obj-mini-name own-words"/);
  assert.match(template(objectives, 'tpl-completed'), /class="obj-done-name own-words"/);

  const games = await read('objectivegames.html');
  assert.match(template(games, 'tpl-ev'), /class="og-ev-note own-words"/);
  assert.match(template(games, 'tpl-game'), /class="og-grow-exec own-words"/);

  const notes = await read('objectivenotes.html');
  const bodies = [...notes.matchAll(/<div class="on-card-body[^"]*"/g)].map(m => m[0]);
  assert.equal(bodies.length, 4, 'review, execution, prompt answer and clip notes');
  for (const body of bodies) assert.match(body, /\bown-words\b/);
});

test('learning-page CSS reads tones and lit surfaces from the theme', async () => {
  const css = await read('learning-pages.css');
  assert.match(css, /\.obj-crit-hit\[data-tone\]\{color:var\(--tone\)\}/);
  assert.match(css, /\.obj-evtpill\[data-tone\]\{--evt:var\(--tone\)\}/);
  assert.match(css, /\.obj-card\.obj-card-priority\{\s*background:var\(--lit-fill\)!important;[^}]*box-shadow:var\(--lit-shadow\)!important/);
  assert.match(css, /\.og-ev-chip\[data-tone\]::before\{[^}]*background:var\(--tone\)/);
  // The milestone dialog stays a raised surface over the scrim in every mode.
  assert.match(css, /\.obj-celebrate \.obj-celebrate-card\{\s*background:var\(--raised\)!important/);
  // Progress stays monochrome.
  assert.match(css, /\.obj-mini-fill\{[^}]*background:var\(--t2\)/);
  assert.match(await read('objective-progress.css'), /\.is-current \{ border-color: var\(--t1\); color: var\(--t1\); \}/);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// The learning objective games and notes pages: real page scripts run against a
// minimal document so the delegated handlers are exercised, plus their templates.
const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
const template = (html, id) => html.match(new RegExp(`<template id="${id}">([\\s\\S]*?)</template>`))[1];

async function runPage(file, search) {
  const source = (await read(file)).replace(/^import .*?;\r?\n/gm, '');
  const listeners = {};
  const location = { href: '', search };
  const context = {
    URLSearchParams, console,
    document: { readyState: 'complete', addEventListener: (type, fn) => (listeners[type] ||= []).push(fn),
      querySelector: () => null, getElementById: () => null },
    window: { location, history: { length: 1, back() {} } },
    // The snapshot never resolves: these tests drive the handlers directly.
    readSnapshot: () => new Promise(() => {}),
    $: () => null, show() {}, clear() {}, tpl() {},
    objectiveDisplayText: value => String(value ?? ''), objectivePhaseLabel: value => String(value ?? ''),
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  return { listeners, location, context };
}

// A button inside a game row, as the delegated click handler sees it.
function buttonInRow(action, gameId) {
  const row = { dataset: { gameId: String(gameId) }, closest: sel => (sel === '[data-game-id]' ? row : null) };
  const button = { dataset: { action }, closest: sel => (sel === '[data-action]' ? button : row.closest(sel)) };
  return button;
}
const click = (page, target) => page.listeners.click.forEach(fn => fn({ target, preventDefault() {} }));

test('objective game rows give Review match and Watch VOD their own buttons', async () => {
  const html = await read('objectivegames.html');
  const row = template(html, 'tpl-game');
  assert.doesNotMatch(row, /role="button"|tabindex=/, 'the row is a plain container, not a control');
  assert.match(row, /<button[^>]*data-action="review_game"[^>]*>Review match<\/button>/);
  assert.match(row, /<button[^>]*data-action="watch_vod"[^>]*>Watch VOD<\/button>/);
  assert.doesNotMatch(html, /<style>/);
  assert.match(html, /<title>Revu: Learning objective games<\/title>/);

  const page = await runPage('objectivegames.js', '?id=7');
  assert.equal(page.listeners.keydown, undefined, 'Enter on Watch VOD is not rerouted to the review');
  click(page, buttonInRow('watch_vod', 42));
  assert.equal(page.location.href, 'vodplayer.html?gameId=42&objectiveId=7');
  click(page, buttonInRow('review_game', 42));
  assert.equal(page.location.href, 'review.html?gameId=42');
});

test('objective game results use the shared result names, and a remake is never a win', async () => {
  const { context } = await runPage('objectivegames.js', '?id=7');
  assert.equal(context.resultOf({ resultText: 'W', win: true }), 'win');
  assert.equal(context.resultOf({ resultText: 'L', win: false }), 'loss');
  assert.equal(context.resultOf({ resultText: 'Remake', win: true }), 'remake');
});

test('objective notes clips keep one control and sentence-case section headings', async () => {
  const html = await read('objectivenotes.html');
  assert.doesNotMatch(template(html, 'tpl-clip'), /role="button"/);
  assert.match(template(html, 'tpl-clip'), /data-action="play_clip"[^>]*>Watch VOD</);
  assert.doesNotMatch(html, /<style>/);
  for (const heading of ['Review notes', 'Execution notes', 'Prompt answers', 'Clips and bookmarks']) {
    assert.match(html, new RegExp(`<h2 class="on-eyebrow">${heading}</h2>`));
  }
  const source = await read('objectivenotes.js');
  assert.doesNotMatch(source, /setAttribute\('role', 'button'\)/);
  const page = await runPage('objectivenotes.js', '?id=7');
  assert.equal(page.listeners.keydown, undefined);
});

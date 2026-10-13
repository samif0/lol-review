import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { TONES, eventTone, polarityTone, resultOf, tokenTone, toneFromHex } from '../ui/meaning-tone.mjs';

const source = readFileSync(new URL('../ui/meaning-tone.mjs', import.meta.url), 'utf8');
const css = readFileSync(new URL('../ui/appearance.css', import.meta.url), 'utf8');

// Every legacy hex the sidecar, the catalog or a page sends today, by meaning.
const HEX = {
  ok: ['#8ee7ba', '#7ec9a0', '#9cddba'], loss: ['#f3a3a8', '#ff7a9e'], death: ['#d6455e', '#b23a72'],
  fight: ['#f26d7d', '#f87171'], pending: ['#f3c794', '#c9956a'], gold: ['#d4a44e', '#e8c15e'],
  calm: ['#5ec4d4', '#7cc7ff'], summoner: ['#7fd4ff'], recall: ['#a9c8ff'], trade: ['#ffb86b', '#ffd9a3', '#ff9248'],
  proximity: ['#b07cd8'], win: ['#6bd6c8'], danger: ['#d38c90'],
  neutral: ['#9d8bff', '#a79ec2', '#8a80a8', '#aaa7b5', '#9fb0c3', '#9ca3af', '#7b8494', '#6b7280', '#7fe0d4'],
};

test('every tone has a token in appearance.css and results map too', () => {
  for (const tone of TONES) assert.match(css, new RegExp(`\\[data-tone="${tone}"\\]\\{--tone:var\\(--[\\w-]+\\)\\}`), tone);
  for (const result of ['win', 'loss', 'remake']) assert.match(css, new RegExp(`\\[data-result="${result}"\\]\\{--tone:var\\(`), result);
  assert.match(css, /\[data-tone="neutral"\]\{--tone:var\(--t3\)\}/);
  assert.match(css, /\[data-tone="muted"\]\{--tone:var\(--t2\)\}/);
  assert.match(css, /\[data-result="remake"\]\{--tone:var\(--t3\)\}/);
});

test('legacy hex colors map to tones, case-insensitively, and unknown colors are neutral', () => {
  for (const [tone, list] of Object.entries(HEX)) {
    for (const hex of list) {
      assert.equal(toneFromHex(hex), tone, hex);
      assert.equal(toneFromHex(hex.toUpperCase()), tone, hex.toUpperCase());
      assert.equal(toneFromHex(` ${hex} `), tone, 'trimmed');
    }
  }
  for (const value of ['#123456', '#fff', '', null, undefined, 42, 'red', '8ee7ba', '#8ee7ba80']) {
    assert.equal(toneFromHex(value), 'neutral', String(value));
  }
  for (const tone of Object.keys(HEX)) assert.ok(TONES.includes(tone), tone);
});

test('every timeline event type gets its tone, type first, then refinements, kind and hex', () => {
  const byType = {
    kill: ['KILL', 'MULTI_KILL'], assist: ['ASSIST'], death: ['DEATH', 'FIRST_BLOOD'], dragon: ['DRAGON'],
    void: ['BARON', 'HERALD'], structure: ['TURRET', 'INHIBITOR', 'LEVEL_UP', 'UNCERTAIN_COMBAT', 'UNCERTAIN'],
    summoner: ['FLASH', 'SUMMONER_SPELL', 'SUMMONER', 'SPELL_IGNITE'], recall: ['RECALL'],
    trade: ['TRADE', 'SHORT_TRADE', 'EXTENDED_TRADE'], fight: ['ALL_IN', 'TEAMFIGHT'], proximity: ['JUNGLE_PROXIMITY'],
  };
  for (const [tone, types] of Object.entries(byType)) {
    for (const eventType of types) {
      assert.equal(eventTone({ eventType }), tone, eventType);
      assert.equal(eventTone({ eventType: eventType.toLowerCase(), colorHex: '#8ee7ba' }), tone, `${eventType} beats hex`);
    }
  }
  // Refinements the sidecar carries.
  assert.equal(eventTone({ eventType: 'DEATH', shortLabel: 'GNK', colorHex: '#d6455e' }), 'death');
  assert.equal(eventTone({ eventType: 'JUNGLE_PROXIMITY', shortLabel: 'EJX', colorHex: '#ff7a9e' }), 'loss');
  assert.equal(eventTone({ eventType: 'JUNGLE_PROXIMITY', shortLabel: 'ajx' }), 'win');
  assert.equal(eventTone({ eventType: 'JUNGLE_PROXIMITY', shortLabel: 'JPX' }), 'proximity');
  for (const verdict of ['up', 'even', 'down']) {
    assert.equal(eventTone({ eventType: 'TEAMFIGHT', kind: 'teamfight', teamfight: { verdict, self: 'in' } }), 'fight', verdict);
  }
  assert.equal(eventTone({ eventType: 'TEAMFIGHT', kind: 'teamfight-away', teamfight: { self: 'away' } }), 'away');
  assert.equal(eventTone({ eventType: 'TEAMFIGHT', teamfight: { self: 'AWAY' } }), 'away');
  assert.equal(eventTone({ kind: 'teamfight-away' }), 'away');
  // Kind, then hex, when the type says nothing.
  const byKind = { win: 'kill', loss: 'death', gold: 'gold', summoner: 'summoner', recall: 'recall', trade: 'trade', 'all-in': 'fight', teamfight: 'fight' };
  for (const [kind, tone] of Object.entries(byKind)) assert.equal(eventTone({ eventType: 'NEW_TYPE', kind }), tone, kind);
  assert.equal(eventTone({ eventType: 'NEW_TYPE', kind: 'neutral', colorHex: '#ffb86b' }), 'trade');
  assert.equal(eventTone({ eventType: 'NEW_TYPE', kind: 'neutral' }), 'structure', 'an unknown event is a map marker');
  for (const value of [undefined, {}, { kind: 'neutral' }, { colorHex: '#123456' }, { eventType: '' }]) {
    assert.equal(eventTone(value), 'neutral', JSON.stringify(value));
  }
});

test('objective tokens, polarity and results', () => {
  const tokens = {
    KILL: 'kill', MULTI_KILL: 'kill', ASSIST: 'assist', DEATH: 'death', FIRST_BLOOD: 'death', JUNGLE_GANK: 'death',
    FOG_DEATH: 'death', DRAGON: 'dragon', BARON: 'void', HERALD: 'void', TURRET: 'structure', INHIBITOR: 'structure',
    RECALL: 'recall', ALL_IN: 'fight', TRADE: 'trade', SHORT_TRADE: 'trade', EXTENDED_TRADE: 'trade',
    JUNGLE_PROXIMITY: 'proximity', ENEMY_JUNGLE_PROXIMITY: 'loss', ALLY_JUNGLE_PROXIMITY: 'win',
    TEAMFIGHT: 'fight', OUTNUMBERED_TEAMFIGHT: 'fight', EVEN_TEAMFIGHT: 'fight', NUMBERS_UP_TEAMFIGHT: 'fight',
    ABSENT_TEAMFIGHT: 'away', SPELL_FLASH: 'summoner', spell_teleport: 'summoner',
  };
  for (const [token, tone] of Object.entries(tokens)) assert.equal(tokenTone(token), tone, token);
  for (const value of ['', 'MYSTERY', null, undefined, 7, 'constructor', '__proto__']) assert.equal(tokenTone(value), 'neutral', String(value));
  assert.equal(polarityTone('good'), 'ok');
  assert.equal(polarityTone('POSITIVE'), 'ok');
  assert.equal(polarityTone('bad'), 'loss');
  assert.equal(polarityTone(' Negative '), 'loss');
  for (const value of ['neutral', '', null, undefined, 'meh', 'constructor']) assert.equal(polarityTone(value), 'neutral', String(value));
  assert.equal(resultOf({ win: true }), 'win');
  assert.equal(resultOf({ win: 1 }), 'win');
  assert.equal(resultOf({ win: false }), 'loss');
  assert.equal(resultOf({ win: 0 }), 'loss');
  assert.equal(resultOf({ win: true, isRemake: true }), 'remake');
  assert.equal(resultOf({ win: false, isRemake: 1 }), 'remake');
  assert.equal(resultOf(), 'loss');
});

test('the module has no em or en dashes and every tone is a plain word', () => {
  assert.doesNotMatch(source, /[\u2013\u2014]/);
  for (const tone of TONES) assert.match(tone, /^[a-z]+$/);
  assert.equal(new Set(TONES).size, TONES.length);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Copy rules for the pages below: no em or en dashes in what players read, sentence
// case, one honest claim per feature. Comments and console messages are not copy.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFileSync(new URL(name, ui), 'utf8');
const visibleText = html => html.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '')
  .replace(/<style[\s\S]*?<\/style>/g, '').replace(/<svg[\s\S]*?<\/svg>/g, '');

// Pages and scripts this copy pass covers.
const COPY_FILES = ['objectives.html', 'objectives.js', 'objective-labels.mjs', 'objective-progress.mjs',
  'objectivegames.html', 'objectivegames.js', 'objectivenotes.html', 'objectivenotes.js', 'rules.html', 'rules.js',
  'rule-labels.mjs', 'tiltcheck.html', 'tiltcheck.js', 'manualentry.js', 'onboarding.html', 'onboarding.js',
  'matchups.html', 'matchups.js', 'patterns.html', 'patterns.js', 'tutorial.js', 'first-review-guide.mjs',
  'pregame.html', 'ingame.html', 'audio-devices.mjs', 'audio-settings.mjs'];

function copyLines(name) {
  let text = read(name);
  text = name.endsWith('.html') ? visibleText(text) : text.replace(/\/\*[\s\S]*?\*\//g, '');
  return text.split(/\r?\n/)
    .map(line => (name.endsWith('.html') ? line : line.replace(/(^|[^:'"`])\/\/.*$/, '$1')))
    // Console messages are for developers; translation keys match what the sidecar sends.
    .filter(line => !/console\.|\['MASTERY — NO DATA YET'/.test(line));
}

test('player-facing copy uses words or a plain hyphen, never an em or en dash', () => {
  for (const name of COPY_FILES) {
    copyLines(name).forEach((line, i) => {
      assert.doesNotMatch(line, /[–—]/, `${name}:${i + 1}: ${line.trim()}`);
    });
  }
});

test('onboarding promises nothing Settings cannot do and says Sign in', () => {
  const html = visibleText(read('onboarding.html'));
  assert.doesNotMatch(html, /change this in Settings/i, 'Settings has no main role control');
  assert.doesNotMatch(html, />\s*Log in\s*</);
  assert.match(html, /<div class="seclabel onb-sectitle">Sign in<\/div>/);
  for (const role of ['Top', 'Jungle', 'Mid', 'ADC', 'Support']) assert.match(html, new RegExp(`type="button">${role}</button>`));
  assert.doesNotMatch(read('onboarding.js'), /RANK DETECTED:/);
});

test('in-game cards have distinct titles and every pregame field has a label', () => {
  const ingame = visibleText(read('ingame.html'));
  const titles = [...ingame.matchAll(/<div class="seclabel-in"[^>]*>([^<]+)</g)].map(m => m[1]);
  assert.ok(titles.includes('Matchup notes') && titles.includes('Objective reminders'), titles.join(', '));
  assert.equal(new Set(titles).size, titles.length, 'no two cards share a title');
  assert.match(ingame, /<h1 id="hero-title">In game<\/h1>/);
  // pregame.js swaps the static title for the snapshot's header, which must keep the wording.
  assert.match(read('pregame.js'), /setText\('mh-header', mh\.headerText/);
  assert.match(JSON.parse(read('sample-pregame.json')).matchupHistory.headerText, /^Matchup notes vs /);

  const pregame = visibleText(read('pregame.html'));
  for (const id of ['intent-input', 'session-input']) assert.match(pregame, new RegExp(`<label[^>]*for="${id}"`), id);
  for (const html of [pregame, ingame]) {
    const prompt = html.match(/<template id="tpl-prompt">([\s\S]*?)<\/template>/)[1];
    assert.match(prompt, /<label class="p-row">[\s\S]*<textarea[\s\S]*<\/label>/, 'prompt answers are labelled by their question');
  }
  assert.doesNotMatch(pregame, />PRIORITY<|>Lock In<|>Locked In</);
});

test('tilt check states one duration and makes no physiological claim', () => {
  const html = visibleText(read('tiltcheck.html'));
  const js = read('tiltcheck.js');
  assert.doesNotMatch(html + js, /cortisol|arousal|90-second|4-minute|~4/i);
  assert.match(html, /about 3 minutes, including 80 seconds of breathing/);
  assert.match(html, /id="tc-runbtn"[^>]*>Start a reset</);
  assert.match(js, /TOTAL_BREATH_SECONDS = 80;/, 'the stated 80 seconds is the real breathing timer');
});

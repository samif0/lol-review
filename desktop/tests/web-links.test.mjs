import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { MY_CLIPS_URL, canOpenExternal, openMyClips } from '../ui/web-links.mjs';

const read = (name) => readFileSync(new URL(`../ui/${name}`, import.meta.url), 'utf8');

test('My clips points at the revu.lol page', () => {
  assert.equal(MY_CLIPS_URL, 'https://revu.lol/my-clips');
});

test('canOpenExternal is true only when the host grants external links', async () => {
  const caps = (value) => ({ capabilities: async () => value });
  assert.equal(await canOpenExternal(caps({ externalLinks: true })), true);
  assert.equal(await canOpenExternal(caps({ externalLinks: false })), false);
  assert.equal(await canOpenExternal(caps({ externalLinks: 'true' })), false);
  assert.equal(await canOpenExternal(caps({})), false);
  assert.equal(await canOpenExternal(caps(null)), false);
  assert.equal(await canOpenExternal({ capabilities: async () => { throw new Error('no host'); } }), false);
});

test('openMyClips opens the page once and reports success', async () => {
  const calls = [];
  const ok = await openMyClips({ openExternal: async (url) => { calls.push(url); } });
  assert.equal(ok, true);
  assert.deepEqual(calls, [MY_CLIPS_URL]);
});

test('openMyClips returns false instead of throwing when the host refuses', async (t) => {
  const warn = t.mock.method(console, 'warn', () => {});
  const ok = await openMyClips({
    openExternal: async () => { throw new Error('Desktop capability unavailable: externalLinks'); },
  });
  assert.equal(ok, false);
  assert.equal(warn.mock.callCount(), 1);
});

test('signed-in card carries the hidden My clips link between the account and Home', () => {
  const html = read('onboarding.html');
  const open = html.indexOf('id="card-signedIn"');
  assert.ok(open > 0);
  const close = html.indexOf('</section>', open);
  const card = html.slice(open, close);

  const button = card.match(/<button[^>]*data-action="open_my_clips"[^>]*>([^<]*)<\/button>/);
  assert.ok(button, 'open_my_clips button is inside card-signedIn');
  assert.match(button[0], /\shidden[\s>]/);
  assert.match(button[0], /id="signedin-my-clips"/);
  assert.equal(button[1], 'See your shared clips on revu.lol');
  assert.doesNotMatch(button[0], /[–—]/);

  const email = card.indexOf('id="onb-acct-email"');
  const link = card.indexOf('data-action="open_my_clips"');
  const home = card.indexOf('data-action="go_dashboard"');
  const signOut = card.indexOf('data-action="sign_out"');
  assert.ok(email >= 0 && email < link, 'link follows the account email');
  assert.ok(link < home, 'link precedes Go to Home');
  assert.ok(home < signOut, 'Go to Home precedes Sign out');
});

test('onboarding maps open_my_clips and gates the link on the external-link capability', () => {
  const js = read('onboarding.js');
  const handlers = js.slice(js.indexOf('const HANDLERS = {'), js.indexOf('};', js.indexOf('const HANDLERS = {')));
  assert.match(handlers, /open_my_clips:\s*\(\)\s*=>\s*openMyClips\(platform\)/);
  assert.match(js, /import \{ canOpenExternal, openMyClips \} from '\.\/web-links\.mjs';/);
  assert.match(js, /canOpenExternal\(platform\)\.then/);
  const renderStart = js.indexOf('function render()');
  const renderBody = js.slice(renderStart).split(/\r?\n\}\r?\n/)[0];
  assert.ok(renderStart >= 0 && renderBody.length < js.length - renderStart);
  assert.doesNotMatch(renderBody, /signedin-my-clips/);
});

test('hidden onboarding links stay hidden despite their flex display', () => {
  assert.match(read('styles.css'), /\.onb-link\[hidden\]\{display:none\}/);
});

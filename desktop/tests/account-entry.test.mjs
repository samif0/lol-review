import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  ACCOUNT_CHANGED, accountHero, announceAccountChange, applyAccountPage, isAccountPage, linkedLine, needsRiotId, signedInFor,
} from '../ui/account-page.mjs';

// The Account page (onboarding.html?signin=1) is the one destination for every account
// entry, and every page that changes the Revu sign-in tells the shell's account row.
// onboarding.js itself runs in onboarding-account.test.mjs.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFile(new URL(name, ui), 'utf8');

test('?signin=1 is the Account page; first launch is not', () => {
  assert.equal(isAccountPage('?signin=1'), true);
  assert.equal(isAccountPage('?x=2&signin=1'), true);
  for (const search of ['', '?signin=0', '?signin', undefined, null]) assert.equal(isAccountPage(search), false, String(search));
});

test('the heading follows the card: signed in, a sign-in step, or neither', () => {
  assert.equal(signedInFor('signedIn'), true);
  for (const state of ['emailEntry', 'codeSent']) assert.equal(signedInFor(state), false, state);
  for (const state of ['account', 'role', 'done', 'welcome', undefined]) assert.equal(signedInFor(state), null, String(state));
});

test('the Account heading states what the page holds for each sign-in state', () => {
  assert.deepEqual(accountHero(true), { title: 'Account', subtitle: 'Your Revu sign-in.' }, 'true with or without a Riot ID');
  assert.deepEqual(accountHero(false), { title: 'Account', subtitle: 'Sign in with email to share clips and look up match details.' });
  assert.deepEqual(accountHero(null), { title: 'Account', subtitle: '' }, 'nothing is claimed before the state is known');
  for (const value of [true, false]) assert.doesNotMatch(Object.values(accountHero(value)).join(' '), /[–—]|Log in/);
});

test('the signed-in card never claims a link that does not exist', () => {
  assert.equal(linkedLine('bye#NA1', 'na1'), 'Linked: bye#NA1 · NA1');
  assert.equal(linkedLine(' bye#NA1 ', ''), 'Linked: bye#NA1');
  assert.equal(needsRiotId('bye#NA1'), false);
  for (const none of ['', '   ', null, undefined]) {
    assert.equal(linkedLine(none, 'na1'), 'No Riot ID linked yet.', 'a region alone is no link');
    assert.equal(needsRiotId(none), true, 'the card links to the Riot ID field instead');
  }
});

function fakePage() {
  const els = {};
  for (const id of ['onb-eyebrow', 'onb-title', 'onb-subt', 'signedin-continue', 'email-back', 'signedin-logout']) {
    els[id] = { hidden: false, textContent: id };
  }
  return { title: 'Revu: Welcome', els, getElementById: id => els[id] || null };
}

test('the Account page swaps the welcome hero and hides Go to Home and the Back to Get started, never Sign out', () => {
  const doc = fakePage();
  applyAccountPage(doc, null);
  assert.equal(doc.title, 'Revu: Account');
  assert.equal(doc.els['onb-eyebrow'].hidden, true);
  assert.equal(doc.els['onb-title'].textContent, 'Account');
  assert.equal(doc.els['onb-subt'].hidden, true, 'no subtitle until the state is known');
  applyAccountPage(doc, true);
  assert.equal(doc.els['onb-subt'].textContent, 'Your Revu sign-in.');
  assert.equal(doc.els['onb-subt'].hidden, false);
  applyAccountPage(doc, false);
  assert.equal(doc.els['onb-subt'].textContent, 'Sign in with email to share clips and look up match details.');
  assert.equal(doc.els['signedin-continue'].hidden, true, 'the sidebar already navigates');
  assert.equal(doc.els['email-back'].hidden, true, 'Back on the email step would open the first-launch card');
  assert.equal(doc.els['signedin-logout'].hidden, false);
  assert.doesNotThrow(() => applyAccountPage({ getElementById: () => null }, true));
});

test('a sign-in change is announced on the shell window, and is harmless without one', () => {
  const seen = [];
  const top = { dispatchEvent: event => { seen.push(event.type); return true; } };
  announceAccountChange({ top });
  assert.deepEqual(seen, [ACCOUNT_CHANGED]);
  assert.equal(ACCOUNT_CHANGED, 'revu:account-changed');
  assert.doesNotThrow(() => announceAccountChange({}));
  assert.doesNotThrow(() => announceAccountChange({ top: { dispatchEvent() { throw new Error('closed'); } } }));
  assert.doesNotThrow(() => announceAccountChange(undefined));
});

test('the VOD player tells the shell after signing in to share a clip', async () => {
  // vodplayer.js is too large to run here; Settings (settings-behavior.test.mjs), the
  // Account page (onboarding-account.test.mjs) and the shell row (shell-recent.test.mjs)
  // are tested by running them.
  const vod = await read('vodplayer.js');
  const verify = vod.slice(vod.indexOf('async function shareVerify('), vod.indexOf('\n}', vod.indexOf('async function shareVerify(')));
  assert.match(verify, /await _core\.invoke\('auth_verify'[^\n]*\n\s*try \{ window\.top\?\.dispatchEvent\(new CustomEvent\('revu:account-changed'\)\); \} catch/);
});

test('every sign-in entry opens the Account page', async () => {
  assert.match(await read('settings.html'), /<a class="rule-act" href="onboarding\.html\?signin=1">Manage sign-in<\/a>/);
  assert.match(await read('vodplayer.html'), /<a class="vp-signin-hint" id="vp-signin-hint" href="onboarding\.html\?signin=1" hidden>/);
  assert.match(await read('index.html'), /<a class="nav-i side-acct" id="side-acct" data-nav="onboarding" href="onboarding\.html\?signin=1"/);
  const html = await read('onboarding.html');
  for (const id of ['onb-hero', 'onb-eyebrow', 'onb-title', 'onb-subt', 'signedin-continue', 'email-back']) assert.match(html, new RegExp(`id="${id}"`), id);
  assert.doesNotMatch(html, /Your account is linked\./);
  // The hero stays invisible until onboarding.js has picked Welcome or Account.
  assert.match(await read('learning-pages.css'), /\.onb-shell \.onb-hero:not\(\[data-ready\]\)\{visibility:hidden\}/);
});

test('every prompt to link a Riot ID opens the Riot ID field in Settings', async () => {
  // settings-navigation.mjs opens the category named by #settings-<category>, and
  // settings.js puts the cursor in a blank Riot ID field (settings-behavior.test.mjs).
  assert.match(await read('settings.html'), /<section class="settings-section" id="settings-account" data-settings-section="account"[\s\S]*?id="riotId"/);
  assert.match(await read('dashboard.html'), /<a class="ui-link" href="settings\.html#settings-account">Link your Riot account<\/a>/);
  assert.match(await read('onboarding.html'),
    /<span id="onb-acct-sub"><\/span> <a class="ui-link onb-acct-add" id="onb-acct-add" href="settings\.html#settings-account" hidden>Add it in Settings<\/a>/);
  for (const name of ['dashboard.html', 'onboarding.html']) {
    assert.doesNotMatch(await read(name), /href="onboarding\.html"/, `${name} never opens the first-launch page`);
  }
});

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const html = readFileSync(new URL('../privacy.html', import.meta.url), 'utf8');
const text = html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

test('The policy discloses every kind of data the server holds', () => {
  assert.doesNotMatch(text, /only things Revu's server holds are clips/,
    'The server also holds sign-in and usage data');
  assert.match(text, /email address and session you use to sign in/);
  assert.match(text, /transcription usage counts \(kept for 7 days\)/);
  assert.match(text, /Email sign-in/);
});

test('Clip uploads and transcription are credited to the Worker host', () => {
  assert.match(text, /revu-proxy\.lol-review\.workers\.dev , with shared links served from clips\.revu\.lol/);
  assert.match(text, /clip sharing uploads, and narration transcription/);
  // Changed on purpose for web accounts: clips.revu.lol now also serves sign-in.
  assert.match(text, /serves the links for clips you share, and the sign-in and My clips requests/);
});

test('Web accounts are disclosed', () => {
  assert.match(text, /__Host-revu_web/);
  assert.match(text, /up to 30 days/);
  assert.match(text, /never asks for or stores a password/);
  assert.match(text, /Cloudflare Turnstile/);
  assert.match(text, /delete your whole account/);
  assert.match(text, /don't post your email address in a public issue/);
  assert.match(text, /It holds no ID or email\./);
});

test('Remote delete is not promised as immediate', () => {
  assert.doesNotMatch(text, /deletes the shared copy right away/);
  assert.match(text, /as soon as Revu can reach its server while you are signed in/);
});

test('Sign-in code retention and IP address handling are disclosed', () => {
  assert.match(text, /pending sign-in codes \(deleted within about two hours\)/);
  assert.match(text, /deleted about an hour after the code expires, whether or not it was used/);
  // The per-isolate limiters key on the address itself, and their entries are
  // not expired on a timer, so the page must not promise a hash or an hour.
  assert.match(text, /keeps your IP address, or a hash of it, only in short-lived\s+server memory/);
  assert.doesNotMatch(text, /for at most an hour/);
  assert.match(text, /never written to Revu's database/);
});

test('Shared clip titles: the note fallback in older desktop builds is disclosed', () => {
  // Desktop builds up to 3.14.1 fall back to the bookmark note as the share
  // title (ClipShareRequests), so the page must not claim notes are never sent.
  assert.match(text, /In Revu 3\.14\.1 and\s+earlier, if you share a clip without typing a title, the\s+clip's note is used as its title/);
  assert.doesNotMatch(text, /Revu does not use your private review notes as the title/);
  assert.match(text, /reloading a clip does not count as a new view/);
  assert.match(text, /Review notes are kept separately for each match/);
});

test('The policy has no em or en dashes', () => {
  assert.doesNotMatch(html, /[\u2013\u2014]/);
});

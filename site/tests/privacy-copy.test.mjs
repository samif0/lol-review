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

test('The policy has no em or en dashes', () => {
  assert.doesNotMatch(html, /[\u2013\u2014]/);
});

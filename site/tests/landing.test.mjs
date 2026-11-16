// The home page (index.html + landing.css): a download-first landing in the
// Neutral theme. Copy, the download fallback, the screenshots it shows, and
// the routes that moved when the web review tool went to /review.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { jpegSize, webpSize } from '../../scripts/image-size.mjs';

const site = new URL('../', import.meta.url);
const read = name => readFileSync(new URL(name, site), 'utf8').replace(/\r\n/g, '\n');
const html = read('index.html');
const INSTALLER = 'https://github.com/samif0/lol-review/releases/latest/download/Revu-Setup.exe';

const visibleText = source => source
  .replace(/<!--[\s\S]*?-->/g, ' ')
  .replace(/<(script|style)\b[\s\S]*?<\/\1>/gi, ' ')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&middot;/g, '·')
  .replace(/\s+/g, ' ');
const text = visibleText(html);

test('The hero leads with the download and the agreed copy', () => {
  const hero = html.match(/<section class="hero" id="download"[\s\S]*?<\/section>/)?.[0];
  assert.ok(hero, 'the hero is the #download target');
  assert.match(hero, /<h1 class="hero-title" id="hero-title">Review every ranked game, alone or with your coach\.<\/h1>/);
  assert.match(visibleText(hero), /Revu records your League games and helps you pick one thing to practice\. Share a clip or a game as a link your coach can draw on, and set stop rules that cancel the queue when you said you would stop\./);
  assert.match(hero, new RegExp(`<a id="download-btn" class="btn-primary btn-lg" data-download href="${INSTALLER.replace(/[.]/g, '\\.')}" rel="noopener">Download for Windows</a>`));
  // One primary action and one secondary: the download, and a jump to how a coach reviews a shared game.
  assert.equal((hero.match(/class="btn-primary/g) || []).length, 1);
  assert.equal((hero.match(/class="btn-secondary/g) || []).length, 1);
  assert.match(hero, /<a class="btn-secondary btn-lg" href="#coach">How coach review works<\/a>/);
  assert.match(html, /<section class="feature is-text" id="coach" aria-labelledby="feature-coach">/);
  assert.match(hero, /<span data-download-version>Latest version<\/span> &middot; Windows 10\/11<span data-download-size><\/span>/);
  assert.match(hero, /<p class="download-note" data-download-note hidden><\/p>/);
  // Nothing under the top buttons but the version line: the FFmpeg notice sits by the closing download.
  assert.doesNotMatch(hero, /hero-trust|FFmpeg|GPL/);
  // The H1 and the button come before any screenshot, so they lead the first screen.
  assert.ok(html.indexOf('id="download-btn"') < html.indexOf('<figure'));
});

test('Five feature rows, four with a screenshot, then a closing download band', () => {
  const titles = [...html.matchAll(/<section class="feature[^"]*"[\s\S]*?<h2 id="[^"]+">([^<]+)<\/h2>/g)].map(m => m[1]);
  assert.deepEqual(titles, ['Send a game to your coach', 'Stop when you said you would', 'Start with the recording', 'Practice one thing', 'See what repeats']);
  const closing = html.match(/<section class="closing"[\s\S]*?<\/section>/)?.[0];
  assert.match(closing, /<h2 class="closing-title" id="closing-title">Your next game starts with your last one\.<\/h2>/);
  assert.match(closing, new RegExp(`class="btn-primary btn-lg" data-download href="${INSTALLER.replace(/[.]/g, '\\.')}"`));
  // GPLv3: directions to the FFmpeg source next to a download (desktop/tests/ffmpeg-notices.test.mjs).
  assert.match(closing, /Includes FFmpeg under the GPLv3: <a href="\/terms#third-party">licenses and source code<\/a>/);
  assert.equal(html.split(`href="${INSTALLER}"`).length - 1, 2, 'hero and closing band link the installer');
});

test('Copy is plain and honest', () => {
  assert.doesNotMatch(text, /[\u2013\u2014]/, 'no en or em dashes');
  assert.doesNotMatch(text, /\p{Extended_Pictographic}/u, 'no emojis');
  assert.doesNotMatch(text, /local-first|telemetry-free|no telemetry|records every game|automatic recording/i);
  assert.match(text, /Built-in recording is in beta and stays off until you turn it on in Settings\./);
  assert.match(text, /Ranked Solo\/Duo/);
  assert.match(text, /hardware H\.264 encoding/);
  // The app offers "Watch all N back to back" only once a trend has four moments across three
  // games (PatternReviewGate.Readiness, PatternConstants); a two-game trend is grouped to compare.
  assert.match(text, /two or more games, Revu groups them into a mistake trend you can compare\. Once a trend has four moments across three games, watch them back to back/);
  assert.doesNotMatch(text, /two or more games[^.]*back to back/);
  // A review starts from the recording only when the game has one.
  assert.match(text, /When a game has a recording, its review starts there\./);
  // The app has had no recordings-folder import since 4.5, and names no other product.
  assert.doesNotMatch(html, /ascent|connect its folder/i);
  // No prices or promises about them, and nothing about Riot beyond the footer disclaimer.
  assert.doesNotMatch(text, /[$\u20ac\u00a3]\s?\d|per month|\/mo\b|pricing|subscription|free forever|always free/i);
  assert.doesNotMatch(text.replace(/Revu isn't endorsed by Riot Games[\s\S]*?Riot Games, Inc\./, ''), /Riot/);
});

test('Stop rules say exactly what Revu does to the League client', () => {
  const rules = visibleText(html.match(/<section class="feature[^"]*" aria-labelledby="feature-rules">[\s\S]*?<\/section>/)[0]);
  // HardStopEnforcer: a tripped rule with its hard stop on cancels the queue search (the client's
  // own Cancel request); once a match is found Revu sends the client nothing.
  assert.match(rules, /Revu cancels your ranked queue search, the same as pressing Cancel in the client\./);
  assert.match(rules, /If a match has already been found, Revu leaves it to you: it never accepts or declines a ready check\./);
  // Opt-in per rule (rules.enforce defaults to 0), queues 420 and 440 only, a five-minute hold
  // before Queue anyway, and every override counted.
  assert.match(rules, /Hard stop is off until you turn it on for a rule, and it holds Ranked Solo\/Duo and Flex only\./);
  assert.match(rules, /You can queue anyway after a five-minute wait, and Revu counts each time you do\./);
  assert.doesNotMatch(rules, /dodge|blocks? you|locks? (you|the client)|declines? (the|your|a) (match|queue)/i);
});

test('The coach section states the limits of sharing', () => {
  const coach = visibleText(html.match(/<section class="feature[^"]*"[^>]* aria-labelledby="feature-coach">[\s\S]*?<\/section>/)[0]);
  assert.match(coach, /Your coach opens it in a browser, with nothing to install and no account\./);
  assert.match(coach, /draw on the video, bookmark moments, clip parts of the game and write a note on each\. Copy link with notes/);
  // proxy: CLIP_TTL_SECONDS and VOD_TTL_SECONDS are 3 days; full games need a vod_sharers row.
  assert.match(coach, /Sharing needs a Revu account, signed in by email, and a link stays up for 3 days\./);
  assert.match(coach, /Any account can share clips\. Full-game sharing is switched on one account at a time for now\./);
  assert.match(coach, /A shared video shows other players' names and in-game chat\./);
  // review-notes.js: notes live in the browser's storage and in the link's fragment.
  assert.match(coach, /A coach's notes stay in their browser and in the links they copy, and are never uploaded\./);
});

test('The page runs no inline script', () => {
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];
  for (const [, attrs, body] of scripts) {
    if (/type="application\/ld\+json"/.test(attrs)) continue;
    assert.match(attrs, /\bsrc="\/[\w-]+\.js"/);
    assert.equal(body.trim(), '');
  }
  assert.doesNotMatch(html, /\son[a-z]+\s*=\s*"/i, 'no inline event handlers');
});

test('Every screenshot is a current 1600x1000 WebP with alt text', () => {
  const images = [...html.matchAll(/<img\b([^>]*)>/g)].map(m => m[1]).filter(attrs => /src="\/shot-/.test(attrs));
  assert.equal(images.length, 5);
  images.forEach((attrs, index) => {
    const src = attrs.match(/src="\/([^"]+)"/)[1];
    assert.match(src, /^shot-[a-z]+\.webp$/);
    assert.match(attrs, /width="1600" height="1000"/, src);
    assert.match(attrs, /alt="[^"]{20,}"/, src);
    assert.match(attrs, index === 0 ? /fetchpriority="high"/ : /loading="lazy"/, src);
    assert.deepEqual(webpSize(readFileSync(new URL(src, site))), { width: 1600, height: 1000 }, src);
  });
  assert.deepEqual(readdirSync(site).filter(name => /^shot-.*\.png$/.test(name)), [], 'the pre-Neutral PNG shots are gone');
  assert.deepEqual(jpegSize(readFileSync(new URL('og-image.jpg', site))), { width: 1200, height: 630 });
});

test('/learn redirects home, /review is the web tool, and the sitemap follows', () => {
  assert.equal(existsSync(new URL('learn.html', site)), false);
  const rules = read('_redirects').split('\n').filter(line => line.trim() && !line.startsWith('#'))
    .map(line => line.trim().split(/\s+/));
  for (const from of ['/learn', '/learn.html', '/learn/']) {
    assert.deepEqual(rules.find(rule => rule[0] === from), [from, '/', '301'], from);
  }
  assert.match(read('review.html'), /<h1>Find a game<\/h1>/);
  const sitemap = read('sitemap.xml');
  assert.match(sitemap, /<loc>https:\/\/revu\.lol\/<\/loc>/);
  assert.match(sitemap, /<loc>https:\/\/revu\.lol\/review<\/loc>/);
  assert.doesNotMatch(sitemap, /learn/);
});

test('The closed lightbox is out of the tab order and every phone button is a 44px target', () => {
  const css = read('styles.css');
  assert.match(css, /\.lightbox:not\(\.is-open\) \{ visibility: hidden; \}/);
  const phone = css.match(/@media \(max-width: 639\.98px\) \{([\s\S]*?)\n\}/)[1];
  const targets = [...phone.matchAll(/((?:\s*[.\w-]+,)*\s*[.\w-]+) \{ min-height: 44px; \}/g)].flatMap(m => m[1].split(',').map(s => s.trim()));
  for (const button of ['.btn-primary', '.btn-secondary', '.btn-pill', '.btn-danger', '.btn-link', '.header-cta']) {
    assert.ok(targets.includes(button), button);
  }
});

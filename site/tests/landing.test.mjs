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
  assert.match(hero, /<p class="hero-eyebrow">Free for Windows 10 and 11<\/p>/);
  assert.match(hero, /<h1 class="hero-title" id="hero-title">Review every ranked game\.<\/h1>/);
  assert.match(visibleText(hero), /Revu records your League games, links each recording to its match, and helps you pick one thing to practice next game\./);
  assert.match(hero, new RegExp(`<a id="download-btn" class="btn-primary btn-lg" data-download href="${INSTALLER.replace(/[.]/g, '\\.')}" rel="noopener">Download for Windows</a>`));
  assert.match(hero, /<a class="btn-secondary btn-lg" href="\/review">Review a game in your browser<\/a>/);
  assert.match(hero, /<span data-download-version>Latest version<\/span> &middot; Windows 10\/11<span data-download-size><\/span>/);
  assert.match(hero, /<p class="download-note" data-download-note hidden><\/p>/);
  assert.match(hero, /<p class="hero-trust">Your recordings and notes are stored on your PC\.<\/p>/);
  // The H1 and the button come before any screenshot, so they lead the first screen.
  assert.ok(html.indexOf('id="download-btn"') < html.indexOf('<figure'));
});

test('Three feature rows with screenshots, then a closing download band', () => {
  const titles = [...html.matchAll(/<section class="feature[^"]*"[\s\S]*?<h2 id="[^"]+">([^<]+)<\/h2>/g)].map(m => m[1]);
  assert.deepEqual(titles, ['Start with the recording', 'Practice one thing', 'See what repeats']);
  const closing = html.match(/<section class="closing"[\s\S]*?<\/section>/)?.[0];
  assert.match(closing, /<h2 class="closing-title" id="closing-title">Your next game starts with your last one\.<\/h2>/);
  assert.match(closing, new RegExp(`class="btn-primary btn-lg" data-download href="${INSTALLER.replace(/[.]/g, '\\.')}"`));
  assert.equal(html.split(`href="${INSTALLER}"`).length - 1, 2, 'hero and closing band link the installer');
});

test('Copy is plain and honest', () => {
  assert.doesNotMatch(text, /[\u2013\u2014]/, 'no en or em dashes');
  assert.doesNotMatch(text, /\p{Extended_Pictographic}/u, 'no emojis');
  assert.doesNotMatch(text, /local-first|telemetry-free|no telemetry|records every game|automatic recording/i);
  assert.match(text, /Built-in recording is in beta and stays off until you turn it on in Settings\./);
  assert.match(text, /Ranked Solo\/Duo/);
  assert.match(text, /hardware H\.264 encoding/);
});

test('Every screenshot is a current 1600x1000 WebP with alt text', () => {
  const images = [...html.matchAll(/<img\b([^>]*)>/g)].map(m => m[1]).filter(attrs => /src="\/shot-/.test(attrs));
  assert.equal(images.length, 4);
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

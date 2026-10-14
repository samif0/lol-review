// Pure checks for scripts/capture-site-shots.mjs. Nothing here starts a browser or a
// dev server, so it runs on CI's Ubuntu job.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { OG_SIZE, PANE, PERSONAL_TEXT, SHOTS, VIEWPORT, ogTemplate, parseArgs, repositoryRoot, shotFile } from './capture-site-shots.mjs';
import { jpegSize, webpSize } from './image-size.mjs';

const site = path.join(repositoryRoot, 'site');
const ui = path.join(repositoryRoot, 'desktop', 'ui');

function riff(chunk, payload) {
  const body = Buffer.concat([Buffer.from('WEBP'), Buffer.from(chunk), Buffer.alloc(4), payload]);
  const header = Buffer.alloc(8);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(body.length, 4);
  return Buffer.concat([header, body, Buffer.alloc(16)]);
}

test('webpSize reads lossy, lossless and extended WebP headers', () => {
  const lossy = Buffer.alloc(10);
  lossy.set([0, 0, 0, 0x9d, 0x01, 0x2a], 0);
  lossy.writeUInt16LE(1600, 6);
  lossy.writeUInt16LE(1000, 8);
  assert.deepEqual(webpSize(riff('VP8 ', lossy)), { width: 1600, height: 1000 });
  const lossless = Buffer.alloc(5);
  lossless[0] = 0x2f;
  lossless.writeUInt32LE((1600 - 1) | ((1000 - 1) << 14), 1);
  assert.deepEqual(webpSize(riff('VP8L', lossless)), { width: 1600, height: 1000 });
  const extended = Buffer.alloc(10);
  extended.writeUIntLE(1200 - 1, 4, 3);
  extended.writeUIntLE(630 - 1, 7, 3);
  assert.deepEqual(webpSize(riff('VP8X', extended)), { width: 1200, height: 630 });
  assert.equal(webpSize(Buffer.from('not a webp image at all, just text')), null);
});

test('the committed shots and link preview have the sizes the script writes', () => {
  for (const shot of SHOTS) assert.deepEqual(webpSize(readFileSync(path.join(site, shotFile(shot.name)))), VIEWPORT, shot.name);
  assert.deepEqual(jpegSize(readFileSync(path.join(site, 'og-image.jpg'))), OG_SIZE);
});

test('feature shots are one part of a page at twice the density, Home is the whole window', () => {
  assert.deepEqual({ width: PANE.width * 2, height: PANE.height * 2 }, VIEWPORT);
  assert.equal(SHOTS[0].name, 'home');
  assert.equal(SHOTS[0].top, undefined);
  for (const shot of SHOTS.slice(1)) {
    assert.ok(Number.isInteger(shot.top) && shot.top >= 0 && shot.top + PANE.height < VIEWPORT.height, shot.name);
  }
  const review = SHOTS.find(shot => shot.name === 'review');
  const subject = JSON.parse(readFileSync(path.join(ui, 'sample-review.json'), 'utf8')).subject;
  assert.equal(review.page, `review.html?gameId=${subject.gameId}`, 'the review shot opens the fixture game');
  for (const list of ['sample-games.json', 'sample-dashboard.json']) {
    const data = JSON.parse(readFileSync(path.join(ui, list), 'utf8'));
    const row = (data.items || data.unreviewed.items).find(item => item.gameId === subject.gameId);
    const { header } = subject;
    assert.deepEqual([row.championName, row.enemyChampion, row.win, row.kills, row.deaths, row.assists, row.duration, row.datePlayed],
      [header.championName, header.enemyChampion, header.win, header.kills, header.deaths, header.assists, header.duration, header.datePlayed], list);
  }
});

test('the shot manifest is exactly what the site shows', () => {
  const written = SHOTS.map(shot => shotFile(shot.name)).sort();
  const onDisk = readdirSync(site).filter(name => /^shot-/.test(name)).sort();
  const referenced = [...new Set([...readFileSync(path.join(site, 'index.html'), 'utf8').matchAll(/\/(shot-[\w-]+\.\w+)/g)].map(m => m[1]))].sort();
  assert.deepEqual(onDisk, written);
  assert.deepEqual(referenced, written);
});

test('the privacy guard catches personal strings and passes the sample data', () => {
  for (const personal of ['Good morning, bye', 'Coaching stint · Violet', 'chapy', 'you@example.com', 'Player#NA1', 'Faker#KR1'])
    assert.match(personal, PERSONAL_TEXT, personal);
  for (const generic of ['Good evening', 'Jinx vs Caitlyn', 'Ranked Solo/Duo · 27:48', '8 / 2 / 11', 'Player', 'Goodbye to tilt'])
    assert.doesNotMatch(generic, PERSONAL_TEXT, generic);
});

test('the desktop sample fixtures carry no personal data', () => {
  const dashboard = JSON.parse(readFileSync(path.join(ui, 'sample-dashboard.json'), 'utf8'));
  assert.equal(dashboard.greeting, 'Good evening.');
  assert.equal(dashboard.stint, null);
  for (const name of readdirSync(ui).filter(file => /^sample-.*\.json$/.test(file))) {
    const raw = readFileSync(path.join(ui, name), 'utf8');
    assert.doesNotMatch(raw, /\bbye\b|\bviolet\b|\bchapy\b/i, name);
    for (const email of raw.match(/[\w.+-]+@[\w-]+(?:\.[\w-]+)+/g) || []) assert.match(email, /@example\.com$/, `${name}: ${email}`);
  }
});

test('the link preview template uses the hero copy and inlines its assets', () => {
  const html = ogTemplate('data:font/woff2;base64,AA==', 'data:image/webp;base64,AA==');
  assert.match(html, /<h1>Review every ranked game\.<\/h1>/);
  assert.match(html, /Free for Windows 10 and 11/);
  assert.doesNotMatch(html, /(?:src|url)\(?['"]?(?:https?:|\/)/, 'no network or server paths');
});

test('parseArgs validates shot names and normalizes the dev server URL', () => {
  const options = parseArgs(['--only', 'home, og', '--base', 'http://127.0.0.1:1434/index.html']);
  assert.deepEqual(options.only, ['home', 'og']);
  assert.equal(options.base, 'http://127.0.0.1:1434');
  assert.equal(options.out, site);
  assert.throws(() => parseArgs(['--only', 'vod']), /Unknown shots: vod/);
  assert.throws(() => parseArgs(['--poster']), /Unknown or incomplete option/);
});

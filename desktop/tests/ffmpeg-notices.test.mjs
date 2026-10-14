import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';

// scripts/Get-PinnedFfmpeg.ps1 is the one place the bundled ffmpeg build is pinned. The
// GPLv3 notices that ship beside it (licenses/ffmpeg) and the attribution in Settings and
// on revu.lol must name that same build, so a pin bump fails here until they are updated.
const repository = new URL('../../', import.meta.url);
const read = relative => readFileSync(new URL(relative, repository), 'utf8').replace(/\r\n/g, '\n');
const visibleText = markup => markup.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

const pin = read('scripts/Get-PinnedFfmpeg.ps1');
const url = pin.match(/^\$url = '([^']+)'$/m)?.[1] ?? '';
const sha256 = pin.match(/^\$expectedSha256 = '([0-9A-F]{64})'$/m)?.[1] ?? '';
const [, tag, version, shortCommit, variant, flavor] =
  /^https:\/\/github\.com\/BtbN\/FFmpeg-Builds\/releases\/download\/(autobuild-[\d-]+)\/ffmpeg-(n[\d.]+-\d+-g([0-9a-f]{7,}))-(win64-(gpl|lgpl)(?:-[\d.]+)?)\.zip$/
    .exec(url) ?? [];
const readme = read('licenses/ffmpeg/README.txt');
const commit = readme.match(/^FFmpeg commit +([0-9a-f]{40})\b/m)?.[1] ?? '';
const notices = new URL('licenses/ffmpeg/', repository);

test('the pin names a BtbN release, an FFmpeg revision and a GPL variant', () => {
  assert.ok(tag && version && variant, `unrecognised ffmpeg pin: ${url}`);
  assert.match(sha256, /^[0-9A-F]{64}$/);
  // licenses/ffmpeg describes a GPLv3 build; an LGPL pin needs LGPL notices instead.
  assert.equal(flavor, 'gpl');
  assert.ok(commit.startsWith(shortCommit), `README.txt names FFmpeg commit ${commit}, the pin ${shortCommit}`);
});

test('README.txt identifies the pinned build and how to get its source', () => {
  for (const value of [url, sha256.toLowerCase(), tag, version, variant,
    `https://github.com/FFmpeg/FFmpeg/tree/${commit}`, `https://github.com/BtbN/FFmpeg-Builds/tree/${tag}`]) {
    assert.ok(readme.includes(value), value);
  }
  assert.match(readme, /GNU General Public License,\nversion 3 or \(at your option\) any later version/);
  assert.match(readme, /COPYING\.GPLv3\.txt/);
  assert.match(readme, /Written offer: for at least three years after Revu last distributes this build/);
  assert.match(readme, /--enable-gpl --enable-version3/);
  assert.match(readme, /--enable-libx264/);
});

test('COPYING.GPLv3.txt is the complete GPLv3 text', () => {
  const license = read('licenses/ffmpeg/COPYING.GPLv3.txt');
  assert.match(license, /^\s*GNU GENERAL PUBLIC LICENSE\n\s*Version 3, 29 June 2007\n/);
  assert.match(license, /END OF TERMS AND CONDITIONS/);
  assert.match(license, /How to Apply These Terms to Your New Programs/);
});

test('LIBRARIES.txt matches the pin and lists exactly the license texts in third-party', () => {
  const libraries = read('licenses/ffmpeg/LIBRARIES.txt');
  const intro = libraries.slice(0, libraries.indexOf('\nFFmpeg\n')).replace(/\s+/g, ' ');
  assert.ok(intro.includes(`release ${tag} (variant ${variant})`), `${tag} ${variant}`);
  assert.match(libraries, new RegExp(`^FFmpeg\\n  Source: +https://github\\.com/FFmpeg/FFmpeg\\.git\\n  Revision: +${commit}$`, 'm'));
  for (const name of ['x264', 'x265', 'LAME']) assert.match(libraries, new RegExp(`^${name}\\n  Source:`, 'm'), name);
  const listed = [...libraries.matchAll(/third-party\/[^\s,]+/g)].map(([match]) => match).sort();
  const shipped = readdirSync(new URL('third-party/', notices), { recursive: true })
    .map(entry => `third-party/${String(entry).replaceAll('\\', '/')}`)
    .filter(relative => statSync(new URL(relative, notices)).isFile()).sort();
  assert.deepEqual([...new Set(listed)], shipped);
});

test('Settings names the bundled FFmpeg build, its license and where the notices are', () => {
  const html = read('desktop/ui/settings.html');
  const card = html.slice(html.indexOf('<details class="set-card" id="licenses-settings"'), html.indexOf('</details>',
    html.indexOf('id="licenses-settings"')));
  for (const word of ['ffmpeg', 'licenses', 'gpl', 'about']) assert.match(card, new RegExp(`data-search="[^"]*\\b${word}\\b`), word);
  const text = visibleText(card);
  for (const value of [version, commit, tag, variant, 'GNU General Public License version 3', 'MIT License',
    '%LOCALAPPDATA%\\LoLReview\\current\\licenses\\ffmpeg']) assert.ok(text.includes(value), value);
  assert.doesNotMatch(text, /—/);
});

test('revu.lol names the build and its source next to the download', () => {
  const terms = read('site/terms.html');
  const section = terms.slice(terms.indexOf('<h2 id="third-party">'), terms.indexOf('<h2>', terms.indexOf('<h2 id="third-party">')));
  const text = visibleText(section);
  for (const value of [version, commit, tag, variant, 'GNU General Public License version 3']) assert.ok(text.includes(value), value);
  for (const href of [`https://github.com/FFmpeg/FFmpeg/tree/${commit}`, `https://github.com/BtbN/FFmpeg-Builds/tree/${tag}`,
    'https://github.com/samif0/lol-review/tree/main/licenses/ffmpeg']) assert.ok(section.includes(`href="${href}"`), href);
  const home = read('site/index.html');
  const hero = home.slice(home.indexOf('id="download-btn"'), home.indexOf('</section>', home.indexOf('id="download-btn"')));
  assert.match(hero, /Includes FFmpeg under the GPLv3: <a href="\/terms#third-party">licenses and source code<\/a>/);
});

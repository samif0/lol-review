// Self-hosted hls.js (site/vendor/hls-1.7.3/). Until the file is vendored, vod-player.js has
// no pin and refuses to load anything (the page says the game couldn't be played). Once it
// is, the SRI the loader uses must match the file, and the licence travels with it.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const site = new URL('../', import.meta.url);
const vendor = new URL('vendor/hls-1.7.3/', site);
const vendored = existsSync(vendor);
const source = readFileSync(new URL('vod-player.js', site), 'utf8');

function player() {
  const context = {};
  vm.runInNewContext(source, context);
  return context.RevuVodPlayer;
}

test('the loader pins hls.js by path and SRI, and has no pin while the file is not vendored', () => {
  const P = player();
  assert.equal(P.HLS_SRC, '/vendor/hls-1.7.3/hls.light.min.js');
  if (vendored) assert.match(P.HLS_INTEGRITY, /^sha384-[A-Za-z0-9+/]{64}$/);
  else assert.equal(P.HLS_INTEGRITY, '', 'no file, no pin');
  // One constant, read by the loader and nothing else; the loader refuses an empty pin.
  assert.equal(source.match(/HLS_INTEGRITY = /g).length, 1);
  assert.match(source, /if \(!INTEGRITY\.test\(HLS_INTEGRITY\)\) return Promise\.reject\(/);
  assert.match(source, /script\.integrity = HLS_INTEGRITY;/);
});

test('vendor/hls-1.7.3 holds hls.js 1.7.3 light, matching the SRI the loader uses, with LICENSE.txt beside it',
  { skip: vendored ? false : 'hls.js not vendored yet' }, () => {
    assert.deepEqual(readdirSync(new URL('vendor/', site)), ['hls-1.7.3']);
    const files = readdirSync(vendor).sort();
    assert.ok(files.includes('hls.light.min.js'), 'the light build');
    assert.ok(files.includes('LICENSE.txt'), 'the licence, with an extension (deploy-site.mjs refuses LICENSE)');
    for (const file of files) assert.match(file, /^(hls\.light\.min\.js|LICENSE(-[\w.-]+)?\.txt)$/, `vendor/hls-1.7.3/${file}`);
    const bytes = readFileSync(new URL('hls.light.min.js', vendor));
    assert.equal(player().HLS_INTEGRITY, `sha384-${createHash('sha384').update(bytes).digest('base64')}`,
      'the vendored file changed: recompute HLS_INTEGRITY in vod-player.js (site/README.md, Vendored hls.js)');
    assert.ok(readFileSync(new URL('LICENSE.txt', vendor), 'utf8').trim().length > 0);
  });

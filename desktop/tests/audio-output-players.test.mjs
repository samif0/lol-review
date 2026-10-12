import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { createTransport } from '../ui/vodtransport.js';

// Every element that plays game or clip audio follows the Settings output choice.
// The VOD player (#vp-video) and the Patterns inline player (#m-video) route through
// the shared transport; the narration studio routes its own preview (.vp-nr-video),
// covered in clip-narration-studio.test.mjs.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFile(new URL(name, ui), 'utf8');
const flush = async (n = 6) => { for (let i = 0; i < n; i++) await new Promise(resolve => setTimeout(resolve, 0)); };

function withBrowser(fn) {
  const storage = new Map([['revu.playback.output', JSON.stringify({ deviceId: 'phones', label: 'Headphones' })]]);
  const window = new EventTarget();
  const mediaDevices = new EventTarget();
  mediaDevices.list = [{ kind: 'audiooutput', deviceId: 'default', label: 'Default' },
    { kind: 'audiooutput', deviceId: 'phones', label: 'Headphones' }, { kind: 'audiooutput', deviceId: 'spk', label: 'Speakers' }];
  mediaDevices.enumerateDevices = async () => mediaDevices.list;
  const globals = { window, navigator: { mediaDevices },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } };
  const previous = {};
  for (const [name, value] of Object.entries(globals)) {
    previous[name] = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
  }
  const restore = () => {
    for (const [name, descriptor] of Object.entries(previous)) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
  };
  return Promise.resolve(fn({ storage, window, mediaDevices })).finally(restore);
}
function video() {
  const element = new EventTarget();
  return Object.assign(element, { sinkId: '', sinks: [], muted: false, volume: 1, playbackRate: 1, paused: true,
    async setSinkId(id) { this.sinks.push(id); this.sinkId = id; } });
}

test('a player transport plays on the chosen output and follows later changes', () => withBrowser(async ({ storage, window, mediaDevices }) => {
  const element = video();
  const transport = createTransport({ video: element });
  await flush();
  assert.deepEqual(element.sinks, [], 'nothing is routed before the page attaches its player');
  transport.attachVideo({ clickToToggle: false });
  transport.attachVideo({ clickToToggle: false }); // attaching twice binds once
  await flush();
  assert.deepEqual(element.sinks, ['phones']);
  // Settings saves another output in a different page.
  storage.set('revu.playback.output', JSON.stringify({ deviceId: 'spk', label: 'Speakers' }));
  window.dispatchEvent(Object.assign(new Event('storage'), { key: 'revu.playback.output' }));
  await flush();
  assert.deepEqual(element.sinks, ['phones', 'spk']);
  // The speakers are unplugged: playback continues on the system default.
  mediaDevices.list = mediaDevices.list.filter(device => device.deviceId !== 'spk');
  mediaDevices.dispatchEvent(new Event('devicechange'));
  await flush();
  assert.equal(element.sinkId, '');
}));

test('a player without output switching still plays', () => withBrowser(async () => {
  const element = new EventTarget();
  Object.assign(element, { muted: false, volume: 1, playbackRate: 1 });
  assert.doesNotThrow(() => createTransport({ video: element }).attachVideo({ clickToToggle: false }));
  await flush();
}));

test('the VOD and Patterns players attach their videos to the shared transport', async () => {
  const vod = await read('vodplayer.js');
  assert.match(vod, /const video = \(\) => \$\('vp-video'\);/);
  const vodBoot = vod.slice(vod.indexOf('async function boot()'));
  assert.match(vodBoot, /_T = createTransport\(\{\s*video: video\(\),/);
  assert.match(vodBoot, /_T\.attachVideo\(/);
  const patterns = await read('patterns.js');
  const patternsBoot = patterns.slice(patterns.indexOf('async function boot()'));
  assert.match(patternsBoot, /_T = createTransport\(\{\s*video: \$\('m-video'\),/);
  assert.match(patternsBoot, /_T\.attachVideo\(/);
  const narration = await read('clip-narration.mjs');
  assert.match(narration, /const video = q\('video'\);/);
  assert.match(narration, /bindOutputSink\(video\)/);
});

test('every audible element in the app is one of the routed players', async () => {
  const files = (await readdir(ui)).filter(name => /\.(html|m?js)$/.test(name));
  const found = [];
  for (const name of files) {
    const source = await read(name);
    if (name.endsWith('.html')) {
      for (const [tag] of source.replace(/<!--[\s\S]*?-->/g, '').matchAll(/<(?:video|audio)\b[^>]*>/g))
        found.push(`${name}: ${(tag.match(/(?:id|class)="[^"]+"/g) || []).join(' ')}`);
    }
    assert.doesNotMatch(source, /new Audio\(|createElement\(\s*['"](?:audio|video)['"]/, `${name} creates an unrouted player`);
  }
  assert.deepEqual(found.sort(), [
    'patterns.html: class="pat-video" id="m-video"',
    'vodplayer.html: id="vp-video" class="vp-video"',
    'vodplayer.html: class="vp-nr-video"',
  ].sort());
});

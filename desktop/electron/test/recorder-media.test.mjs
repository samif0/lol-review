import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { mp4Layout, mp4Summary, restoreFragmentedLayout, validateRecorderMedia, runRecorderTool } from '../recorder-media.mjs';

function box(type, payload = Buffer.alloc(0)) { const header = Buffer.alloc(8); header.writeUInt32BE(payload.length + 8); header.write(type, 4); return Buffer.concat([header, payload]); }
function largeBox(type, payload) { const header = Buffer.alloc(16); header.writeUInt32BE(1); header.write(type, 4); header.writeBigUInt64BE(BigInt(payload.length + 16), 8); return Buffer.concat([header, payload]); }
const ftyp = box('ftyp', Buffer.from('iso5\0\0\x02\0iso5iso6mp41', 'latin1'));
const init = box('moov', box('mvex'));
const fragments = Buffer.concat([box('moof', box('mfhd')), box('mdat', Buffer.alloc(64, 1)), box('moof', box('mfhd')), box('mdat', Buffer.alloc(64, 2))]);
const index = box('moov', box('trak', Buffer.alloc(48)));
// Layouts written by movflags +hybrid_fragmented: while recording, an empty free
// placeholder and a free box hiding a copy of ftyp precede the fragmented file;
// a clean stop appends a complete moov, then turns the placeholder into one mdat.
const recording = Buffer.concat([ftyp, box('free'), box('free', ftyp), init, fragments]);
const swallowed = Buffer.concat([ftyp, init, fragments]);
const finished = Buffer.concat([ftyp, box('free'), box('mdat', swallowed), index]);
const finishedLarge = Buffer.concat([ftyp, largeBox('mdat', swallowed), index]);
const layouts = {
  fragmented: [Buffer.concat([ftyp, init, fragments]), 'fragmented'],
  'hybrid still recording (complete fragments)': [recording, 'fragmented'],
  // A killed recorder leaves its last mdat cut short; validation never accepts that file.
  'hybrid killed inside a fragment': [recording.subarray(0, recording.length - 20), null],
  'finished hybrid': [finished, 'progressive'],
  'finished hybrid above 4 GiB header': [finishedLarge, 'progressive'],
  faststart: [Buffer.concat([ftyp, index, box('mdat', Buffer.alloc(64, 3))]), 'progressive'],
  'truncated moov': [finished.subarray(0, finished.length - 9), null],
  'placeholder flipped to mdat without moov': [Buffer.concat([ftyp, box('free'), box('mdat', swallowed)]), null],
  'mdat running past EOF': [Buffer.concat([ftyp, init, box('moof'), box('mdat', Buffer.alloc(64))]).subarray(0, -16), null],
  'two moov': [Buffer.concat([finished, index]), null],
  'stop interrupted between trailer moov and placeholder': [Buffer.concat([recording, index]), null],
  'no media payload': [Buffer.concat([ftyp, index, box('mdat')]), null],
  'no ftyp': [Buffer.concat([box('free'), box('mdat', swallowed), index]), null],
  'trailing partial header': [Buffer.concat([finished, Buffer.alloc(5)]), null],
};

async function scratch(t) {
  const root = await realpath(await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-media-')));
  t.after(() => rm(root, { recursive: true, force: true }));
  return root;
}

test('MP4 layout accepts fragmented and finalized hybrid recordings and rejects malformed indexes', async t => {
  const root = await scratch(t);
  for (const [name, [bytes, expected]] of Object.entries(layouts)) {
    const file = path.join(root, `${name.replace(/\W+/g, '-')}.mp4`);
    await writeFile(file, bytes);
    assert.equal(await mp4Layout(file), expected, name);
  }
  const aborted = new AbortController(); aborted.abort();
  await writeFile(path.join(root, 'abort.mp4'), finished);
  await assert.rejects(mp4Layout(path.join(root, 'abort.mp4'), aborted.signal), { code: 'recording-aborted' });
});

test('MP4 summary counts fragments so short recordings can be told apart', async t => {
  const root = await scratch(t), file = path.join(root, 'capture.mp4');
  await writeFile(file, recording);
  assert.deepEqual(await mp4Summary(file), { layout: 'fragmented', fragments: 2 });
  await writeFile(file, finished);
  assert.deepEqual(await mp4Summary(file), { layout: 'progressive', fragments: 0 });
  await writeFile(file, layouts['two moov'][0]);
  assert.equal(await mp4Summary(file), null);
});

test('a finished hybrid whose index never reached the disk gets its recording-time layout back', async t => {
  const root = await scratch(t), file = path.join(root, 'capture.mp4');
  const lost = Buffer.concat([ftyp, box('free'), box('mdat', swallowed)]);
  const lostLarge = Buffer.concat([ftyp, largeBox('mdat', swallowed)]);
  const cases = {
    'index missing': [lost, recording],
    'index missing above 4 GiB': [lostLarge, recording],
    // The file grew but the index pages were never written: zeros after the last fragment.
    'index pages left as zeros': [Buffer.concat([lost, Buffer.alloc(index.length)]), Buffer.concat([recording, Buffer.alloc(index.length)])],
  };
  for (const [name, [damaged, expected]] of Object.entries(cases)) {
    await writeFile(file, damaged);
    assert.equal(await restoreFragmentedLayout(file), true, name);
    assert.ok((await readFile(file)).equals(expected), name);
    assert.equal(await mp4Layout(file), 'fragmented', name);
  }
  const untouched = {
    'a finished hybrid': finished, 'a finished hybrid above 4 GiB': finishedLarge, 'a recording in progress': recording,
    'plain fragmented': layouts.fragmented[0], 'faststart': layouts.faststart[0],
    'a different hidden copy': Buffer.concat([ftyp, box('free'), box('mdat', Buffer.concat([box('ftyp', Buffer.alloc(20)), init, fragments]))]),
    'a torn index past EOF': Buffer.concat([lost, index.subarray(0, 20)]),
    'no fragments to restore': Buffer.concat([ftyp, box('free'), box('mdat', Buffer.concat([ftyp, init, box('mdat', Buffer.alloc(8, 1))]))]),
    'too short': ftyp,
  };
  for (const [name, bytes] of Object.entries(untouched)) {
    await writeFile(file, bytes);
    assert.equal(await restoreFragmentedLayout(file), false, name);
    assert.ok((await readFile(file)).equals(bytes), name);
  }
});

test('validation reports the container layout, restores a lost index and rejects an unindexed file before running tools', async t => {
  const root = await scratch(t);
  const file = path.join(root, 'capture.mp4');
  const runs = [];
  const run = async (tool, args) => {
    runs.push(tool);
    if (args.includes('-show_entries')) return { stdout: JSON.stringify({ format: { duration: '5', format_name: 'mov,mp4,m4a,3gp,3g2,mj2' }, streams: [
      { codec_type: 'video', codec_name: 'h264', width: 160, height: 90, avg_frame_rate: '30/1' },
      { codec_type: 'audio', codec_name: 'aac', channels: 2, sample_rate: '48000' }] }) };
    return { stdout: '', stderr: '[Parsed_showinfo_0 @ 01] n: 0 pts: 0 pts_time:0\n[Parsed_ashowinfo_1 @ 02] n:0 pts:0 pts_time:0 nb_samples:1024' };
  };
  const recorder = { ffmpegPath: path.join(root, 'ffmpeg.exe'), ffprobePath: path.join(root, 'ffprobe.exe') };
  const context = { root, video: { outputWidth: 160, outputHeight: 90, fps: 30 } };
  const result = { hasError: false, filePath: file, duration: 5000, reason: 0, splitCount: 0 };
  for (const [bytes, container] of [[finished, 'progressive'], [recording, 'fragmented'],
    [layouts['placeholder flipped to mdat without moov'][0], 'fragmented']]) {
    await writeFile(file, bytes);
    const media = await validateRecorderMedia(recorder, context, result, undefined, run);
    assert.equal(media.container, container); assert.equal(media.fileSize, bytes.length); assert.equal(media.durationSeconds, 5);
  }
  assert.ok((await readFile(file)).equals(recording), 'the lost index left the recording-time layout');
  assert.equal(runs.length, 12);
  await writeFile(file, layouts['no ftyp'][0]);
  await assert.rejects(validateRecorderMedia(recorder, context, result, undefined, run), { code: 'recording-media-invalid' });
  assert.equal(runs.length, 12);
});

const ffmpegPath = process.env.REVU_FIXTURE_FFMPEG;
const ffprobePath = process.env.REVU_FIXTURE_FFPROBE;
const fixtureSkip = !ffmpegPath || !ffprobePath ? 'Set REVU_FIXTURE_FFMPEG and REVU_FIXTURE_FFPROBE to explicit local tools.' : false;
const options = { timeout: 10_000, maxBuffer: 65_536, windowsHide: true };
const generate = (file, video, audio, movflags = '+frag_keyframe+empty_moov+default_base_moof') => runRecorderTool(ffmpegPath, ['-v', 'error', '-y',
  '-f', 'lavfi', '-i', `testsrc2=size=160x90:rate=30:duration=${video}`,
  '-f', 'lavfi', '-i', `sine=frequency=1000:sample_rate=48000:duration=${audio}`, '-map', '0:v:0', '-map', '1:a:0',
  '-c:v', 'libx264', '-preset', 'ultrafast', '-threads', '2', '-pix_fmt', 'yuv420p', '-g', '30',
  '-c:a', 'aac', '-ac', '2', '-movflags', movflags, '-frag_duration', '500000', file], options);
const fixture = root => ({ file: path.join(root, 'capture.mp4'), recorder: { ffmpegPath, ffprobePath },
  context: { root, video: { outputWidth: 160, outputHeight: 90, fps: 30 } } });

test('generated fMP4 requires actual video and audio at each sample, including an otherwise successful early EOF', { skip: fixtureSkip }, async t => {
  const { file, recorder, context } = fixture(await scratch(t));
  const result = { hasError: false, filePath: file, duration: 6000, reason: 0, splitCount: 0 };
  for (const [video, audio] of [[6, 6], [1, 6], [6, 1], [4, 6]]) {
    await generate(file, video, audio);
    const sampled = [];
    const run = async (tool, args, config) => { if (args.includes('-ss')) sampled.push(Number(args[args.indexOf('-ss') + 1])); return runRecorderTool(tool, args, config); };
    if (video === audio) {
      const media = await validateRecorderMedia(recorder, context, result, undefined, run);
      assert.ok(media.durationSeconds >= 6 && media.durationSeconds < 6.1);
      assert.equal(media.container, 'fragmented');
      assert.equal(sampled.length, 3);
    } else {
      // This is the old validation command: exit 0 does not establish that both
      // requested streams had frames at this seek position.
      await runRecorderTool(ffmpegPath, ['-v', 'error', '-xerror', '-threads', '2', '-ss', '4.2', '-i', file,
        '-map', '0:v:0', '-map', '0:a:0', '-t', '0.2', '-f', 'null', '-'], options);
      await assert.rejects(validateRecorderMedia(recorder, context, result, undefined, run), { code: 'recording-media-invalid' }, `video ${video}s, audio ${audio}s`);
      assert.ok(sampled.length >= 2, 'metadata and beginning passed before the unequal stream ended');
      if (video === 4) assert.equal(sampled.length, 3, 'the final sample independently rejects early video EOF');
    }
  }
});

test('generated hybrid MP4 finalized on a clean stop validates as an indexed progressive file', { skip: fixtureSkip }, async t => {
  const { file, recorder, context } = fixture(await scratch(t));
  await generate(file, 6, 6, '+frag_keyframe+empty_moov+default_base_moof+hybrid_fragmented');
  assert.equal(await mp4Layout(file), 'progressive');
  const media = await validateRecorderMedia(recorder, context, { hasError: false, filePath: file, duration: 6000, reason: 0, splitCount: 0 });
  assert.equal(media.container, 'progressive');
  assert.ok(media.durationSeconds >= 6 && media.durationSeconds < 6.1);
});

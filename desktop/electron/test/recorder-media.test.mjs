import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, realpath, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { validateRecorderMedia, runRecorderTool } from '../recorder-media.mjs';

const ffmpegPath = process.env.REVU_FIXTURE_FFMPEG;
const ffprobePath = process.env.REVU_FIXTURE_FFPROBE;
test('generated fMP4 requires actual video and audio at each sample, including an otherwise successful early EOF', {
  skip: !ffmpegPath || !ffprobePath ? 'Set REVU_FIXTURE_FFMPEG and REVU_FIXTURE_FFPROBE to explicit local tools.' : false,
}, async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-media-'));
  const root = await realpath(directory);
  t.after(() => rm(root, { recursive: true, force: true }));
  const file = path.join(root, 'capture.mp4');
  const recorder = { ffmpegPath, ffprobePath };
  const context = { root, video: { outputWidth: 160, outputHeight: 90, fps: 30 } };
  const result = { hasError: false, filePath: file, duration: 6000, reason: 0, splitCount: 0 };
  const options = { timeout: 10_000, maxBuffer: 65_536, windowsHide: true };
  for (const [video, audio] of [[6, 6], [1, 6], [6, 1], [4, 6]]) {
    await runRecorderTool(ffmpegPath, ['-v', 'error', '-y', '-f', 'lavfi', '-i', `testsrc2=size=160x90:rate=30:duration=${video}`,
      '-f', 'lavfi', '-i', `sine=frequency=1000:sample_rate=48000:duration=${audio}`, '-map', '0:v:0', '-map', '1:a:0',
      '-c:v', 'libx264', '-preset', 'ultrafast', '-threads', '2', '-pix_fmt', 'yuv420p', '-g', '30',
      '-c:a', 'aac', '-ac', '2', '-movflags', '+frag_keyframe+empty_moov+default_base_moof', file], options);
    const sampled = [];
    const run = async (tool, args, config) => { if (args.includes('-ss')) sampled.push(Number(args[args.indexOf('-ss') + 1])); return runRecorderTool(tool, args, config); };
    if (video === audio) {
      const media = await validateRecorderMedia(recorder, context, result, undefined, run);
      assert.ok(media.durationSeconds >= 6 && media.durationSeconds < 6.1);
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

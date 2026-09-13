import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectEncoder, encoderPolicy, type RecorderConfig } from '../src/recording-policy.ts';
import { dimensions } from '../src/policy.ts';

const config: RecorderConfig = { sourceWidth: 1920, sourceHeight: 1080, preset: '720p30', allowSoftwareEncoder: false };
const nv = { type: 'obs_nvenc_h264_tex', codec: 'h264' };
const amf = { type: 'h264_texture_amf', codec: 'h264' };
const qsv = { type: 'obs_qsv11_v2', codec: 'h264' };
const sw = { type: 'obs_x264', codec: 'h264' };

test('a supported provider default takes precedence over fixed vendor ordering', () => {
  assert.equal(selectEncoder({ defaultEncoder: amf.type, encoders: [nv, amf] } as never, config), amf);
});
test('software default cannot override hardware and software fallback needs opt-in', () => {
  assert.equal(selectEncoder({ defaultEncoder: sw.type, encoders: [sw, qsv] } as never, { ...config, allowSoftwareEncoder: true }), qsv);
  assert.throws(() => selectEncoder({ defaultEncoder: sw.type, encoders: [sw] } as never, config));
  assert.equal(selectEncoder({ defaultEncoder: sw.type, encoders: [sw] } as never, { ...config, allowSoftwareEncoder: true }), sw);
});
test('explicit selection never substitutes a different encoder or bypasses software consent', () => {
  const video = { encoders: [nv, amf, sw] } as never;
  assert.equal(selectEncoder(video, { ...config, encoder: 'h264_texture_amf' }), amf);
  assert.throws(() => selectEncoder(video, { ...config, encoder: 'obs_qsv11_v2' }));
  assert.throws(() => selectEncoder(video, { ...config, encoder: 'obs_x264' }));
  assert.throws(() => selectEncoder({ encoders: [{ ...nv, codec: 'av1' }] } as never, config));
});
test('NVENC policy removes optional work even when property metadata is absent', () => {
  const settings = encoderPolicy(nv as never, '1080p60');
  assert.equal(settings.bitrate, 12000); assert.equal(settings.rate_control, 'CBR');
  assert.equal(settings.preset, 'p3'); assert.equal(settings.multipass, 'disabled');
  assert.equal(settings.lookahead, false); assert.equal(settings.psycho_aq, false);
});
test('contradictory runtime enum/range metadata fails before recording', () => {
  assert.throws(() => encoderPolicy({ ...nv, properties: { preset: { values: ['p5'] } } } as never, '720p30'));
  assert.throws(() => encoderPolicy({ ...nv, properties: { bitrate: { min: 6000 } } } as never, '720p30'));
  assert.throws(() => encoderPolicy({ ...nv, properties: { bitrate: { max: 10000 } } } as never, '1080p60'));
  assert.equal(encoderPolicy({ ...nv, properties: { bitrate: { min: 1000, max: 20000 } } } as never, '1080p30').bitrate, 8000);
  assert.equal(encoderPolicy({ ...nv, properties: { bitrate: { values: [8000], min: 1000, max: 20000 } } } as never, '720p30').bitrate, 5000);
});
test('vendor settings remain scoped to their documented encoder', () => {
  const amd = encoderPolicy(amf as never, '720p30'), intel = encoderPolicy(qsv as never, '720p30');
  assert.equal(amd.preset, 'speed'); assert.equal(amd.lookahead, undefined);
  assert.equal(intel.target_usage, 'TU6'); assert.equal(intel.enhancements, false); assert.equal(intel.preset, undefined);
});
test('detail preset preserves non-16:9 aspect ratio and never upscales', () => {
  assert.deepEqual(dimensions(1024, 768, '1080p30'), { baseWidth: 1024, baseHeight: 768, outputWidth: 1024, outputHeight: 768, fps: 30 });
  assert.deepEqual(dimensions(3440, 1440, '1080p30'), { baseWidth: 3440, baseHeight: 1440, outputWidth: 1920, outputHeight: 802, fps: 30 });
  assert.throws(() => dimensions(1920, 1080, 'unknown' as never));
});

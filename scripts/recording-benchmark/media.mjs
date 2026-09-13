import { execFileSync } from 'node:child_process';
import { statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const numericMetadata = value => ((typeof value === 'number' && Number.isFinite(value)) ||
  (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value)))) ? Number(value) : null;

export function inspectMedia(file, ffprobe = 'ffprobe') {
  const probe = JSON.parse(execFileSync(ffprobe, ['-v', 'error', '-show_entries', 'format=duration,size,format_name:stream=index,codec_name,codec_type,width,height,avg_frame_rate,start_time,duration,nb_frames,sample_rate,channels', '-of', 'json', file], { encoding: 'utf8', windowsHide: true, timeout: 15000, maxBuffer: 65536 }));
  const video = probe.streams.find(s => s.codec_type === 'video');
  const audio = probe.streams.find(s => s.codec_type === 'audio');
  const number = numericMetadata;
  const difference = (a, b) => a !== null && b !== null ? (a - b) * 1000 : null;
  const end = stream => number(stream?.start_time) !== null && number(stream?.duration) !== null ? number(stream.start_time) + number(stream.duration) : null;
  const bytes = statSync(file).size; const duration = number(probe.format?.duration);
  return { probe, bytes, sizeMB: bytes / 1e6, meanTotalBitrateKbps: duration > 0 ? bytes * 8 / duration / 1000 : null,
    audioMinusVideoStartMs: difference(number(audio?.start_time), number(video?.start_time)), audioMinusVideoEndMs: difference(end(audio), end(video)),
    avSyncVerified: false, timingLimit: 'Container timestamps/durations are structural checks. Matching timestamps do not establish content synchronization or long-match drift; inspect paired visual/audio events.' };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error('Usage: node media.mjs VIDEO [FFPROBE_PATH]');
  console.log(JSON.stringify(inspectMedia(process.argv[2], process.argv[3]), null, 2));
}

import type {} from '@overwolf/ow-electron-packages-types';

type Recorder = overwolf.packages.OverwolfPackageManager['recorder'];
type VideoInfo = Awaited<ReturnType<Recorder['queryInformation']>>['video'];
export const PRESETS = {
  '720p30': { width: 1280, height: 720, fps: 30, bitrate: 5000 },
  '1080p30': { width: 1920, height: 1080, fps: 30, bitrate: 8000 },
  '1080p60': { width: 1920, height: 1080, fps: 60, bitrate: 12000 }
} as const;
export type RecordingPreset = keyof typeof PRESETS;
export const H264_ENCODERS = ['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2', 'obs_x264'] as const;
export type H264Encoder = typeof H264_ENCODERS[number];
export interface RecorderConfig {
  sourceWidth: number;
  sourceHeight: number;
  preset: RecordingPreset;
  allowSoftwareEncoder: boolean;
  /** Exact diagnostic override. An unavailable request fails; it never silently falls back. */
  encoder?: H264Encoder;
}

export function selectEncoder(video: VideoInfo, config: RecorderConfig) {
  const allowed = (type: string): type is H264Encoder => H264_ENCODERS.includes(type as H264Encoder) &&
    (type !== 'obs_x264' || config.allowSoftwareEncoder);
  const find = (type: string) => allowed(type) ? video.encoders.find(e => e.type === type && e.codec === 'h264') : undefined;
  if (config.encoder !== undefined) {
    const explicit = find(config.encoder);
    if (!explicit) throw new Error('requested-encoder-unavailable');
    return explicit;
  }
  // The provider default is a preference, not proof of game/encoder adapter affinity.
  const hardwareDefault = video.defaultEncoder !== 'obs_x264' && find(video.defaultEncoder);
  const chosen = hardwareDefault || H264_ENCODERS.map(find).find(Boolean);
  if (!chosen) throw new Error('required-encoder-unavailable');
  return chosen;
}

/** Research starting points, not a measured performance guarantee. No raw passthrough flags. */
export function encoderPolicy(encoder: VideoInfo['encoders'][number], preset: RecordingPreset) {
  const settings: Record<string, string | number | boolean> = {
    type: encoder.type, bitrate: PRESETS[preset].bitrate, keyint_sec: 2, rate_control: 'CBR'
  };
  switch (encoder.type) {
    case 'obs_nvenc_h264_tex':
      Object.assign(settings, { preset: 'p3', multipass: 'disabled', lookahead: false, psycho_aq: false }); break;
    case 'h264_texture_amf': Object.assign(settings, { preset: 'speed' }); break;
    case 'obs_qsv11_v2': Object.assign(settings, { target_usage: 'TU6', enhancements: false }); break;
    case 'obs_x264': Object.assign(settings, { preset: 'ultrafast' }); break;
    default: throw new Error('required-encoder-unavailable');
  }
  // Older packages may omit property metadata. Apply only the documented contract;
  // if live metadata explicitly contradicts it, stop before starting native capture.
  for (const [key, value] of Object.entries(settings)) {
    const property = encoder.properties?.[key];
    if (!property) continue;
    if (typeof value === 'string' && property.values?.length && !property.values.includes(value)) throw new Error('encoder-property-unsupported');
    if (typeof value === 'number' && ((property.min !== undefined && value < property.min) ||
        (property.max !== undefined && value > property.max))) throw new Error('encoder-property-unsupported');
  }
  return settings;
}

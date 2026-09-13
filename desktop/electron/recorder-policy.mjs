export const LEAGUE_ID = 5426;
export const LEAGUE_EXE = 'League of Legends.exe';
export const RECORDING_PRESETS = Object.freeze({
  '720p30': Object.freeze({ width: 1280, height: 720, fps: 30, bitrate: 5000 }),
  '1080p30': Object.freeze({ width: 1920, height: 1080, fps: 30, bitrate: 8000 }),
  '1080p60': Object.freeze({ width: 1920, height: 1080, fps: 60, bitrate: 12000 }),
});
const hardware = ['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2'];

export class RecorderAdapterError extends Error {
  constructor(code) { super(code); this.name = 'RecorderAdapterError'; this.code = code; }
}
export const recorderError = code => new RecorderAdapterError(code);
export function checkRecordingAbort(signal) { if (signal?.aborted) throw recorderError('recording-aborted'); }
export function sourceDimensions(width, height, preset) {
  if (!Object.hasOwn(RECORDING_PRESETS, preset)) throw recorderError('recording-preset-invalid');
  if (![width, height].every(value => Number.isInteger(value) && value >= 64 && value <= 7680))
    throw recorderError('source-dimensions-unavailable');
  const target = RECORDING_PRESETS[preset];
  const scale = Math.min(1, target.width / width, target.height / height);
  return { baseWidth: width, baseHeight: height, outputWidth: Math.floor(width * scale / 2) * 2,
    outputHeight: Math.floor(height * scale / 2) * 2, fps: target.fps, colorFormat: 'NV12', colorSpec: '709', colorRange: 'Partial' };
}

export function selectHardwareEncoder(information) {
  const encoders = information?.video?.encoders;
  if (!Array.isArray(encoders)) throw recorderError('hardware-encoder-unavailable');
  const find = type => hardware.includes(type) ? encoders.find(value => value?.type === type && value.codec === 'h264') : undefined;
  const selected = find(information.video.defaultEncoder) || hardware.map(find).find(Boolean);
  if (!selected) throw recorderError('hardware-encoder-unavailable');
  if (!information.audio?.encoders?.some(value => value?.type === 'ffmpeg_aac' && value.codec === 'aac'))
    throw recorderError('audio-encoder-unavailable');
  return selected;
}

export function hardwareEncoderSettings(encoder, preset) {
  if (!Object.hasOwn(RECORDING_PRESETS, preset)) throw recorderError('recording-preset-invalid');
  const value = { type: encoder.type, bitrate: RECORDING_PRESETS[preset].bitrate, keyint_sec: 2, rate_control: 'CBR' };
  // Documented SDK settings, starting points pending live performance verification.
  if (encoder.type === hardware[0]) Object.assign(value, { preset: 'p3', multipass: 'disabled', lookahead: false, psycho_aq: false });
  else if (encoder.type === hardware[1]) Object.assign(value, { preset: 'speed' });
  else if (encoder.type === hardware[2]) Object.assign(value, { target_usage: 'TU6', enhancements: false });
  else throw recorderError('hardware-encoder-unavailable');
  for (const [name, setting] of Object.entries(value)) {
    const property = encoder.properties?.[name];
    if (!property) continue;
    if (typeof setting === 'string' && property.values?.length && !property.values.includes(setting))
      throw recorderError('encoder-property-unsupported');
    if (typeof setting === 'number' && ((Number.isFinite(property.min) && setting < property.min) ||
        (Number.isFinite(property.max) && setting > property.max))) throw recorderError('encoder-property-unsupported');
  }
  return value;
}

export function configureGameCapture(builder, { pid, video, encoder }) {
  if (builder?.videoEncoderSettings?.type !== encoder.type) throw recorderError('encoder-selection-mismatch');
  builder.videoSettings = { ...builder.videoSettings, ...video };
  Object.assign(builder.videoEncoderSettings, encoder);
  const audio = { sampleRate: 48000, speakerLayer: 'SPEAKERS_STEREO', lowLatencyAudioBuffering: false };
  Object.assign(builder.audioSettings, audio);
  const properties = { gameProcess: pid, captureCursor: true, captureOverlays: false, limitFramerate: true, sliCompatibility: false };
  builder.addGameSource({ ...properties });
  builder.addApplicationAudioCapture({ processName: LEAGUE_EXE }, { volume: 1, filters: [] });
  const settings = builder.build();
  const matches = (actual, expected) => actual && Object.entries(expected).every(([key, value]) => actual[key] === value);
  if (!matches(settings?.videoSettings, video) || !matches(settings?.videoEncoderSettings, encoder) ||
      settings?.audioEncoder?.type !== 'ffmpeg_aac' || settings.audioEncoder.codec !== 'aac' || !matches(settings.audioSettings, audio))
    throw recorderError('capture-settings-mismatch');
  const applications = settings.audioSettings.applications;
  if (settings.sources?.length !== 1 || settings.sources[0].type !== 'Game' || !matches(settings.sources[0].properties, properties) ||
      !Array.isArray(settings.audioSettings.inputs) || settings.audioSettings.inputs.length !== 0 ||
      !Array.isArray(settings.audioSettings.outputs) || settings.audioSettings.outputs.length !== 0 ||
      applications?.length !== 1 || applications[0].name !== LEAGUE_EXE || applications[0].type !== 'output' ||
      (applications[0].volume ?? 1) !== 1 || (applications[0].filters?.length ?? 0) !== 0)
    throw recorderError('capture-scope-mismatch');
  return settings;
}

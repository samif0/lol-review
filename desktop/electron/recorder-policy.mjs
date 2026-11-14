export const RECORDING_PRESETS = Object.freeze({
  '720p30': Object.freeze({ width: 1280, height: 720, fps: 30, bitrate: 5000 }),
  '1080p30': Object.freeze({ width: 1920, height: 1080, fps: 30, bitrate: 8000 }),
  '1080p60': Object.freeze({ width: 1920, height: 1080, fps: 60, bitrate: 12000 }),
});

export class RecorderAdapterError extends Error {
  constructor(code) { super(code); this.name = 'RecorderAdapterError'; this.code = code; }
}
export const recorderError = code => new RecorderAdapterError(code);
export function checkRecordingAbort(signal) { if (signal?.aborted) throw recorderError('recording-aborted'); }

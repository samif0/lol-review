import { micErrorMessage } from './narration-capture.mjs';

// Microphone helpers for the narration studio (clip-narration.mjs): opening the
// narration microphone and listing microphones in the studio picker. Browser
// globals are read on each call.

const constraints = deviceId => ({ audio: { deviceId: deviceId ? { exact: deviceId } : undefined, echoCancellation: true,
  noiseSuppression: true, autoGainControl: true, channelCount: { ideal: 1 }, sampleRate: { ideal: 48000 } }, video: false });

/**
 * Open the narration microphone (no deviceId = system default). A device that
 * refuses its id falls back to the system default, unless cancelled() reports that
 * the studio no longer wants this microphone; then the refusal is rethrown.
 */
export async function openNarrationMic(deviceId, { accessStatus = 'unknown', cancelled = () => false } = {}) {
  const devices = globalThis.navigator.mediaDevices;
  try { return await devices.getUserMedia(constraints(deviceId)); }
  catch (err) {
    if (deviceId && micErrorMessage(err, accessStatus) === null && !cancelled()) return devices.getUserMedia(constraints(undefined));
    throw err;
  }
}

/**
 * List microphones in the studio picker: Default first, the Windows communications
 * device last, activeId selected. Returns a map of device id to name.
 */
export async function fillMicSelect(select, activeId) {
  const labels = new Map();
  if (!select) return labels;
  let inputs = [];
  try { inputs = (await globalThis.navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === 'audioinput'); } catch (_) { /* keep empty */ }
  const rank = device => device.deviceId === 'default' ? 0 : device.deviceId === 'communications' ? 2 : 1;
  inputs.sort((a, b) => rank(a) - rank(b));
  for (const device of inputs) labels.set(device.deviceId, device.label || '');
  select.replaceChildren(...inputs.map((device, index) => {
    const option = globalThis.document.createElement('option');
    option.value = device.deviceId;
    option.textContent = device.label || `Microphone ${index + 1}`;
    return option;
  }));
  select.value = inputs.some(device => device.deviceId === activeId) ? activeId : (inputs[0]?.deviceId || '');
  return labels;
}

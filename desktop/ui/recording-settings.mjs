import { getInvoke } from './platform/index.mjs';

const UNAVAILABLE_MESSAGE = 'Built-in recording is unavailable here. You can still connect an external recordings folder above.';
const ENCODERS = Object.freeze({ obs_nvenc_h264_tex: 'NVIDIA NVENC', h264_texture_amf: 'AMD AMF', obs_qsv11_v2: 'Intel Quick Sync' });

const STATES = Object.freeze({ disabled: 'Recording off', unavailable: 'Recording unavailable', idle: 'Ready for your next match',
  starting: 'Starting recording', recording: 'Recording your match', stopping: 'Stopping recording', finalizing: 'Saving your match',
  saved: 'Recording saved', partial: 'Partial recording saved', failed: 'Recording failed' });

export function recordingStatusText(status) {
  const heading = STATES[status?.state] || 'Recording status unavailable';
  const message = typeof status?.message === 'string' ? status.message.trim() : '';
  return message ? `${heading}. ${message}` : heading;
}
export function recordingEncoderText(status) {
  const name = id => Object.hasOwn(ENCODERS, id) ? ENCODERS[id] : null;
  const selected = name(status?.selectedEncoder), last = name(status?.lastEncoder);
  if (!status?.available || !selected) return 'A supported NVIDIA, AMD or Intel hardware encoder is required.';
  return `Automatically selected: ${selected}.${last && last !== selected ? ` Last recording used: ${last}.` : ''}`;
}

// The view owns unsaved edits. Polling updates status only, so an in-flight read
// can never change the selected preset or undo a toggle while the user edits.
export function createRecordingSettingsModel({ invoke, renderStatus, renderRecording, renderBackground }) {
  let generation = 0, disposed = false;
  return {
    async load() {
      const current = ++generation;
      const results = await Promise.allSettled([invoke('get_recording_status'), invoke('get_background_settings')]);
      if (disposed || current !== generation) return;
      if (results[0].status === 'fulfilled') {
        renderStatus(results[0].value); renderRecording(results[0].value.settings);
      } else renderStatus({ message: 'Recording settings could not be loaded. Reopen Settings to try again.' });
      if (results[1].status === 'fulfilled') renderBackground(results[1].value);
      else renderBackground(null);
    },
    async refresh() {
      const current = generation;
      const result = await invoke('get_recording_status');
      if (!disposed && current === generation) renderStatus(result);
    },
    async saveRecording(payload) {
      ++generation;
      const result = await invoke('save_recording_settings', { payload });
      if (result?.ok === false) throw new Error(result.message || 'Recording settings could not be saved.');
      if (!disposed) { renderStatus(result); renderRecording(result.settings); }
      return result;
    },
    async saveBackground(payload) {
      const result = await invoke('save_background_settings', { payload });
      if (result?.ok === false) throw new Error(result.message || 'Background settings could not be saved.');
      if (!disposed) renderBackground(result);
      return result;
    },
    dispose() { disposed = true; ++generation; },
  };
}

export async function initializeRecordingSettings({ document = globalThis.document, scope = globalThis.window } = {}) {
  const el = id => document.getElementById(id);
  if (!el('recording-settings')) return;
  let available = false;
  if (el('recording-controls')) el('recording-controls').hidden = true;
  let disposed = false, polling = false, savingRecording = false, model = null, timer = null, onVisibility = null;
  let backgroundValue = null;
  scope.addEventListener('pagehide', () => {
    disposed = true; model?.dispose();
    if (timer !== null) scope.clearInterval(timer);
    if (onVisibility) document.removeEventListener('visibilitychange', onVisibility);
  }, { once: true });
  const controls = ['recordingEnabled', 'recordingPreset', 'save-recording-settings', 'open-recordings-folder',
    'closeToTray', 'startWithWindows', 'save-background-settings'];
  const setToggle = (id, on) => {
    const button = el(id);
    button.setAttribute('aria-checked', on ? 'true' : 'false'); button.classList.toggle('on', !!on);
  };
  const toggleValue = id => el(id).getAttribute('aria-checked') === 'true';
  const text = (id, value) => { el(id).textContent = value; };
  const lockRecording = () => {
    ['recordingEnabled', 'recordingPreset', 'save-recording-settings', 'open-recordings-folder']
      .forEach(id => { el(id).disabled = true; });
    setToggle('recordingEnabled', false);
    text('recording-status', UNAVAILABLE_MESSAGE);
  };
  lockRecording();
  let invoke;
  try { invoke = await getInvoke(); } catch { /* A broken host must leave editing unavailable. */ }
  if (disposed) return;
  if (!invoke) {
    controls.forEach(id => { el(id).disabled = true; });
    text('recording-status', 'Open the Revu desktop app to manage automatic recording.');
    text('background-settings-status', 'Background settings are available in the Revu desktop app.');
    return;
  }
  model = createRecordingSettingsModel({ invoke,
    renderStatus: value => {
      available = value?.available === true;
      if (el('recording-controls')) el('recording-controls').hidden = !available;
      if (el('recording-coming-soon')) el('recording-coming-soon').textContent = available
        ? value?.recordingTest ? 'Recording test' : 'Beta' : 'Unavailable';
      if (el('recordingEnabled-label')) el('recordingEnabled-label').textContent = value?.recordingTest
        ? 'Record my test game' : 'Record my ranked Solo/Duo matches';
      if (el('recording-scope-help')) el('recording-scope-help').textContent = value?.recordingTest
        ? 'Start a Practice Tool game in Borderless mode. This launch saves a test video separately from your normal match history.'
        : 'Capture the League window and game audio. Use Borderless mode. Your desktop and microphone are excluded.';
      if (el('recording-encoder')) el('recording-encoder').textContent = recordingEncoderText(value);
      if (!available) lockRecording();
      else ['recordingEnabled', 'recordingPreset', 'save-recording-settings', 'open-recordings-folder']
        .forEach(id => { el(id).disabled = false; });
      text('recording-status', value?.message ? recordingStatusText(value) : UNAVAILABLE_MESSAGE);
    },
    renderRecording: value => {
      if (!available) return;
      if (!value) return;
      setToggle('recordingEnabled', value.enabled);
      el('recordingPreset').value = value.preset;
      ['recordingEnabled', 'recordingPreset', 'save-recording-settings'].forEach(id => { el(id).disabled = false; });
      el('open-recordings-folder').disabled = false;
    },
    renderBackground: value => {
      if (!value) { text('background-settings-status', 'Background settings could not be loaded. Reopen Settings to try again.'); return; }
      backgroundValue = value;
      setToggle('closeToTray', value.closeToTray); setToggle('startWithWindows', value.startWithWindows);
      el('closeToTray').disabled = value.trayAvailable === false;
      el('startWithWindows').disabled = value.startupAvailable === false;
      el('save-background-settings').disabled = false;
      text('background-settings-status', value.message || (value.startupAvailable === false
        ? 'Start with Windows is available after installing Revu.' : 'Background preferences are saved separately below.'));
    },
  });
  const save = async (kind, operation) => {
    const button = el(`save-${kind}-settings`), status = `${kind}-settings-status`;
    if (button.disabled || (kind === 'recording' && !available)) return;
    const fields = kind === 'recording' ? ['recordingEnabled', 'recordingPreset'] : ['closeToTray', 'startWithWindows'];
    fields.forEach(id => { el(id).disabled = true; });
    button.disabled = true; text(status, 'Saving…');
    try { await operation(); if (!disposed) text(status, 'Saved.'); }
    catch (error) { if (!disposed) text(status, error?.message || 'Settings could not be saved. Try again.'); }
    finally {
      if (!disposed) {
        button.disabled = kind === 'recording' && !available;
        fields.forEach(id => { el(id).disabled = kind === 'recording' && !available; });
        if (kind === 'background') {
          el('closeToTray').disabled = backgroundValue?.trayAvailable === false;
          el('startWithWindows').disabled = backgroundValue?.startupAvailable === false;
        }
      }
    }
  };
  el('save-recording-settings').addEventListener('click', () => save('recording', async () => {
    savingRecording = true;
    try { await model.saveRecording({ enabled: toggleValue('recordingEnabled'), preset: el('recordingPreset').value }); }
    finally { savingRecording = false; }
  }));
  el('save-background-settings').addEventListener('click', () => save('background', () => model.saveBackground({
    closeToTray: toggleValue('closeToTray'), startWithWindows: toggleValue('startWithWindows'),
  })));
  el('open-recordings-folder').addEventListener('click', async () => {
    if (!available || el('open-recordings-folder').disabled) return;
    try { await invoke('open_recordings_folder'); }
    catch (error) { text('recording-settings-status', error?.message || 'The recordings folder could not be opened.'); }
  });
  await model.load();
  if (disposed || !available) return;
  const poll = async () => {
    if (disposed || document.hidden || polling || savingRecording) return;
    polling = true;
    try { await model.refresh(); }
    catch { if (!disposed) text('recording-status', 'Recording status could not be refreshed.'); }
    finally { polling = false; }
  };
  timer = scope.setInterval(poll, 2000);
  onVisibility = poll;
  document.addEventListener('visibilitychange', onVisibility);
}

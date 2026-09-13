import { getInvoke } from './platform/index.mjs';

// Product release is independent of whether this machine has Recorder access.
// Developer recorder diagnostics keep their existing native API path.
export const BUILT_IN_RECORDING_AVAILABLE = false;
const COMING_SOON_MESSAGE = 'Automatic match recording is coming in a future update. Use Ascent recordings above for now.';

const STATES = Object.freeze({ disabled: 'Recording off', unavailable: 'Recording unavailable', idle: 'Ready for your next match',
  starting: 'Starting recording', recording: 'Recording your match', stopping: 'Stopping recording', finalizing: 'Saving your match',
  saved: 'Recording saved', partial: 'Partial recording saved', failed: 'Recording failed' });

export function recordingStatusText(status) {
  const heading = STATES[status?.state] || 'Recording status unavailable';
  const message = typeof status?.message === 'string' ? status.message.trim() : '';
  return message ? `${heading}. ${message}` : heading;
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
  if (el('recording-coming-soon')) el('recording-coming-soon').hidden = BUILT_IN_RECORDING_AVAILABLE;
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
    text('recording-status', COMING_SOON_MESSAGE);
  };
  if (!BUILT_IN_RECORDING_AVAILABLE) lockRecording();
  let invoke;
  try { invoke = await getInvoke(); } catch { /* A broken host must leave editing unavailable. */ }
  if (disposed) return;
  if (!invoke) {
    controls.forEach(id => { el(id).disabled = true; });
    text('recording-status', BUILT_IN_RECORDING_AVAILABLE
      ? 'Open the Revu desktop app to manage automatic recording.' : COMING_SOON_MESSAGE);
    text('background-settings-status', 'Background settings are available in the Revu desktop app.');
    return;
  }
  model = createRecordingSettingsModel({ invoke,
    renderStatus: value => BUILT_IN_RECORDING_AVAILABLE ? text('recording-status', recordingStatusText(value)) : lockRecording(),
    renderRecording: value => {
      if (!BUILT_IN_RECORDING_AVAILABLE) { lockRecording(); return; }
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
    if (button.disabled || (kind === 'recording' && !BUILT_IN_RECORDING_AVAILABLE)) return;
    const fields = kind === 'recording' ? ['recordingEnabled', 'recordingPreset'] : ['closeToTray', 'startWithWindows'];
    fields.forEach(id => { el(id).disabled = true; });
    button.disabled = true; text(status, 'Saving…');
    try { await operation(); if (!disposed) text(status, 'Saved.'); }
    catch (error) { if (!disposed) text(status, error?.message || 'Settings could not be saved. Try again.'); }
    finally {
      if (!disposed) {
        button.disabled = false;
        fields.forEach(id => { el(id).disabled = false; });
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
    if (!BUILT_IN_RECORDING_AVAILABLE || el('open-recordings-folder').disabled) return;
    try { await invoke('open_recordings_folder'); }
    catch (error) { text('recording-settings-status', error?.message || 'The recordings folder could not be opened.'); }
  });
  await model.load();
  if (disposed || !BUILT_IN_RECORDING_AVAILABLE) return;
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

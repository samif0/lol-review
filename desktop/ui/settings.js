import { $, show, clear as clearEl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke } from './platform/index.mjs';
import { initializeRecordingSettings } from './recording-settings.mjs';
import { initializeSettingsNavigation, preserveSettingsDraft } from './settings-navigation.mjs';

// Each editable card saves only its own fields through the existing platform
// boundary. Refreshes preserve drafts in other categories. Recording and startup
// remain independently owned by their native settings handlers.

// ── small DOM helpers ───────────────────────────────────────────────────────

// Editable text/number/select inputs ↔ config field names (camelCase wire).
const TEXT_FIELDS = ['ascentFolder', 'clipsFolder', 'backupFolder', 'riotId', 'region'];
// Folder paths get special save handling (P-023): an EMPTY folder input means
// "leave unchanged" (so a not-yet-rendered field on a fetch-race never blanks the
// saved path).
const FOLDER_FIELDS = new Set(['ascentFolder', 'clipsFolder', 'backupFolder']);
const FOLDER_CLEAR_SENTINEL = ' __REVU_CLEAR__ ';
// Riot identity text fields share the folder empty-overwrite hazard but with no
// "clear" affordance: an empty input always means "leave unchanged" (omit the key),
// never "blank the linked account". Guarded in collectPayload below.
const IDENTITY_FIELDS = new Set(['riotId', 'region']);
const NUM_FIELDS = ['clipsMaxSizeMb'];
// role=switch toggle buttons ↔ config bool field names.
const TOGGLE_FIELDS = [
  'backupEnabled', 'tiltFixMode', 'requireReviewNotes',
  'autoTimelineClippingEnabled', 'minimizeDuringGame',
  'autoClipObjectivesEnabled',
];
// Browse buttons ↔ the text field they fill.
const PICK_TARGETS = { pick_ascent: 'ascentFolder', pick_clips: 'clipsFolder', pick_backup: 'backupFolder' };

let _data = null;
let _baseline = {};
let _savingGroup = null;
let _ascentAvailable = null;
let _ascentScanning = false;
let _ascentScanResult = null;
const _nativeBaseline = new Map();
const CONFIG_FIELDS = [...TEXT_FIELDS, ...NUM_FIELDS, ...TOGGLE_FIELDS, 'windowResolution'];
const configValues = () => Object.fromEntries(CONFIG_FIELDS.map(id => [id,
  TOGGLE_FIELDS.includes(id) ? toggleState($(id)) : $(id)?.value ?? '']));
const groupFields = group => CONFIG_FIELDS.filter(id => group?.contains($(id)));
function restoreValues(values) {
  for (const [id, value] of Object.entries(values)) {
    if (TOGGLE_FIELDS.includes(id)) setToggle($(id), value);
    else if ($(id)) $(id).value = value;
  }
}
function cardStatus(group, text, tone) {
  const status = group?.querySelector('[data-save-status]');
  if (!status) return;
  status.textContent = text;
  status.classList.remove('good', 'bad');
  if (tone) status.classList.add(tone);
}
function syncCategoryDrafts() {
  for (const button of document.querySelectorAll('[data-settings-category]')) {
    const section = document.querySelector(`[data-settings-section="${button.dataset.settingsCategory}"]`);
    const dirty = !!section?.querySelector('[data-dirty="true"]');
    button.dataset.dirty = String(dirty);
    button.title = dirty ? 'Contains unsaved changes' : '';
  }
}
function updateDirtyGroups() {
  const values = configValues();
  for (const group of document.querySelectorAll('[data-config-group]')) {
    const dirty = !!_data && groupFields(group).some(id => !Object.is(values[id], _baseline[id]));
    const changed = group.dataset.dirty !== String(dirty);
    group.dataset.dirty = String(dirty);
    group.querySelector('[data-action="save_config"]').disabled = !_data || !!_savingGroup || !dirty;
    group.querySelector('[data-action="discard_config"]').disabled = !_data || _savingGroup === group || !dirty;
    if (changed && _savingGroup !== group) cardStatus(group, dirty ? 'Unsaved changes' : 'No unsaved changes');
  }
  syncAscentControls();
  syncCategoryDrafts();
}

// A configured folder is not proof that recordings exist. Only a completed scan
// supplies a link count; browser previews never pretend to access local files.
function syncAscentControls() {
  const group = $('ascent-settings');
  if (!group) return;
  const ready = !!_data && _ascentAvailable === true;
  const folder = String(_data?.ascentFolder || '').trim();
  const dirty = group.dataset.dirty === 'true';
  const busy = _savingGroup === group || _ascentScanning;
  $('ascentFolder').disabled = !ready || busy;
  $('ascent-browse').disabled = !ready || busy;
  $('ascent-save').disabled = !ready || busy || !!_savingGroup || !dirty || !$('ascentFolder').value.trim();
  $('ascent-save').textContent = folder ? 'Save folder' : 'Connect folder';
  $('ascent-disconnect').disabled = !ready || busy || !!_savingGroup || !folder;
  $('ascent-scan').disabled = !ready || busy || !!_savingGroup || !folder || dirty;
  group.querySelector('[data-action="discard_config"]').disabled = !ready || busy || !dirty;
  let text, tone;
  if (_ascentAvailable === null) text = 'Checking folder linking availability…';
  else if (!_ascentAvailable) text = 'Connect Ascent in the Revu desktop app. Browser preview cannot access local recordings.';
  else if (!_data) text = 'Loading your recordings folder…';
  else if (_ascentScanning) text = 'Scanning Ascent recordings and linking matches…';
  else if (dirty) text = $('ascentFolder').value.trim()
    ? 'Save this folder before scanning or linking recordings.'
    : 'Enter a folder to connect, or use Disconnect to stop automatic linking.';
  else if (_ascentScanResult?.folder === folder) {
    text = _ascentScanResult.text; tone = _ascentScanResult.tone;
  } else text = folder
    ? 'Folder connected. Revu checks for matching recordings automatically. Scan now to check existing videos.'
    : 'No folder connected. Choose the recordings folder configured in Ascent.';
  setStatusEl($('ascent-status'), text, tone, false);
}

async function loadAscentAccess() {
  try { _ascentAvailable = !!(await getInvoke()); }
  catch { _ascentAvailable = false; }
  syncAscentControls();
}

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchConfig() {
  return readSnapshot('get_config', 'sample-settings.json');
}

async function fetchStatus() {
  const invoke = await getInvoke();
  if (invoke) return invoke('get_settings_status');
  // Browser preview: best-effort sample, else a benign empty shape.
  try {
    const res = await fetch('./sample-settings-status.json');
    if (res.ok) return res.json();
  } catch (_) { /* no sample present in preview — fine */ }
  return { ffmpeg: null, clipUsage: null, backups: [] };
}

// ── toggle helpers ──────────────────────────────────────────────────────────
function setToggle(el, on) {
  if (!el) return;
  el.classList.toggle('on', !!on);
  el.setAttribute('aria-checked', on ? 'true' : 'false');
}
function toggleState(el) { return el ? el.getAttribute('aria-checked') === 'true' : false; }

// ── render: editable config surface ──────────────────────────────────────────
function render(d, { savedFields = [] } = {}) {
  const draft = _data ? preserveSettingsDraft(configValues(), _baseline, savedFields) : {};
  _data = d;
  clearError();
  for (const f of TEXT_FIELDS) {
    const el = $(f);
    if (el) el.value = d[f] != null ? String(d[f]) : '';
  }
  for (const f of NUM_FIELDS) {
    const el = $(f);
    if (el) el.value = d[f] != null ? String(d[f]) : '';
  }
  for (const f of TOGGLE_FIELDS) {
    setToggle($(f), !!d[f]);
  }

  // Window size select. Stored "" means the default. A stored non-preset "WxH"
  // (hand-edited config.json — the sidecar guard accepts any sane size) gets a
  // dynamic "Custom" option so the select can REPRESENT it: without one, the
  // fallback-to-Default would be sent back on the next save and silently erase
  // the custom value (the P-020 clobber class). Garbage falls back to Default.
  const winRes = $('windowResolution');
  if (winRes) {
    const stored = d.windowResolution ? String(d.windowResolution) : 'default';
    winRes.querySelector('option[data-custom]')?.remove();
    if (![...winRes.options].some((o) => o.value === stored) && /^\d{3,5}x\d{3,5}$/.test(stored)) {
      const opt = document.createElement('option');
      opt.value = stored;
      opt.dataset.custom = '1';
      opt.textContent = `Custom (${stored.replace('x', ' x ')})`;
      winRes.appendChild(opt);
    }
    winRes.value = stored;
    if (winRes.selectedIndex < 0) winRes.value = 'default';
  }

  // Riot account attachment status. "Attached" means we have a stored identity
  // (riotId) — independent of an active OTP session — so a configured account
  // still reads as attached after a session lapses. The email line is the
  // stronger "signed in this session" signal and stays gated on the live state.
  const signedIn = d.riotAuthState === 'loggedIn' && !!d.riotSessionEmail;
  const attached = signedIn || !!(d.riotId && String(d.riotId).trim());
  const stateEl = $('acct-state');
  const dotEl = $('acct-dot');
  const statusEl = $('acct-status');
  if (stateEl) {
    if (attached) {
      const region = d.region ? ` · ${String(d.region).toUpperCase()}` : '';
      stateEl.textContent = `Account attached: ${d.riotId}${region}`;
    } else {
      stateEl.textContent = 'No account attached';
    }
  }
  if (dotEl) dotEl.classList.toggle('on', attached);
  if (statusEl) statusEl.classList.toggle('on', attached);

  // Account email (display-only; visible when signed in this session).
  const email = $('acct-email');
  if (email) {
    if (signedIn) email.textContent = `Signed in as ${d.riotSessionEmail}`;
    show(email, signedIn);
  }

  // Header status line.
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = d.riotId ? `Connected as ${d.riotId}` : 'Your preferences';
  _baseline = configValues();
  restoreValues(draft);
  for (const id of CONFIG_FIELDS) if ($(id)) $(id).disabled = false;
  updateDirtyGroups();
}

// ── render: read-only diagnostics (ffmpeg / clip usage / backups) ─────────────
function applyStatusText(el, status, fallbackText) {
  if (!el) return;
  const text = status && status.text != null ? status.text : (fallbackText || '');
  el.textContent = text;
  // Server-provided hex applied to style only (never trusted as markup).
  const hex = status && status.colorHex ? status.colorHex : '';
  el.style.color = hex || '';
}

function renderStatus(s) {
  if (!s) return;

  applyStatusText($('ffmpeg-status'), s.ffmpeg, 'ffmpeg status unavailable.');
  applyStatusText($('clip-usage'), s.clipUsage, '');

  // Backups list — each row is selectable; selecting one enables Restore.
  const list = $('backups-list');
  const empty = $('backups-empty');
  const backups = Array.isArray(s.backups) ? s.backups : [];
  if (list) {
    clearEl(list);
    _selectedBackupPath = '';
    syncRestoreEnabled();
    for (const b of backups) {
      const row = document.createElement('div');
      row.className = 'set-backup-row';
      row.setAttribute('role', 'button');
      row.tabIndex = 0;
      if (b.filePath) row.dataset.path = b.filePath;

      const label = document.createElement('div');
      label.className = 'set-backup-label';
      label.textContent = b.label || b.fileName || 'Backup';

      const meta = document.createElement('div');
      meta.className = 'set-backup-meta';
      const parts = [];
      if (b.timestamp) parts.push(String(b.timestamp));
      if (b.sizeMb != null) parts.push(`${b.sizeMb} MB`);
      meta.textContent = parts.join(' · ');

      row.appendChild(label);
      row.appendChild(meta);
      list.appendChild(row);
    }
  }
  show(empty, backups.length === 0);
}

// ── Restore-backup selection + Reset-confirm gating ──────────────────────────
let _selectedBackupPath = '';
function syncRestoreEnabled() {
  const btn = $('restore-btn');
  if (btn) btn.disabled = !_selectedBackupPath;
}
// Select a backup row (delegated; rows re-render on refresh).
document.addEventListener('click', (ev) => {
  const row = ev.target.closest && ev.target.closest('.set-backup-row');
  if (!row || !row.dataset.path) return;
  _selectedBackupPath = row.dataset.path;
  document.querySelectorAll('.set-backup-row').forEach((r) => r.classList.toggle('on', r === row));
  syncRestoreEnabled();
});
document.addEventListener('keydown', event => {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  const row = event.target.closest?.('.set-backup-row');
  if (row) { event.preventDefault(); row.click(); }
});
// Reset button enables only when the confirm box reads exactly RESET.
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.id === 'reset-confirm') {
    const btn = $('reset-btn');
    if (btn) btn.disabled = ev.target.value.trim() !== 'RESET';
  }
});

// ── collect the editable surface into a save payload ────────────────────────
// Only fields the page owns; the sidecar read-modify-writes so unrelated config
// keys (secrets, keybinds, puuid) are never touched.
function collectPayload(group) {
  const p = {};
  for (const f of TEXT_FIELDS) {
    const el = $(f);
    if (!el || !group.contains(el)) continue;
    const v = el.value.trim();
    // Never blank a saved folder when Save runs before the config has rendered.
    if (FOLDER_FIELDS.has(f) && v === '') continue;
    // Same empty-overwrite class for the Riot identity (riotId/region): a save
    // fired before render() populates the inputs would otherwise send "" and blank
    // the linked Riot account, detaching match-history sync (RiotProxyEnabled). The
    // sidecar guards this too, but omit here at the source. There is no "clear Riot
    // ID" affordance, so empty simply means "leave unchanged" — no sentinel needed.
    if (IDENTITY_FIELDS.has(f) && v === '') continue;
    p[f] = v;
  }
  for (const f of NUM_FIELDS) {
    const el = $(f);
    if (el && group.contains(el)) {
      const n = parseInt(el.value, 10);
      // Mirror the WinUI clamp/reject: only send a valid in-range int.
      if (Number.isFinite(n) && n >= 100 && n <= 50000) p[f] = n;
    }
  }
  for (const f of TOGGLE_FIELDS) {
    if (group.contains($(f))) p[f] = toggleState($(f));
  }
  // Window size: only send once the page has rendered real config (_data set) —
  // a pre-hydration save would otherwise send the select's built-in "default"
  // and reset a saved Maximized preference (same P-020/P-023 clobber class).
  const winRes = $('windowResolution');
  if (winRes && _data && group.contains(winRes)) p.windowResolution = winRes.value || 'default';
  return p;
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  const detail = $('err-detail');
  if (detail) detail.textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function loadConfig() {
  if (_loading) return;
  _loading = true;
  const maxAttempts = 25; // cold-start grace while the sidecar binds
  try {
    for (let attempt = 1; ; attempt++) {
      try {
        render(await fetchConfig());
        // Status is a best-effort second read — never blocks the editable page.
        loadStatus();
        return;
      } catch (err) {
        const transient = /sidecar not ready|not ready|connection refused|failed to fetch/i.test(String(err));
        if (transient && attempt < maxAttempts) {
          await new Promise((r) => setTimeout(r, 400));
          continue;
        }
        renderError(err);
        console.error('[settings] load failed:', err);
        return;
      }
    }
  } finally {
    _loading = false;
  }
}

async function loadStatus() {
  try {
    renderStatus(await fetchStatus());
  } catch (err) {
    console.warn('[settings] status load failed (non-fatal):', err);
  }
}

// ── generic + save status helpers (auto-clear, mirror the WinUI 2s clear) ────
function setStatusEl(el, text, tone, autoClear) {
  if (!el) return;
  el.textContent = text || '';
  el.classList.remove('good', 'bad');
  if (tone) el.classList.add(tone);
  if ('hidden' in el) el.hidden = !text;
  if (el._timer) { clearTimeout(el._timer); el._timer = null; }
  if (text && autoClear) {
    el._timer = setTimeout(() => {
      el.textContent = '';
      el.classList.remove('good', 'bad');
      if ('hidden' in el) el.hidden = true;
    }, 2400);
  }
}
// ── single delegated action handler ─────────────────────────────────────────
const ACTIONS = new Set([
  'save_config', 'discard_config', 'pick_ascent', 'pick_clips', 'pick_backup',
  'disconnect_ascent', 'scan_vods',
  'refresh_backups', 'export_data', 'open_logs',
  'restore_backup', 'reset_all_data', 'check_update', 'install_update',
  'run_backfill',
]);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  if (target.disabled) return;
  ev.preventDefault();

  if (action === 'discard_config') {
    const group = target.closest('[data-config-group]');
    restoreValues(Object.fromEntries(groupFields(group).map(id => [id, _baseline[id]])));
    updateDirtyGroups(); cardStatus(group, 'Changes discarded.'); return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[settings] (preview) ${action} — no Electron backend.`);
    if (action === 'save_config') cardStatus(target.closest('[data-config-group]'), 'Preview only. Changes are not saved.', 'bad');
    return;
  }

  try {
    if (action === 'save_config') return await doSave(invoke, target);
    if (action === 'disconnect_ascent') return await doSave(invoke, target, { disconnectAscent: true });
    if (action === 'scan_vods') return await doScanAscent(invoke);
    if (action in PICK_TARGETS) return await doPick(invoke, action);
    if (action === 'run_backfill') return await doBackfill(invoke, target);
    if (action === 'refresh_backups') return await loadStatus();
    if (action === 'export_data') return await doExport(invoke, target);
    if (action === 'open_logs') return await invoke('open_log_folder');
    if (action === 'restore_backup') return await doRestore(invoke, target);
    if (action === 'reset_all_data') return await doReset(invoke, target);
    if (action === 'check_update') return await doCheckUpdate(invoke);
    if (action === 'install_update') return await doInstallUpdate(invoke, target);
  } catch (err) {
    console.error(`[settings] ${action} failed:`, err);
    renderError(err);
  }
});

// Restore the selected backup. The sidecar takes a pre-restore safety backup first;
// on success the app RELAUNCHES (the invoke never resolves — the process restarts).
async function doRestore(invoke, target) {
  if (!_selectedBackupPath) { setStatusEl($('restore-status'), 'Select a backup to restore.', 'bad', true); return; }
  const ok = window.confirm(
    'Restore this backup?\n\nYour current data will be replaced by the selected backup. A safety backup of your current data is taken first, then the app relaunches.');
  if (!ok) return;
  if ('disabled' in target) target.disabled = true;
  setStatusEl($('restore-status'), 'Restoring… the app will relaunch.', null, false);
  try {
    // restore_backup relaunches the app on success, so this call won't return.
    await invoke('restore_backup', { payload: { backupFilePath: _selectedBackupPath } });
  } catch (err) {
    setStatusEl($('restore-status'), errText(err) || 'Restore failed.', 'bad', false);
    if ('disabled' in target) target.disabled = false;
    console.error('[settings] restore_backup failed:', err);
  }
}

// Reset all data (gated behind the type-RESET confirm + a final dialog). The sidecar
// takes a FULL backup first; on success the app RELAUNCHES.
async function doReset(invoke, target) {
  const box = $('reset-confirm');
  if (!box || box.value.trim() !== 'RESET') { setStatusEl($('reset-status'), 'Type RESET to confirm.', 'bad', true); return; }
  const ok = window.confirm(
    'Reset ALL data?\n\nEvery game, review, objective, rule, and note will be wiped. A full backup is taken first (you can restore it), then the app relaunches. This cannot be undone otherwise.');
  if (!ok) return;
  if ('disabled' in target) target.disabled = true;
  setStatusEl($('reset-status'), 'Resetting… a backup is being taken and the app will relaunch.', null, false);
  try {
    // reset_all_data relaunches the app on success, so this call won't return.
    await invoke('reset_all_data');
  } catch (err) {
    setStatusEl($('reset-status'), errText(err) || 'Reset failed.', 'bad', false);
    if ('disabled' in target) target.disabled = false;
    console.error('[settings] reset_all_data failed:', err);
  }
}

// Normalize a sidecar/Electron error to a readable string (strip the HTTP prefix).
function errText(err) {
  const s = (err && err.message) ? err.message : String(err);
  const m = s.match(/sidecar HTTP \d+:\s*(.*)$/i);
  return m ? m[1] : s;
}

// ── App updates (Velopack) ───────────────────────────────────────────────────
// Show the current version on load; Check queries the GitHub feed; Install
// downloads + applies (the app relaunches). Mirrors the shell banner, here as an
// explicit Settings control.
let _updateInfo = null;
async function loadAppVersion() {
  const verEl = $('update-ver');
  const invoke = await getInvoke();
  if (!invoke) { if (verEl) verEl.textContent = 'Version: preview'; return; }
  try {
    const v = await invoke('app_version');
    if (verEl) verEl.textContent = `Version: ${v || 'unknown'}`;
  } catch (_) { if (verEl) verEl.textContent = 'Version: unknown'; }
}

async function doCheckUpdate(invoke) {
  const btn = $('check-update-btn');
  const installBtn = $('install-update-btn');
  const prev = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = 'Checking…'; }
  setStatusEl($('update-status'), 'Checking for updates…', null, false);
  try {
    const r = await invoke('check_update');
    _updateInfo = r;
    if (r && r.available) {
      setStatusEl($('update-status'), r.message || `Update available: v${r.newVersion}`, 'good', false);
      if (installBtn) installBtn.hidden = false;
    } else {
      setStatusEl($('update-status'), (r && r.message) || "You're on the latest version.", null, true);
      if (installBtn) installBtn.hidden = true;
    }
  } catch (err) {
    setStatusEl($('update-status'), 'Update check failed.', 'bad', false);
    console.error('[settings] check_update failed:', err);
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = prev || 'Check for updates'; }
  }
}

async function doInstallUpdate(invoke, target) {
  if ('disabled' in target) target.disabled = true;
  setStatusEl($('update-status'), 'Downloading update…', null, false);
  try {
    const d = await invoke('download_update');
    if (!d || d.ok === false) {
      setStatusEl($('update-status'), (d && d.message) || 'Download failed.', 'bad', false);
      if ('disabled' in target) target.disabled = false;
      return;
    }
    setStatusEl($('update-status'), 'Installing… the app will restart.', null, false);
    // apply_update relaunches the app — this won't return on success.
    await invoke('apply_update');
  } catch (err) {
    setStatusEl($('update-status'), 'Update failed. Try again later.', 'bad', false);
    if ('disabled' in target) target.disabled = false;
    console.error('[settings] install_update failed:', err);
  }
}

// save_config = persist the editable surface; refetch after to reflect the
// canonical (and server-normalized, e.g. lower-cased region) values + status.
async function doSave(invoke, target, { disconnectAscent = false } = {}) {
  const group = target.closest('[data-config-group]');
  if (!group || !_data || _savingGroup) return;
  const isAscent = group.dataset.configGroup === 'ascent';
  if (isAscent && (!_ascentAvailable || _ascentScanning || (!disconnectAscent && !$('ascentFolder').value.trim()))) return;
  if (disconnectAscent && (!isAscent || !_data.ascentFolder)) return;
  for (const field of group.querySelectorAll('input,select')) if (!field.reportValidity()) return;
  const fields = groupFields(group);
  const payload = disconnectAscent ? { ascentFolder: FOLDER_CLEAR_SENTINEL } : collectPayload(group);
  _savingGroup = group;
  for (const field of group.querySelectorAll('input,select,button')) field.disabled = true;
  updateDirtyGroups(); cardStatus(group, 'Saving…');
  try {
    const result = await invoke('save_config', { payload });
    if (result?.ok === false) throw new Error(result.message || 'These changes could not be saved.');
    // Apply the window-size choice immediately (no relaunch needed) — but ONLY
    // when the user actually changed it. Applying unconditionally would snap a
    // manually resized/moved window back to the preset on every unrelated save.
    // Best-effort: the preference is already persisted, so launch-time apply
    // still happens even if the live resize fails.
    const loadedRes = _data && _data.windowResolution ? String(_data.windowResolution) : 'default';
    if (payload.windowResolution && payload.windowResolution !== loadedRes) {
      try {
        await invoke('set_window_resolution', { resolution: payload.windowResolution });
      } catch (resizeErr) {
        console.warn('[settings] live window resize failed (non-fatal):', resizeErr);
      }
    }
    let canonical;
    try { canonical = await fetchConfig(); }
    catch { canonical = { ..._data, ...payload, ...(disconnectAscent ? { ascentFolder: '' } : {}) }; }
    if (isAscent) _ascentScanResult = null;
    render(canonical, { savedFields: fields });
    cardStatus(group, disconnectAscent ? 'Disconnected. Existing videos and match links are kept.' : 'Changes saved.', 'good');
    loadStatus();
  } catch (err) {
    cardStatus(group, errText(err) || 'Could not save. Your changes are still here.', 'bad');
    console.error('[settings] save_config failed:', err);
  } finally {
    _savingGroup = null;
    for (const field of group.querySelectorAll('input,select,button')) field.disabled = false;
    updateDirtyGroups();
  }
}

async function doScanAscent(invoke) {
  const group = $('ascent-settings');
  const folder = String(_data?.ascentFolder || '').trim();
  if (!_ascentAvailable || _ascentScanning || _savingGroup || !folder || group?.dataset.dirty === 'true') return;
  _ascentScanning = true;
  syncAscentControls();
  try {
    const result = await invoke('scan_vods');
    if (result?.ok !== true) throw new Error(result?.message || 'Could not scan this folder. Check the path and try again.');
    const matched = Number.isSafeInteger(result.matched) && result.matched >= 0 ? result.matched : null;
    const count = Number.isSafeInteger(result.recordingCount) && result.recordingCount >= 0 ? result.recordingCount : null;
    const summary = matched !== null && count !== null
      ? `${matched} ${matched === 1 ? 'match linked' : 'matches linked'} · ${count} ${count === 1 ? 'recording found' : 'recordings found'}.`
      : 'Recording scan completed.';
    _ascentScanResult = { folder, text: String(result.message || '').trim() || summary, tone: 'good' };
  } catch (error) {
    _ascentScanResult = { folder, text: errText(error), tone: 'bad' };
  } finally {
    _ascentScanning = false;
    syncAscentControls();
  }
}

// Native folder picker → drop the chosen path into the matching field. Not
// persisted until Save (mirrors the WinUI Browse commands).
async function doPick(invoke, action) {
  const fieldId = PICK_TARGETS[action];
  const picked = await invoke('pick_folder');
  if (picked) {
    const el = $(fieldId);
    if (el) el.value = String(picked);
    updateDirtyGroups();
  }
}

// Backfill = long-running write (enemy laners + laning@10 + map-state events via
// Match-V5). The sidecar walks every unprocessed game throttled, so this can run
// minutes on a deep backlog; the shared command contract supplies its deadline. Shows the result
// text the sidecar built. ranBackfill=false means the account gate refused (not
// signed in) — surface that as an error tone so the user knows to sign in first.
async function doBackfill(invoke, target) {
  const canDisable = 'disabled' in target;
  const prev = target.textContent;
  if (canDisable) target.disabled = true;
  target.textContent = 'Filling in data…';
  setStatusEl($('backfill-status'),
    'Looking up missing match details. A large history can take a few minutes.', null, false);
  try {
    const res = await invoke('run_backfill');
    const text = res && res.text ? String(res.text) : 'Missing match data checked.';
    const failed = (res && res.ok === false) || (res && res.ranBackfill === false);
    setStatusEl($('backfill-status'), text, failed ? 'bad' : 'good', false);
  } catch (err) {
    setStatusEl($('backfill-status'), `Could not fill in match data: ${err && err.message ? err.message : err}`, 'bad', false);
  } finally {
    if (canDisable) target.disabled = false;
    target.textContent = prev;
  }
}

// Export = build the Markdown (sidecar read) then write it via the native save
// dialog. 'saved:false' means the user cancelled (no error).
async function doExport(invoke, target) {
  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  setStatusEl($('export-status'), 'Building export…', null, false);
  try {
    const built = await invoke('get_export_markdown');
    if (!built || typeof built.markdown !== 'string') {
      setStatusEl($('export-status'), 'Export failed: no data returned.', 'bad', false);
      return;
    }
    const out = await invoke('save_export_file', {
      fileName: built.fileName || 'revu-review-export.md',
      markdown: built.markdown,
    });
    if (out && out.saved) {
      setStatusEl($('export-status'), 'Export saved.', 'good', true);
    } else {
      setStatusEl($('export-status'), 'Export canceled.', null, true);
    }
  } catch (err) {
    setStatusEl($('export-status'), `Export failed: ${err && err.message ? err.message : err}`, 'bad', false);
  } finally {
    if (canDisable) target.disabled = false;
  }
}

// Toggle buttons flip their own visual state on click (saved on Save).
document.addEventListener('click', (ev) => {
  const t = ev.target.closest('.set-toggle[role="switch"]');
  if (!t || t.disabled) return;
  setToggle(t, !toggleState(t));
  settingsEdited(t);
});

// Keyboard activation for the role="switch" toggles (Enter / Space).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const t = ev.target.closest('.set-toggle[role="switch"]');
  if (!t || t.disabled) return;
  ev.preventDefault();
  setToggle(t, !toggleState(t));
  settingsEdited(t);
});

function settingsEdited(target) {
  if (target.closest('[data-config-group]')) updateDirtyGroups();
  const group = target.closest('[data-native-group]');
  if (group) {
    const dirty = nativeValues(group) !== _nativeBaseline.get(group.id);
    group.dataset.dirty = String(dirty);
    const id = group.dataset.nativeGroup === 'recording' ? 'recording-settings-status' : 'background-settings-status';
    const saveId = group.dataset.nativeGroup === 'recording' ? 'save-recording-settings' : 'save-background-settings';
    $(saveId).disabled = !dirty;
    $(id).textContent = dirty ? 'Unsaved changes' : 'No unsaved changes';
    syncCategoryDrafts();
  }
}
function nativeValues(group) {
  return JSON.stringify([...group.querySelectorAll('select,[role="switch"]')].map(field =>
    field.getAttribute('role') === 'switch' ? toggleState(field) : field.value));
}
document.addEventListener('input', event => { if (event.target.matches('input,select')) settingsEdited(event.target); });
document.addEventListener('change', event => { if (event.target.matches('input,select')) settingsEdited(event.target); });

// ── boot ────────────────────────────────────────────────────────────────────
function boot() {
  initializeSettingsNavigation();
  for (const id of CONFIG_FIELDS) if ($(id)) $(id).disabled = true;
  for (const [id, groupId] of [['recording-settings-status', 'recording-settings'], ['background-settings-status', 'background-settings']]) {
    new MutationObserver(() => {
      if ($(id).textContent.trim() === 'Saved.') {
        _nativeBaseline.set(groupId, nativeValues($(groupId)));
        $(groupId === 'recording-settings' ? 'save-recording-settings' : 'save-background-settings').disabled = true;
        $(groupId).dataset.dirty = 'false'; syncCategoryDrafts();
      }
    }).observe($(id), { childList: true, characterData: true, subtree: true });
  }
  loadConfig(); loadAppVersion(); loadAscentAccess();
  initializeRecordingSettings().then(() => {
    for (const group of document.querySelectorAll('[data-native-group]')) {
      _nativeBaseline.set(group.id, nativeValues(group));
      $(group.dataset.nativeGroup === 'recording' ? 'save-recording-settings' : 'save-background-settings').disabled = true;
    }
  });
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

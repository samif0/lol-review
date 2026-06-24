// Revu desktop — Settings page renderer for the glass-aurora layout.
//
// Reads/writes app config via the Tauri commands get_config / save_config (see
// Revu.Sidecar GET /api/config + POST /api/config/save) AND the Batch-6 settings
// surface: get_settings_status (ffmpeg + Ascent/clip status + backups list),
// scan_vods, get_export_markdown, and the native ops pick_folder /
// save_export_file / open_log_folder. Mirrors app.js conventions exactly:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • fetchConfig/fetchStatus try invoke FIRST, then a sample JSON fallback so
//     the page previews in a plain browser.
//   • Every server string is written via textContent (never innerHTML); color
//     hexes arrive as strings applied to style props only.
//   • ONE delegated [data-action] click handler; refetch after writes.
//
// DEFERRED to the auth batch (secrets): account login/logout/OTP. Riot ID +
// region are plain config fields and ARE editable here. DEFERRED (platform):
// app update (installer-managed), restore/reset (destructive + relaunch) — those
// render as static notes / disabled buttons.

// ── invoke resolver ────────────────────────────────────────────────────────
let _invoke = null;
async function getInvoke() {
  if (_invoke) return _invoke;
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') {
      _invoke = mod.invoke;
      return _invoke;
    }
  } catch (_) {
    // module not resolvable outside the Tauri bundler — fall through
  }
  if (window.__TAURI__ && window.__TAURI__.core && typeof window.__TAURI__.core.invoke === 'function') {
    _invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
    return _invoke;
  }
  return null;
}

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clearEl(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }

// Editable text/number/select inputs ↔ config field names (camelCase wire).
const TEXT_FIELDS = ['ascentFolder', 'clipsFolder', 'backupFolder', 'riotId', 'region'];
// Folder paths get special save handling (P-023): an EMPTY folder input means
// "leave unchanged" (so a not-yet-rendered field on a fetch-race never blanks the
// saved path) UNLESS the user explicitly cleared it via the Clear button, tracked
// here. A field is removed from this set the moment it's re-picked or fresh config
// is rendered.
const FOLDER_FIELDS = new Set(['ascentFolder', 'clipsFolder', 'backupFolder']);
const _clearedFolders = new Set();
// Sent verbatim to the sidecar for a DELIBERATE folder clear (P-023). Must match
// Program.cs TryResolveFolderWrite's FolderClearSentinel byte-for-byte. The input
// stays visually empty; collectPayload substitutes this only for explicit clears, so
// the sidecar can tell "user pressed Clear" from "field was empty" (which it ignores).
const FOLDER_CLEAR_SENTINEL = ' __REVU_CLEAR__ ';
const NUM_FIELDS = ['clipsMaxSizeMb'];
// role=switch toggle buttons ↔ config bool field names.
const TOGGLE_FIELDS = [
  'backupEnabled', 'tiltFixMode', 'requireReviewNotes',
  'autoTimelineClippingEnabled', 'minimizeDuringGame', 'sidebarAnimationEnabled',
  'autoClipObjectivesEnabled',
];
// Browse buttons ↔ the text field they fill.
const PICK_TARGETS = { pick_ascent: 'ascentFolder', pick_clips: 'clipsFolder', pick_backup: 'backupFolder' };

let _data = null;

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchConfig() {
  const invoke = await getInvoke();
  if (invoke) return invoke('get_config');
  const res = await fetch('./sample-settings.json');
  if (!res.ok) throw new Error(`sample-settings.json ${res.status}`);
  return res.json();
}

async function fetchStatus() {
  const invoke = await getInvoke();
  if (invoke) return invoke('get_settings_status');
  // Browser preview: best-effort sample, else a benign empty shape.
  try {
    const res = await fetch('./sample-settings-status.json');
    if (res.ok) return res.json();
  } catch (_) { /* no sample present in preview — fine */ }
  return { ffmpeg: null, ascent: null, clipUsage: null, backups: [] };
}

// ── toggle helpers ──────────────────────────────────────────────────────────
function setToggle(el, on) {
  if (!el) return;
  el.classList.toggle('on', !!on);
  el.setAttribute('aria-checked', on ? 'true' : 'false');
}
function toggleState(el) { return el ? el.getAttribute('aria-checked') === 'true' : false; }

// ── render: editable config surface ──────────────────────────────────────────
function render(d) {
  _data = d;
  clearError();
  // Fresh canonical config rendered → any pending "explicit clear" is moot (the
  // fields now reflect the saved truth). Reset so an empty folder after this is
  // "unchanged", not "cleared" (P-023).
  _clearedFolders.clear();

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
  if (statusB) statusB.textContent = d.riotId ? `Configured · ${d.riotId}` : 'Configured';

  playEntrance();
}

// ── render: read-only diagnostics (ffmpeg / Ascent / clip usage / backups) ────
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
  applyStatusText($('ascent-status'), s.ascent, '');
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
function collectPayload() {
  const p = {};
  for (const f of TEXT_FIELDS) {
    const el = $(f);
    if (!el) continue;
    const v = el.value.trim();
    // P-023: never let an empty folder input OVERWRITE an already-saved path.
    // Sending "" blanks the stored folder — which is what zeroed ascent_folder/
    // clips_folder/backup_folder on a save made before the config finished
    // rendering. For an empty folder field: send the explicit-clear SENTINEL if the
    // user pressed Clear (the sidecar maps it to ""), otherwise OMIT the key so the
    // server leaves the saved value untouched. Non-empty folders send normally.
    if (FOLDER_FIELDS.has(f) && v === '') {
      if (_clearedFolders.has(f)) p[f] = FOLDER_CLEAR_SENTINEL;
      continue;
    }
    p[f] = v;
  }
  for (const f of NUM_FIELDS) {
    const el = $(f);
    if (el) {
      const n = parseInt(el.value, 10);
      // Mirror the WinUI clamp/reject: only send a valid in-range int.
      if (Number.isFinite(n) && n >= 100 && n <= 50000) p[f] = n;
    }
  }
  for (const f of TOGGLE_FIELDS) {
    p[f] = toggleState($(f));
  }
  return p;
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  const detail = $('err-detail');
  if (detail) detail.textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance stagger ────────────────────────────────────────────────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const cards = Array.from(document.querySelectorAll('.set-card, .set-saverow'));
  cards.forEach((el, i) => el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`));
}

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
function setSaveStatus(text, tone) { setStatusEl($('save-status'), text, tone, true); }

// ── single delegated action handler ─────────────────────────────────────────
const ACTIONS = new Set([
  'save_config', 'pick_ascent', 'pick_clips', 'pick_backup', 'clear_ascent',
  'scan_vods', 'refresh_backups', 'export_data', 'open_logs',
  'restore_backup', 'reset_all_data', 'check_update', 'install_update',
]);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Local-only action (no backend needed).
  if (action === 'clear_ascent') {
    const f = $('ascentFolder');
    if (f) f.value = '';
    // Mark this as a DELIBERATE clear so the next Save actually blanks the stored
    // folder (P-023): collectPayload otherwise omits empty folders to avoid the
    // fetch-race overwrite. Persisted on the next Save, like Browse.
    _clearedFolders.add('ascentFolder');
    applyStatusText($('ascent-status'), { text: 'Ascent VOD disabled', colorHex: '#8A80A8' });
    return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[settings] (preview) ${action} — no Tauri backend.`);
    if (action === 'save_config') setSaveStatus('Preview only; not saved.', 'bad');
    return;
  }

  try {
    if (action === 'save_config') return await doSave(invoke, target);
    if (action in PICK_TARGETS) return await doPick(invoke, action);
    if (action === 'scan_vods') return await doScan(invoke, target);
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

// Normalize a sidecar/Tauri error to a readable string (strip the HTTP prefix).
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
async function doSave(invoke, target) {
  const payload = collectPayload();
  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  setSaveStatus('Saving…');
  try {
    await invoke('save_config', { payload });
    setSaveStatus('Settings saved.', 'good');
    await loadConfig(); // manual invalidation — no message bus
  } catch (err) {
    renderError(err);
    setSaveStatus('Error saving settings.', 'bad');
    console.error('[settings] save_config failed:', err);
  } finally {
    if (canDisable) target.disabled = false;
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
    // Picking a real path supersedes any pending "explicit clear" for this folder.
    _clearedFolders.delete(fieldId);
  }
}

// Scan = read+write (auto-matches recordings to games). Shows the result text the
// sidecar built; refresh status after so any clip/ascent counts update.
async function doScan(invoke, target) {
  const canDisable = 'disabled' in target;
  const prev = target.textContent;
  if (canDisable) target.disabled = true;
  target.textContent = 'Scanning…';
  setStatusEl($('scan-result'), 'Scanning your recordings folder…', null, false);
  try {
    const res = await invoke('scan_vods');
    const text = res && res.text ? String(res.text) : 'Scan complete.';
    setStatusEl($('scan-result'), text, res && res.ok === false ? 'bad' : 'good', false);
    await loadStatus();
    window.dispatchEvent(new CustomEvent('revu:first-review-vod-scan-done', {
      detail: { ok: !(res && res.ok === false), text },
    }));
  } catch (err) {
    setStatusEl($('scan-result'), `Scan failed: ${err && err.message ? err.message : err}`, 'bad', false);
  } finally {
    if (canDisable) target.disabled = false;
    target.textContent = prev;
  }
}

// Export = build the Markdown (sidecar read) then write it via the native save
// dialog (Rust). 'saved:false' means the user cancelled (no error).
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
  if (!t) return;
  setToggle(t, !toggleState(t));
});

// Keyboard activation for the role="switch" toggles (Enter / Space).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const t = ev.target.closest('.set-toggle[role="switch"]');
  if (!t) return;
  ev.preventDefault();
  setToggle(t, !toggleState(t));
});

// ── boot ────────────────────────────────────────────────────────────────────
function boot() { loadConfig(); loadAppVersion(); }
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

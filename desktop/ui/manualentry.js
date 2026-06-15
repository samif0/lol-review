// Revu desktop — Manual Entry page renderer.
// Hand-log a League game that wasn't auto-captured + a minimal review. Mirrors
// ManualEntryDialogViewModel: loads the active post-game objectives (practiced
// toggle + execution note per objective), validates a required champion name,
// then POSTs the whole form via save_manual_game and navigates to the Games
// page (where the logged game appears). Mirrors app.js conventions:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri the objectives list falls back to sample-objectives-active.json
//     (or empty) so the form previews standalone; save no-ops in preview.
//   • Server strings written via textContent only (XSS-safe).
//   • ONE delegated [data-action] click handler (save / cancel).

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
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) { return $(id).content.firstElementChild.cloneNode(true); }

// ── module state ────────────────────────────────────────────────────────────
let _objectives = []; // active post-game objectives → assessment rows

// ── load active objectives ────────────────────────────────────────────────────
async function fetchObjectives() {
  const invoke = await getInvoke();
  if (invoke) {
    try {
      const r = await invoke('get_active_objectives');
      return Array.isArray(r && r.objectives) ? r.objectives : [];
    } catch (err) {
      // Mirror the VM: objective-load failure is non-fatal (card stays collapsed).
      console.warn('[manualentry] failed to load objectives:', err);
      return [];
    }
  }
  // Browser preview: best-effort sample, else empty.
  try {
    const res = await fetch('./sample-objectives-active.json');
    if (res.ok) {
      const j = await res.json();
      return Array.isArray(j && j.objectives) ? j.objectives : [];
    }
  } catch (_) { /* no sample — render with no objectives */ }
  return [];
}

// ── render: learning-objective assessment rows ───────────────────────────────
function renderObjectives() {
  const card = $('me-obj-card');
  const host = $('me-obj-list');
  clear(host);

  if (!_objectives.length) {
    show(card, false);
    return;
  }
  show(card, true);

  for (const o of _objectives) {
    const el = tpl('tpl-meobj');
    el.dataset.objId = String(o.objectiveId);
    el.querySelector('.me-obj-title').textContent = o.title || '';
    el.querySelector('.me-obj-phase').textContent = o.phaseLabel || '';

    // The toggle label tracks the checkbox state (Practiced / Not practiced).
    const chk = el.querySelector('.me-obj-practiced');
    const lbl = el.querySelector('.me-toggle-lbl');
    chk.addEventListener('change', () => {
      lbl.textContent = chk.checked ? 'Practiced' : 'Not practiced';
    });

    host.appendChild(el);
  }
}

// ── validation + helpers ──────────────────────────────────────────────────────
function setError(msg) {
  const e = $('me-error');
  if (!e) return;
  e.textContent = msg || '';
  show(e, !!msg);
}

// Parse a KDA field: non-negative ints only (negatives / NaN → 0), mirroring the
// VM's int.TryParse + v>=0 guard.
function readNum(id) {
  const raw = ($(id) && $(id).value || '').trim();
  const n = Number.parseInt(raw, 10);
  return Number.isFinite(n) && n >= 0 ? n : 0;
}

// ── build the save payload ────────────────────────────────────────────────────
function readPayload() {
  const champion = ($('me-champ').value || '').trim();
  if (!champion) {
    setError('Champion name is required.');
    return null;
  }
  setError('');

  const objectives = [];
  for (const row of $('me-obj-list').querySelectorAll('.me-obj')) {
    const objectiveId = Number(row.dataset.objId || 0);
    if (objectiveId <= 0) continue;
    objectives.push({
      objectiveId,
      practiced: row.querySelector('.me-obj-practiced').checked,
      executionNote: (row.querySelector('.me-obj-note').value || '').trim(),
    });
  }

  return {
    championName: champion,
    win: $('me-win').checked, // Defeat is the implicit complement (unchecked)
    kills: readNum('me-k'),
    deaths: readNum('me-d'),
    assists: readNum('me-a'),
    mentalRating: Number($('me-mental').value) || 5,
    gameMode: 'Manual Entry',
    notes: ($('me-notes').value || '').trim(),
    mistakes: ($('me-mistakes').value || '').trim(),
    wentWell: ($('me-well').value || '').trim(),
    focusNext: ($('me-focus').value || '').trim(),
    objectives,
  };
}

// ── save ──────────────────────────────────────────────────────────────────────
async function save(btn) {
  const payload = readPayload();
  if (!payload) return; // validation failed (champion required)

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[manualentry] (preview) save_manual_game — no Tauri backend.', payload);
    // In preview just bounce to Games like the real flow would (the logged game
    // shows up in Today / History there).
    window.location.href = 'games.html';
    return;
  }

  if (btn) btn.disabled = true;
  try {
    await invoke('save_manual_game', { payload });
    window.location.href = 'games.html';
  } catch (err) {
    setError('Failed to save game entry.');
    renderError(err);
    console.error('[manualentry] save_manual_game failed:', err);
    if (btn) btn.disabled = false;
  }
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}

// ── wiring ────────────────────────────────────────────────────────────────────
function wire() {
  // Mental-rating live readout.
  const mental = $('me-mental');
  const mentalVal = $('me-mental-val');
  if (mental && mentalVal) {
    mental.addEventListener('input', () => { mentalVal.textContent = mental.value; });
  }
  // Clear the required-field error as soon as a champion name is typed.
  const champ = $('me-champ');
  if (champ) champ.addEventListener('input', () => { if (champ.value.trim()) setError(''); });
}

// ── single delegated action handler (save / cancel) ──────────────────────────
document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  ev.preventDefault();
  if (action === 'cancel') {
    window.location.href = 'games.html';
  } else if (action === 'save') {
    await save(target);
  }
});

// ── boot ──────────────────────────────────────────────────────────────────────
async function boot() {
  wire();
  try {
    _objectives = await fetchObjectives();
    renderObjectives();
  } catch (err) {
    console.error('[manualentry] boot failed:', err);
  }
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();

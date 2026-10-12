import { $, show, clear, tpl } from './dom.mjs';
import { getInvoke } from './platform/index.mjs';

// Revu desktop — Add a game manually.
// Hand-log a League game that wasn't auto-captured + a minimal review. Mirrors
// ManualEntryDialogViewModel: loads the active post-game objectives (practiced
// toggle + execution note per objective), validates a required champion name and
// an explicit result, then POSTs the whole form via save_manual_game and navigates
// to the Match library (where the logged game appears). Mirrors app.js conventions:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron the objectives list falls back to sample-objectives.json
//     (or empty) so the form previews standalone; save no-ops in preview.
//   • Server strings written via textContent only (XSS-safe).
//   • ONE delegated [data-action] click handler (save / cancel).
// The reflection field is the Review page's "One thing to take into your next
// game", stored in the same column, so the note shows up when the match is reviewed.

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
  // Browser preview: the shared objectives sample, shaped like get_active_objectives.
  try {
    const res = await fetch('./sample-objectives.json');
    if (res.ok) {
      const j = await res.json();
      const active = Array.isArray(j && j.activeObjectives) ? j.activeObjectives : [];
      return active.map((o) => ({ objectiveId: o.id, title: o.title, phaseLabel: o.phaseLabel }));
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
    const title = o.title || '';
    el.querySelector('.me-obj-title').textContent = title;
    el.querySelector('.me-obj-phase').textContent = o.phaseLabel || '';
    // Every row repeats the same controls, so name them after their objective.
    el.querySelector('.me-obj-practiced').setAttribute('aria-label', `Practiced: ${title}`);
    el.querySelector('.me-obj-note').setAttribute('aria-label', `How did it go: ${title}`);
    host.appendChild(el);
  }
}

// ── validation + helpers ──────────────────────────────────────────────────────
function setError(msg, field) {
  const e = $('me-error');
  if (!e) return;
  e.textContent = msg || '';
  show(e, !!msg);
  if (msg && field) field.focus();
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
    setError('Enter the champion you played.', $('me-champ'));
    return null;
  }
  // No default result: a forgotten choice must not silently record a loss.
  const win = $('me-win').checked;
  if (!win && !$('me-loss').checked) {
    setError('Choose Victory or Defeat.', $('me-win'));
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
    win,
    kills: readNum('me-k'),
    deaths: readNum('me-d'),
    assists: readNum('me-a'),
    mentalRating: Number($('me-mental').value) || 5,
    gameMode: 'Manual Entry',
    notes: ($('me-notes').value || '').trim(),
    objectives,
  };
}

// ── save ──────────────────────────────────────────────────────────────────────
async function save(btn) {
  const payload = readPayload();
  if (!payload) return; // validation failed (champion or result missing)

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[manualentry] (preview) save_manual_game — no Electron backend.', payload);
    // In preview just bounce to the Match library like the real flow would.
    window.location.href = 'games.html';
    return;
  }

  if (btn) btn.disabled = true;
  try {
    await invoke('save_manual_game', { payload });
    window.location.href = 'games.html';
  } catch (err) {
    setError("Couldn't save this game. Try again.");
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
  // Clear a required-field error as soon as it is resolved.
  const champ = $('me-champ');
  if (champ) champ.addEventListener('input', () => { if (champ.value.trim()) setError(''); });
  for (const id of ['me-win', 'me-loss']) {
    const radio = $(id);
    if (radio) radio.addEventListener('change', () => setError(''));
  }
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

// Revu desktop — VOD timeline corrections (v3.11 event corrections ledger).
// Owns the "Add or correct a timeline event" panel on the VOD player: marker
// selection (.evbar-sel), the fast-path keys (E edit, N add at playhead, Delete
// remove, [ ] nudge, Z undo), the undo toast, ghost bars for removed events, the
// correction form (op / type / catalog-driven attribute chips / reason), the
// per-game corrections list with Revert, and the Copy-as-JSON export. The
// stateless render helpers live in ./vodcorrections-panel.js.
//
// vodplayer.js owns the DOM this decorates: it calls decorateBar() per marker and
// appendGhosts() once at the end of every placeMarkers() pass, renderPanel() on
// every reload, and handleKey() from its single keydown handler AFTER the typing
// and chord guards. Every write goes through the Tauri commands
// save_event_correction / revert_event_correction ({ payload }) and
// export_event_corrections, then ends in ctx.reloadBookmarks(), which re-renders
// the markers without touching playback. Server strings land via textContent only.

import {
  parseClock, catalogOf, typeDefIn, labelIn, prefillAttrs,
  renderTypeOptions, renderAttrChips, renderList,
} from './vodcorrections-panel.js';

const NUDGE_FLUSH_MS = 600;   // bracket presses inside this window coalesce into ONE ledger row
const TOAST_MS = 6000;
const PREVIEW_MSG = 'Preview only; no backend to save to.';

// ctx = { $, show, clear, tpl, clock, errText, video, seekTo, reloadBookmarks,
//         currentMarker, get vod(), get core(), get gameId() }
export function createCorrections(ctx) {
  const { $, show, clear, tpl, clock } = ctx;
  const errText = ctx.errText || ((err) => (err && err.message) ? err.message : String(err));

  let _sel = null;      // { key, eventKey, id, type, timeS, endS, label, summary, ghost, added, fixed, correctionId }
  let _form = null;     // { correctionId, op, attrs } while the form is open
  let _nudge = null;    // { correctionId, key, subject, label, baseTimeS, total, timer } while a nudge stream is open
  const _undo = [];     // stack of { correctionId, key, label }; Z / the toast's Undo reverts the top
  let _toastTimer = null;
  let _lastAddType = '';
  let _selSeen = false; // did the placeMarkers pass in progress re-draw the selected marker

  const vod = () => ctx.vod || {};
  const gameId = () => Number(ctx.gameId) || 0;
  const hasBackend = () => !!(ctx.core && gameId() > 0);
  const durationS = () => {
    const v = ctx.video();
    return (v && v.duration) || Number(vod().gameDurationSeconds) || 0;
  };
  const catalog = () => catalogOf(vod());
  const typeDef = (type) => typeDefIn(catalog(), type);
  const isSpan = (type) => !!(typeDef(type) && typeDef(type).kind === 'span');
  const labelOf = (type) => labelIn(catalog(), type);

  function hint(msg, isErr) {
    const h = $('vp-fix-hint');
    if (!h) return;
    h.textContent = msg || '';
    h.classList.toggle('err', !!isErr);
    show(h, !!msg);
  }

  // The undo stack holds ONE live entry per subject: the server keeps exactly one
  // applicable correction per subject with a cumulative patch (a later fix supersedes
  // the earlier row), so the newest id is the only revertable one and replacing the
  // earlier entry is exact, not lossy. Reverting an id (from Z, the toast, the list or
  // Delete on a ghost) drops it wherever it sits.
  function pushUndo(entry) {
    for (let i = _undo.length - 1; i >= 0; i--) if (entry.key && _undo[i].key === entry.key) _undo.splice(i, 1);
    _undo.push(entry);
  }
  function dropUndo(correctionId) {
    for (let i = _undo.length - 1; i >= 0; i--) if (_undo[i].correctionId === correctionId) _undo.splice(i, 1);
  }

  // ── selection ────────────────────────────────────────────────────────────────
  // Keyed on the served eventKey (falls back to the row id) so it survives the
  // full marker rebuild every reload does; decorateBar re-applies it per bar.
  const keyOf = (ds) => ds.eventKey || ('id:' + (ds.eventId || '0'));
  const anchorOf = (e) => {
    const a = e.teamfight ? Number(e.teamfight.startSeconds) : Number(e.gameTimeSeconds);
    return Number.isFinite(a) ? a : (Number(e.gameTimeSeconds) || 0);
  };
  const endOf = (e) => e.encounterEndSeconds ?? (e.teamfight ? e.teamfight.endSeconds : null) ?? null;

  function fromBar(bar) {
    const d = bar.dataset;
    return {
      key: keyOf(d), eventKey: d.eventKey || '', id: Number(d.eventId) || 0, type: String(d.eventType || ''),
      timeS: Number(d.anchor) || 0, endS: (d.endSeconds == null || d.endSeconds === '') ? null : Number(d.endSeconds),
      label: d.label || '', summary: d.summary || '', ghost: d.ghost === '1',
      added: bar.classList.contains('evbar-added'), fixed: bar.classList.contains('evbar-fixed'),
      correctionId: d.correctionId || '',
    };
  }
  function fromEvent(e) {
    const id = Number(e.id) || 0;
    const endS = endOf(e);
    return {
      key: e.eventKey || ('id:' + id), eventKey: e.eventKey || '', id, type: String(e.eventType || ''),
      timeS: anchorOf(e), endS: endS == null ? null : Number(endS),
      label: e.label || '', summary: e.summary || '', ghost: e.removed === true,
      added: !!e.addedByUser, fixed: !!e.corrected, correctionId: e.correctionId || '',
    };
  }
  const eventFor = (sel) => (vod().gameEvents || []).find((e) =>
    (sel.eventKey && e.eventKey === sel.eventKey) || (sel.id && Number(e.id) === sel.id)) || null;
  const subjectOf = (sel) => ({ eventKey: sel.eventKey || null, eventId: sel.id || null, type: sel.type, timeS: sel.timeS });

  function findBar(key) {
    const host = $('vp-markers');
    if (!host) return null;
    for (const b of host.querySelectorAll('.evbar')) if (keyOf(b.dataset) === key) return b;
    return null;
  }

  function renderSelection() {
    const el = $('vp-fix-sel');
    if (!el) return;
    if (!_sel) { el.textContent = 'No event selected.'; return; }
    const suffix = _sel.ghost ? ' (removed)' : _sel.added ? ' (added)' : _sel.fixed ? ' (fixed)' : '';
    const what = _sel.label || labelOf(_sel.type);
    el.textContent = `Selected: ${clock(_sel.timeS)} ${what}${_sel.summary ? ', ' + _sel.summary : ''}${suffix}`;
  }

  function select(sel) {
    const changed = !sel || !_sel || sel.key !== _sel.key;
    if (_nudge && (!sel || sel.key !== _nudge.key)) flushNudge();
    _sel = sel || null;
    const host = $('vp-markers');
    if (host) for (const b of host.querySelectorAll('.evbar-sel')) b.classList.remove('evbar-sel');
    if (_sel) { const b = findBar(_sel.key); if (b) b.classList.add('evbar-sel'); }
    renderSelection();
    // A form open on the old subject follows the new one (fresh id), so the prefilled
    // time/type never lands on the wrong event.
    if (changed && _form && _form.op !== 'add') { if (_sel) openFor(_sel); else closeForm(); }
  }
  function clearSelection() { select(null); }

  function decorateBar(bar, e) {
    const d = bar.dataset;
    d.eventKey = e.eventKey || '';
    d.eventId = String(e.id ?? 0);
    d.eventType = String(e.eventType || '');
    d.anchor = String(anchorOf(e));
    const endS = endOf(e);
    d.endSeconds = endS == null ? '' : String(endS);
    d.label = e.label || '';
    d.summary = e.summary || '';
    if (e.correctionId) d.correctionId = String(e.correctionId);
    if (e.corrected) bar.classList.add('evbar-fixed');
    if (e.addedByUser) bar.classList.add('evbar-added');
    // A row selected under the 'id:' fallback (a pre-v16 row the startup sweep had not
    // reached) comes back with its stable key once the first correction stamps it; the
    // row id is unchanged (the stamp is an UPDATE), so follow the key and keep the
    // selection and an open nudge stream alive across the reload.
    if (_sel && !_sel.eventKey && _sel.id && _sel.key === 'id:' + _sel.id && e.eventKey && Number(e.id) === _sel.id) {
      const old = _sel.key;
      _sel.key = e.eventKey;
      _sel.eventKey = e.eventKey;
      if (_nudge && _nudge.key === old) { _nudge.key = e.eventKey; _nudge.subject = { ..._nudge.subject, eventKey: e.eventKey }; }
    }
    if (!_sel || keyOf(d) !== _sel.key) return;
    bar.classList.add('evbar-sel');
    _selSeen = true;
    if (_nudge && _nudge.key === _sel.key) {
      moveBar(bar, _nudge.baseTimeS + _nudge.total); // keep the optimistic position mid-stream
    } else {
      _sel = fromBar(bar); // a fix may have moved or re-typed it since
      renderSelection();
    }
  }

  // One dashed ghost per event the user removed (reconstructed by the snapshot from
  // the ledger). No label and no bucket write, so ghosts never steal label slots.
  function appendGhosts(host, pctOf) {
    for (const e of (vod().gameEvents || [])) {
      if (e.removed !== true) continue;
      const g = document.createElement('span');
      g.className = 'evbar evbar-ghost';
      g.style.left = `${pctOf(e.gameTimeSeconds || 0)}%`;
      g.style.pointerEvents = 'auto';
      g.style.cursor = 'pointer';
      g.title = `Removed ${e.label || labelOf(e.eventType)} at ${clock(e.gameTimeSeconds)}. Select and press Delete to restore.`;
      g.dataset.ghost = '1';
      g.dataset.action = 'jump';
      g.dataset.seconds = String(e.gameTimeSeconds || 0);
      decorateBar(g, e);
      host.appendChild(g);
    }
    // The pass is complete: a selection the rebuild did not re-draw is stale (the
    // re-capture renumbered its row, or the event is gone) and quietly drops.
    if (_sel && !_selSeen) { _sel = null; renderSelection(); }
    _selSeen = false;
  }

  // ── keys (after vodplayer's typing + chord guards) ───────────────────────────
  function handleKey(ev) {
    const k = ev.key;
    if (k === 'Escape') {
      if (_form) { closeForm(); return true; }
      if (_sel) { clearSelection(); return true; }
      return false;
    }
    if (ev.code === 'BracketLeft' || ev.code === 'BracketRight') {
      if (!_sel) return false;
      const step = ev.shiftKey ? 5 : 1;
      nudge(ev.code === 'BracketLeft' ? -step : step);
      return true;
    }
    if (ev.repeat) return false;
    if (k === 'e' || k === 'E') { if (_sel) openFor(_sel); else hint('Click a marker first.'); return true; }
    if (k === 'n' || k === 'N') { openAdd(); return true; }
    if (k === 'z' || k === 'Z') { undoLast(); return true; }
    if (k === 'Delete') { if (!_sel) return false; removeSelected(); return true; }
    return false;
  }

  // ── writes ───────────────────────────────────────────────────────────────────
  async function post(payload) {
    try {
      const r = await ctx.core.invoke('save_event_correction', { payload });
      return r || {};
    } catch (err) {
      hint(errText(err), true);
      return null;
    }
  }

  // Resolves to the server's result ({ idempotent } when the row was already reverted),
  // or null on failure (the sidecar's sentence is already in the hint).
  async function revert(correctionId, doneMsg) {
    if (!hasBackend()) { hint(PREVIEW_MSG); return null; }
    try {
      const r = await ctx.core.invoke('revert_event_correction', { payload: { gameId: gameId(), correctionId, reason: '' } });
      dropUndo(correctionId);
      hint(doneMsg || 'Reverted.');
      ctx.reloadBookmarks();
      return r || {};
    } catch (err) {
      hint(errText(err), true);
      return null;
    }
  }

  // Delete: a live marker is removed (undoable); a ghost restores; an event the user
  // added is undone (reverting the add deletes its row).
  async function removeSelected() {
    const sel = _sel;
    if (!hasBackend()) { hint(PREVIEW_MSG); return; }
    if (sel.ghost || sel.added) {
      if (!sel.correctionId) { hint('That event is not in this game.', true); return; }
      await revert(sel.correctionId, sel.ghost ? 'Restored.' : 'Removed.');
      return;
    }
    const label = `${sel.label || labelOf(sel.type)} at ${clock(sel.timeS)}`;
    const correctionId = crypto.randomUUID();
    const r = await post({ gameId: gameId(), correctionId, op: 'remove', subject: subjectOf(sel), patch: null,
      reason: 'Marked as not real from the VOD player' });
    if (!r) return;
    pushUndo({ correctionId, key: sel.key, label: `removed ${label}` });
    toast(`Removed ${label}.`, { undo: true });
    ctx.reloadBookmarks();
  }

  // [ ] nudge. Presses within NUDGE_FLUSH_MS share one correctionId: the bar moves
  // optimistically on every press and the stream POSTs once when the timer fires.
  function nudge(delta) {
    const sel = _sel;
    if (sel.ghost) { hint('Restore the removed event before moving it.'); return; }
    if (!hasBackend()) { hint(PREVIEW_MSG); return; }
    if (_nudge && _nudge.key !== sel.key) flushNudge();
    if (!_nudge) {
      _nudge = { correctionId: crypto.randomUUID(), key: sel.key, subject: subjectOf(sel),
        label: `${sel.label || labelOf(sel.type)} at ${clock(sel.timeS)}`, baseTimeS: sel.timeS, total: 0, timer: null };
    }
    const dur = Number(vod().gameDurationSeconds) || 0;
    let next = _nudge.baseTimeS + _nudge.total + delta;
    next = Math.max(0, dur > 0 ? Math.min(dur, next) : next);
    _nudge.total = next - _nudge.baseTimeS;
    const bar = findBar(_nudge.key);
    if (bar) moveBar(bar, next);
    const sign = _nudge.total > 0 ? '+' : '';
    hint(`${sign}${_nudge.total}s, saving...`);
    clearTimeout(_nudge.timer);
    _nudge.timer = setTimeout(flushNudge, NUDGE_FLUSH_MS);
  }

  function moveBar(bar, t) {
    const dur = durationS();
    if (dur > 0) bar.style.left = `${Math.max(0, Math.min(100, (t / dur) * 100))}%`;
    bar.dataset.anchor = String(t);
    bar.title = `${clock(t)} ${bar.dataset.label || ''}${bar.dataset.summary ? ' · ' + bar.dataset.summary : ''}`.trim();
    if (_sel && keyOf(bar.dataset) === _sel.key) { _sel.timeS = t; renderSelection(); }
  }

  async function flushNudge() {
    const n = _nudge;
    if (!n) return;
    clearTimeout(n.timer);
    _nudge = null;
    if (n.total === 0) { hint(''); return; }
    const sign = n.total > 0 ? '+' : '';
    const r = await post({ gameId: gameId(), correctionId: n.correctionId, op: 'retime', subject: n.subject,
      patch: { gameTimeS: n.baseTimeS + n.total }, reason: `Nudged ${sign}${n.total}s from the VOD player` });
    if (r) {
      pushUndo({ correctionId: n.correctionId, key: n.key, label: `moved ${n.label}` });
      hint(r.message ? `Moved ${sign}${n.total}s. ${r.message}` : `Moved ${sign}${n.total}s.`);
    }
    ctx.reloadBookmarks(); // success: fresh keys and marks; failure: the bar snaps back
  }

  // Z: revert the newest entry that still does something. An id already reverted from
  // the list answers idempotent and is skipped silently rather than announced.
  async function undoLast() {
    if (!_undo.length) { hint('Nothing to undo.'); return; }
    if (!hasBackend()) { hint(PREVIEW_MSG); return; }
    show($('vp-toast'), false);
    while (_undo.length) {
      const top = _undo.pop();
      const r = await revert(top.correctionId, 'Undone.');
      if (!r) return;
      if (!r.idempotent) { toast('Undone.', {}); return; }
    }
    hint('Nothing to undo.');
  }

  function toast(msg, opts) {
    const t = $('vp-toast');
    if (!t) return;
    $('vp-toast-txt').textContent = msg || '';
    show($('vp-toast-undo'), !!(opts && opts.undo));
    show(t, true);
    clearTimeout(_toastTimer);
    _toastTimer = setTimeout(() => show(t, false), TOAST_MS);
  }

  // ── form ─────────────────────────────────────────────────────────────────────
  function openFor(sel) {
    if (!sel) { hint('Click a marker first.'); return; }
    // A ghost has no editable subject: a form still open on the previous event must
    // not stay bound to it (Save would post the ghost as its subject).
    if (sel.ghost) { closeForm(); hint('This event was removed. Press Delete to restore it.'); return; }
    const def = typeDef(sel.type);
    _form = { correctionId: null, op: def && def.attrs.length ? 'attr' : 'retype', attrs: prefillAttrs(eventFor(sel), def) };
    fillForm(sel.type, sel.timeS, sel.endS);
  }

  function openAdd() {
    const v = ctx.video();
    const t = Math.max(0, Math.floor((v && v.currentTime) || 0));
    _form = { correctionId: null, op: 'add', attrs: {} };
    fillForm(_lastAddType || 'DEATH', t, t);
    const typeSel = $('vp-fix-type');
    if (typeSel) typeSel.focus();
  }

  function fillForm(type, startS, endS) {
    const op = $('vp-fix-op');
    if (op) op.value = _form.op;
    renderTypeOptions($('vp-fix-type'), catalog(), String(type || '').toUpperCase(), clear);
    $('vp-fix-start').value = clock(startS);
    $('vp-fix-end').value = clock(endS == null ? startS : endS);
    $('vp-fix-reason').value = '';
    renderFields();
    show($('vp-fix-form'), true);
    hint('');
  }

  function closeForm() {
    _form = null;
    show($('vp-fix-form'), false);
  }

  // Which fields an op needs: type for retype/add, time for retime/add (end only for
  // span types), attribute chips for attr/add. Every other op is reason-only.
  function renderFields() {
    if (!_form) return;
    const op = String(($('vp-fix-op') || {}).value || _form.op);
    _form.op = op;
    const typeSel = $('vp-fix-type');
    if (typeSel && op !== 'retype' && op !== 'add' && _sel) renderTypeOptions(typeSel, catalog(), String(_sel.type || '').toUpperCase(), clear);
    const type = typeSel ? typeSel.value : '';
    const span = isSpan(type);
    show($('vp-fix-type-wrap'), op === 'retype' || op === 'add');
    show($('vp-fix-time-row'), op === 'retime' || op === 'add');
    show($('vp-fix-end-wrap'), span && (op === 'retime' || op === 'add'));
    renderAttrChips($('vp-fix-attrs'), typeDef(type), _form.attrs, op === 'attr' || op === 'add', { show, clear });
  }

  function toggleAttr(chip) {
    if (!_form) return;
    const key = chip.dataset.key;
    let val;
    try { val = JSON.parse(chip.dataset.val); } catch (_) { return; }
    if (JSON.stringify(_form.attrs[key]) === JSON.stringify(val)) delete _form.attrs[key];
    else _form.attrs[key] = val;
    renderAttrChips($('vp-fix-attrs'), typeDef(($('vp-fix-type') || {}).value || ''), _form.attrs, true, { show, clear });
  }

  function buildRequest() {
    const op = String(($('vp-fix-op') || {}).value || '');
    if (op !== 'add' && !_sel) { hint('Click a marker first.'); return null; }
    const type = String(($('vp-fix-type') || {}).value || '').toUpperCase();
    const span = isSpan(type);
    let startS = null;
    let endS = null;
    if (op === 'retime' || op === 'add') {
      startS = parseClock($('vp-fix-start').value);
      if (span) endS = parseClock($('vp-fix-end').value);
      if (!Number.isFinite(startS) || (span && !Number.isFinite(endS))) { hint('Enter a time as m:ss.', true); return null; }
      const dur = Number(vod().gameDurationSeconds) || 0;
      if ((dur > 0 && (startS > dur || (span && endS > dur))) || (span && endS < startS)) {
        hint('Enter a time inside this game.', true);
        return null;
      }
    }
    const attrs = { ..._form.attrs };
    const hasAttrs = Object.keys(attrs).length > 0;
    let patch = null;
    if (op === 'retype') patch = { eventType: type };
    else if (op === 'retime') patch = span ? { gameTimeS: startS, endS } : { gameTimeS: startS };
    else if (op === 'attr') {
      if (!hasAttrs) { hint('Pick an attribute value first.', true); return null; }
      patch = { attrs };
    } else if (op === 'add') {
      patch = { eventType: type, gameTimeS: startS };
      if (span) patch.endS = endS;
      if (hasAttrs) patch.attrs = attrs;
    }
    _form.correctionId ||= crypto.randomUUID(); // kept across a failed save so a retry stays idempotent
    const req = { gameId: gameId(), correctionId: _form.correctionId, op, patch, reason: String($('vp-fix-reason').value || '').trim() };
    if (op !== 'add') req.subject = subjectOf(_sel);
    return req;
  }

  async function saveFromForm() {
    if (!_form) return;
    const req = buildRequest();
    if (!req) return;
    if (!hasBackend()) { hint(PREVIEW_MSG); return; }
    const btn = $('vp-fix-save');
    if (btn) btn.disabled = true;
    try {
      const r = await post(req);
      if (!r) return;
      const what = req.op === 'add' ? labelOf(req.patch.eventType) : (_sel.label || labelOf(_sel.type));
      const key = req.op === 'add' ? (r.eventKey || 'usr:' + req.correctionId) : _sel.key;
      pushUndo({ correctionId: req.correctionId, key, label: `${req.op} ${what}` });
      if (req.op === 'add') _lastAddType = req.patch.eventType;
      hint(r.message ? `Saved. ${r.message}` : 'Saved.');
      closeForm();
      ctx.reloadBookmarks();
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  function usePlayhead() {
    const v = ctx.video();
    const t = Math.max(0, Math.floor((v && v.currentTime) || 0));
    $('vp-fix-start').value = clock(t);
    const endS = parseClock($('vp-fix-end').value);
    if (!Number.isFinite(endS) || endS <= t) $('vp-fix-end').value = clock(t);
  }

  // The stepper's Fix button: the marker the playhead is on (or just passed).
  function fixCurrentMarker() {
    const m = ctx.currentMarker ? ctx.currentMarker() : null;
    const e = m && (vod().gameEvents || []).find((x) =>
      (m.key && x.eventKey === m.key) || (!m.key && m.id != null && Number(x.id) === Number(m.id)));
    if (!e) { hint('Click a marker first.'); return; }
    const sel = fromEvent(e);
    select(sel);
    openFor(sel);
  }

  async function exportCorrections() {
    if (!hasBackend()) { hint(PREVIEW_MSG); return; }
    try {
      const r = await ctx.core.invoke('export_event_corrections', { gameId: gameId() });
      await navigator.clipboard.writeText((r && r.json) || '');
      hint(`Copied ${Number(r && r.count) || 0} corrections as JSON (contains champion and player names).`);
    } catch (err) {
      hint(errText(err), true);
    }
  }

  // Runs on every reload (from renderAutoClipPanel): refresh the type options and
  // the list; an open form keeps its inputs and chip state.
  function renderPanel() {
    const typeSel = $('vp-fix-type');
    if (typeSel) renderTypeOptions(typeSel, catalog(), String(typeSel.value || ''), clear);
    if (_form) renderFields();
    renderSelection();
    const list = $('vp-fix-list');
    const count = $('vp-fix-count');
    if (list) {
      const n = renderList(list, vod().corrections, { tpl, show, clear, clock, labelOf });
      if (count) count.textContent = String(n);
    }
  }

  // ── wiring (delegated; the rows and bars are rebuilt on every reload) ────────
  const markers = $('vp-markers');
  if (markers) {
    // Capturing, so the selection lands before the jump handler seeks (unchanged UX).
    markers.addEventListener('click', (ev) => {
      const bar = ev.target.closest && ev.target.closest('.evbar');
      if (bar) select(fromBar(bar));
    }, true);
    markers.addEventListener('contextmenu', (ev) => {
      const bar = ev.target.closest && ev.target.closest('.evbar');
      if (!bar) return;
      ev.preventDefault();
      select(fromBar(bar));
      openFor(_sel);
    });
  }
  document.addEventListener('click', async (ev) => {
    const t = ev.target.closest && ev.target.closest('[data-action]');
    if (!t) return;
    const action = t.dataset.action;
    if (action === 'save_correction') { ev.preventDefault(); await saveFromForm(); }
    else if (action === 'fix_cancel') { ev.preventDefault(); closeForm(); }
    else if (action === 'fix_now') { ev.preventDefault(); usePlayhead(); }
    else if (action === 'fix_attr') { ev.preventDefault(); toggleAttr(t); }
    else if (action === 'fix_event') { ev.preventDefault(); fixCurrentMarker(); }
    else if (action === 'toast_undo') { ev.preventDefault(); await undoLast(); }
    else if (action === 'export_corrections') { ev.preventDefault(); await exportCorrections(); }
    else if (action === 'revert_correction') {
      ev.preventDefault();
      ev.stopPropagation(); // don't let the row's jump fire
      const row = t.closest('.vp-fix-row');
      if (row && row.dataset.correctionId) await revert(row.dataset.correctionId, 'Reverted.');
    }
  });
  document.addEventListener('change', (ev) => {
    if (ev.target && (ev.target.id === 'vp-fix-op' || ev.target.id === 'vp-fix-type')) renderFields();
  });
  // Field-local keys inside the form (the page's global keydown ignores INPUT/SELECT
  // targets): Enter in a text field saves, Escape closes.
  const form = $('vp-fix-form');
  if (form) {
    form.addEventListener('keydown', (ev) => {
      const tag = ev.target && ev.target.tagName;
      if (tag !== 'INPUT' && tag !== 'SELECT') return;
      if (ev.key === 'Escape') { ev.preventDefault(); closeForm(); }
      else if (ev.key === 'Enter' && tag === 'INPUT') { ev.preventDefault(); saveFromForm(); }
    });
  }

  return { renderPanel, decorateBar, appendGhosts, handleKey, select, openFor, clearSelection };
}

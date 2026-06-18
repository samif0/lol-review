// Revu desktop review — data-driven renderer for ONE game's review snapshot.
// Renders the JSON returned by the Tauri command `get_review` (GET /api/review).
// All server-supplied strings are written via textContent (never innerHTML) to
// keep the surface XSS-free. Colors arrive as *Hex strings and are applied to
// style/stroke properties only.
//
// EDITABLE: the review form (mental rating / debrief notes / concept tags /
// objective-practiced toggles) is gathered and committed via invoke('save_review').
// "Skip" marks the game reviewed without notes via invoke('skip_review'). Both
// re-fetch the snapshot on success so the UI reflects the committed state.
// save_review COMMITS — there is no un-review — so the button confirms inline.
//
// GRANULAR WRITES (Batch 2): some interactions persist IMMEDIATELY, one field at
// a time, then refetch:
//   • death cause chips    → classify_death / clear_death
//   • evidence triage      → set_evidence_polarity / set_evidence_objective /
//                            set_evidence_status   (SHARED with the VOD player)
//   • prompt answer boxes  → save_prompt_answer (on blur; empty deletes)
//   • focus adherence      → set_focus_adherence (Yes/Partly/No, immediate)
// The objective practiced toggles + execution notes + concept-tag selection ride
// the batched save_review payload (collected in gatherForm).

// ── invoke resolver ────────────────────────────────────────────────────────
// Prefer the official @tauri-apps/api/core import; fall back to the global the
// Tauri webview injects. In a plain browser (no Tauri) both are absent and we
// fall through to a local sample JSON so the page previews standalone.
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

const isTauri = () => typeof window.__TAURI__ !== 'undefined';

// ── granular-write helper ────────────────────────────────────────────────────
// Fire one immediate-persist write (death classify, evidence triage, prompt
// answer, focus adherence). In browser preview (no Tauri) it's a logged no-op so
// the page stays interactive. Returns true on success, false on failure.
async function postWrite(cmd, args) {
  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[review] (preview) ${cmd} — no Tauri backend.`, args);
    return true;
  }
  try {
    // Every review WRITE command (save_review/skip_review/classify_death/
    // set_evidence_*/save_prompt_answer/set_focus_adherence …) takes a single
    // `payload` arg in Rust, so the body must be wrapped — otherwise Tauri rejects
    // it with "missing required key payload" (which surfaces as a blank-message
    // error → the "Save failed." fallback).
    await invoke(cmd, { payload: args });
    return true;
  } catch (err) {
    console.error(`[review] ${cmd} failed:`, err);
    showCommit((err && err.message) ? err.message : `${cmd} failed.`, 'err');
    return false;
  }
}

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}
// Grow a textarea to fit its content (prompt answer boxes start one row tall).
function autoSize(ta) {
  if (!ta) return;
  ta.style.height = 'auto';
  ta.style.height = `${Math.max(ta.scrollHeight, 28)}px`;
}

const RING_CIRCUMFERENCE = 150.8; // 2·π·r, r=24

// The subject currently rendered — save/skip read gameId/champ/win from here.
let _subject = null;

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchReview() {
  // A ?gameId= in the URL means we were opened by clicking a specific game row;
  // pass it so the backend loads THAT game's review (else the sample subject).
  const params = new URLSearchParams(window.location.search);
  const gid = params.get('gameId');
  const gameId = gid ? Number(gid) : null;

  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_review', gameId ? { gameId } : {});
  }
  const res = await fetch('./sample-review.json');
  if (!res.ok) throw new Error(`sample-review.json ${res.status}`);
  return res.json();
}

// ── render: header (the hero game card) ─────────────────────────────────────
function renderHeader(subject) {
  const h = subject.header || {};
  $('hero-title').textContent = h.championName
    ? `${h.championName}: Review`
    : 'Review';

  $('rv-champ').textContent = h.championName || '—';

  const res = $('rv-res');
  res.textContent = h.resultText || '';
  if (h.resultColorHex) res.style.color = h.resultColorHex;

  $('rv-gmode').textContent = h.gameMode || '';
  $('rv-gdur').textContent = h.duration || '';
  $('rv-matchup').textContent = h.matchupHeading || '';

  $('rv-kda').textContent = h.kdaText || '';
  $('rv-kdar').textContent = h.kdaRatioText ? `${h.kdaRatioText} KDA` : '';

  // "Already reviewed" marker replaces nothing — it's an inline note beside VOD.
  show($('rv-reviewed'), !!h.hasReview);

  // Statusline: the subject-source explanation (which game was chosen).
  const statusB = document.querySelector('#statusline b');
  statusB.textContent = h.metaLine || '';
  show($('rv-hero'), true);

  // LANING @10 line — sits under the stat strip; shows only when the timeline
  // backfill has run (hasLaningAt10).
  const laning = $('rv-laning');
  if (laning) {
    laning.textContent = h.laningAt10Line || '';
    show(laning, !!h.hasLaningAt10 && !!h.laningAt10Line);
  }
}

// ── render: stat strip (auto-captured) ──────────────────────────────────────
function renderStats(subject) {
  const stats = Array.isArray(subject.stats) ? subject.stats : [];
  const strip = $('rv-strip');
  clear(strip);
  for (const c of stats) {
    const el = tpl('tpl-stat');
    el.querySelector('.k').textContent = c.label || '';
    el.querySelector('.v').textContent = c.value || '';
    const s = el.querySelector('.s');
    s.textContent = c.sub || '';
    strip.appendChild(el);
  }
}

// ── render: objectives in play (animated rings + practiced switch) ──────────
// Reuses the dashboard ring card + the draw-on-load animation. Each card carries
// a "Practiced" ON/OFF switch and an execution-note input that feed
// objectivePractices on save, plus any custom coaching prompts as read-only
// guided questions. The objective id is stamped on the card so gatherForm() reads it.
function renderObjectives(subject) {
  const objs = Array.isArray(subject.objectives) ? subject.objectives : [];
  const host = $('rv-objectives');
  clear(host);

  // The objectives section is the focus block at the top; hide it wholesale when
  // there are no objectives so the debrief rises directly under the game header.
  if (objs.length === 0) {
    show($('rv-obj-label'), false);
    show($('rv-objsec'), subject.hasObjectives === false);
    show($('rv-obj-empty'), subject.hasObjectives === false);
    return;
  }
  show($('rv-objsec'), true);
  show($('rv-obj-label'), true);
  show($('rv-obj-empty'), false);

  for (const o of objs) {
    const el = tpl('tpl-objective');
    const progress = Math.max(0, Math.min(1, Number(o.progress) || 0));
    const offset = RING_CIRCUMFERENCE * (1 - progress);

    const track = el.querySelector('.ring-track');
    const prog = el.querySelector('.ring-prog');
    track.setAttribute('stroke', o.levelDimColorHex || 'rgba(255,255,255,0.13)');
    prog.setAttribute('stroke', o.levelColorHex || '#9d8bff');
    // Start empty, then transition to the target on the next frame so the CSS
    // transition on .ring-prog animates the arc filling.
    prog.setAttribute('stroke-dashoffset', String(RING_CIRCUMFERENCE));
    requestAnimationFrame(() => requestAnimationFrame(() => {
      prog.setAttribute('stroke-dashoffset', String(offset));
    }));

    el.querySelector('.pc').textContent =
      o.progressLabel || `${Math.round(progress * 100)}%`;

    // The shared `.pill { display:inline-block }` rule overrides the [hidden]
    // attribute, so toggling `.hidden` leaves a stray "PRIORITY" tag visible.
    // Remove the node outright for non-priority objectives instead.
    const pill = el.querySelector('.pill');
    if (o.isPriority) { pill.hidden = false; } else { pill.remove(); }
    el.querySelector('.oname').textContent = o.title || '';
    el.querySelector('.ometa').textContent =
      o.metaText || [o.levelName, o.phaseLabel].filter(Boolean).join(' · ').toUpperCase();

    const crit = el.querySelector('.rv-crit');
    if (o.completionCriteria) {
      crit.textContent = o.completionCriteria;
      show(crit, true);
    }

    // Custom coaching prompts — EDITABLE guided answers. The tiny phase chip is
    // optional; the label is the question; the textarea is the answer for THIS
    // game (saved on blur via save_prompt_answer, empty text deletes the row).
    // The prompt id is stamped on the input so the blur handler can write it.
    const prompts = Array.isArray(o.prompts) ? o.prompts : [];
    const promptHost = el.querySelector('.rv-prompts');
    for (const p of prompts) {
      const label = String(p && p.label || '').trim();
      if (!label) continue;
      const promptId = Number(p && p.id);
      const row = tpl('tpl-prompt');
      const phaseEl = row.querySelector('.rv-prompt-phase');
      const phase = String(p && p.phase || '').trim();
      if (phase) { phaseEl.textContent = phase; show(phaseEl, true); } else { phaseEl.remove(); }
      row.querySelector('.rv-prompt-txt').textContent = label;
      const ansInput = row.querySelector('.rv-prompt-input');
      if (ansInput) {
        const answer = String(p && p.answer || '');
        ansInput.value = answer;
        // A valid prompt id is required to persist; without one keep it editable
        // but inert (no id to key the answer on).
        if (Number.isFinite(promptId) && promptId > 0) {
          ansInput.dataset.promptId = String(promptId);
          // Remember the last-saved value so blur only writes on a real change.
          ansInput.dataset.savedValue = answer;
        }
        autoSize(ansInput);
      }
      promptHost.appendChild(row);
    }
    show(promptHost, promptHost.childElementCount > 0);

    // Stamp the objective id; the execution note shows only when "Practiced" is on.
    el.dataset.objectiveId = String(o.id);
    const cb = el.querySelector('.rv-practiced-cb');
    const note = el.querySelector('.rv-objnote');
    const swLbl = el.querySelector('.rv-switch-lbl');
    // Seed the toggle + note from the SAVED state so a re-review (or the reload after
    // save) shows the persisted "Practiced" state instead of reverting to OFF. The
    // server now returns o.practiced / o.executionNote (hydrated per game).
    cb.checked = !!o.practiced;
    el.classList.toggle('is-practiced', cb.checked);
    if (swLbl) swLbl.textContent = cb.checked ? 'Practiced' : 'Not practiced';
    if (note) { note.value = o.executionNote || ''; show(note, cb.checked); }
    cb.addEventListener('change', () => {
      el.classList.toggle('is-practiced', cb.checked);
      if (swLbl) swLbl.textContent = cb.checked ? 'Practiced' : 'Not practiced';
      show(note, cb.checked);
      if (cb.checked) note.focus();
    });

    host.appendChild(el);
  }
}

// ── render: editable review form ────────────────────────────────────────────
// Each debrief field is an editable textarea, pre-filled with any saved value so
// re-reviewing edits in place. The `key` is the save_review payload field name.
const FIELD_ORDER = [
  ['wentWell',        'What went well',       'What clicked? wins, good reads…'],
  ['mistakes',        'What could improve',   'Mistakes, missed reads, habits to fix…'],
  ['focusNext',       'Focus next game',      'One thing to carry into the next queue…'],
  ['spottedProblems', 'Spotted problems',     'Recurring problems you noticed…'],
  ['attribution',     'Attribution',          'My play / teammates / matchup / variance…'],
  ['reviewNotes',     'Review notes',         'Anything else worth keeping…'],
];

// R-001: the cognitive-reappraisal pair, rendered as ONE blame-vs-improvable unit
// (not two stray fields). The "outside" box accepts the blame instinct; the
// "within" box pulls the protective internal-control half — attribution retraining
// WITH the blame instinct, not against it (Weiner/Dweck; brief 2026-06-16-03).
// Both round-trip through the existing save_review payload (sidecar already carries
// outsideControl/withinControl). Descriptive only — never scored or flagged.
const REAPPRAISAL_PAIR = [
  ['outsideControl', 'What was outside your control',
    'Granting the game went sideways — what genuinely wasn’t on you?'],
  ['withinControl', 'What you can still repeat',
    'Whatever else happened, the ONE thing YOU could do again regardless…'],
];

function parseTags(tagsJson) {
  if (!tagsJson) return [];
  try {
    const arr = JSON.parse(tagsJson);
    return Array.isArray(arr) ? arr.filter((t) => typeof t === 'string' && t.trim()) : [];
  } catch (_) {
    return [];
  }
}

// Append one editable tag chip; the ✕ removes it.
function addTagChip(label) {
  const text = String(label || '').trim();
  if (!text) return;
  const host = $('rv-tags');
  // Skip exact duplicates (case-insensitive) so the chip set stays clean.
  const existing = Array.from(host.querySelectorAll('.rv-tag-txt'))
    .map((n) => n.textContent.toLowerCase());
  if (existing.includes(text.toLowerCase())) return;
  const chip = tpl('tpl-tag');
  chip.querySelector('.rv-tag-txt').textContent = text;
  chip.querySelector('.rv-tag-x').addEventListener('click', () => chip.remove());
  host.appendChild(chip);
}

function renderForm(subject) {
  const f = subject.form || {};

  // "Already reviewed" banner — saving again overwrites the prior review. The
  // Delete-review button only makes sense once a review exists, so it shares the
  // same gate (and disappears after a delete blanks those columns).
  const hdr = subject.header || {};
  show($('rv-savednote'), !!hdr.hasReview);
  show($('rv-deletebtn'), !!hdr.hasReview);

  // Mental rating — seed the slider + readout (default 5 when unset). Tint the
  // readout with the server-supplied color when one is provided.
  const slider = $('rv-mental-input');
  const readout = $('rv-mental');
  const savedMental = Number(f.mentalRating);
  slider.value = String(savedMental > 0 ? savedMental : 5);
  readout.textContent = slider.value;
  if (f.mentalRatingColorHex) readout.style.color = f.mentalRatingColorHex;

  // Editable debrief textareas, pre-filled with any saved value.
  const fieldHost = $('rv-fields');
  clear(fieldHost);
  // Build one editable field card (reused by the main list + the reappraisal pair).
  // The card keeps the standard .rv-field-in class so gatherForm() picks every box
  // up by data-field automatically — no extra collection code for the new pair.
  const buildField = ([key, label, placeholder]) => {
    const el = tpl('tpl-field');
    const ta = el.querySelector('.rv-field-in');
    const lbl = el.querySelector('.rv-field-k');
    const id = `rv-in-${key}`;
    lbl.textContent = label;
    lbl.setAttribute('for', id);
    ta.id = id;
    ta.dataset.field = key;
    ta.placeholder = placeholder;
    const val = f[key];
    if (typeof val === 'string') ta.value = val;
    return el;
  };
  for (const spec of FIELD_ORDER) fieldHost.appendChild(buildField(spec));

  // R-001: the cognitive-reappraisal pair as ONE labelled unit (blame box +
  // improvable box), set off from the main fields so the two read together.
  const pairWrap = document.createElement('div');
  pairWrap.className = 'rv-reappraisal';
  const pairHd = document.createElement('div');
  pairHd.className = 'rv-reappraisal-hd';
  pairHd.textContent = 'Reframe the loss';
  pairWrap.appendChild(pairHd);
  for (const spec of REAPPRAISAL_PAIR) pairWrap.appendChild(buildField(spec));
  fieldHost.appendChild(pairWrap);

  // Concept tags — seed editable chips from the saved tagsJson.
  const tagHost = $('rv-tags');
  clear(tagHost);
  for (const t of parseTags(f.tagsJson)) addTagChip(t);
  $('rv-tag-input').value = '';

  // Reset the commit message between renders.
  showCommit('', null);
}

// ── render: focus check (Yes/Partly/No, immediate write) ────────────────────
// Reflects the session intention (if the snapshot carries one) and the saved
// focus-adherence value for this game. Tapping a button persists immediately
// (set_focus_adherence); re-tapping the selected one clears it (value null).
// The read snapshot may not yet emit focusAdherence/sessionIntention — the card
// degrades gracefully (no preselection, intention line hidden) but still writes.
function renderFocus(subject) {
  const card = $('rv-focus');
  if (!card) return;
  const f = subject.form || {};

  // Session intention line (whatever the snapshot exposes; optional).
  const intent = String(subject.sessionIntention || f.sessionIntention || '').trim();
  const intentEl = $('rv-focus-intent');
  if (intentEl) {
    if (intent) { intentEl.textContent = intent; show(intentEl, true); }
    else { show(intentEl, false); }
  }

  // Preselect the saved adherence (2=Yes / 1=Partly / 0=No; <0 or absent = unset).
  let saved = f.focusAdherence;
  saved = (saved === 0 || saved === 1 || saved === 2) ? saved : null;
  for (const btn of card.querySelectorAll('.rv-focus-btn')) {
    btn.classList.toggle('on', saved != null && Number(btn.dataset.focus) === saved);
  }
}

// Persist a focus-adherence choice immediately. Re-tapping the selected button
// clears it (value null). Updates the buttons in place, then writes + refetches.
async function onFocusClick(btn) {
  const card = $('rv-focus');
  if (!card) return;
  const gameId = Number(_subject && _subject.gameId);
  if (!(gameId > 0)) return;
  const value = Number(btn.dataset.focus);
  const wasSelected = btn.classList.contains('on');

  for (const b of card.querySelectorAll('.rv-focus-btn')) b.classList.remove('on');
  let payloadValue;
  if (wasSelected) {
    payloadValue = null; // clear
  } else {
    btn.classList.add('on');
    payloadValue = value;
  }
  const ok = await postWrite('set_focus_adherence', { gameId, value: payloadValue });
  if (ok) await loadReview();
}

// ── render: death audit (one-tap cause chips, immediate write) ──────────────
// One row per death: the timestamp, the saved cause (if classified), and the
// six cause chips. Tapping a chip classifies the death (classify_death);
// re-tapping the selected chip clears it (clear_death). The death is keyed on
// its gameTimeSeconds (stamped on the row + each chip). Persist is immediate;
// the snapshot refetches after so the saved cause label + selection stay true.
function renderDeaths(subject) {
  const deaths = Array.isArray(subject.deaths) ? subject.deaths : [];
  const host = $('rv-deaths');
  clear(host);
  show($('rv-deathsec'), deaths.length > 0);
  if (deaths.length === 0) return;

  for (const d of deaths) {
    const el = tpl('tpl-death');
    const timeS = Number(d.gameTimeSeconds);
    el.dataset.timeS = String(Number.isFinite(timeS) ? timeS : 0);
    el.querySelector('.rv-death-time').textContent = d.timeText || '';
    const cause = el.querySelector('.rv-death-cause');
    if (d.isClassified && d.selectedLabel) {
      cause.textContent = d.selectedLabel;
      show(cause, true);
    }
    const chipHost = el.querySelector('.rv-death-chips');
    const chips = Array.isArray(d.chips) ? d.chips : [];
    for (const c of chips) {
      const chip = tpl('tpl-deathchip');
      chip.querySelector('.rv-dchip-lbl').textContent = c.label || '';
      if (c.hint) chip.title = c.hint;
      // Stamp the class key so the click handler knows what to write.
      chip.dataset.deathKey = String(c.key || '');
      if (c.isSelected) chip.classList.add('on');
      chip.setAttribute('role', 'button');
      chip.tabIndex = 0;
      chipHost.appendChild(chip);
    }
    host.appendChild(el);
  }
}

// Classify (or clear) one death from a chip click. Updates the row in place so
// the selection feels instant, then writes + refetches to confirm.
async function onDeathChipClick(chip) {
  const row = chip.closest('.rv-death');
  if (!row) return;
  const gameId = Number(_subject && _subject.gameId);
  const timeS = Number(row.dataset.timeS);
  const key = String(chip.dataset.deathKey || '');
  if (!(gameId > 0) || !Number.isFinite(timeS) || !key) return;

  const wasSelected = chip.classList.contains('on');
  // Sibling chips deselect; re-tapping the selected chip clears it.
  for (const sib of row.querySelectorAll('.rv-dchip')) sib.classList.remove('on');
  const causeEl = row.querySelector('.rv-death-cause');
  let ok;
  if (wasSelected) {
    if (causeEl) { causeEl.textContent = ''; show(causeEl, false); }
    ok = await postWrite('clear_death', { gameId, timeS });
  } else {
    chip.classList.add('on');
    if (causeEl) {
      causeEl.textContent = chip.querySelector('.rv-dchip-lbl')?.textContent || '';
      show(causeEl, true);
    }
    ok = await postWrite('classify_death', { gameId, timeS, key });
  }
  if (ok) await loadReview();
}

// ── render: evidence triage (immediate writes) ──────────────────────────────
// Two lists: ATTACHED (tagged/clip/triaged moments, capped) and EVIDENCE TO
// SORT (unassigned, uncapped). Each row shows time + title + polarity dot +
// objective tag + status, plus triage controls:
//   • Good / Bad   → set_evidence_polarity  (promotes out of needs_review)
//   • Attach picker→ set_evidence_objective (marks the objective practiced)
//   • Dismiss      → set_evidence_status (status='dismissed'); removed in place
// The picker options are built from the active objectives the snapshot ships.
// EVIDENCE TO SORT rows show all controls; ATTACHED rows show only the picker
// (to re-assign/detach) + Dismiss (Good/Bad already implied by being triaged).
function evidRow(item, objectiveOptions, isAttached) {
  const el = tpl('tpl-evid');
  const id = Number(item.id);
  el.dataset.evidId = String(Number.isFinite(id) ? id : 0);

  // Make the moment card jump to the VOD at its start time. Only when we have a
  // real seek point — moments without a timestamp aren't clickable. The click is
  // routed through the delegated handler (data-action), which ignores clicks that
  // land on the inner triage controls so Good/Bad/picker/Dismiss still work.
  const seek = Number(item.startTimeSeconds);
  if (Number.isFinite(seek) && seek > 0) {
    el.dataset.action = 'view_moment';
    el.dataset.seek = String(Math.floor(seek));
    el.classList.add('rv-evid-clickable');
    el.setAttribute('role', 'button');
    el.tabIndex = 0;
  }

  const dot = el.querySelector('.rv-evid-dot');
  if (item.polarityColorHex) dot.style.background = item.polarityColorHex;

  const timeEl = el.querySelector('.rv-evid-time');
  if (item.timeText) { timeEl.textContent = item.timeText; show(timeEl, true); }
  el.querySelector('.rv-evid-title').textContent = item.title || '(untitled moment)';

  const noteEl = el.querySelector('.rv-evid-note');
  const note = String(item.note || '').trim();
  if (note) { noteEl.textContent = note; show(noteEl, true); }

  const objEl = el.querySelector('.rv-evid-obj');
  const objTitle = String(item.objectiveTitle || '').trim();
  if (objTitle) { objEl.textContent = objTitle; show(objEl, true); }

  el.querySelector('.rv-evid-status').textContent = item.statusLabel || '';

  // ── Triage controls ──
  const actions = el.querySelector('.rv-evid-actions');
  show(actions, true);

  // Polarity buttons reflect the current polarity; hidden on ATTACHED rows.
  const goodBtn = el.querySelector('.rv-evid-good');
  const badBtn = el.querySelector('.rv-evid-bad');
  if (isAttached) {
    if (goodBtn) goodBtn.remove();
    if (badBtn) badBtn.remove();
  } else {
    if (item.polarity === 'good' && goodBtn) goodBtn.classList.add('on');
    if (item.polarity === 'bad' && badBtn) badBtn.classList.add('on');
  }

  // Objective picker: "(no objective)" + each active objective; current selected.
  const pick = el.querySelector('.rv-evid-pick');
  if (pick) {
    const none = document.createElement('option');
    none.value = '';
    none.textContent = 'Attach to objective…';
    pick.appendChild(none);
    const curObj = item.objectiveId != null ? Number(item.objectiveId) : null;
    for (const o of objectiveOptions) {
      const opt = document.createElement('option');
      opt.value = String(o.id);
      opt.textContent = o.title || `Objective ${o.id}`;
      if (curObj != null && Number(o.id) === curObj) opt.selected = true;
      pick.appendChild(opt);
    }
  }
  return el;
}

function renderEvidence(subject) {
  const ev = subject.evidence || {};
  const attached = Array.isArray(ev.attached) ? ev.attached : [];
  const unassigned = Array.isArray(ev.unassigned) ? ev.unassigned : [];

  // Objective options for the attach picker come from the active objectives the
  // snapshot already ships (id + title), so no extra read is needed.
  const objs = Array.isArray(subject.objectives) ? subject.objectives : [];
  const objectiveOptions = objs.map((o) => ({ id: Number(o.id), title: o.title }));

  const attachedHost = $('rv-evid-attached');
  const unassignedHost = $('rv-evid-unassigned');
  clear(attachedHost);
  clear(unassignedHost);

  for (const it of attached) attachedHost.appendChild(evidRow(it, objectiveOptions, true));
  for (const it of unassigned) unassignedHost.appendChild(evidRow(it, objectiveOptions, false));

  show($('rv-evid-attached-label'), attached.length > 0);
  show($('rv-evid-unassigned-label'), unassigned.length > 0);
  show($('rv-evidsec'), attached.length > 0 || unassigned.length > 0);
}

// Handle an evidence triage control (Good/Bad/Dismiss button or objective <select>).
async function onEvidenceAction(action, el) {
  const row = el.closest('.rv-evid');
  if (!row) return;
  const evidenceId = Number(row.dataset.evidId);
  if (!(evidenceId > 0)) return;
  const gameId = Number(_subject && _subject.gameId) || null;

  let ok = false;
  if (action === 'good' || action === 'bad') {
    ok = await postWrite('set_evidence_polarity', { evidenceId, polarity: action });
  } else if (action === 'dismiss') {
    // Remove in place first (preserve scroll feel), then write + refetch.
    row.remove();
    ok = await postWrite('set_evidence_status', { evidenceId, status: 'dismissed' });
  } else if (action === 'objective') {
    const raw = el.value;
    const objectiveId = raw ? Number(raw) : null;
    ok = await postWrite('set_evidence_objective', { evidenceId, objectiveId, gameId });
  }
  if (ok) await loadReview();
}

// ── render: concept-tag catalog (selectable grid → selectedTagIds) ──────────
// The full tag catalog with the tags selected for THIS game highlighted (.on).
// Clicking a chip toggles its selection; the chosen tag ids are collected in
// gatherForm() as selectedTagIds and committed with the batched save_review.
// Each chip stamps its tag id + its own color so the tint applies when selected.
function renderTagCatalog(subject) {
  const tags = Array.isArray(subject.tagCatalog) ? subject.tagCatalog : [];
  const grid = $('rv-tagcat-grid');
  clear(grid);
  show($('rv-tagcat'), tags.length > 0);
  if (tags.length === 0) return;

  for (const t of tags) {
    const chip = tpl('tpl-tagcat');
    chip.querySelector('.rv-tagcat-txt').textContent = t.name || '';
    const id = Number(t.id);
    if (Number.isFinite(id)) chip.dataset.tagId = String(id);
    if (t.colorHex) chip.dataset.color = t.colorHex;
    chip.setAttribute('role', 'button');
    chip.tabIndex = 0;
    if (t.isSelected) applyTagcatSelected(chip, true);
    grid.appendChild(chip);
  }
}

// Apply/remove the selected look on a catalog chip (tint with the tag's color).
function applyTagcatSelected(chip, on) {
  chip.classList.toggle('on', on);
  const color = chip.dataset.color;
  if (on && color) {
    chip.style.borderColor = color;
    chip.style.color = color;
  } else {
    chip.style.borderColor = '';
    chip.style.color = '';
  }
}

// ── render: matchup history (read-only) ─────────────────────────────────────
// Past notes for the same champ-vs-enemy matchup (newest first), each with a
// meta line (game id · date · helpful).
function renderMatchupHistory(subject) {
  const items = Array.isArray(subject.matchupHistory) ? subject.matchupHistory : [];
  const host = $('rv-matchlist');
  clear(host);
  show($('rv-matchsec'), items.length > 0);
  if (items.length === 0) return;

  for (const m of items) {
    const el = tpl('tpl-match');
    el.querySelector('.rv-match-note').textContent = m.note || '';
    el.querySelector('.rv-match-meta').textContent = m.metaText || '';
    if (m.helpful === true) el.classList.add('helpful');
    else if (m.helpful === false) el.classList.add('unhelpful');
    host.appendChild(el);
  }
}

// ── gather: read the editable form into the save_review payload ─────────────
// Maps each input back to its save_review field. selectedTagIds comes from the
// concept-tag CATALOG grid (each chip stamps its real tag id), so the saved
// selection round-trips by id (not by free-text). objectivePractices collects
// each objective's practiced toggle + execution note.
function gatherForm() {
  if (!_subject) return null;
  const h = _subject.header || {};

  // Debrief textareas keyed by data-field (wentWell / mistakes / focusNext / …).
  const fields = {};
  for (const ta of document.querySelectorAll('#rv-fields .rv-field-in')) {
    fields[ta.dataset.field] = ta.value.trim();
  }

  // Objective practiced toggles + execution notes → objectivePractices.
  const objectivePractices = [];
  for (const card of document.querySelectorAll('#rv-objectives [data-objective-id]')) {
    const objectiveId = Number(card.dataset.objectiveId);
    if (!Number.isFinite(objectiveId)) continue;
    const practiced = !!card.querySelector('.rv-practiced-cb')?.checked;
    const executionNote = card.querySelector('.rv-objnote')?.value.trim() || '';
    objectivePractices.push({ objectiveId, practiced, executionNote });
  }

  // Concept tags — the selected catalog chips, by tag id.
  const selectedTagIds = [];
  for (const chip of document.querySelectorAll('#rv-tagcat-grid .rv-tagcat-chip.on')) {
    const id = Number(chip.dataset.tagId);
    if (Number.isFinite(id) && id > 0) selectedTagIds.push(id);
  }

  return {
    gameId: Number(_subject.gameId),
    championName: h.championName || '',
    win: !!h.win,
    mentalRating: Number($('rv-mental-input').value) || 0,
    wentWell: fields.wentWell || '',
    mistakes: fields.mistakes || '',
    focusNext: fields.focusNext || '',
    spottedProblems: fields.spottedProblems || '',
    attribution: fields.attribution || '',
    reviewNotes: fields.reviewNotes || '',
    // R-001 reappraisal pair (round-trips to games.outside_control/within_control).
    outsideControl: fields.outsideControl || '',
    withinControl: fields.withinControl || '',
    selectedTagIds,
    objectivePractices,
  };
}

// ── commit message line (saved confirmation / error) ────────────────────────
function showCommit(text, kind) {
  const el = $('rv-commit-msg');
  if (!el) return;
  el.textContent = text || '';
  el.classList.remove('ok', 'err');
  if (kind) el.classList.add(kind);
  show(el, !!text);
}

// ── empty / error states ────────────────────────────────────────────────────
function renderEmpty() {
  _subject = null;
  show($('rv-hero'), false);
  show($('rv-body'), false);
  show($('rv-empty'), true);
  const statusB = document.querySelector('#statusline b');
  statusB.textContent = 'Nothing to review right now.';
}

function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
// Only on the FIRST render of a page load (not on every refresh).
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  // Reflects the on-page order: hero → objectives (the focus) → stats → death
  // audit → evidence → debrief form.
  const order = [
    $('rv-hero'),
    $('rv-objsec'),
    $('rv-strip'),
    $('rv-deathsec'),
    $('rv-evidsec'),
    $('rv-form'),
  ].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  const subject = d && d.subject;
  if (!subject) {
    renderEmpty();
    return;
  }
  _subject = subject;
  show($('rv-empty'), false);
  show($('rv-body'), true);

  renderHeader(subject);
  renderStats(subject);
  renderObjectives(subject);
  renderFocus(subject);
  renderDeaths(subject);
  renderEvidence(subject);
  renderForm(subject);
  renderTagCatalog(subject);
  renderMatchupHistory(subject);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function loadReview() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchReview();
    render(data);
  } catch (err) {
    renderError(err);
    // Surface to console for diagnosis without leaking into the DOM markup.
    console.error('[review] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── live form interactions: mental slider + tag input ───────────────────────
// Mental slider mirrors its value into the readout as it moves.
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.id === 'rv-mental-input') {
    $('rv-mental').textContent = ev.target.value;
  }
});

// Tag input: Enter or comma commits the current text as a chip; Backspace on an
// empty input removes the last chip. Blur also commits any pending text.
function commitTagInput() {
  const input = $('rv-tag-input');
  if (!input) return;
  // Allow a single comma-paste to expand into several chips.
  for (const part of input.value.split(',')) addTagChip(part);
  input.value = '';
}
document.addEventListener('keydown', (ev) => {
  const input = ev.target;
  if (!input || input.id !== 'rv-tag-input') return;
  if (ev.key === 'Enter' || ev.key === ',') {
    ev.preventDefault();
    commitTagInput();
  } else if (ev.key === 'Backspace' && input.value === '') {
    const last = $('rv-tags').lastElementChild;
    if (last) last.remove();
  }
});
document.addEventListener('blur', (ev) => {
  if (ev.target && ev.target.id === 'rv-tag-input') commitTagInput();
}, true);

// ── delegated granular-write handlers (immediate persist) ───────────────────
// One click handler routes all the immediate-write controls by what was hit:
//   • a death cause chip (.rv-dchip)          → onDeathChipClick
//   • an evidence triage button ([data-evid-action] button) → onEvidenceAction
//   • a focus-check button (.rv-focus-btn)     → onFocusClick
//   • a concept-tag catalog chip (.rv-tagcat-chip) → toggle selection (local)
// These coexist with the [data-action] handler below (save/skip/vod).
document.addEventListener('click', (ev) => {
  const chip = ev.target.closest('.rv-dchip');
  if (chip && chip.closest('#rv-deaths')) { ev.preventDefault(); onDeathChipClick(chip); return; }

  const evidBtn = ev.target.closest('button[data-evid-action]');
  if (evidBtn) { ev.preventDefault(); onEvidenceAction(evidBtn.dataset.evidAction, evidBtn); return; }

  const focusBtn = ev.target.closest('.rv-focus-btn');
  if (focusBtn) { ev.preventDefault(); onFocusClick(focusBtn); return; }

  const tagChip = ev.target.closest('.rv-tagcat-chip');
  if (tagChip && tagChip.closest('#rv-tagcat-grid')) {
    ev.preventDefault();
    applyTagcatSelected(tagChip, !tagChip.classList.contains('on'));
    return;
  }
});

// Keyboard activation (Enter/Space) for the role=button chips (death + tag) and
// the clickable evidence cards.
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const t = ev.target;
  if (t && t.classList && (t.classList.contains('rv-dchip') || t.classList.contains('rv-tagcat-chip') || t.classList.contains('rv-evid-clickable'))) {
    ev.preventDefault();
    t.click();
  }
});

// Objective attach picker (a <select>) fires on change, not click.
document.addEventListener('change', (ev) => {
  const pick = ev.target.closest('select[data-evid-action="objective"]');
  if (pick) onEvidenceAction('objective', pick);
});

// Prompt-answer boxes save on blur (empty deletes). Only writes when the value
// actually changed from what was loaded/last-saved, to avoid redundant writes.
document.addEventListener('blur', async (ev) => {
  const input = ev.target;
  if (!input || !input.classList || !input.classList.contains('rv-prompt-input')) return;
  const promptId = Number(input.dataset.promptId);
  if (!(promptId > 0)) return;
  const gameId = Number(_subject && _subject.gameId);
  if (!(gameId > 0)) return;
  const text = input.value;
  if (text === (input.dataset.savedValue ?? '')) return; // unchanged
  input.dataset.savedValue = text;
  await postWrite('save_prompt_answer', { promptId, gameId, text });
  // No refetch here — re-rendering would steal focus / rebuild the form. The
  // saved value is already reflected locally; the next full load reconciles.
}, true);

// Grow prompt-answer boxes as the user types.
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.classList && ev.target.classList.contains('rv-prompt-input')) {
    autoSize(ev.target);
  }
});

// ── single delegated action handler ─────────────────────────────────────────
// review_vod  = VOD button on the hero card (→ vod viewer, deferred).
// save_review = gather the editable form and COMMIT (no un-review), then refetch.
// skip_review = mark the game reviewed without notes, then refetch.
const ACTIONS = new Set(['review_vod', 'view_moment', 'save_review', 'skip_review', 'delete_review', 'copy_review', 'export_review']);

document.addEventListener('click', async (ev) => {
  // An evidence card jump must NOT fire when the click landed on its inner triage
  // controls (Good/Bad/Dismiss/objective picker) — those have their own handler.
  if (ev.target.closest('.rv-evid-actions')) return;

  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // "Review VOD" navigates to the VOD player for the loaded game (not a backend
  // command). Uses the subject's gameId.
  if (action === 'review_vod') {
    const gid = (_subject && _subject.gameId) || target.dataset.gameId;
    if (gid) window.location.href = `vodplayer.html?gameId=${encodeURIComponent(gid)}`;
    return;
  }

  // Clicking a moment/evidence card jumps to that game's VOD at the moment's start
  // time (vodplayer reads ?t=seconds). gameId is the loaded subject's.
  if (action === 'view_moment') {
    const gid = (_subject && _subject.gameId) || target.dataset.gameId;
    const t = Number(target.dataset.seek) || 0;
    if (gid && t > 0) {
      window.location.href =
        `vodplayer.html?gameId=${encodeURIComponent(gid)}&t=${encodeURIComponent(t)}`;
    }
    return;
  }

  // "Delete review" un-reviews the game (clears the saved debrief/tags/markers and
  // returns it to the queue; objective progress + streak data are PRESERVED). It's
  // confirm-gated, then navigates back to Games. Self-contained so it doesn't touch
  // the save/skip commit-button flow below.
  if (action === 'delete_review') {
    const gid = Number((_subject && _subject.gameId) || target.dataset.gameId || 0);
    if (!(gid > 0)) { showCommit('No game loaded to delete.', 'err'); return; }
    const ok = window.confirm(
      'Delete this review?\n\nThe written debrief, tags, and notes will be cleared and the game returns to your review queue. Objective progress and streak history are kept.');
    if (!ok) return;
    const invoke = await getInvoke();
    if (!invoke) { showCommit('Deleted (preview, no backend).', 'ok'); return; }
    const delBtn = $('rv-deletebtn');
    if (delBtn) delBtn.disabled = true;
    showCommit('Deleting…', null);
    try {
      // delete_review takes a single {payload} arg in Rust (the {payload} convention).
      await invoke('delete_review', { payload: { gameId: gid } });
      // Back to Games — the game is now unreviewed and back in the queue.
      window.location.href = 'games.html';
    } catch (err) {
      renderError(err);
      showCommit((err && err.message) ? err.message : 'Delete failed.', 'err');
      if (delBtn) delBtn.disabled = false;
      console.error('[review] delete_review failed:', err);
    }
    return;
  }

  // "Copy" / "Export" — this game's review as markdown. Both fetch the single-game
  // markdown from the sidecar (get_review_export_markdown takes a plain {gameId}
  // named arg — a READ command, NOT the {payload} write convention). Copy writes to
  // the clipboard; Export opens the native save dialog via save_export_file (also
  // plain named args, mirroring settings.js). Self-contained, no refetch.
  if (action === 'copy_review' || action === 'export_review') {
    const gid = Number((_subject && _subject.gameId) || target.dataset.gameId || 0);
    if (!(gid > 0)) { showCommit('No game loaded.', 'err'); return; }
    const invoke = await getInvoke();
    if (!invoke) { showCommit(action === 'copy_review' ? 'Copied (preview, no backend).' : 'Export (preview, no backend).', 'ok'); return; }
    const btn = target;
    if ('disabled' in btn) btn.disabled = true;
    showCommit(action === 'copy_review' ? 'Copying…' : 'Exporting…', null);
    try {
      const built = await invoke('get_review_export_markdown', { gameId: gid });
      if (!built || built.found === false || typeof built.markdown !== 'string') {
        showCommit('Could not build this review.', 'err');
        return;
      }
      if (action === 'copy_review') {
        await navigator.clipboard.writeText(built.markdown);
        showCommit('Copied to clipboard.', 'ok');
      } else {
        const out = await invoke('save_export_file', { fileName: built.fileName || `revu-${gid}-review.md`, markdown: built.markdown });
        showCommit(out && out.saved ? 'Export saved.' : 'Export canceled.', out && out.saved ? 'ok' : null);
      }
    } catch (err) {
      showCommit(action === 'copy_review' ? 'Copy failed.' : 'Export failed.', 'err');
      console.error(`[review] ${action} failed:`, err);
    } finally {
      if ('disabled' in btn) btn.disabled = false;
    }
    return;
  }

  // Flush any tag text still sitting in the input before gathering.
  if (action === 'save_review') commitTagInput();

  // Build the payload per action. save/skip read from the loaded subject.
  let args = {};
  if (action === 'save_review') {
    args = gatherForm();
    if (!args || !(args.gameId > 0)) { showCommit('No game loaded to save.', 'err'); return; }
  } else if (action === 'skip_review') {
    if (!_subject || !(Number(_subject.gameId) > 0)) { showCommit('No game loaded to skip.', 'err'); return; }
    args = { gameId: Number(_subject.gameId) };
  } else if (target.dataset.gameId != null) {
    args.gameId = Number(target.dataset.gameId);
  }

  const invoke = await getInvoke();
  if (!invoke) {
    // Browser preview: no backend to talk to. Acknowledge the click so the
    // standalone preview still feels responsive.
    console.info(`[review] (preview) action "${action}" — no Tauri backend.`, args);
    if (action === 'save_review') showCommit('Saved (preview, no backend).', 'ok');
    if (action === 'skip_review') showCommit('Skipped (preview, no backend).', 'ok');
    return;
  }

  // Disable the commit buttons together while the write is in flight.
  const saveBtn = $('rv-savebtn');
  const skipBtn = $('rv-skipbtn');
  const canDisable = 'disabled' in target;
  if (saveBtn) saveBtn.disabled = true;
  if (skipBtn) skipBtn.disabled = true;
  if (canDisable) target.disabled = true;
  if (action === 'save_review') showCommit('Saving…', null);
  if (action === 'skip_review') showCommit('Skipping…', null);
  try {
    // save_review / skip_review take a single `payload` arg in Rust — wrap the
    // gathered form/body so Tauri doesn't reject with "missing required key payload".
    await invoke(action, { payload: args });
    if (action === 'save_review') showCommit('Review saved.', 'ok');
    if (action === 'skip_review') showCommit('Game skipped.', 'ok');
    // RE-FETCH so the UI reflects the committed state (next subject / reviewed mark).
    if (action === 'save_review' || action === 'skip_review') {
      await loadReview();
    }
  } catch (err) {
    renderError(err);
    showCommit((err && err.message) ? err.message : 'Save failed.', 'err');
    console.error(`[review] action "${action}" failed:`, err);
  } finally {
    if (saveBtn) saveBtn.disabled = false;
    if (skipBtn) skipBtn.disabled = false;
    if (canDisable) target.disabled = false;
  }
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', loadReview);
} else {
  loadReview();
}

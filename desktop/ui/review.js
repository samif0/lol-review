import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke } from './platform/index.mjs';
import { objectiveMetaText, objectivePhaseLabel } from './objective-labels.mjs';
import { createMatchNavigation } from './match-navigation.mjs';

// Revu desktop review — data-driven renderer for ONE game's review snapshot.
// Renders the JSON returned by the Electron command `get_review` (GET /api/review).
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
//   • evidence triage      → set_evidence_polarity / set_evidence_objective /
//                            set_evidence_status   (SHARED with the VOD player)
//   • prompt answer boxes  → save_prompt_answer (on blur; empty deletes)
//   • focus adherence      → set_focus_adherence (Yes/Partly/No, immediate)
// The objective practiced toggles + execution notes + concept-tag selection ride
// the batched save_review payload (collected in gatherForm).

// ── granular-write helper ────────────────────────────────────────────────────
// Fire one immediate-persist write (evidence triage, prompt
// answer, focus adherence). In browser preview (no Electron) it's a logged no-op so
// the page stays interactive. Returns true on success, false on failure.
async function postWrite(cmd, args) {
  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[review] (preview) ${cmd} — no Electron backend.`, args);
    return true;
  }
  try {
    // Review writes use the same payload envelope as the shared command registry.
    await invoke(cmd, { payload: args });
    return true;
  } catch (err) {
    console.error(`[review] ${cmd} failed:`, err);
    // Accept Error objects and string failures, showing the sidecar's message.
    const raw = (err && err.message) ? err.message : (err == null ? '' : String(err));
    const m = raw.match(/sidecar HTTP \d+(?:\s+[^:]+)?:\s*(.*)$/i);
    showCommit((m ? m[1] : raw) || `${cmd} failed.`, 'err');
    return false;
  }
}

// ── draft autosave ──────────────────────────────────────────────────────────
// Typed debrief text used to ride ONLY the explicit SAVE REVIEW commit — any
// navigation (clip card jump, death Watch, Review VOD) silently discarded it.
// Now every edit debounce-saves the whole form as a review DRAFT
// (save_review_draft → review_drafts row, deleted on final save), and the
// snapshot builder hydrates the form from that draft on the next load.
let _draftTimer = null;
let _draftDirty = false;
let _draftSaving = false;
let _suppressDraft = false; // true while rendering or while save/skip commits

function markDraftDirty() {
  if (_suppressDraft || !_subject) return;
  matchNav.enableCapture();
  _draftDirty = true;
  if (_draftTimer) clearTimeout(_draftTimer);
  _draftTimer = setTimeout(() => { flushDraft(); }, 1200);
}

async function flushDraft() {
  if (_draftTimer) { clearTimeout(_draftTimer); _draftTimer = null; }
  // Wait out an in-flight save, then loop while dirty: edits typed DURING an
  // in-flight save re-raise _draftDirty and must also reach the server before
  // a navigation proceeds (the naive clear-after-await version silently
  // dropped them).
  while (!_suppressDraft && (_draftSaving || _draftDirty || _promptWrites.size)) {
    if (_draftSaving) {
      await new Promise((r) => setTimeout(r, 40));
      continue;
    }
    if (!_draftDirty) {
      // A prompt blur can start just before navigation. Wait for that write,
      // then check again for debrief edits made while it was finishing.
      await Promise.allSettled([..._promptWrites]);
      continue;
    }
    const payload = gatherForm();
    if (!payload || !(payload.gameId > 0)) { _draftDirty = false; break; }
    _draftSaving = true;
    _draftDirty = false; // claimed by this write; a new edit re-raises it
    try {
      const invoke = await getInvoke();
      if (invoke) await invoke('save_review_draft', { payload });
    } catch (err) {
      // Autosave is best-effort — never surface an error mid-typing, and don't
      // re-raise dirty (a dead backend would spin the loop forever).
      console.warn('[review] draft autosave failed:', err);
    } finally {
      _draftSaving = false;
    }
  }
}

// Cancel any pending autosave and mark clean — used when a real save/skip/delete
// commits (the backend deletes the draft; an autosave after would resurrect it).
function cancelDraft() {
  if (_draftTimer) { clearTimeout(_draftTimer); _draftTimer = null; }
  _draftDirty = false;
}

// ── small DOM helpers ───────────────────────────────────────────────────────
// Grow a textarea to fit its content (prompt answer boxes start one row tall).
function autoSize(ta) {
  if (!ta) return;
  ta.style.height = 'auto';
  ta.style.height = `${Math.max(ta.scrollHeight, 28)}px`;
}

const RING_CIRCUMFERENCE = 150.8; // 2·π·r, r=24

// The subject currently rendered — save/skip read gameId/champ/win from here.
let _subject = null;
// The whole snapshot (subject + queue info) — the Next-game chaining reads
// nextUnreviewedGameId / unreviewedRemaining off this.
let _snapshot = null;
const matchNav = createMatchNavigation({
  view: 'review', capture: captureReviewState, restore: restoreReviewState, beforeLeave: flushDraft,
});
let _restoreAttempted = false;
let _linkedRecordingGameId = 0;

function captureReviewState() {
  if (!_subject) return null;
  const mental = $('rv-mental-input');
  return {
    version: 1, gameId: Number(_subject.gameId), draftDirty: _draftDirty,
    fields: [...document.querySelectorAll('#rv-fields .rv-field-in')]
      .map(input => ({ field: input.dataset.field, value: input.value })),
    mental: { value: mental.value, touched: mental.dataset.touched || '' },
    tags: [...document.querySelectorAll('#rv-tags .rv-tag-txt')].map(tag => tag.textContent),
    pendingTag: $('rv-tag-input').value,
    selectedTagIds: [...document.querySelectorAll('#rv-tagcat-grid .rv-tagcat-chip.on')].map(chip => chip.dataset.tagId),
    focus: document.querySelector('#rv-focus .rv-focus-btn.on')?.dataset.focus ?? null,
    objectives: [...document.querySelectorAll('#rv-objectives [data-objective-id]')].map(card => ({
      id: card.dataset.objectiveId, practiced: !!card.querySelector('.rv-practiced-cb')?.checked,
      note: card.querySelector('.rv-objnote')?.value || '',
      prompts: [...card.querySelectorAll('.rv-prompt-input')].map((input, index) => ({
        id: input.dataset.promptId || null, index, value: input.value, savedValue: input.dataset.savedValue ?? null,
      })),
    })),
  };
}

async function restoreReviewState(state) {
  if (state?.version !== 1 || Number(state.gameId) !== Number(_subject?.gameId)) return;
  const previousSuppress = _suppressDraft;
  const before = JSON.stringify(gatherForm());
  _suppressDraft = true;
  try {
    const fields = new Map((state.fields || []).map(field => [field.field, field.value]));
    for (const input of document.querySelectorAll('#rv-fields .rv-field-in')) {
      if (typeof fields.get(input.dataset.field) === 'string') input.value = fields.get(input.dataset.field);
      autoSize(input);
    }
    updateReflectionCount();
    if (typeof state.mental?.value === 'string') {
      const mental = $('rv-mental-input');
      mental.value = state.mental.value;
      mental.dataset.touched = state.mental.touched === '1' ? '1' : '';
      $('rv-mental').textContent = mental.value;
    }
    clear($('rv-tags'));
    for (const tag of state.tags || []) if (typeof tag === 'string') addTagChip(tag);
    $('rv-tag-input').value = typeof state.pendingTag === 'string' ? state.pendingTag : '';
    const selectedTags = new Set(state.selectedTagIds || []);
    for (const chip of document.querySelectorAll('#rv-tagcat-grid .rv-tagcat-chip')) {
      applyTagcatSelected(chip, selectedTags.has(chip.dataset.tagId));
    }
    for (const button of document.querySelectorAll('#rv-focus .rv-focus-btn')) {
      button.classList.toggle('on', state.focus != null && button.dataset.focus === state.focus);
    }
    const objectives = new Map((state.objectives || []).map(objective => [objective.id, objective]));
    for (const card of document.querySelectorAll('#rv-objectives [data-objective-id]')) {
      const saved = objectives.get(card.dataset.objectiveId);
      if (!saved) continue;
      const checkbox = card.querySelector('.rv-practiced-cb'), note = card.querySelector('.rv-objnote');
      checkbox.checked = saved.practiced === true;
      card.classList.toggle('is-practiced', checkbox.checked);
      const label = card.querySelector('.rv-switch-lbl');
      if (label) label.textContent = checkbox.checked ? 'Practiced' : 'Not practiced';
      if (note) {
        note.value = typeof saved.note === 'string' ? saved.note : '';
        show(note, checkbox.checked);
        autoSize(note);
      }
      const prompts = [...card.querySelectorAll('.rv-prompt-input')];
      for (const draft of saved.prompts || []) {
        const input = draft.id ? prompts.find(prompt => prompt.dataset.promptId === draft.id) : prompts[draft.index];
        if (!input || typeof draft.value !== 'string') continue;
        // A completed blur write may already be in the fresh snapshot. Otherwise
        // retain the confirmed baseline; restoring raw text never marks it saved.
        const confirmed = input.dataset.savedValue;
        input.value = draft.value;
        if (confirmed !== draft.value) {
          if (typeof draft.savedValue === 'string') input.dataset.savedValue = draft.savedValue;
          else delete input.dataset.savedValue;
        }
        autoSize(input);
      }
    }
    _draftDirty = state.draftDirty === true || before !== JSON.stringify(gatherForm());
    const pendingPrompt = [...document.querySelectorAll('.rv-prompt-input')]
      .some(input => input.value !== (input.dataset.savedValue ?? ''));
    if (_draftDirty || pendingPrompt || $('rv-tag-input').value) showCommit('Unsaved edits restored.', null);
  } finally {
    _suppressDraft = previousSuppress;
  }
}

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchReview() {
  // A ?gameId= in the URL means we were opened by clicking a specific game row;
  // pass it so the backend loads THAT game's review (else the sample subject).
  const params = new URLSearchParams(window.location.search);
  const gid = params.get('gameId');
  const gameId = gid ? Number(gid) : null;

  return readSnapshot('get_review', 'sample-review.json', gameId ? { gameId } : {});
}

// ── render: header (the hero game card) ─────────────────────────────────────
function renderHeader(subject) {
  updateRecordingLaunch(subject.gameId);
  const h = subject.header || {};
  $('hero-title').textContent = h.championName && h.enemyChampion
    ? `${h.championName} vs ${h.enemyChampion}` : h.championName || 'Match review';

  const res = $('rv-res');
  const result = String(h.resultText || '').trim();
  res.textContent = result ? result[0].toUpperCase() + result.slice(1).toLowerCase() : '';
  if (h.resultColorHex) res.style.color = h.resultColorHex;

  $('rv-gmode').textContent = h.gameMode || '';
  $('rv-gdur').textContent = h.duration || '';
  const date = $('rv-date'); if (date) date.textContent = h.datePlayed || '';
  // The matchup line reads "you vs them". Without an opponent on record the
  // builder degrades it to the bare champion name, which would just echo the
  // title above it ("Qiyana" / "Qiyana") — show nothing until the matchup lands.
  const matchup = h.matchupHeading || '';
  $('rv-matchup').textContent = matchup === (h.championName || '') ? '' : matchup;

  // FULL LOBBY strip — one cell per lane (champions only, both teams) from the
  // participant map; hidden entirely when the game has no map yet.
  const lobby = $('rv-lobby');
  if (lobby) {
    clear(lobby);
    const rows = Array.isArray(h.lobbyMatchups) ? h.lobbyMatchups : [];
    for (const r of rows) {
      const cell = document.createElement('div');
      cell.className = 'rv-lobby-cell' + (r.isUserLane ? ' me' : '');
      const role = document.createElement('span');
      role.className = 'rv-lobby-role';
      const roleLabels = { TOP: 'Top', JUNGLE: 'Jungle', JG: 'Jungle', MID: 'Mid', MIDDLE: 'Mid',
        BOT: 'Bot', BOTTOM: 'Bot', SUP: 'Support', SUPPORT: 'Support', UTILITY: 'Support' };
      role.textContent = roleLabels[String(r.roleLabel || '').toUpperCase()] || r.roleLabel || '';
      const pair = document.createElement('span');
      pair.className = 'rv-lobby-pair';
      const own = document.createElement('b');
      own.textContent = r.own || '—';
      const vs = document.createElement('i');
      vs.textContent = 'vs';
      const enemy = document.createElement('b');
      enemy.textContent = r.enemy || '—';
      pair.append(own, vs, enemy);
      cell.append(role, pair);
      lobby.appendChild(cell);
    }
    show(lobby, rows.length > 0);
  }

  $('rv-kda').textContent = h.kdaText || '';
  $('rv-kdar').textContent = h.kdaRatioText ? `${h.kdaRatioText} KDA` : '';

  // "Already reviewed" marker replaces nothing — it's an inline note beside VOD.
  show($('rv-reviewed'), !!h.hasReview);
  // v3.11: how many timeline corrections apply to this game (0 hides the note).
  const fixes = Number(h.timelineFixes) || 0;
  const fixesN = $('rv-fixes-n');
  if (fixesN) fixesN.textContent = String(fixes);
  show($('rv-fixes'), fixes > 0);

  // The loaded header carries the match context; reserve the status line for
  // loading and empty states instead of repeating its date/mode/duration.
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = h.metaLine || '';
  show($('statusline'), false);
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
  $('rv-goal-count').textContent = objs.length > 0
    ? `${objs.length} learning objective${objs.length === 1 ? '' : 's'}` : 'None for this game';

  // Keep practice controls mounted inside their disclosure. Opening or closing
  // it never changes the values collected by the existing save/draft flow.
  if (objs.length === 0) {
    show($('rv-obj-label'), false);
    // Focus and mental-rating controls share this disclosure, including when
    // this match has no learning objectives attached.
    show($('rv-objsec'), true);
    show($('rv-obj-empty'), subject.hasObjectives === false);
    return;
  }
  show($('rv-objsec'), true);
  show($('rv-obj-label'), true);
  show($('rv-obj-empty'), false);

  // Objective options for every clip card's attach picker (id + title), built once
  // from the active objectives the snapshot ships — no extra read.
  const objectiveOptions = objs.map((o) => ({ id: Number(o.id), title: o.title }));

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
    el.querySelector('.ometa').textContent = objectiveMetaText(o);

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
      if (phase) { phaseEl.textContent = objectivePhaseLabel(phase); show(phaseEl, true); } else { phaseEl.remove(); }
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
      // P-027: the clips tagged to THIS prompt render directly under its answer.
      renderClipList(row.querySelector('.rv-prompt-clips'),
        p && p.clips, objectiveOptions);
      promptHost.appendChild(row);
    }
    show(promptHost, promptHost.childElementCount > 0);

    // P-027 homing: clips tagged to this objective but NOT to any prompt render in
    // an "Objective evidence (no prompt)" sub-block so they aren't lost.
    renderClipList(el.querySelector('.rv-obj-unprompted-list'),
      o.unpromptedClips, objectiveOptions, el.querySelector('.rv-obj-unprompted'));

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
// P-027 / 17-16: the per-moment specifics now live in the prompt/objective CLIPS,
// so the general debrief slimmed down to the fields that still earn their place —
// attribution, the Reframe pair (REAPPRAISAL_PAIR, appended in renderForm), and a
// catch-all reviewNotes. The dropped boxes (wentWell / mistakes / focusNext /
// spottedProblems) are no longer rendered; gatherForm ROUND-TRIPS their existing
// saved values from _subject.form (NOT '') so a re-save can't blank a value an
// older review/WinUI build wrote (the sidecar UPDATE is unconditional — see FIX-3).
const FIELD_ORDER = [
  ['reviewNotes',     'One thing to take into your next game', 'One decision to repeat or change, and why…'],
];
const REFLECTION_FIELD = ['attribution', 'What shaped the result?', 'Your decisions, the matchup, team play, or something else…'];

// R-001: the cognitive-reappraisal pair, rendered as ONE blame-vs-improvable unit
// (not two stray fields). The "outside" box accepts the blame instinct; the
// "within" box pulls the protective internal-control half — attribution retraining
// WITH the blame instinct, not against it (Weiner/Dweck; brief 2026-06-16-03).
// Both round-trip through the existing save_review payload (sidecar already carries
// outsideControl/withinControl). Descriptive only — never scored or flagged.
const REAPPRAISAL_PAIR = [
  ['outsideControl', 'What was outside your control',
    'What happened that you could not change?'],
  ['withinControl', 'What was within your control',
    'What decision could you repeat or change next time?'],
];

function updateReflectionCount() {
  const reflection = document.getElementById('rv-reflection');
  const count = reflection?.querySelector('.rv-reflection-count');
  if (!count) return;
  const answered = [...reflection.querySelectorAll('.rv-field-in')]
    .filter(input => typeof input.value === 'string' && input.value.trim().length > 0).length;
  count.textContent = answered ? `${answered} ${answered === 1 ? 'answer' : 'answers'}` : 'Optional';
}

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
  chip.querySelector('.rv-tag-x').addEventListener('click', () => { chip.remove(); markDraftDirty(); });
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
  // readout with the server-supplied color when one is provided. `touched`
  // tracks whether the value is a real answer (saved, or moved this session):
  // an untouched slider submits 0 and the backend keeps the stored value (or
  // the neutral default) instead of silently recording a 5/10 the user never
  // gave.
  const slider = $('rv-mental-input');
  const readout = $('rv-mental');
  const savedMental = Number(f.mentalRating);
  slider.value = String(savedMental > 0 ? savedMental : 5);
  slider.dataset.touched = savedMental > 0 ? '1' : '';
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

  // Reflection stays optional even when prior answers exist. Its count makes
  // those answers discoverable; mounted fields still participate in save/draft.
  // Shared navigation restores a user's expansion after this initial render.
  const reflection = document.createElement('details');
  reflection.id = 'rv-reflection';
  reflection.className = 'learning-details';
  reflection.open = false;
  const reflectionSummary = document.createElement('summary');
  reflectionSummary.textContent = 'Reflect on the result';
  const optional = document.createElement('span');
  optional.className = 'rv-reflection-count';
  optional.textContent = 'Optional';
  reflectionSummary.appendChild(optional);
  const reflectionBody = document.createElement('div');
  reflectionBody.className = 'learning-details-body';
  reflectionBody.appendChild(buildField(REFLECTION_FIELD));

  // Keep both sides of the reappraisal together for wins and losses.
  const pairWrap = document.createElement('div');
  pairWrap.className = 'rv-reappraisal';
  const pairHd = document.createElement('div');
  pairHd.className = 'rv-reappraisal-hd';
  pairHd.textContent = 'Separate what you can change';
  pairWrap.appendChild(pairHd);
  for (const spec of REAPPRAISAL_PAIR) pairWrap.appendChild(buildField(spec));
  reflectionBody.appendChild(pairWrap);
  reflection.append(reflectionSummary, reflectionBody);
  fieldHost.appendChild(reflection);
  updateReflectionCount();

  // Concept tags — seed editable chips from the saved tagsJson.
  const tagHost = $('rv-tags');
  clear(tagHost);
  for (const t of parseTags(f.tagsJson)) addTagChip(t);
  $('rv-tag-input').value = '';

  // Reset the commit message between renders — UNLESS a write just set one that
  // must survive the reload it triggered (e.g. "Review saved." after save_review,
  // which re-fetches the snapshot the same tick → renderForm would otherwise wipe
  // the confirmation that sits beside the Save button; brief 2026-06-17-17). When
  // a pending message is queued, render it (and clear the queue) instead of blanking.
  if (_pendingCommitMsg) {
    showCommit(_pendingCommitMsg.text, _pendingCommitMsg.kind);
    _pendingCommitMsg = null;
  } else if (f.hasDraft) {
    // The form fields above were hydrated from an unsaved autosave draft —
    // say so, so the user knows their earlier typing survived.
    showCommit('Unsaved edits restored from draft.', null);
  } else {
    showCommit('', null);
  }
}

// A commit message queued by a write that triggers a reload, so it survives the
// renderForm() that the reload runs. Consumed (once) by renderForm.
let _pendingCommitMsg = null;

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
  matchNav.enableCapture();
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
  // Persist only — do NOT re-render. The buttons already reflect the new state
  // above; a full loadReview() here would rebuild the debrief form and WIPE any
  // text the user has typed but not yet saved (the "I typed in a field, clicked a
  // focus button, and it didn't save" bug). The next natural load reconciles.
  await postWrite('set_focus_adherence', { gameId, value: payloadValue });
}

// ── render: death review ─────────────────────────────────────────────────────
// One keyboard-accessible watch action per death. Timeline corrections remain
// in the VOD player; the Review list follows the snapshot's chronological order.
function deathClock(seconds) {
  const whole = Math.floor(seconds);
  return `${String(Math.floor(whole / 60)).padStart(2, '0')}:${String(whole % 60).padStart(2, '0')}`;
}

function renderDeaths(subject) {
  const deaths = Array.isArray(subject.deaths) ? subject.deaths : [];
  const count = $('rv-death-count'); if (count) count.textContent = `${deaths.length} ${deaths.length === 1 ? 'death' : 'deaths'}`;
  const host = $('rv-deaths');
  clear(host);
  show($('rv-deathsec'), deaths.length > 0);

  deaths.forEach((death, index) => {
    const row = tpl('tpl-death');
    const timeS = death?.gameTimeSeconds;
    const timed = Number.isFinite(timeS) && timeS >= 0;
    const label = timed ? deathClock(timeS) : 'Time unavailable';
    row.querySelector('.rv-death-number').textContent = `Death ${index + 1}`;
    row.querySelector('.rv-death-time').textContent = label;
    const jump = row.querySelector('.rv-death-jump');
    jump.disabled = !timed;
    jump.setAttribute('aria-label', timed ? `Watch death ${index + 1} at ${label}` : `Death ${index + 1}: time unavailable`);
    if (timed) {
      row.dataset.timeS = String(timeS);
      // A match without a linked recording still opens the existing no-recording
      // VOD state, with a link back to this Review and its preserved draft.
      jump.dataset.seek = String(Math.max(0, timeS - 10));
      jump.title = `Watch death ${index + 1} at ${label}, starting up to 10 seconds earlier`;
    } else {
      delete jump.dataset.seek;
      jump.title = 'This death has no available timestamp.';
    }
    host.appendChild(row);
  });
}
// ── render: evidence triage (immediate writes) ──────────────────────────────
// P-027 replaced the old two-list evidence UI (ATTACHED / EVIDENCE TO SORT) with
// prompt-centric homing: clips render UNDER their prompt, under an objective's
// "no prompt" sub-block, or (fully untagged) in the top-level To-sort strip. The
// triage controls (Good/Bad/attach/dismiss) ride each clip card via the SAME
// data-evid-action wiring (onEvidenceAction), so triage still works. The old
// evidRow/renderEvidence were dropped; the set_evidence_* writes are unchanged.

// ── render: prompt-centric clip cards (P-027) ────────────────────────────────
// Build ONE clip card from a ReviewPromptClipDto (evidenceId, timeText, note,
// startSeconds, polarity, polarityColorHex, shareUrl). Clicking the card jumps to
// the VOD AT the clip: vodplayer.html?gameId=N&t=startSeconds&clip=evidenceId (the
// &clip deep-link the VOD player consumes to highlight that exact clip). The same
// triage controls as the old evidence rows ride along (Good/Bad/attach/dismiss) so
// triage still works now that clips live under prompts + the sub-block + To-sort.
function clipCard(clip, objectiveOptions) {
  const el = tpl('tpl-clip');
  const id = Number(clip.evidenceId);
  el.dataset.evidId = String(Number.isFinite(id) ? id : 0);

  // Jump-to-VOD deep-link. Only when there's a real start second (null = the
  // row genuinely has no time; 0 IS a real time — a clip covering the game
  // start must stay clickable). The delegated view_moment handler reads
  // data-seek + (P-027) data-clip to build the &clip= URL and ignores clicks
  // that land on the inner triage controls.
  const seek = clip.startSeconds == null ? NaN : Number(clip.startSeconds);
  if (Number.isFinite(seek) && seek >= 0) {
    el.dataset.action = 'view_moment';
    el.dataset.seek = String(Math.floor(seek));
    if (Number.isFinite(id) && id > 0) el.dataset.clip = String(id);
    el.classList.add('rv-evid-clickable');
    el.setAttribute('role', 'button');
    el.tabIndex = 0;
  }

  const dot = el.querySelector('.rv-evid-dot');
  if (clip.polarityColorHex) dot.style.background = clip.polarityColorHex;

  const timeEl = el.querySelector('.rv-evid-time');
  if (clip.timeText) { timeEl.textContent = clip.timeText; show(timeEl, true); }

  // The note is the clip's headline here (clips have no separate title); fall back
  // to a neutral placeholder so an untitled moment still reads as a row.
  const noteEl = el.querySelector('.rv-clip-note');
  noteEl.textContent = String(clip.note || '').trim() || '(clip)';

  // Public share link (revu.lol/<id>) when the bookmark was uploaded; hidden until
  // shared. A real href so it opens externally; click is stopped from the card jump.
  const shareEl = el.querySelector('.rv-clip-share');
  const share = String(clip.shareUrl || '').trim();
  if (shareEl) {
    if (share) {
      shareEl.textContent = 'Share link';
      shareEl.href = share;
      // Don't let the share-link click bubble into the card's VOD jump.
      shareEl.addEventListener('click', (e) => e.stopPropagation());
      show(shareEl, true);
    } else {
      shareEl.remove();
    }
  }

  // Triage controls — same as the old evidence rows. Reflect current polarity.
  show(el.querySelector('.rv-evid-actions'), true);
  const goodBtn = el.querySelector('.rv-evid-good');
  const badBtn = el.querySelector('.rv-evid-bad');
  if (clip.polarity === 'good' && goodBtn) goodBtn.classList.add('on');
  if (clip.polarity === 'bad' && badBtn) badBtn.classList.add('on');

  // Objective picker: "(no objective)" + each active objective. The clip dto does
  // not carry its current objectiveId, so we don't pre-select — the picker is for
  // (re)attaching, and the clip already renders under its prompt/objective group.
  const pick = el.querySelector('.rv-evid-pick');
  if (pick) {
    const none = document.createElement('option');
    none.value = '';
    none.textContent = 'Attach to objective…';
    pick.appendChild(none);
    for (const o of (objectiveOptions || [])) {
      const opt = document.createElement('option');
      opt.value = String(o.id);
      opt.textContent = o.title || `Objective ${o.id}`;
      pick.appendChild(opt);
    }
  }
  return el;
}

// Populate a `.rv-clips` host with clip cards; toggle the host (and its enclosing
// wrapper, if given) on whether there are any. Returns the count rendered.
function renderClipList(host, clips, objectiveOptions, wrapper) {
  if (!host) return 0;
  clear(host);
  const list = Array.isArray(clips) ? clips : [];
  for (const c of list) host.appendChild(clipCard(c, objectiveOptions));
  show(host, list.length > 0);
  if (wrapper) show(wrapper, list.length > 0);
  return list.length;
}

// ── render: TO-SORT strip (P-027 homing) ─────────────────────────────────────
// Fully-untagged auto-moments for this game (no objective AND no prompt). The
// prompt clips + per-objective no-prompt clips render inline under the objectives;
// these leftovers home into a single top-level strip so nothing is unreachable now
// that the old two-list evidence UI is gone.
function renderUnsorted(subject) {
  const clips = Array.isArray(subject.unsortedClips) ? subject.unsortedClips : [];
  const objs = Array.isArray(subject.objectives) ? subject.objectives : [];
  const objectiveOptions = objs.map((o) => ({ id: Number(o.id), title: o.title }));
  const n = renderClipList($('rv-tosort'), clips, objectiveOptions);
  const count = $('rv-unsorted-count'); if (count) count.textContent = `${n} ${n === 1 ? 'clip' : 'clips'}`;
  show($('rv-tosortsec'), n > 0);
}

// Hide clip-list hosts (and their labelled wrappers) that just emptied out —
// called after an in-place dismiss removes a row.
function pruneEmptyClipSections() {
  for (const host of document.querySelectorAll('.rv-prompt-clips, .rv-obj-unprompted-list')) {
    if (host.childElementCount === 0) {
      show(host, false);
      const wrap = host.closest('.rv-obj-unprompted');
      if (wrap) show(wrap, false);
    }
  }
  const tosort = $('rv-tosort');
  const count = $('rv-unsorted-count');
  if (count && tosort) count.textContent = `${tosort.childElementCount} ${tosort.childElementCount === 1 ? 'clip' : 'clips'}`;
  if (tosort && tosort.childElementCount === 0) show($('rv-tosortsec'), false);
}

// Handle an evidence triage control (Good/Bad/Dismiss button or objective <select>).
async function onEvidenceAction(action, el) {
  const row = el.closest('.rv-evid');
  if (!row) return;
  const evidenceId = Number(row.dataset.evidId);
  if (!(evidenceId > 0)) return;
  const gameId = Number(_subject && _subject.gameId) || null;

  if (action === 'good' || action === 'bad') {
    // Reflect the polarity choice on the row's buttons IN PLACE — no re-render
    // (a full loadReview() would wipe unsaved debrief/tag text). Toggle off if the
    // same polarity was re-tapped.
    const goodBtn = row.querySelector('.rv-evid-good');
    const badBtn = row.querySelector('.rv-evid-bad');
    const btn = action === 'good' ? goodBtn : badBtn;
    const other = action === 'good' ? badBtn : goodBtn;
    const turningOff = btn && btn.classList.contains('on');
    if (other) other.classList.remove('on');
    if (btn) btn.classList.toggle('on', !turningOff);
    await postWrite('set_evidence_polarity', { evidenceId, polarity: turningOff ? '' : action });
  } else if (action === 'dismiss') {
    // Remove the row in place; no re-render needed — but re-check the section
    // wrappers so dismissing the LAST clip doesn't leave an empty "To sort" /
    // "Objective evidence" header floating until the next full load.
    row.remove();
    pruneEmptyClipSections();
    await postWrite('set_evidence_status', { evidenceId, status: 'dismissed' });
  } else if (action === 'objective') {
    // The <select> already shows the chosen value; just persist it.
    const raw = el.value;
    const objectiveId = raw ? Number(raw) : null;
    await postWrite('set_evidence_objective', { evidenceId, objectiveId, gameId });
    // Objective attach moves the row between lists, but re-rendering here would wipe
    // unsaved form text. The attach is persisted; the next natural load reorders.
  }
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

  // Concept tags come from TWO UIs:
  //  • the catalog grid — predefined tags toggled on, collected by tag id
  //  • the free-text input — custom tags typed as chips in #rv-tags, collected by
  //    NAME (the backend find-or-creates each, so typed tags actually save).
  const selectedTagIds = [];
  for (const chip of document.querySelectorAll('#rv-tagcat-grid .rv-tagcat-chip.on')) {
    const id = Number(chip.dataset.tagId);
    if (Number.isFinite(id) && id > 0) selectedTagIds.push(id);
  }
  const freeTextTags = [];
  for (const txt of document.querySelectorAll('#rv-tags .rv-tag-txt')) {
    const name = (txt.textContent || '').trim();
    if (name) freeTextTags.push(name);
  }

  // Focus adherence — the currently-lit Focus Check button (2/1/0), or null if none.
  // MUST be included: save_review writes focus_adherence from this, so omitting it
  // would clear the value the user set by clicking the button before saving.
  const litFocus = document.querySelector('#rv-focus .rv-focus-btn.on');
  const focusAdherence = litFocus ? Number(litFocus.dataset.focus) : null;

  // P-027 / 17-16: these four prose boxes were removed from the form (specifics
  // live in the clips now). They're no longer RENDERED, but the save path does a
  // blind UPDATE of these columns — so we must NOT send '' (that would silently
  // blank any value an older review/WinUI build wrote). Round-trip the existing
  // saved value off the loaded snapshot so a re-save leaves them untouched. Still
  // clearable in any future build that re-renders them.
  const savedForm = _subject.form || {};

  const mentalSlider = $('rv-mental-input');
  return {
    gameId: Number(_subject.gameId),
    championName: h.championName || '',
    win: !!h.win,
    // 0 = "never answered" — the backend preserves the stored value (or the
    // neutral default) instead of recording an untouched slider as a real 5.
    mentalRating: mentalSlider.dataset.touched ? (Number(mentalSlider.value) || 0) : 0,
    wentWell: typeof savedForm.wentWell === 'string' ? savedForm.wentWell : '',
    mistakes: typeof savedForm.mistakes === 'string' ? savedForm.mistakes : '',
    focusNext: typeof savedForm.focusNext === 'string' ? savedForm.focusNext : '',
    spottedProblems: typeof savedForm.spottedProblems === 'string' ? savedForm.spottedProblems : '',
    attribution: fields.attribution || '',
    reviewNotes: fields.reviewNotes || '',
    // R-001 reappraisal pair (round-trips to games.outside_control/within_control).
    outsideControl: fields.outsideControl || '',
    withinControl: fields.withinControl || '',
    selectedTagIds,
    freeTextTags,
    objectivePractices,
    focusAdherence,
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
  matchNav.setGame(null);
  // The form isn't rendered in the empty state, so a queued commit message would
  // otherwise leak onto a later render — drop it here.
  _pendingCommitMsg = null;
  show($('rv-hero'), false);
  show($('rv-body'), false);
  show($('rv-empty'), true);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = 'Nothing to review right now.';
  show($('statusline'), true);
}

function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
  if (!_subject) {
    const status = document.querySelector('#statusline b');
    if (status) status.textContent = 'Could not load this review.';
    show($('statusline'), true);
  }
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
// Only on the FIRST render of a page load (not on every refresh).
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  // Follow the launchpad's reading order: match → watch → takeaway → save.
  const order = [
    $('rv-hero'),
    $('rv-launchpad'),
    $('rv-form'),
    $('rv-commitbar'),
  ].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  _snapshot = d || null;
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
  renderUnsorted(subject);
  renderForm(subject);
  renderTagCatalog(subject);
  renderMatchupHistory(subject);
  renderNextGame(d);
  matchNav.setGame(subject.gameId);
  playEntrance();
}

// ── render: "Next game →" chaining ──────────────────────────────────────────
// The snapshot carries the newest OTHER unreviewed game + the remaining count,
// so a session of several reviews chains directly instead of bouncing through
// the Games list after every save. The commit bar links to review.html?gameId=<next>.
function renderNextGame(d) {
  const nextId = Number(d && d.nextUnreviewedGameId) || 0;
  const remaining = Number(d && d.unreviewedRemaining) || 0;
  const suffix = remaining > 1 ? ` (${remaining} left)` : '';
  const nextBtn = $('rv-nextbtn');
  if (nextBtn) { nextBtn.textContent = `Next game${suffix} →`; show(nextBtn, nextId > 0); }
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function loadReview() {
  if (_loading) return;
  _loading = true;
  if (!_subject) {
    const status = document.querySelector('#statusline b');
    if (status) status.textContent = 'Loading review…';
    show($('statusline'), true);
  }
  try {
    const data = await fetchReview();
    render(data);
    if (!_restoreAttempted && _subject) {
      _restoreAttempted = true;
      if (new URLSearchParams(window.location.search).get('resume') === '1') await matchNav.restoreState();
    }
  } catch (err) {
    renderError(err);
    // Surface to console for diagnosis without leaking into the DOM markup.
    console.error('[review] load failed:', err);
  } finally {
    _loading = false;
  }
}

// The link event confirms availability without refetching or touching the form.
function updateRecordingLaunch(gameId) {
  const button = $('rv-open-vod');
  if (!button) return;
  const linked = Number(gameId) === _linkedRecordingGameId && _linkedRecordingGameId > 0;
  button.dataset.recordingLinked = String(linked);
  button.title = linked ? 'A recording was just linked to this match.' : '';
  if (button.firstChild?.nodeType === 3) button.firstChild.textContent = linked ? 'Review linked recording ' : 'Review in VOD ';
}

window.addEventListener('revu:vod-linked', event => {
  const gameId = Number(event.detail?.gameId);
  const shown = Number(_subject?.gameId || new URLSearchParams(window.location.search).get('gameId'));
  if (!Number.isSafeInteger(gameId) || gameId <= 0 || gameId !== shown) return;
  _linkedRecordingGameId = gameId;
  updateRecordingLaunch(gameId);
});

// Post-game matchup enrichment refreshes the header, never the mounted form.
window.addEventListener('revu:matchup-updated', async (ev) => {
  const gid = Number(ev && ev.detail && ev.detail.gameId);
  const shown = Number(_subject && _subject.gameId) || 0;
  if (!shown || (gid > 0 && gid !== shown)) return;
  try {
    const data = await fetchReview();
    if (data && data.subject) renderHeader(data.subject);
  } catch (err) {
    console.error('[review] header refresh after matchup update failed:', err);
  }
});

// Timeline corrections from the VOD player refresh death links and the header
// count without rebuilding the user's debrief fields.
window.addEventListener('revu:events-corrected', async (ev) => {
  const gid = Number(ev && ev.detail && ev.detail.gameId);
  const shown = Number(_subject && _subject.gameId) || 0;
  if (!shown || (gid > 0 && gid !== shown)) return;
  try {
    const data = await fetchReview();
    if (Number(data?.subject?.gameId) === shown && Number(_subject?.gameId) === shown) {
      renderHeader(data.subject);
      renderDeaths(data.subject);
    }
  } catch (err) {
    console.error('[review] refresh after events corrected failed:', err);
  }
});

// ── live form interactions: mental slider + tag input ───────────────────────
// Hidden prompt textareas have no measurable height during initial rendering.
// Recalculate after an explicit open so existing multi-line answers stay readable.
const goalDetails = $('rv-objsec');
goalDetails.addEventListener('toggle', () => {
  if (!goalDetails.open) return;
  requestAnimationFrame(() => {
    for (const input of goalDetails.querySelectorAll('.rv-prompt-input')) autoSize(input);
  });
});
goalDetails.addEventListener('invalid', () => { goalDetails.open = true; }, true);

// Mental slider mirrors its value into the readout as it moves (and counts as
// a real answer from the first move). Any edit to a debrief textarea, objective
// note, or the slider marks the draft dirty for the debounced autosave.
document.addEventListener('input', (ev) => {
  const t = ev.target;
  if (!t) return;
  if (t.id === 'rv-mental-input') {
    $('rv-mental').textContent = t.value;
    t.dataset.touched = '1';
    markDraftDirty();
    return;
  }
  if (t.classList && (t.classList.contains('rv-field-in') || t.classList.contains('rv-objnote') || t.id === 'rv-tag-input')) {
    if (t.classList.contains('rv-field-in')) updateReflectionCount();
    markDraftDirty();
  }
});

// Practiced toggles ride the batched save — autosave them too.
document.addEventListener('change', (ev) => {
  if (ev.target && ev.target.classList && ev.target.classList.contains('rv-practiced-cb')) {
    markDraftDirty();
  }
});

// Ctrl+Enter (or Cmd+Enter) commits the review — the highest-frequency action
// on the page was mouse-only.
document.addEventListener('keydown', (ev) => {
  if ((ev.ctrlKey || ev.metaKey) && ev.key === 'Enter') {
    const saveBtn = $('rv-savebtn');
    if (saveBtn && !saveBtn.disabled && _subject) {
      ev.preventDefault();
      // Blur the focused field first — a prompt-answer box persists via its
      // blur handler only, and a mouse click on Save would have blurred it;
      // the keyboard path must not skip that write.
      if (document.activeElement && typeof document.activeElement.blur === 'function') {
        document.activeElement.blur();
      }
      saveBtn.click();
    }
  }
});

// Best-effort flush when the page is being torn down (navigation, app close).
window.addEventListener('pagehide', () => { flushDraft(); });

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
//   • an evidence triage button ([data-evid-action] button) → onEvidenceAction
//   • a focus-check button (.rv-focus-btn)     → onFocusClick
//   • a concept-tag catalog chip (.rv-tagcat-chip) → toggle selection (local)
// These coexist with the [data-action] handler below (save/skip/vod).
document.addEventListener('click', (ev) => {
  const evidBtn = ev.target.closest('button[data-evid-action]');
  if (evidBtn) { ev.preventDefault(); onEvidenceAction(evidBtn.dataset.evidAction, evidBtn); return; }

  const focusBtn = ev.target.closest('.rv-focus-btn');
  if (focusBtn) { ev.preventDefault(); onFocusClick(focusBtn); return; }

  const tagChip = ev.target.closest('.rv-tagcat-chip');
  if (tagChip && tagChip.closest('#rv-tagcat-grid')) {
    ev.preventDefault();
    applyTagcatSelected(tagChip, !tagChip.classList.contains('on'));
    markDraftDirty();
    return;
  }
});

// Keyboard activation (Enter/Space) for the role=button tag chips and
// the clickable evidence cards.
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const t = ev.target;
  if (t && t.classList && (t.classList.contains('rv-tagcat-chip') || t.classList.contains('rv-evid-clickable'))) {
    ev.preventDefault();
    t.click();
  }
});

// HARD GUARD: never let a form submit navigate/reload the page. The review form
// holds a tag <input> and a range <input>; pressing Enter in either would otherwise
// implicitly submit the form → the WebView reloads review.html, dropping the
// ?gameId and ALL unsaved state (the "page refreshes before save" bug). Saving is
// always done explicitly via the SAVE REVIEW button / per-control writes, so a
// native submit is never wanted here.
document.addEventListener('submit', (ev) => {
  ev.preventDefault();
}, true);

// Objective attach picker (a <select>) fires on change, not click.
document.addEventListener('change', (ev) => {
  const pick = ev.target.closest('select[data-evid-action="objective"]');
  if (pick) onEvidenceAction('objective', pick);
});

// Serialize blur saves per answer, keeping the confirmed value separate from
// its raw draft. Failed saves remain retryable after navigation and restoration.
const _promptWrites = new Set();
const _promptWriteTails = new WeakMap();
function savePromptAnswer(input) {
  const promptId = Number(input.dataset.promptId);
  const gameId = Number(_subject && _subject.gameId);
  if (!(promptId > 0) || !(gameId > 0)) return Promise.resolve();
  const text = input.value;
  const write = (_promptWriteTails.get(input) || Promise.resolve()).catch(() => {}).then(async () => {
    if (text === (input.dataset.savedValue ?? '')) return;
    if (await postWrite('save_prompt_answer', { promptId, gameId, text })) input.dataset.savedValue = text;
  });
  _promptWrites.add(write);
  _promptWriteTails.set(input, write);
  write.then(() => _promptWrites.delete(write), () => _promptWrites.delete(write));
  return write;
}
document.addEventListener('blur', (ev) => {
  const input = ev.target;
  if (!input?.classList?.contains('rv-prompt-input')) return;
  savePromptAnswer(input).catch(err => console.warn('[review] prompt answer save failed:', err));
}, true);

// Grow prompt-answer boxes as the user types.
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.classList && ev.target.classList.contains('rv-prompt-input')) {
    matchNav.enableCapture();
    autoSize(ev.target);
  }
});

// ── single delegated action handler ─────────────────────────────────────────
// review_vod  = primary launch action (resume this match's VOD workspace).
// save_review = gather the editable form and COMMIT (no un-review), then refetch.
// skip_review = mark the game reviewed without notes, then refetch.
const ACTIONS = new Set(['review_vod', 'view_moment', 'save_review', 'skip_review', 'delete_review', 'copy_review', 'export_review', 'next_unreviewed']);

document.addEventListener('click', async (ev) => {
  // An evidence card jump must NOT fire when the click landed on its inner triage
  // controls (Good/Bad/Dismiss/objective picker) — those have their own handler.
  if (ev.target.closest('.rv-evid-actions')) return;

  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // The shared controller adds resume=1 for this match and waits for draft
  // writes. Omitting t/clip retains the user's saved VOD position and tools.
  if (action === 'review_vod') {
    const gid = (_subject && _subject.gameId) || target.dataset.gameId;
    if (gid) {
      await matchNav.navigate(`vodplayer.html?gameId=${encodeURIComponent(gid)}`);
    }
    return;
  }

  // "Next game →" chains to the newest other unreviewed game (queue info rides
  // the snapshot). Unsaved edits flush as a draft first.
  if (action === 'next_unreviewed') {
    const nextId = Number(_snapshot && _snapshot.nextUnreviewedGameId) || 0;
    if (nextId > 0) {
      await matchNav.navigate(`review.html?gameId=${encodeURIComponent(nextId)}`);
    }
    return;
  }

  // Clicking a moment/evidence/clip card jumps to that game's VOD at the moment's
  // start time (vodplayer reads ?t=seconds; 0 is a valid time — the handler keys
  // on data-seek being present, not truthy). gameId is the loaded subject's.
  // P-027: a clip card also stamps data-clip with its evidenceId, so we append
  // &clip=ID — the deep-link the VOD player consumes to highlight that clip.
  if (action === 'view_moment') {
    const gid = (_subject && _subject.gameId) || target.dataset.gameId;
    const t = target.dataset.seek != null ? Number(target.dataset.seek) : NaN;
    if (gid && Number.isFinite(t) && t >= 0) {
      let url =
        `vodplayer.html?gameId=${encodeURIComponent(gid)}&t=${encodeURIComponent(t)}`;
      const clipId = Number(target.dataset.clip);
      if (Number.isFinite(clipId) && clipId > 0) {
        url += `&clip=${encodeURIComponent(clipId)}`;
      }
      await matchNav.navigate(url);
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
      await flushDraft();
      cancelDraft();
      _suppressDraft = true;
      // delete_review takes a single {payload} arg in the bridge (the {payload} convention).
      await invoke('delete_review', { payload: { gameId: gid } });
      matchNav.invalidate();
      // Back to Games — the game is now unreviewed and back in the queue.
      window.location.href = 'games.html';
    } catch (err) {
      _suppressDraft = false;
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

  // Skip is an irreversible "reviewed with no notes" one click away from SAVE —
  // require a second tap to confirm instead of a modal (keeps the flow fast,
  // kills the misclick cost).
  if (action === 'skip_review') {
    const skipBtn = $('rv-skipbtn');
    if (skipBtn && !skipBtn.dataset.confirm) {
      skipBtn.dataset.confirm = '1';
      skipBtn.classList.add('rv-skip-confirm');
      skipBtn.textContent = 'Skip with no notes?';
      setTimeout(() => {
        delete skipBtn.dataset.confirm;
        skipBtn.classList.remove('rv-skip-confirm');
        skipBtn.textContent = 'Skip review';
      }, 4000);
      return;
    }
    if (skipBtn) {
      delete skipBtn.dataset.confirm;
      skipBtn.classList.remove('rv-skip-confirm');
      skipBtn.textContent = 'Skip review';
    }
  }

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
    console.info(`[review] (preview) action "${action}" — no Electron backend.`, args);
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
  // The commit deletes the server-side draft — cancel any pending autosave so
  // it can't fire mid-save and resurrect the draft.
  try {
    if (action === 'save_review' || action === 'skip_review') {
      await flushDraft();
      cancelDraft();
      _suppressDraft = true;
      // Include edits made while an earlier autosave was finishing.
      if (action === 'save_review') args = gatherForm();
    }
    // save_review / skip_review take a single `payload` arg in the bridge — wrap the
    // gathered form/body so Electron doesn't reject with "missing required key payload".
    await invoke(action, { payload: args });
    if (action === 'save_review' || action === 'skip_review') matchNav.invalidate();
    // RE-FETCH so the UI reflects the committed state (next subject / reviewed
    // mark). Queue the confirmation so it SURVIVES the renderForm() the reload
    // runs — renderForm resets the commit line on every render, so a plain
    // showCommit() would flash and die the same tick (brief 2026-06-17-17).
    if (action === 'save_review') _pendingCommitMsg = { text: 'Review saved.', kind: 'ok' };
    if (action === 'skip_review') _pendingCommitMsg = { text: 'Game skipped.', kind: 'ok' };
    if (action === 'save_review' || action === 'skip_review') {
      await loadReview();
    }
    if (action === 'save_review') {
      window.dispatchEvent(new CustomEvent('revu:first-review-review-saved', {
        detail: { gameId: args.gameId },
      }));
    }
  } catch (err) {
    renderError(err);
    showCommit((err && err.message) ? err.message : 'Save failed.', 'err');
    console.error(`[review] action "${action}" failed:`, err);
  } finally {
    _suppressDraft = false;
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

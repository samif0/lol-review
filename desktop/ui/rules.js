// Revu desktop — Rules (session protocols) page renderer for the glass-aurora
// layout. Renders the JSON returned by the Tauri command `get_rules`
// (see Revu.Sidecar GET /api/rules) AND drives the full rule CRUD via the
// create_rule / update_rule / toggle_rule / delete_rule commands. Mirrors
// objectives.js conventions:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-rules.json so the page previews in a
//     plain browser (writes no-op with a console note).
//   • Every server string is written via textContent (never innerHTML) so the
//     surface stays XSS-free; colors arrive as *Hex strings applied to style
//     properties only.
//   • ONE delegated [data-action] click handler.
//   • After every write we refetch get_rules (no message bus — manual invalidation).

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

const isTauri = () => typeof window.__TAURI__ !== 'undefined';

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}
function setVal(id, v) { const el = $(id); if (el) el.value = v == null ? '' : String(v); }
function getVal(id) { const el = $(id); return el ? el.value.trim() : ''; }

// ── per-type form metadata ────────────────────────────────────────────────────
// Mirrors RulesViewModel.UpdateConditionField: which condition field shows, its
// label + placeholder, and whether the loss_streak cooldown field appears.
const TYPE_FIELDS = {
  custom: { showCond: false },
  no_play_day: { showCond: true, label: 'Days (comma-separated)', ph: 'e.g., Monday, Sunday' },
  no_play_after: { showCond: true, label: 'Hour (0-23)', ph: 'e.g., 23 for 11 PM' },
  loss_streak: { showCond: true, label: 'Max consecutive losses', ph: 'e.g., 3', cooldown: true },
  max_games: { showCond: true, label: 'Max games per day', ph: 'e.g., 5' },
  min_mental: { showCond: true, label: 'Minimum mental rating', ph: 'e.g., 4' },
};

// ── condition-text formatting (mirror RuleDisplayItem.ConditionText) ──────────
// Used for the live IF-leg preview so it matches what the saved card will show.
function parseLossStreak(raw) {
  // Mirror RulesRepository.ParseLossStreakCondition: "X" or "X:Y" (Y = minutes).
  if (!raw) return { threshold: 0, cooldown: null };
  const parts = String(raw).split(':');
  const t = parseInt((parts[0] || '').trim(), 10);
  if (!Number.isFinite(t) || t <= 0) return { threshold: 0, cooldown: null };
  if (parts.length < 2 || !parts[1] || !parts[1].trim()) return { threshold: t, cooldown: null };
  const cd = parseInt(parts[1].trim(), 10);
  return { threshold: t, cooldown: Number.isFinite(cd) && cd > 0 ? cd : null };
}
function formatHour(value) {
  const h = parseInt(value, 10);
  if (!Number.isFinite(h)) return '';
  const suffix = h < 12 ? 'AM' : 'PM';
  let displayH = h <= 12 ? h : h - 12;
  if (displayH === 0) displayH = 12;
  return `No play after ${displayH}:00 ${suffix}`;
}
function formatLossStreak(value) {
  const { threshold, cooldown } = parseLossStreak(value);
  if (threshold <= 0) return '';
  if (!cooldown || cooldown <= 0) return `Stop after ${threshold} consecutive losses`;
  const m = cooldown;
  const window = m >= 60 ? `${Math.floor(m / 60)}h${m % 60 > 0 ? ` ${m % 60}m` : ''}` : `${m}m`;
  return `Stop after ${threshold} losses (unlock after ${window})`;
}
function conditionText(ruleType, conditionValue) {
  const v = (conditionValue || '').trim();
  if (!v) return '';
  switch (ruleType) {
    case 'no_play_day': return `Days: ${v}`;
    case 'no_play_after': return formatHour(v);
    case 'loss_streak': return formatLossStreak(v);
    case 'max_games': return `Max ${v} games per day`;
    case 'min_mental': return `Don't queue below mental ${v}`;
    default: return '';
  }
}

// ── suggested starter rules (mirror RulesViewModel.SuggestedRules) ────────────
// AddSuggestedRuleAsync calls CreateAsync(name, description, ruleType,
// conditionValue) — NO replacement plan (defaults '').
const SUGGESTED = [
  {
    name: 'Stop after 2 losses',
    description: 'Tilt compounds quickly; two losses in a row is a good signal to take a break.',
    ruleType: 'loss_streak',
    conditionValue: '2',
    badgeText: 'LOSS STREAK',
    conditionText: 'Stop after 2 consecutive losses',
  },
  {
    name: 'Max 5 games per day',
    description: 'Marathon sessions rarely improve your play. Keep it focused.',
    ruleType: 'max_games',
    conditionValue: '5',
    badgeText: 'MAX GAMES/DAY',
    conditionText: 'Max 5 games per day',
  },
  {
    name: 'No ranked after midnight',
    description: 'Late-night games hurt decision-making and sleep quality.',
    ruleType: 'no_play_after',
    conditionValue: '0',
    badgeText: 'NO PLAY AFTER',
    conditionText: 'No play after 12:00 AM',
  },
  {
    name: "Don't queue below mental 4",
    description: 'Playing on tilt is the fastest way to lose LP and reinforce bad habits.',
    ruleType: 'min_mental',
    conditionValue: '4',
    badgeText: 'MINIMUM MENTAL',
    conditionText: "Don't queue below mental 4",
  },
];

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchRules() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_rules');
  }
  const res = await fetch('./sample-rules.json');
  if (!res.ok) throw new Error(`sample-rules.json ${res.status}`);
  return res.json();
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const active = d.activeCount ?? (Array.isArray(d.activeRules) ? d.activeRules.length : 0);
  const inactive = d.inactiveCount ?? (Array.isArray(d.inactiveRules) ? d.inactiveRules.length : 0);
  const total = d.totalCount ?? (active + inactive);

  const parts = [];
  parts.push(total === 0 ? 'No rules yet' : `${total} rule${total === 1 ? '' : 's'}`);
  if (total > 0) parts.push(`${active} active`);
  if (inactive > 0) parts.push(`${inactive} disabled`);

  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: one rule card ────────────────────────────────────────────────────
// Type badge + name + status pill on top; optional description, condition line,
// the green left-border "YOUR PLAN INSTEAD" box (P2c replacement plan), the P2b
// evidence line, and the action row (Edit / Enable-or-Disable / Delete).
function buildRule(r, opts) {
  const off = !!(opts && opts.off);
  const el = tpl('tpl-rule');
  if (off) el.classList.add('off');
  if (r.id != null) el.dataset.ruleId = String(r.id);

  const badge = el.querySelector('.rule-badge');
  const name = el.querySelector('.rule-name');
  const status = el.querySelector('.rule-status');
  const vreason = el.querySelector('.rule-vreason');
  const desc = el.querySelector('.rule-desc');
  const cond = el.querySelector('.rule-cond');
  const plan = el.querySelector('.rule-plan');
  const planT = el.querySelector('.rule-plan-t');
  const evid = el.querySelector('.rule-evid');
  const toggleBtn = el.querySelector('.rule-act-toggle');

  // Type badge — text from the server; accent rim tinted by the per-rule hex.
  badge.textContent = r.typeBadge || (r.ruleType ? r.ruleType.toUpperCase() : 'RULE');
  if (r.accentHex) {
    badge.style.color = r.accentHex;
    badge.style.borderColor = r.accentHex;
  }

  name.textContent = r.name || '';

  // Status pill — live RULE CHECK state. TRIPPED (gold) when violated, OK (green)
  // when checked and clean. Custom/unchecked rules show neither. Disabled rules
  // read as dimmed and suppress the pill entirely. Mirrors RuleDisplayItem.
  status.classList.remove('ok', 'tripped');
  if (off) {
    show(status, false);
  } else if (r.isViolated) {
    status.textContent = 'TRIPPED';
    status.classList.add('tripped');
    show(status, true);
  } else if (r.isOk) {
    status.textContent = 'OK';
    status.classList.add('ok');
    show(status, true);
  } else {
    show(status, false);
  }

  // Live violation reason — only on tripped rules.
  if (!off && r.hasViolationReason && r.violationReason) {
    vreason.textContent = r.violationReason;
    show(vreason, true);
  } else {
    show(vreason, false);
  }

  // Description — only when present.
  if (r.hasDescription && r.description) {
    desc.textContent = r.description;
    show(desc, true);
  } else {
    show(desc, false);
  }

  // Condition line — mono, dim; only when the rule carries one (custom rules don't).
  if (r.hasCondition && r.conditionText) {
    cond.textContent = r.conditionText;
    show(cond, true);
  } else {
    show(cond, false);
  }

  // Replacement plan — the green-edged "YOUR PLAN INSTEAD" box (P2c).
  if (r.hasReplacementPlan && r.replacementPlan) {
    planT.textContent = r.replacementPlan;
    show(plan, true);
  } else {
    show(plan, false);
  }

  // P2b behavioral record — mono teal line; empty for custom rules / failures.
  if (r.hasEvidenceLine && r.evidenceLine) {
    evid.textContent = r.evidenceLine;
    show(evid, true);
  } else {
    show(evid, false);
  }

  // Toggle button label + intent: disabled rules read "Enable" (green), active
  // rules read "Disable" (calm ghost). Mirrors RuleDisplayItem.ToggleText.
  if (toggleBtn) {
    if (r.enabled === false) {
      toggleBtn.textContent = 'Enable';
      toggleBtn.classList.add('rule-act-win');
    } else {
      toggleBtn.textContent = 'Disable';
      toggleBtn.classList.remove('rule-act-win');
    }
  }

  return el;
}

// ── render: the live RULE CHECK banner ───────────────────────────────────────
function renderViolations(d) {
  const banner = $('rule-check');
  const list = $('rule-check-list');
  clear(list);
  const items = Array.isArray(d.violations) ? d.violations : [];
  const has = (d.hasViolations ?? items.length > 0) && items.length > 0;
  if (!has) { show(banner, false); return; }

  for (const v of items) {
    if (v.hasPlan) {
      const el = tpl('tpl-vbanner-plan');
      el.querySelector('.rule-check-cue').textContent = v.conditionCue || '';
      el.querySelector('.rule-check-plan').textContent = v.replacementPlan || '';
      el.querySelector('.rule-check-by-name').textContent = v.ruleName || '';
      list.appendChild(el);
    } else {
      const el = tpl('tpl-vbanner');
      el.querySelector('.rule-check-name').textContent = v.ruleName || '';
      el.querySelector('.rule-check-reason').textContent = v.reason || '';
      list.appendChild(el);
    }
  }
  show(banner, true);
}

// ── render: a list section (label + count + cards) ──────────────────────────
function renderSection(rules, hostId, labelId, countId, opts) {
  const host = $(hostId);
  clear(host);
  const items = Array.isArray(rules) ? rules : [];

  if (items.length === 0) {
    show($(labelId), false);
    return;
  }
  show($(labelId), true);
  const cnt = $(countId);
  if (cnt) cnt.textContent = String(items.length);

  for (const r of items) host.appendChild(buildRule(r, opts));
}

// ── render: the 4 starter-rule preset chips (empty state) ────────────────────
function renderSuggested() {
  const host = $('rules-suggest');
  if (!host) return;
  clear(host);
  SUGGESTED.forEach((s, i) => {
    const el = tpl('tpl-suggest');
    el.dataset.suggestIdx = String(i);
    el.querySelector('.rules-suggest-badge').textContent = s.badgeText;
    el.querySelector('.rules-suggest-name').textContent = s.name;
    el.querySelector('.rules-suggest-desc').textContent = s.description;
    el.querySelector('.rules-suggest-cond').textContent = s.conditionText;
    host.appendChild(el);
  });
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('rule-check'),
    $('active-list'),
    $('inactive-list'),
    $('rules-empty'),
  ].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── create / edit form ────────────────────────────────────────────────────────
// One <form> serves both create_rule (no id) and update_rule (id set). _editId
// holds the id under edit, or null when creating.
let _editId = null;

function clearFormError() { show($('form-err'), false); $('form-err').textContent = ''; }
function setFormError(msg) { const e = $('form-err'); e.textContent = msg; show(e, true); }

// Reveal/hide the condition + cooldown fields and swap label/placeholder per type.
function syncTypeFields() {
  const type = $('f-type').value;
  const meta = TYPE_FIELDS[type] || TYPE_FIELDS.custom;
  show($('f-cond-wrap'), !!meta.showCond);
  if (meta.showCond) {
    $('f-cond-label').textContent = meta.label || 'Condition';
    $('f-cond').placeholder = meta.ph || '';
  }
  show($('f-cooldown-wrap'), !!meta.cooldown);
  syncIfPreview();
}

// Live IF-leg preview — built from the current type + condition, exactly the
// sentence the saved card's condition line will show. Hidden until non-empty.
function syncIfPreview() {
  const type = $('f-type').value;
  const cue = conditionText(type, getVal('f-cond'));
  const wrap = $('f-if-preview');
  if (cue) {
    $('f-if-cue').textContent = cue;
    show(wrap, true);
  } else {
    show(wrap, false);
  }
}

// Open the form in create mode: blank fields, "Create" label.
function openCreateForm() {
  _editId = null;
  $('form-title').textContent = 'New Rule';
  $('form-submit').textContent = 'Create';
  setVal('f-name', ''); setVal('f-cond', ''); setVal('f-cooldown', '');
  setVal('f-desc', ''); setVal('f-plan', '');
  $('f-type').value = 'custom';
  clearFormError();
  syncTypeFields();
  show($('rule-form'), true);
  $('rule-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('f-name').focus();
}

// Open the form in edit mode for a rule pulled from the last fetch.
function openEditForm(id) {
  const r = ruleById(id);
  if (!r) { openCreateForm(); return; }
  _editId = r.id;
  $('form-title').textContent = 'Edit Rule';
  $('form-submit').textContent = 'Save';
  setVal('f-name', r.name || '');
  setVal('f-desc', r.description || '');
  setVal('f-plan', r.replacementPlan || '');
  $('f-type').value = r.ruleType || 'custom';

  // loss_streak stores "threshold[:cooldown]" — split back into the two inputs.
  if (r.ruleType === 'loss_streak') {
    const { threshold, cooldown } = parseLossStreak(r.conditionValue);
    setVal('f-cond', threshold > 0 ? String(threshold) : '');
    setVal('f-cooldown', cooldown ? String(cooldown) : '');
  } else {
    setVal('f-cond', r.conditionValue || '');
    setVal('f-cooldown', '');
  }

  clearFormError();
  syncTypeFields();
  show($('rule-form'), true);
  $('rule-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('f-name').focus();
}

function closeForm() {
  _editId = null;
  clearFormError();
  show($('rule-form'), false);
}

// Assemble the create/update payload from the form. Returns null (+ inline error)
// when the required name is missing. Encodes the loss_streak cooldown as
// "threshold:minutes" exactly like RulesViewModel.CreateRuleAsync.
function readFormPayload() {
  const name = getVal('f-name');
  if (!name) { setFormError('Name is required.'); $('f-name').focus(); return null; }
  const type = $('f-type').value || 'custom';

  let conditionValue = '';
  if (type !== 'custom') {
    conditionValue = getVal('f-cond');
    if (type === 'loss_streak') {
      const cd = parseInt(getVal('f-cooldown'), 10);
      if (Number.isFinite(cd) && cd > 0) conditionValue = `${conditionValue}:${cd}`;
    }
  }

  return {
    name,
    ruleType: type,
    conditionValue,
    description: getVal('f-desc'),
    replacementPlan: getVal('f-plan'),
  };
}

// Submit the form → create_rule or update_rule, then reload.
async function submitForm(submitBtn) {
  const payload = readFormPayload();
  if (!payload) return;

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[rules] (preview) submit — no Tauri backend.');
    closeForm();
    return;
  }

  if (_editId != null) payload.id = _editId;
  const cmd = _editId != null ? 'update_rule' : 'create_rule';

  if (submitBtn) submitBtn.disabled = true;
  try {
    await invoke(cmd, { payload });
    closeForm();
    await loadRules();
  } catch (err) {
    setFormError((err && err.message) ? err.message : String(err));
    console.error(`[rules] ${cmd} failed:`, err);
  } finally {
    if (submitBtn) submitBtn.disabled = false;
  }
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderViolations(d);

  const active = Array.isArray(d.activeRules) ? d.activeRules : [];
  const inactive = Array.isArray(d.inactiveRules) ? d.inactiveRules : [];
  const empty = d.isEmpty || (active.length === 0 && inactive.length === 0);

  renderSection(active, 'active-list', 'active-label', 'active-n', { off: false });
  renderSection(inactive, 'inactive-list', 'inactive-label', 'inactive-n', { off: true });

  if (empty) {
    if (d.emptyMessage) $('rules-empty-h').textContent = d.emptyMessage;
    renderSuggested();
    show($('rules-empty'), true);
  } else {
    show($('rules-empty'), false);
  }

  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _lastData = null;
let _loading = false;
async function loadRules() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchRules();
    _lastData = data;
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[rules] load failed:', err);
  } finally {
    _loading = false;
  }
}

// Resolve a card's ruleId back to the full rule from the last fetch, so Edit can
// prefill from the in-memory row (no extra read).
function ruleById(id) {
  if (_lastData == null) return null;
  const pools = [_lastData.activeRules, _lastData.inactiveRules];
  for (const pool of pools) {
    if (!Array.isArray(pool)) continue;
    const hit = pool.find((r) => String(r.id) === String(id));
    if (hit) return hit;
  }
  return null;
}

// Walk up to the card to find its ruleId.
function ruleIdForTarget(target) {
  const card = target.closest('[data-rule-id]');
  return card && card.dataset.ruleId ? card.dataset.ruleId : null;
}

// ── single delegated action handler ─────────────────────────────────────────
// Local (no backend):
//   new_rule    = open the create form.
//   edit_rule   = open the edit form, prefilled.
//   cancel_form = close the form.
// Form submit:
//   submit_form = create_rule / update_rule.
// Per-rule mutations (carry {id}):
//   toggle_rule, delete_rule (delete confirms first).
// Empty-state preset:
//   add_suggested = create_rule from a hardcoded SUGGESTED entry.
const LOCAL_ACTIONS = new Set(['new_rule', 'edit_rule', 'cancel_form']);
const ID_ACTIONS = new Set(['toggle_rule', 'delete_rule']);
const ACTIONS = new Set([
  'new_rule', 'edit_rule', 'cancel_form', 'submit_form',
  'toggle_rule', 'delete_rule', 'add_suggested',
]);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Local-only actions never touch the backend.
  if (LOCAL_ACTIONS.has(action)) {
    if (action === 'new_rule') openCreateForm();
    else if (action === 'edit_rule') openEditForm(ruleIdForTarget(target));
    else if (action === 'cancel_form') closeForm();
    return;
  }

  if (action === 'submit_form') {
    await submitForm(target);
    return;
  }

  // Delete confirms before firing the hard delete.
  if (action === 'delete_rule') {
    if (!window.confirm('Delete this rule? This cannot be undone.')) return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[rules] (preview) action "${action}" — no Tauri backend.`);
    return;
  }

  // Build the payload per action.
  let payload;
  if (action === 'add_suggested') {
    const idx = Number(target.closest('[data-suggest-idx]')?.dataset.suggestIdx);
    const s = SUGGESTED[idx];
    if (!s) return;
    // Mirror AddSuggestedRuleAsync: no replacementPlan (defaults '').
    payload = {
      name: s.name,
      ruleType: s.ruleType,
      conditionValue: s.conditionValue,
      description: s.description,
    };
  } else if (ID_ACTIONS.has(action)) {
    const id = ruleIdForTarget(target);
    if (id == null) return;
    payload = { id: Number(id) };
  } else {
    payload = {};
  }

  const cmd = action === 'add_suggested' ? 'create_rule' : action;

  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  try {
    await invoke(cmd, { payload });
    await loadRules();
  } catch (err) {
    renderError(err);
    console.error(`[rules] action "${action}" failed:`, err);
  } finally {
    if (canDisable) target.disabled = false;
  }
});

// Type select + condition input drive the form's per-type fields + IF preview.
document.addEventListener('change', (ev) => {
  if (ev.target && ev.target.id === 'f-type') syncTypeFields();
});
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.id === 'f-cond') syncIfPreview();
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', loadRules);
} else {
  loadRules();
}

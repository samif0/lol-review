// Revu desktop — stateless helpers for the VOD timeline corrections panel
// (./vodcorrections.js owns the state and calls these): the event-type catalog
// view over the snapshot, the type options, the attribute chips, the attribute
// prefill from an event, the corrections list rows, and m:ss parsing. Every
// function takes what it renders; server strings land via textContent only.

const CLOCK_RE = /^(\d{1,4}):([0-5]\d)$/;
const LISTED_STATES = new Set(['active', 'absorbed', 'orphaned']);

export function parseClock(s) {
  const m = CLOCK_RE.exec(String(s || '').trim());
  return m ? Number(m[1]) * 60 + Number(m[2]) : NaN;
}

// The snapshot's eventTypeCatalog, normalised. Preview / older snapshot: the
// distinct types on the timeline, as point types with no attributes.
export function catalogOf(vod) {
  const cat = vod && vod.eventTypeCatalog;
  if (Array.isArray(cat) && cat.length) {
    return cat.map((t) => ({
      type: String(t.type || '').toUpperCase(), label: t.label || t.type || '',
      kind: t.kind || 'point', colorHex: t.colorHex || '', attrs: Array.isArray(t.attrs) ? t.attrs : [],
    }));
  }
  const seen = new Map();
  for (const e of ((vod && vod.gameEvents) || [])) {
    const t = String(e.eventType || '').toUpperCase();
    if (t && !seen.has(t)) seen.set(t, { type: t, label: e.label || t, kind: 'point', colorHex: e.colorHex || '', attrs: [] });
  }
  return [...seen.values()];
}
export const typeDefIn = (types, type) => types.find((t) => t.type === String(type || '').toUpperCase()) || null;
export const labelIn = (types, type) => (typeDefIn(types, type) || {}).label || String(type || '');

// Prefill the attribute chips from what the snapshot already tells us about the
// event; only values the catalog lists are kept, so a stale value never lights a
// chip the server would reject.
export function prefillAttrs(e, def) {
  const out = {};
  if (!e || !def) return out;
  const raw = {};
  if (def.type === 'TRADE' && e.encounterClassification) raw.kind = String(e.encounterClassification).toLowerCase();
  if (def.type === 'TEAMFIGHT' && e.teamfight) {
    if (e.teamfight.self) raw.self = String(e.teamfight.self).toLowerCase();
    if (e.teamfight.verdict) raw.verdict = String(e.teamfight.verdict).toLowerCase();
  }
  for (const a of def.attrs) {
    if (!(a.key in raw)) continue;
    if ((a.options || []).some((o) => String(o.value).toLowerCase() === raw[a.key])) out[a.key] = raw[a.key];
  }
  return out;
}

// Fill the type <select>. The subject's own type can sit outside the catalog (FLASH,
// LEVEL_UP): keep it visible so the form reads right; a retype still picks a catalog type.
export function renderTypeOptions(sel, types, want, clear) {
  clear(sel);
  for (const t of types) sel.add(new Option(t.label, t.type));
  if (want && !types.some((t) => t.type === want)) sel.add(new Option(labelIn(types, want), want));
  if (want) sel.value = want;
  if (sel.selectedIndex < 0 && sel.options.length) sel.selectedIndex = 0;
}

// Chips per catalog attribute: bool attrs offer Yes / No (so "not in fog" is an
// explicit false), choice attrs offer their options; the lit chip is the value that
// will be sent (clicking it again unsets it, handled by the caller's fix_attr action).
export function renderAttrChips(host, def, attrs, visible, { show, clear }) {
  clear(host);
  const list = def ? def.attrs : [];
  show(host, visible && list.length > 0);
  for (const a of list) {
    const group = document.createElement('div');
    group.className = 'vp-fix-attr-group';
    const lbl = document.createElement('span');
    lbl.className = 'vp-fix-attr-lbl';
    lbl.textContent = a.label || a.key || '';
    group.appendChild(lbl);
    const options = a.input === 'bool'
      ? [{ value: true, label: 'Yes' }, { value: false, label: 'No' }]
      : (a.options || []);
    for (const o of options) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'q vp-fix-attr';
      chip.dataset.action = 'fix_attr';
      chip.dataset.key = String(a.key || '');
      chip.dataset.val = JSON.stringify(o.value);
      chip.textContent = o.label || String(o.value);
      const cur = attrs[a.key];
      if (cur !== undefined && JSON.stringify(cur) === chip.dataset.val) chip.classList.add('is-on');
      group.appendChild(chip);
    }
    host.appendChild(group);
  }
}

// The "Corrections in this game" rows: applicable states only, newest first as
// served. Returns the number listed (the caller writes the count badge).
export function renderList(host, corrections, { tpl, show, clear, clock, labelOf }) {
  clear(host);
  const rows = (corrections || []).filter((c) => LISTED_STATES.has(String(c.state || '')));
  for (const c of rows) {
    const row = tpl('tpl-fix-row');
    row.dataset.seconds = String(c.gameTimeSeconds || 0);
    row.dataset.correctionId = String(c.correctionId || '');
    row.querySelector('.vp-fix-when').textContent = c.timeLabel || clock(c.gameTimeSeconds);
    row.querySelector('.vp-fix-what').textContent = c.summary || `${labelOf(c.eventType)} ${c.op || ''}`.trim();
    const st = row.querySelector('.vp-fix-state');
    st.textContent = c.stateLabel || c.state || '';
    st.classList.add(`is-${String(c.state || 'active')}`);
    const rs = row.querySelector('.vp-fix-reason');
    rs.textContent = c.reason || '';
    show(rs, !!c.reason);
    show(row.querySelector('.vp-fix-revert'), !!c.canRevert && !!c.correctionId);
    if (c.applyError) row.title = c.applyError;
    host.appendChild(row);
  }
  return rows.length;
}

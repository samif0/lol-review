// Revu desktop — Tilt Check page renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_tiltcheck`
// (see Revu.Sidecar GET /api/tiltcheck). Mirrors app.js conventions exactly:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-tiltcheck.json so the page previews in a
//     plain browser.
//   • Every server string is written via textContent (never innerHTML) so the
//     surface stays XSS-free; colors arrive as *Hex strings applied to style
//     properties only.
//   • ONE delegated [data-action] click handler.
//   • The reset RITUAL is a WRITE: it collects {emotion, intensityBefore,
//     intensityAfter?, reframeThought?, reframeResponse?, ifThenPlan?} and
//     invoke('run_reset', {…}) → reloads the page so the new entry appears.

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

// ── Content pools (rotated each run to resist habituation) ───────────────────
// Mirror TiltCheckViewModel's pools + per-run sample sizes. The label is shown;
// the lowercase comma-joined selection is what we POST as `emotion`.
const EMOTION_POOL = [
  'Angry', 'Frustrated', 'Anxious', 'Hopeless', 'Numb', 'Restless', 'Helpless',
  'Cheated', 'Deflated', 'Ashamed', 'Disrespected', 'Envious', 'Crushed', 'Done',
  'Tense', 'Hostile',
];
const EMOTION_CHIPS_SHOWN = 10;
const MAX_EMOTION_SELECTIONS = 3;

const REAPPRAISAL_POOL = [
  "This is one of ~2,000 games I'll play this year: one data point.",
  'Rank is noisy under 100 games. One loss is not a trend.',
  "I was on tilt by mid-game; that's the real lesson, not the loss.",
  'The past 30 minutes are sunk cost. Only the next game is in front of me.',
  'Losses are priced in. My rank over a month is what matters.',
  "If a friend told me about this game, I'd tell them to let it go.",
  "I can be frustrated AND play well next game. They're not the same thing.",
  "What I notice about this loss now is not what I'll notice in a week.",
];
const REAPPRAISAL_SHOWN = 4;

const TRIGGER_POOL = [
  'If I notice myself flame-typing in chat',
  'If I die and start to blame a teammate',
  'If I fall two levels behind in lane',
  "If my jungler hasn't ganked by minute 10",
  'If I catch myself tilting at champ select',
  "If I start forcing fights I wouldn't normally take",
  'If I die the same way twice',
  "If I'm about to ping-spam someone",
];
const RESPONSE_POOL = [
  'I will mute-all for the next 2 minutes.',
  'I will farm for 60 seconds before doing anything else.',
  'I will ward a safe bush and take one slow breath.',
  'I will ping retreat instead of typing.',
  'I will play the next minute purely around vision.',
  'I will type gg wp and close the chat box.',
  'I will stand up and stretch for 15 seconds next death-wait.',
  'I will call one objective to refocus the team.',
];
const IFTHEN_SHOWN = 3;

// Cyclic-sighing pacer: double-inhale (2s) then 6s exhale; 80s total.
const BREATH_PHASES = [['Inhale…', 1], ['Top off…', 1], ['Exhale slowly…', 6]];
const TOTAL_BREATH_SECONDS = 80;

// Pick N distinct random items from a pool (Fisher-Yates partial shuffle).
function sample(pool, n) {
  const a = pool.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a.slice(0, Math.min(n, a.length));
}

// ── module state ────────────────────────────────────────────────────────────
let _data = null;
let _ritualOpen = false;

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchTiltcheck() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_tiltcheck');
  }
  const res = await fetch('./sample-tiltcheck.json');
  if (!res.ok) throw new Error(`sample-tiltcheck.json ${res.status}`);
  return res.json();
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const s = d.stats || {};
  const parts = [];
  const total = s.total || 0;
  parts.push(total === 0 ? 'No resets yet.' : `${total} reset${total === 1 ? '' : 's'} logged.`);
  if (total > 0 && s.avgReductionText) parts.push(s.avgReductionText);
  if (total > 0 && s.avgBefore != null && s.avgAfter != null) {
    parts.push(`${s.avgBefore} → ${s.avgAfter} typical`);
  }
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: stat strip (fixed order) ────────────────────────────────────────
function renderStrip(d) {
  const s = d.stats || {};
  const strip = $('tc-strip');
  clear(strip);

  const total = s.total || 0;
  const topEmotion = Array.isArray(s.topEmotions) && s.topEmotions.length
    ? s.topEmotions[0]
    : null;

  const cells = [
    { k: 'Resets',     v: String(total),                                      sub: 'ALL-TIME',  flag: false },
    { k: 'Avg Before', v: total ? fmt(s.avgBefore) : '—',                     sub: 'INTENSITY', flag: false },
    { k: 'Avg After',  v: total ? fmt(s.avgAfter) : '—',                      sub: 'INTENSITY', flag: false },
    { k: 'Avg Drop',   v: total ? fmt(s.avgReduction) : '—',                  sub: 'POINTS',    flag: true  },
    { k: 'Top',        v: topEmotion ? cap(topEmotion.emotion) : '—',         sub: topEmotion ? `${topEmotion.count}×` : '', flag: false },
  ];

  for (const c of cells) {
    const el = tpl('tpl-stat');
    if (c.flag) el.classList.add('flag');
    el.querySelector('.k').textContent = c.k;
    el.querySelector('.v').textContent = c.v;
    el.querySelector('.s').textContent = c.sub;
    strip.appendChild(el);
  }
}

function fmt(n) { return n == null ? '—' : String(n); }
function cap(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : ''; }

// ── render: top emotions chips ──────────────────────────────────────────────
function renderEmotions(d) {
  const s = d.stats || {};
  const list = Array.isArray(s.topEmotions) ? s.topEmotions : [];
  const host = $('tc-emotions');
  clear(host);

  if (list.length === 0) {
    show($('tc-emotions-label'), false);
    return;
  }
  show($('tc-emotions-label'), true);

  for (const e of list) {
    const el = tpl('tpl-emotion');
    const dot = el.querySelector('.tc-emo-dot');
    if (e.emotionColorHex) {
      dot.style.background = e.emotionColorHex;
      dot.style.boxShadow = `0 0 8px ${e.emotionColorHex}`;
    }
    el.querySelector('.tc-emo-name').textContent = cap(e.emotion || '');
    el.querySelector('.tc-emo-n').textContent = `${e.count ?? 0}×`;
    host.appendChild(el);
  }
}

// ── render: latest if-then plan ─────────────────────────────────────────────
function renderPlan(d) {
  const card = $('tc-plan');
  if (d.hasLatestPlan && d.latestPlan) {
    $('tc-plan-v').textContent = d.latestPlan;
    show(card, true);
  } else {
    show(card, false);
  }
}

// ── render: recent reset log ────────────────────────────────────────────────
function renderRecent(d) {
  const items = Array.isArray(d.recent) ? d.recent : [];
  const host = $('tc-recent');
  clear(host);

  if (!d.hasRecent || items.length === 0) {
    show($('tc-recent-label'), false);
    show($('tc-empty'), true);
    return;
  }
  show($('tc-empty'), false);
  show($('tc-recent-label'), true);

  for (const r of items) host.appendChild(buildRecentRow(r));
}

function buildRecentRow(r) {
  const el = tpl('tpl-recent');
  const emo = el.querySelector('.tc-row-emo');
  const when = el.querySelector('.tc-row-when');
  const planTag = el.querySelector('.tc-row-plan');
  const text = el.querySelector('.tc-row-text');
  const before = el.querySelector('.tc-row-before');
  const arr = el.querySelector('.tc-row-arr');
  const after = el.querySelector('.tc-row-after');
  const drop = el.querySelector('.tc-row-drop');
  const edge = el.querySelector('.tc-row-edge');

  emo.textContent = cap(r.emotion || '');
  if (r.emotionColorHex) {
    emo.style.color = r.emotionColorHex;
    el.style.setProperty('--emo', r.emotionColorHex);
  }
  when.textContent = r.createdAtText || '';
  show(planTag, !!r.hasPlan);

  // The body line favors the reframe response, then the if-then plan, then the
  // thought, then the focus intention — whatever the user actually wrote.
  text.textContent = r.reframeResponse || r.ifThenPlan || r.reframeThought
    || r.focusIntention || '';

  // Before → after intensity duo. After/reduction are nullable.
  before.textContent = r.intensityBefore != null ? String(r.intensityBefore) : '—';
  if (r.intensityAfter != null) {
    after.textContent = String(r.intensityAfter);
    if (r.intensityReduction != null && r.intensityReduction > 0) {
      drop.textContent = `−${r.intensityReduction}`;
      drop.classList.add('good');
    } else if (r.intensityReduction != null && r.intensityReduction < 0) {
      drop.textContent = `+${Math.abs(r.intensityReduction)}`;
      drop.classList.add('bad');
    } else {
      drop.textContent = '±0';
    }
  } else {
    // No "after" logged — show the before only, hide the arrow/after/drop.
    show(arr, false);
    show(after, false);
    show(drop, false);
  }

  return el;
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
    $('tc-primary'),
    $('tc-strip'),
    $('tc-emotions'),
    $('tc-plan'),
    $('tc-recent'),
  ].filter(Boolean);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  _data = d;
  clearError();
  renderHeader(d);
  renderStrip(d);
  renderEmotions(d);
  renderPlan(d);
  renderRecent(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function loadTiltcheck() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchTiltcheck();
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[tiltcheck] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── the reset ritual (WRITE) — 5-step wizard ─────────────────────────────────
// Clicking RUN A RESET swaps the primary CTA for the inline guided wizard built
// from <template id="tpl-ritual">. Five sequential steps: Name It (emotion chips
// + pre-intensity) / Breathe (80s cyclic-sighing pacer) / Reframe / Plan (if-then)
// / Done (post-intensity + save). Content pools rotate every run. SAVE invokes
// run_reset; SAVE → SET INTENT also navigates to the dashboard Start Block.
// Cancel/Escape (or Start Over) reset without writing.
function openRitual() {
  if (_ritualOpen) return;
  _ritualOpen = true;
  const card = $('tc-primary');
  card.classList.add('is-open');

  const runbtn = $('tc-runbtn');
  show(runbtn, false);

  const ritual = tpl('tpl-ritual');
  card.appendChild(ritual);

  // ── wizard state ──────────────────────────────────────────────────────────
  let step = 0;
  const selectedEmotions = [];      // FIFO, max 3
  let reframe = '';
  let trigger = '';
  let response = '';
  let breatheTimer = null;
  let breatheElapsed = 0;
  let breathePhaseIdx = 0;
  let breathePhaseLeft = 0;
  let breatheCycle = 1;

  const steps = Array.from(ritual.querySelectorAll('.tc-step'));
  const progress = ritual.querySelector('#tc-progress');
  for (let i = 0; i < steps.length; i++) {
    const dot = document.createElement('span');
    dot.className = 'tc-pdot';
    progress.appendChild(dot);
  }

  const before = ritual.querySelector('#tc-before');
  const beforeVal = ritual.querySelector('#tc-before-val');
  const after = ritual.querySelector('#tc-after');
  const afterVal = ritual.querySelector('#tc-after-val');
  const result = ritual.querySelector('#tc-result');
  const msg = ritual.querySelector('#tc-ritual-msg');
  const selReadout = ritual.querySelector('#tc-sel-readout');

  function setMsg(textStr, tone) {
    if (!msg) return;
    msg.textContent = textStr;
    msg.className = 'tc-ritual-msg' + (tone ? ' ' + tone : '');
    show(msg, !!textStr);
  }

  // ── step navigation ────────────────────────────────────────────────────────
  function showStep(n) {
    step = n;
    steps.forEach((s) => show(s, Number(s.dataset.step) === n));
    progress.querySelectorAll('.tc-pdot').forEach((d, i) => {
      d.classList.toggle('on', i === n);
      d.classList.toggle('done', i < n);
    });
    if (n === 1) startBreathing();
    if (n === 4) enterDone();
  }

  function nextStep() {
    if (step === 0 && selectedEmotions.length === 0) return; // hard gate
    if (step < steps.length - 1) showStep(step + 1);
  }

  // ── step 0: emotion chips (multi-select ≤3, FIFO) + pre-intensity ──────────
  const chipHost = ritual.querySelector('#tc-chips');
  const nextBtn0 = steps[0].querySelector('.tc-next');
  for (const name of sample(EMOTION_POOL, EMOTION_CHIPS_SHOWN)) {
    const chip = tpl('tpl-chip');
    chip.textContent = name;
    chip.addEventListener('click', () => toggleEmotion(name, chip));
    chipHost.appendChild(chip);
  }
  function toggleEmotion(name, chip) {
    const lower = name.toLowerCase();
    const idx = selectedEmotions.indexOf(lower);
    if (idx >= 0) {
      selectedEmotions.splice(idx, 1);
      chip.classList.remove('on');
    } else {
      selectedEmotions.push(lower);
      chip.classList.add('on');
      if (selectedEmotions.length > MAX_EMOTION_SELECTIONS) {
        const evicted = selectedEmotions.shift();
        for (const c of chipHost.querySelectorAll('.tc-chip')) {
          if (c.textContent.toLowerCase() === evicted) c.classList.remove('on');
        }
      }
    }
    if (selReadout) {
      selReadout.textContent = selectedEmotions.map(cap).join(', ');
      show(selReadout, selectedEmotions.length > 0);
    }
    if (nextBtn0) nextBtn0.disabled = selectedEmotions.length === 0;
  }
  before.addEventListener('input', () => { beforeVal.textContent = before.value; });

  // ── step 1: breathing pacer ────────────────────────────────────────────────
  const breatheCount = ritual.querySelector('#tc-breathe-count');
  const breathePhaseEl = ritual.querySelector('#tc-breathe-phase');
  const breatheFill = ritual.querySelector('#tc-breathe-fill');
  const breatheCycleEl = ritual.querySelector('#tc-breathe-cycle');
  const ring = ritual.querySelector('#tc-ring');

  function startBreathing() {
    stopBreathing();
    breatheElapsed = 0;
    breathePhaseIdx = 0;
    breathePhaseLeft = BREATH_PHASES[0][1];
    breatheCycle = 1;
    renderBreathPhase();
    breatheTimer = setInterval(onBreathTick, 1000);
  }
  function stopBreathing() {
    if (breatheTimer) { clearInterval(breatheTimer); breatheTimer = null; }
  }
  function renderBreathPhase() {
    const [label, dur] = BREATH_PHASES[breathePhaseIdx];
    if (breathePhaseEl) breathePhaseEl.textContent = label;
    // Drive the breathing circle DIRECTLY per phase so it actually tracks the
    // cue: inhale grows smoothly, "top off" grows further but accelerates, and
    // exhale shrinks slowly over its full (6s) window. The transition duration is
    // set to the phase length so the motion fills exactly that phase. exhale=last.
    if (ring) {
      const isInhale = breathePhaseIdx === 0;       // "Inhale…"
      const isTopOff = breathePhaseIdx === 1;        // "Top off…"
      const isExhale = breathePhaseIdx === BREATH_PHASES.length - 1;
      const scale = isExhale ? 0.78 : isTopOff ? 1.12 : 1.0;
      // Inhale: ease-out (quick then settle). Top off: ease-in (accelerates into
      // the peak). Exhale: ease-in-out, slow over the whole exhale window.
      const ease = isExhale ? 'cubic-bezier(0.37,0,0.63,1)'
                 : isTopOff ? 'cubic-bezier(0.55,0,1,0.45)'   // accelerating
                 : 'cubic-bezier(0,0,0.2,1)';                  // ease-out
      ring.style.transition = `transform ${dur}s ${ease}, border-color ${dur}s ease, box-shadow ${dur}s ease`;
      ring.style.transform = `scale(${scale})`;
      ring.classList.toggle('exhale', isExhale);
      ring.classList.toggle('inhale', isInhale || isTopOff);
    }
    if (breatheCount) breatheCount.textContent = String(Math.max(0, TOTAL_BREATH_SECONDS - breatheElapsed));
    if (breatheCycleEl) breatheCycleEl.textContent = `Cycle ${breatheCycle}`;
    if (breatheFill) breatheFill.style.width = `${(breatheElapsed / TOTAL_BREATH_SECONDS) * 100}%`;
  }
  function onBreathTick() {
    breatheElapsed++;
    breathePhaseLeft--;
    if (breathePhaseLeft <= 0) {
      breathePhaseIdx = (breathePhaseIdx + 1) % BREATH_PHASES.length;
      if (breathePhaseIdx === 0) breatheCycle++;
      breathePhaseLeft = BREATH_PHASES[breathePhaseIdx][1];
    }
    renderBreathPhase();
    if (breatheElapsed >= TOTAL_BREATH_SECONDS) {
      stopBreathing();
      showStep(2);
    }
  }

  // ── step 2: reframe menu (single-select) ───────────────────────────────────
  const reframeMenu = ritual.querySelector('#tc-reframe-menu');
  const reframeReadout = ritual.querySelector('#tc-reframe-readout');
  buildMenu(reframeMenu, sample(REAPPRAISAL_POOL, REAPPRAISAL_SHOWN), (txt) => {
    reframe = txt;
    if (reframeReadout) { reframeReadout.textContent = txt; show(reframeReadout, true); }
  });

  // ── step 3: if-then plan (two single-selects) ──────────────────────────────
  const triggerMenu = ritual.querySelector('#tc-trigger-menu');
  const responseMenu = ritual.querySelector('#tc-response-menu');
  const planReadout = ritual.querySelector('#tc-plan-readout');
  buildMenu(triggerMenu, sample(TRIGGER_POOL, IFTHEN_SHOWN), (txt) => { trigger = txt; renderPlan(); });
  buildMenu(responseMenu, sample(RESPONSE_POOL, IFTHEN_SHOWN), (txt) => { response = txt; renderPlan(); });
  function ifThenPlan() {
    if (!trigger || !response) return '';
    return `${trigger}, then ${response.replace(/\.\s*$/, '')}.`;
  }
  function renderPlan() {
    const plan = ifThenPlan();
    if (planReadout) { planReadout.textContent = plan; show(planReadout, !!plan); }
  }

  // ── step 4: done (post-intensity + result) ─────────────────────────────────
  function enterDone() {
    // Seed after = before so the initial delta is 0 ("same intensity").
    after.value = before.value;
    afterVal.textContent = after.value;
    const labeled = ritual.querySelector('#tc-done-labeled');
    if (labeled) labeled.textContent = selectedEmotions.length
      ? `You labeled: ${selectedEmotions.map(cap).join(', ')}.`
      : 'You named the feeling.';
    syncResult();
  }
  function syncResult() {
    const b = Number(before.value);
    const a = Number(after.value);
    const diff = b - a;
    const baB = ritual.querySelector('#tc-ba-before');
    const baA = ritual.querySelector('#tc-ba-after');
    if (baB) baB.textContent = String(b);
    if (baA) baA.textContent = String(a);
    if (diff > 0) {
      result.textContent = `You went from ${b} → ${a}. That's ${diff} down.`;
      result.className = 'tc-result good';
    } else if (diff < 0) {
      result.textContent = `Higher than before. Consider a longer break before the next game.`;
      result.className = 'tc-result bad';
    } else {
      result.textContent = `Same intensity, and that's okay. A 4-minute ritual can't clear cortisol; it catches you on the slope.`;
      result.className = 'tc-result';
    }
    show(result, true);
  }
  after.addEventListener('input', () => { afterVal.textContent = after.value; syncResult(); });

  // ── menu builder (single-select option list) ───────────────────────────────
  function buildMenu(host, options, onPick) {
    for (const txt of options) {
      const opt = tpl('tpl-menu-opt');
      opt.textContent = txt;
      opt.addEventListener('click', () => {
        for (const o of host.querySelectorAll('.tc-menuopt')) o.classList.remove('on');
        opt.classList.add('on');
        onPick(txt);
      });
      host.appendChild(opt);
    }
  }

  // ── close / lifecycle ──────────────────────────────────────────────────────
  function close() {
    _ritualOpen = false;
    stopBreathing();
    ritual.remove();
    card.classList.remove('is-open');
    document.removeEventListener('keydown', onKey);
    show(runbtn, true); // loadTiltcheck() doesn't rebuild the primary card
  }
  function onKey(ev) {
    if (ev.key === 'Escape') { ev.preventDefault(); close(); }
  }
  document.addEventListener('keydown', onKey);

  // ── save ────────────────────────────────────────────────────────────────────
  // Returns true on a committed write (or preview), false on error.
  async function doSave() {
    if (selectedEmotions.length === 0) { setMsg('Pick an emotion first.', 'err'); return false; }
    const payload = {
      emotion: selectedEmotions.join(', '),
      intensityBefore: Number(before.value),
      intensityAfter: Number(after.value),
    };
    if (reframe) payload.reframeResponse = reframe;
    const plan = ifThenPlan();
    if (plan) payload.ifThenPlan = plan;

    const invoke = await getInvoke();
    if (!invoke) {
      console.info('[tiltcheck] (preview) run_reset — no Tauri backend.', payload);
      return true;
    }
    try {
      await invoke('run_reset', { payload });
      return true;
    } catch (err) {
      renderError(err);
      console.error('[tiltcheck] action "run_reset" failed:', err);
      setMsg('Save failed; see the error panel.', 'err');
      return false;
    }
  }

  // ── in-wizard button wiring (closure over state) ───────────────────────────
  ritual.addEventListener('click', async (ev) => {
    const btn = ev.target.closest('[data-action]');
    if (!btn || !ritual.contains(btn)) return;
    const action = btn.dataset.action;
    if (action === 'step_next') { nextStep(); return; }
    if (action === 'skip_breathing') { stopBreathing(); showStep(2); return; }
    if (action === 'start_over') { stopBreathing(); close(); openRitual(); return; }

    if (action === 'confirm_reset' || action === 'save_set_intent') {
      const saveBtn = ritual.querySelector('#tc-savebtn');
      const intentBtn = ritual.querySelector('#tc-saveintent');
      if (saveBtn) saveBtn.disabled = true;
      if (intentBtn) intentBtn.disabled = true;
      const ok = await doSave();
      if (!ok) {
        if (saveBtn) saveBtn.disabled = false;
        if (intentBtn) intentBtn.disabled = false;
        return;
      }
      if (action === 'save_set_intent') {
        // Loop-closing path: the reset is saved; jump to the dashboard Start Block
        // (the ?intent=startblock param tells the dashboard to open the intent
        // ritual). The actual intention is set there, not here. Target dashboard.html
        // (the dashboard CONTENT page) — index.html is now the persistent shell, so
        // navigating there inside the iframe would nest the shell.
        window.location.href = 'dashboard.html?intent=startblock';
        return;
      }
      close();
      await loadTiltcheck();
    }
  });

  showStep(0);
  // Focus the first chip so keyboard users land in the wizard.
  const firstChip = chipHost.querySelector('.tc-chip');
  if (firstChip) firstChip.focus();
}

// ── single delegated action handler ─────────────────────────────────────────
// The global handler owns ONLY open_ritual (RUN A RESET). Every in-wizard button
// (step_next / skip_breathing / start_over / confirm_reset / save_set_intent) is
// handled by the ritual-scoped listener inside openRitual() — those need closure
// over the wizard's step/selection state, so they're not routed here.
const ACTIONS = new Set(['open_ritual']);

document.addEventListener('click', (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();
  if (action === 'open_ritual') openRitual();
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', loadTiltcheck);
} else {
  loadTiltcheck();
}

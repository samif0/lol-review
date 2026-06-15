// Revu desktop — first-launch ONBOARDING wizard.
//
// A single-page state machine that mirrors OnboardingViewModel exactly. Six
// states swap which card is visible:
//   welcome → (skip)  role → done                      [OnboardingSkipped=true]
//   welcome → (login) emailEntry → codeSent → account → role → done
//
// Two completion paths:
//   • SKIP / LOCAL  — no Riot account; stamps OnboardingSkipped=true at role-finish.
//   • LOGIN         — email OTP, links Riot ID + region, resolves PUUID, detects
//                     rank (display-only); OnboardingSkipped stays false.
//
// THE PARTIAL-SESSION GATE (load-bearing): VerifyAsync saves a session token
// BEFORE the Riot ID is linked. If the user backs out there, a token-without-
// RiotId state jams OnboardingComplete forever (RiotProxyEnabled needs token AND
// RiotId). Every Back hop calls clearPartialSession() to drop the half-saved
// token, exactly like the VM's ClearPartialSessionAsync.
//
// House conventions (mirrors app.js / manualentry.js):
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.core.
//   • Outside Tauri the wizard runs client-side only (auth/config calls no-op in
//     preview) so the flow is browsable standalone.
//   • All server/user strings written via textContent only (XSS-safe).
//   • ONE delegated [data-action] click handler.

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

// ── auth command names (owned by the parallel auth agent) ────────────────────
// CENTRALIZED so reconciliation is one place if the auth agent renames anything.
// Names verified against desktop/src-tauri/src/lib.rs (Batch-4 auth commands):
//   login        → auth_login        POST /api/auth/login    { email }            (send OTP)
//   verify       → auth_verify       POST /api/auth/verify   { code, email? }     (exchange
//                  code; persists RiotSessionToken/Email/ExpiresAt) → { ok, email }
//   resolve      → auth_resolve      POST /api/auth/resolve  { riotId, region }   (ResolveAccount
//                  + GetSoloRank; persists RiotId/RiotRegion/RiotPuuid + OnboardingSkipped=false)
//                  → { ok, puuid, gameName, tagLine, rank }
//   status       → get_auth_status   GET  /api/auth/status                        (gate snapshot)
//   clearPartial → auth_clear_partial POST /api/auth/clear-partial                (blanks the
//                  half-saved session triplet so the onboarding gate can't jam)
//   finishRole   → save_config       POST /api/config/save  { primaryRole, onboardingSkipped? }
//                  The role-finish has no auth endpoint; it rides the existing config-save,
//                  which now honors PrimaryRole (both paths) + OnboardingSkipped (skip path).
const AUTH_CMD = {
  login: 'auth_login',
  verify: 'auth_verify',
  resolve: 'auth_resolve',
  status: 'get_auth_status',
  logout: 'auth_logout',
  clearPartial: 'auth_clear_partial',
  finishRole: 'save_config',
};

// ── DOM helpers ──────────────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }

// The state cards, keyed by state name (matches each section's data-state).
// 'signedIn' is the returning-user confirmation card (added so the page reflects an
// existing session instead of always re-showing the first-boot 'welcome' intro).
const STATES = ['signedIn', 'welcome', 'emailEntry', 'codeSent', 'account', 'role', 'done'];

// ── module state (mirrors the VM's observable fields) ────────────────────────
const vm = {
  state: 'welcome',
  busy: false,
  error: '',
  info: '',
  chosenLoginPath: false,   // false = skip/local path, true = login path
  email: '',
  otpCode: '',
  riotId: '',
  riotRegion: 'na1',
  primaryRole: '',          // TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY
  detectedRank: '',         // display-only link confirmation (may be empty)
  // Account snapshot from get_auth_status, for the signed-in confirmation card.
  acctEmail: '',
  acctRiotId: '',
  acctRegion: '',
};

// ── error message normalization ──────────────────────────────────────────────
// The Tauri/sidecar proxy surfaces a non-2xx write as a string like
// "sidecar HTTP 400 Bad Request: <friendly>". The friendly half is the
// RiotAuthClient.ThrowIfNotOkAsync message we want to show the user. Strip the
// proxy prefix so the player sees the real reason, not transport noise.
function friendlyError(err, fallback) {
  const raw = (err && err.message) ? err.message : String(err || '');
  const m = raw.match(/sidecar HTTP[^:]*:\s*(.+)$/i);
  if (m && m[1]) return m[1].trim();
  // A bare server message (no proxy prefix) — show it if it looks human, else fallback.
  if (raw && !/^sidecar |failed to fetch|connection refused|not ready/i.test(raw)) {
    return raw;
  }
  return fallback;
}

// ── render ────────────────────────────────────────────────────────────────────
function render() {
  // Exactly one state card visible (the 'done' state shows none — host swaps the
  // shell in by then; here we just stop showing cards).
  for (const s of STATES) {
    const card = document.querySelector(`[data-state="${s}"]`);
    if (card) show(card, s === vm.state && s !== 'done');
  }

  // codeSent body is bound to Info.
  const info = $('code-info');
  if (info) info.textContent = vm.info || 'Check your email for a code.';

  // Live-reflect the input fields (so back-and-forth keeps values where the VM does).
  const email = $('f-email'); if (email && email.value !== vm.email) email.value = vm.email;
  const code = $('f-code'); if (code && code.value !== vm.otpCode) code.value = vm.otpCode;
  const riotId = $('f-riotid'); if (riotId && riotId.value !== vm.riotId) riotId.value = vm.riotId;
  const region = $('f-region'); if (region && region.value !== vm.riotRegion) region.value = vm.riotRegion;

  // Role selection highlight.
  document.querySelectorAll('.onb-role').forEach((b) => {
    b.classList.toggle('on', b.dataset.role === vm.primaryRole && vm.primaryRole !== '');
  });

  // Detected-rank line (display-only).
  const rank = $('onb-rank');
  if (rank) {
    if (vm.detectedRank) {
      rank.textContent = `RANK DETECTED: ${vm.detectedRank}`;
      show(rank, true);
    } else {
      rank.textContent = '';
      show(rank, false);
    }
  }

  // Signed-in card: show the account email + (if linked) the Riot ID · region.
  const acctEmail = $('onb-acct-email');
  if (acctEmail) acctEmail.textContent = vm.acctEmail || 'Signed in';
  const acctSub = $('onb-acct-sub');
  if (acctSub) {
    const linked = [vm.acctRiotId, (vm.acctRegion || '').toUpperCase()].filter(Boolean).join(' · ');
    acctSub.textContent = linked ? `Linked: ${linked}` : 'Your account is linked.';
  }

  // Footer error + spinner.
  const err = $('onb-error');
  if (err) { err.textContent = vm.error || ''; show(err, !!vm.error); }
  show($('onb-spin'), vm.busy);

  // Busy gates every button.
  document.querySelectorAll('.onb-card button, .onb-link').forEach((b) => {
    b.disabled = vm.busy;
  });
}

function setState(next) { vm.state = next; render(); }
function setError(msg) { vm.error = msg || ''; render(); }

// ── auth-call wrapper (preview-safe) ─────────────────────────────────────────
// Returns the command result, or null when running outside Tauri (preview).
//
// The Rust auth_* / save_config commands each take a SINGLE `payload` arg (the
// JSON body), so the body must be passed as invoke(cmd, { payload: body }). The
// no-arg commands (auth_clear_partial) take nothing. Wrapping here, keyed off
// which command needs a body, keeps every call site passing just the plain body
// — and avoids the "missing required key payload" rejection.
const NO_BODY_CMDS = new Set(['auth_clear_partial', 'auth_logout', 'get_auth_status']);
async function call(cmd, body) {
  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[onboarding] (preview) ${cmd} — no Tauri backend.`, body || {});
    return null;
  }
  if (NO_BODY_CMDS.has(cmd)) return invoke(cmd);
  return invoke(cmd, { payload: body || {} });
}

// ── welcome ───────────────────────────────────────────────────────────────────
// Skip/local path: straight to role pick, bypassing all auth.
function startUsing() {
  vm.chosenLoginPath = false;
  vm.error = '';
  setState('role');
}

// Login path: reveal the email input.
function beginLogin() {
  vm.chosenLoginPath = true;
  vm.error = '';
  setState('emailEntry');
}

// ── email entry + OTP ────────────────────────────────────────────────────────
async function sendLoginCode() {
  if (vm.busy) return;
  vm.error = '';
  vm.email = ($('f-email')?.value || '').trim();
  if (!vm.email) { setError('Enter an email to continue.'); return; }

  vm.busy = true; render();
  try {
    await call(AUTH_CMD.login, { email: vm.email });
    vm.info = `Check ${vm.email} for a code.`;
    vm.otpCode = '';
    setState('codeSent');
  } catch (err) {
    console.error('[onboarding] send-login-code failed:', err);
    setError(friendlyError(err, "Couldn't reach the server. Check your connection."));
  } finally {
    vm.busy = false; render();
  }
}

async function verifyCode() {
  if (vm.busy) return;
  vm.error = '';
  vm.otpCode = ($('f-code')?.value || '').trim().toUpperCase();
  if (!vm.otpCode) { setError('Paste the code from your email.'); return; }

  vm.busy = true; render();
  try {
    // verify exchanges the OTP for a session and persists the session triplet
    // (RiotSessionToken/Email/ExpiresAt) server-side, exactly like VerifyAsync.
    await call(AUTH_CMD.verify, { code: vm.otpCode, email: vm.email });
    vm.error = '';
    vm.info = '';
    // A RETURNING user (already linked Riot ID + picked a role) shouldn't be marched
    // back through account-link + role. Check the live status: if fully set up, land
    // them straight on the signed-in confirmation. A NEW user (no Riot ID yet)
    // continues to the account-link step as before.
    let status = null;
    try { const invoke = await getInvoke(); if (invoke) status = await invoke(AUTH_CMD.status); } catch (_) { status = null; }
    if (status && status.signedIn && status.riotId && status.primaryRole) {
      vm.acctEmail = status.email || vm.email;
      vm.acctRiotId = status.riotId || '';
      vm.acctRegion = status.region || '';
      setState('signedIn');
    } else {
      setState('account');
    }
  } catch (err) {
    console.error('[onboarding] verify failed:', err);
    setError(friendlyError(err, "Couldn't verify the code."));
  } finally {
    vm.busy = false; render();
  }
}

// ── Riot ID + region (login path only) ───────────────────────────────────────
async function finishAccount() {
  if (vm.busy) return;
  vm.error = '';
  const id = ($('f-riotid')?.value || '').trim();
  const region = ($('f-region')?.value || '').trim().toLowerCase();
  vm.riotId = id;
  vm.riotRegion = region;

  // Riot ID must be gameName#tagLine: contains '#', not leading/trailing '#'.
  if (!id || !id.includes('#') || id.startsWith('#') || id.endsWith('#')) {
    setError('Enter your Riot ID as gameName#tagLine.');
    return;
  }
  if (!region) { setError('Pick a region.'); return; }

  vm.busy = true; render();
  try {
    // resolve verifies the account with Riot, writes RiotId/RiotRegion/RiotPuuid +
    // OnboardingSkipped=false, and best-effort detects the ranked tier (display-only).
    // The server-side guard returns an error if the session token is missing.
    const res = await call(AUTH_CMD.resolve, { riotId: id, region });
    // /api/auth/resolve returns { ok, puuid, gameName, tagLine, rank }. Accept
    // `rank` (the real field) or `detectedRank` (defensive alias). Best-effort +
    // display-only — empty rank just hides the RANK DETECTED line.
    const detected = res && (res.rank ?? res.detectedRank);
    if (detected) vm.detectedRank = String(detected);
    vm.error = '';
    vm.info = '';
    setState('role');
  } catch (err) {
    console.error('[onboarding] finish-account failed:', err);
    // Missing-session guard: the server reports an unauthenticated/expired session
    // → reset to welcome (mirrors the VM's "Session missing. Start over." branch).
    if (/session (missing|expired|invalid|no longer valid)|unauthorized|start over/i.test(String(err && err.message || err))) {
      vm.state = 'welcome';
      setError('Session missing. Start over.');
    } else {
      setError(friendlyError(err, "Couldn't validate that account."));
    }
  } finally {
    vm.busy = false; render();
  }
}

// ── role (final step) ─────────────────────────────────────────────────────────
function selectRole(role) {
  vm.primaryRole = role;
  vm.error = '';
  render();
}

async function finishRole() {
  if (vm.busy) return;
  if (!vm.primaryRole) { setError('Pick the role you play most.'); return; }

  vm.busy = true; render();
  try {
    // Persists PrimaryRole via config-save (mutates only the keys we send). On the
    // SKIP path also stamps OnboardingSkipped=true so the gate stops firing.
    // Login-path users OMIT onboardingSkipped — resolve already set it false and
    // they rely on RiotProxyEnabled + PrimaryRole; sending null would leave it
    // unchanged anyway, but omitting keeps the write minimal.
    const cfg = { primaryRole: vm.primaryRole };
    if (!vm.chosenLoginPath) cfg.onboardingSkipped = true;
    // call() wraps the body as { payload: ... } to match save_config's Rust arg.
    await call(AUTH_CMD.finishRole, cfg);
    vm.error = '';
    setState('done');
    onComplete();
  } catch (err) {
    console.error('[onboarding] finish-role failed:', err);
    setError(friendlyError(err, "Couldn't save your role."));
  } finally {
    vm.busy = false; render();
  }
}

// ── partial-session cleanup (load-bearing) ───────────────────────────────────
// Blank any session token persisted by verify before account-link completed.
// Without this, backing out leaves a token with no Riot ID and the onboarding
// gate (OnboardingComplete) keeps re-showing forever. No-throw — failure is logged
// and swallowed (mirror ClearPartialSessionAsync's try/catch).
async function clearPartialSession() {
  try {
    await call(AUTH_CMD.clearPartial, {});
  } catch (err) {
    console.warn('[onboarding] could not clear partial session:', err);
  }
}

// ── back navigation ──────────────────────────────────────────────────────────
async function backToWelcome() {
  await clearPartialSession();
  vm.email = '';
  vm.otpCode = '';
  vm.error = '';
  vm.info = '';
  vm.chosenLoginPath = false;
  setState('welcome');
}

async function backToEmail() {
  // Keep Email so the user can re-send; clear the code + any partial session.
  await clearPartialSession();
  vm.otpCode = '';
  vm.error = '';
  vm.info = '';
  setState('emailEntry');
}

// ── completion ────────────────────────────────────────────────────────────────
// Onboarding is finished. In the real app the host swaps in the shell; for now
// this page is reachable (not auto-shown), so we navigate to the dashboard.
function onComplete() {
  // Small beat so the user sees the role lock in before the page flips. Target
  // dashboard.html (the dashboard CONTENT page) — index.html is the persistent shell
  // now, so navigating there inside the iframe would nest the shell.
  setTimeout(() => { window.location.href = 'dashboard.html'; }, 250);
}

// ── signed-in card actions ────────────────────────────────────────────────────
function goDashboard() { window.location.href = 'dashboard.html'; }

async function signOut() {
  if (vm.busy) return;
  vm.busy = true; render();
  try {
    await call(AUTH_CMD.logout, {});
  } catch (err) {
    console.warn('[onboarding] logout failed:', err);
  } finally {
    vm.busy = false;
    // After signing out, drop the account snapshot and return to the email step
    // (a returning user re-signs in; we don't re-show the first-boot intro).
    vm.acctEmail = ''; vm.acctRiotId = ''; vm.acctRegion = '';
    vm.email = ''; vm.otpCode = ''; vm.error = ''; vm.info = '';
    vm.chosenLoginPath = true;
    setState('emailEntry');
  }
}

// ── auth-status-driven initial state ─────────────────────────────────────────
// Decide which card to show on load instead of always starting at 'welcome':
//   • signedIn (status.signedIn)            → the signed-in confirmation card
//   • not signed in, FIRST boot             → the 'welcome' intro (Start using Revu)
//   • not signed in, returning/logged-out   → straight to the email sign-in step
// "First boot" = the user has never gotten through onboarding: no primary role set
// AND onboarding wasn't explicitly skipped. Everyone else who lands here logged-out
// is a returning user choosing to sign in, so we skip the intro card for them.
async function chooseInitialState() {
  const invoke = await getInvoke();
  if (!invoke) {
    // Preview (no backend): keep the standalone first-boot intro so the flow is browsable.
    setState('welcome');
    return;
  }
  let status = null;
  try { status = await invoke(AUTH_CMD.status); } catch (_) { status = null; }

  if (status && status.signedIn) {
    vm.acctEmail = status.email || '';
    vm.acctRiotId = status.riotId || '';
    vm.acctRegion = status.region || '';
    setState('signedIn');
    return;
  }

  // Logged out. First boot vs returning.
  const hasRole = !!(status && status.primaryRole);
  let onboardingSkipped = false;
  try {
    const cfg = await invoke('get_config');
    onboardingSkipped = !!(cfg && cfg.onboardingSkipped);
  } catch (_) { /* treat as not-skipped */ }

  const firstBoot = !hasRole && !onboardingSkipped;
  setState(firstBoot ? 'welcome' : 'emailEntry');
  if (!firstBoot) vm.chosenLoginPath = true; // returning user is on the login path
}

// ── single delegated action handler ──────────────────────────────────────────
const HANDLERS = {
  start_using: startUsing,
  begin_login: beginLogin,
  send_code: sendLoginCode,
  verify_code: verifyCode,
  back_to_welcome: backToWelcome,
  back_to_email: backToEmail,
  finish_account: finishAccount,
  finish_role: finishRole,
  go_dashboard: goDashboard,
  sign_out: signOut,
};

document.addEventListener('click', (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;

  if (action === 'select_role') {
    ev.preventDefault();
    selectRole(target.dataset.role || '');
    return;
  }

  const fn = HANDLERS[action];
  if (!fn) return;
  ev.preventDefault();
  fn();
});

// Keep vm fields in sync as the user types (so validation + back-nav use live
// values even before a button click).
document.addEventListener('input', (ev) => {
  const t = ev.target;
  if (t.id === 'f-email') vm.email = t.value;
  else if (t.id === 'f-code') vm.otpCode = t.value;
  else if (t.id === 'f-riotid') vm.riotId = t.value;
});
document.addEventListener('change', (ev) => {
  if (ev.target.id === 'f-region') vm.riotRegion = ev.target.value;
});

// Enter-to-submit on the single-field steps (quality-of-life; the WinUI page had
// no Enter wiring but this matches web expectations and never bypasses validation).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter') return;
  const id = ev.target && ev.target.id;
  if (id === 'f-email') { ev.preventDefault(); sendLoginCode(); }
  else if (id === 'f-code') { ev.preventDefault(); verifyCode(); }
  else if (id === 'f-riotid') { ev.preventDefault(); finishAccount(); }
});

// ── boot ────────────────────────────────────────────────────────────────────
// Pick the initial card from the live auth status (signed-in / first-boot /
// returning), then render. chooseInitialState calls setState (which renders), so a
// first paint of 'welcome' is avoided by rendering only after the decision resolves.
async function boot() { await chooseInitialState(); render(); }
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

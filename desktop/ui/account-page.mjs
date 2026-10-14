// The Account page: onboarding.html opened with ?signin=1. The sidebar's account row,
// Settings (Manage sign-in) and the VOD player's sign-in hint all lead here. It runs the
// same sign-in flow as first launch under an Account heading, and never shows the
// first-launch Get started card.
// Pages that change the Revu sign-in announce it, so the shell's account row
// (shell-recent.mjs) rereads it without waiting for a navigation.

export const ACCOUNT_CHANGED = 'revu:account-changed';

const text = value => (typeof value === 'string' ? value.trim() : '');

/** Tells the shell that the sign-in may have changed. Outside the shell it does nothing. */
export function announceAccountChange(win = globalThis.window) {
  try { win?.top?.dispatchEvent(new CustomEvent(ACCOUNT_CHANGED)); } catch (_) { /* no shell */ }
}

/** Whether this load is the Account page (?signin=1) rather than first launch. */
export function isAccountPage(search) {
  try { return new URLSearchParams(String(search ?? '')).get('signin') === '1'; } catch (_) { return false; }
}

/**
 * Whether the onboarding state is the signed-in card (true), a sign-in step (false) or
 * neither, such as linking a Riot ID or picking a role (null).
 */
export function signedInFor(state) {
  if (state === 'signedIn') return true;
  return state === 'emailEntry' || state === 'codeSent' ? false : null;
}

/** The Account page heading. signedIn is null while it is unknown or beside the point. */
export function accountHero(signedIn) {
  const subtitle = signedIn === true ? 'Your Revu sign-in.'
    : signedIn === false ? 'Sign in with email to share clips and look up match details.' : '';
  return { title: 'Account', subtitle };
}

/**
 * The signed-in card's second line: the linked Riot ID and region. Without one the
 * page shows its "Add it in Settings" link (#onb-acct-add) after this line.
 */
export function linkedLine(riotId, region) {
  const id = text(riotId);
  if (!id) return 'No Riot ID linked yet.';
  const where = text(region).toUpperCase();
  return `Linked: ${where ? `${id} · ${where}` : id}`;
}

/** Whether the signed-in card needs its link to the Riot ID field in Settings. */
export function needsRiotId(riotId) {
  return !text(riotId);
}

/**
 * Shows the Account heading (#onb-eyebrow, #onb-title, #onb-subt) and the page title.
 * Go to Home is hidden, since the sidebar already navigates, and so is Back on the email
 * step, which would open the first-launch Get started card. Sign out stays.
 */
export function applyAccountPage(doc, signedIn) {
  const $ = id => doc?.getElementById?.(id) || null;
  const hero = accountHero(signedIn);
  if (doc) doc.title = `Revu: ${hero.title}`;
  const eyebrow = $('onb-eyebrow'), title = $('onb-title'), subtitle = $('onb-subt');
  if (eyebrow) eyebrow.hidden = true;
  if (title) title.textContent = hero.title;
  if (subtitle) { subtitle.textContent = hero.subtitle; subtitle.hidden = !hero.subtitle; }
  for (const id of ['signedin-continue', 'email-back']) {
    const el = $(id);
    if (el) el.hidden = true;
  }
}

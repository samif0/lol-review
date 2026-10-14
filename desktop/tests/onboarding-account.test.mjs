import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import * as accountPage from '../ui/account-page.mjs';

// onboarding.js runs here against a small DOM and a fake desktop bridge, the way
// settings-ascent.test.mjs runs settings.js: the Account page (?signin=1) and first
// launch, signing in and out, and what the shell's account row is told.
const source = (await readFile(new URL('../ui/onboarding.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');

const STATES = ['signedIn', 'welcome', 'emailEntry', 'codeSent', 'account', 'role'];
const IDS = ['onb-hero', 'onb-eyebrow', 'onb-title', 'onb-subt', 'onb-step', 'code-info', 'f-email', 'f-code', 'f-riotid',
  'f-region', 'onb-rank', 'onb-acct-email', 'onb-acct-sub', 'onb-acct-add', 'onb-error', 'onb-spin', 'signedin-my-clips',
  'signedin-continue', 'signedin-logout', 'email-back'];

function element(id) {
  const attrs = new Map();
  return {
    id, hidden: false, textContent: '', value: '', disabled: false, dataset: {},
    setAttribute(name, value) { attrs.set(name, String(value)); },
    getAttribute(name) { return attrs.has(name) ? attrs.get(name) : null; },
    hasAttribute(name) { return attrs.has(name); },
    classList: { toggle() {} },
  };
}

/** A desktop bridge over a tiny account server; null is a browser preview. */
function bridge(server) {
  return async (command, args) => {
    switch (command) {
      case 'get_auth_status': return { signedIn: server.signedIn, email: server.signedIn ? server.email : '',
        riotId: server.riotId, region: server.region, primaryRole: server.primaryRole };
      case 'get_config': return { onboardingSkipped: false };
      case 'auth_login': return { ok: true };
      case 'auth_verify': server.signedIn = true; return { ok: true, email: server.email };
      case 'auth_resolve':
        if (server.resolveError) throw new Error(server.resolveError);
        server.riotId = args.payload.riotId; server.region = args.payload.region;
        return { ok: true, rank: '' };
      case 'auth_logout': case 'auth_clear_partial': server.signedIn = false; return { ok: true };
      default: throw new Error(`unexpected ${command}`);
    }
  };
}

function page({ search = '?signin=1', server = null, snapshot = null } = {}) {
  const els = new Map(IDS.map(id => [id, element(id)]));
  // The markup's own text and hidden flags (onboarding.html).
  els.get('onb-eyebrow').textContent = 'Welcome';
  els.get('onb-title').textContent = 'Revu';
  els.get('onb-subt').textContent = 'Build a better habit, one game at a time.';
  for (const id of ['onb-step', 'onb-rank', 'onb-acct-add', 'onb-error', 'onb-spin', 'signedin-my-clips']) els.get(id).hidden = true;
  const cards = Object.fromEntries(STATES.map(state => [state, Object.assign(element(`card-${state}`), { hidden: true })]));
  const listeners = {}, events = [], commands = [];
  const document = {
    title: 'Revu: Welcome', readyState: 'interactive',
    getElementById: id => els.get(id) || null,
    querySelector: selector => cards[/^\[data-state="(\w+)"\]$/.exec(selector)?.[1]] || null,
    querySelectorAll: () => [],
    addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); },
  };
  const win = { location: { search, href: `onboarding.html${search}` },
    top: { dispatchEvent: event => { events.push(event.type); return true; } } };
  const invoke = server && bridge(server);
  const context = vm.createContext({
    document, window: win, platform: {},
    $: id => document.getElementById(id),
    show: (el, visible) => { if (el) el.hidden = !visible; },
    getInvoke: async () => invoke && (async (command, args) => { commands.push(command); return invoke(command, args); }),
    readSnapshot: async (command, sample) => {
      assert.deepEqual([command, sample], ['get_config', 'sample-settings.json']);
      if (snapshot instanceof Error) throw snapshot;
      return snapshot;
    },
    canOpenExternal: async () => false, openMyClips: async () => false,
    ...accountPage, announceAccountChange: () => accountPage.announceAccountChange(win),
    console: { error() {}, warn() {}, info() {} }, setTimeout, clearTimeout,
  });
  vm.runInContext(source, context);
  const settle = async () => { for (let i = 0; i < 12; i++) await new Promise(resolve => setImmediate(resolve)); };
  return {
    els, events, commands, document, settle,
    $: id => els.get(id),
    card: () => STATES.filter(state => !cards[state].hidden),
    click: async action => {
      for (const fn of listeners.click || []) fn({ target: { closest: () => ({ dataset: { action } }) }, preventDefault() {} });
      await settle();
    },
    heading: () => ['onb-eyebrow', 'onb-title', 'onb-subt'].map(id => (els.get(id).hidden ? '' : els.get(id).textContent)),
  };
}
const SIGNED_IN = () => ({ signedIn: true, email: 'you@example.com', riotId: 'bye#NA1', region: 'na1', primaryRole: 'MIDDLE' });

test('the Account page picks its heading before the first status read, then shows the signed-in card', async () => {
  const p = page({ server: SIGNED_IN() });
  // Synchronous part of boot: the heading is final and the hero may show.
  assert.equal(p.$('onb-hero').hasAttribute('data-ready'), true);
  assert.deepEqual(p.heading(), ['', 'Account', '']);
  assert.equal(p.document.title, 'Revu: Account');
  await p.settle();
  assert.deepEqual(p.card(), ['signedIn']);
  assert.deepEqual(p.heading(), ['', 'Account', 'Your Revu sign-in.']);
  assert.equal(p.$('onb-acct-email').textContent, 'you@example.com');
  assert.equal(p.$('onb-acct-sub').textContent, 'Linked: bye#NA1 · NA1');
  assert.equal(p.$('onb-acct-add').hidden, true);
  assert.equal(p.$('signedin-continue').hidden, true, 'no Go to Home on the Account page');
  assert.equal(p.$('signedin-logout').hidden, false);
  assert.deepEqual(p.events, [], 'opening the page changes nothing');
});

test('signed in with no Riot ID, the card says so and links to the Riot ID field in Settings', async () => {
  const p = page({ server: { ...SIGNED_IN(), riotId: '', region: '' } });
  await p.settle();
  assert.deepEqual(p.card(), ['signedIn']);
  assert.equal(p.$('onb-acct-sub').textContent, 'No Riot ID linked yet.');
  assert.equal(p.$('onb-acct-add').hidden, false);
  assert.deepEqual(p.heading(), ['', 'Account', 'Your Revu sign-in.'], 'the subtitle claims no Riot ID');
});

test('signing out and back in tells the shell each time and keeps the Account heading', async () => {
  const server = { ...SIGNED_IN() };
  const p = page({ server });
  await p.settle();
  await p.click('sign_out');
  assert.deepEqual(p.card(), ['emailEntry']);
  assert.deepEqual(p.events, ['revu:account-changed'], 'the row flips to Sign in');
  assert.deepEqual(p.heading(), ['', 'Account', 'Sign in with email to share clips and look up match details.']);
  assert.equal(p.$('email-back').hidden, true, 'no Back to the first-launch card');

  p.$('f-email').value = 'you@example.com';
  await p.click('send_code');
  assert.deepEqual(p.card(), ['codeSent']);
  assert.equal(p.events.length, 1, 'sending a code signs nobody in');
  p.$('f-code').value = 'abcd1234';
  await p.click('verify_code');
  assert.deepEqual(p.events, ['revu:account-changed', 'revu:account-changed'], 'the row flips back');
  assert.deepEqual(p.card(), ['signedIn'], 'a returning user lands on the signed-in card');
  assert.deepEqual(p.heading(), ['', 'Account', 'Your Revu sign-in.']);
  assert.deepEqual(p.commands.filter(command => command.startsWith('auth_')), ['auth_logout', 'auth_login', 'auth_verify']);
});

test('linking a Riot ID tells the shell; a lost session restarts the Account page at its email step', async () => {
  const server = { ...SIGNED_IN(), signedIn: false, riotId: '', region: '' };
  const p = page({ server });
  await p.settle();
  assert.deepEqual(p.card(), ['emailEntry']);
  p.$('f-email').value = 'you@example.com';
  await p.click('send_code');
  p.$('f-code').value = 'abcd1234';
  await p.click('verify_code');
  assert.deepEqual(p.card(), ['account'], 'no Riot ID yet: the link step');
  assert.deepEqual(p.heading(), ['', 'Account', ''], 'no subtitle on the link step');
  p.$('f-riotid').value = 'bye#NA1';
  p.$('f-region').value = 'na1';
  await p.click('finish_account');
  assert.deepEqual(p.card(), ['role']);
  assert.deepEqual(p.events, ['revu:account-changed', 'revu:account-changed'], 'signed in, then linked');

  const lost = page({ server: { ...server, signedIn: false, riotId: '', resolveError: 'sidecar HTTP 401 Unauthorized: Session missing. Start over.' } });
  await lost.settle();
  lost.$('f-email').value = 'you@example.com';
  await lost.click('send_code');
  lost.$('f-code').value = 'abcd1234';
  await lost.click('verify_code');
  lost.$('f-riotid').value = 'bye#NA1';
  lost.$('f-region').value = 'na1';
  await lost.click('finish_account');
  assert.deepEqual(lost.card(), ['emailEntry'], 'never the first-launch Get started card');
  assert.equal(lost.$('onb-error').textContent, 'Session missing. Start over.');
  assert.deepEqual(lost.events, ['revu:account-changed', 'revu:account-changed'], 'signed in, then the session was gone');
  assert.deepEqual(lost.heading(), ['', 'Account', 'Sign in with email to share clips and look up match details.']);
});

test('first launch keeps the Welcome heading and its Get started card', async () => {
  const p = page({ search: '', server: { ...SIGNED_IN(), signedIn: false, riotId: '', primaryRole: '' } });
  assert.equal(p.$('onb-hero').hasAttribute('data-ready'), true);
  await p.settle();
  assert.deepEqual(p.card(), ['welcome']);
  assert.deepEqual(p.heading(), ['Welcome', 'Revu', 'Build a better habit, one game at a time.']);
  assert.equal(p.document.title, 'Revu: Welcome');
  await p.click('begin_login');
  assert.equal(p.$('email-back').hidden, false, 'Back to Get started stays on first launch');
  await p.click('back_to_welcome');
  assert.deepEqual(p.card(), ['welcome']);
  assert.deepEqual(p.events, ['revu:account-changed'], 'dropping a half-session is announced');
});

test('the browser preview shows the same sample account as the shell row', async () => {
  const signedIn = page({ snapshot: { riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com', riotId: 'Player#NA1', region: 'na1' } });
  await signedIn.settle();
  assert.deepEqual(signedIn.card(), ['signedIn']);
  assert.equal(signedIn.$('onb-acct-sub').textContent, 'Linked: Player#NA1 · NA1');
  for (const snapshot of [{ riotAuthState: 'loggedOut', riotSessionEmail: 'you@example.com' }, new Error('404')]) {
    const out = page({ snapshot });
    await out.settle();
    assert.deepEqual(out.card(), ['emailEntry'], String(snapshot));
  }
  const first = page({ search: '', snapshot: new Error('never read') });
  await first.settle();
  assert.deepEqual(first.card(), ['welcome'], 'first launch never reads the fixture');
});

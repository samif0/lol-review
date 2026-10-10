import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { flush, read, response, runPage } from './fake-dom.mjs';

const html = read('sign-in.html');
const script = read('sign-in.js');
const text = html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

function helpers() {
  const context = {};
  vm.runInNewContext(script, context);
  return context.RevuSignIn;
}

function turnstileStub() {
  return {
    renders: [],
    executes: 0,
    resets: 0,
    render(container, options) { this.renders.push(options); this.options = options; return 'widget-1'; },
    execute(id) { assert.equal(id, 'widget-1'); this.executes += 1; },
    reset(id) { assert.equal(id, 'widget-1'); this.resets += 1; },
  };
}

const READY = {
  'GET /auth/web/providers': response(200, { providers: ['email'] }),
  'GET /auth/web/me': response(401, { error: 'signed_out' }),
};

function page({ search = '', routes = {}, turnstile = turnstileStub() } = {}) {
  const run = runPage({ html, script, search, pathname: '/sign-in', routes: { ...READY, ...routes }, extra: { turnstile } });
  return { ...run, turnstile };
}

async function submitEmail(run, email) {
  run.$('email-input').value = email;
  const event = run.$('email-form').dispatch('submit');
  assert.equal(event.defaultPrevented, true);
  await flush();
}

test('normalizeCode strips whitespace and dashes, uppercases and caps at 16', () => {
  const { normalizeCode } = helpers();
  assert.equal(normalizeCode(' ab-cd ef12 '), 'ABCDEF12');
  assert.equal(normalizeCode('a\tb\nc-d'), 'ABCD');
  assert.equal(normalizeCode(''), '');
  assert.equal(normalizeCode(null), '');
  assert.equal(normalizeCode('x'.repeat(20)), 'X'.repeat(16));
});

test('normalizeEmail trims and lowercases', () => {
  const { normalizeEmail } = helpers();
  assert.equal(normalizeEmail('  Me@Example.COM '), 'me@example.com');
  assert.equal(normalizeEmail(undefined), '');
});

test('resendDelay defaults to 60 seconds', () => {
  const { resendDelay } = helpers();
  assert.equal(resendDelay(undefined), 60);
  assert.equal(resendDelay(null), 60);
  assert.equal(resendDelay(0), 60);
  assert.equal(resendDelay('x'), 60);
  assert.equal(resendDelay(42), 42);
  assert.equal(resendDelay(899.2), 900);
});

test('startOutcome maps responses to the next step', () => {
  const { startOutcome } = helpers();
  assert.equal(startOutcome({ status: 200, data: { sent: true } }), 'code_step');
  assert.equal(startOutcome({ status: 429, data: { error: 'rate_limit_email', retry_after: 60 } }), 'code_step_throttled');
  assert.equal(startOutcome({ status: 429, data: { error: 'rate_limit_auth' } }), 'error');
  assert.equal(startOutcome({ status: 403, data: { error: 'turnstile_failed' } }), 'error');
  assert.equal(startOutcome({ status: 0, data: { error: 'network' } }), 'error');
});

test('The page has the agreed copy, with the trust card above the form', () => {
  assert.match(html, /<h1>Sign in or create an account<\/h1>/);
  assert.match(text, /Sign in to see and manage the clips you share from the Revu desktop app\. Enter your email and we'll send you a one-time code\. New to Revu\? The same steps create your account\./);
  assert.match(text, /No passwords, ever/);
  assert.match(text, /Revu never asks for your Riot account login or any password, and never stores one\./);
  assert.match(text, /Each sign-in uses a one-time code we email you\. It works once and expires in 10 minutes\./);
  assert.match(text, /Use the same email in the Revu desktop app and you'll see the same clips\./);
  const trust = html.indexOf('No passwords, ever');
  assert.ok(trust > 0 && trust < html.indexOf('id="email-form"'), 'trust card comes before the email form');
  assert.match(html, /<button[^>]*class="btn-link"[^>]*>Already have a code\?<\/button>/);
  assert.match(html, /<button[^>]*class="btn-primary"[^>]*type="submit"[^>]*>Send sign-in code<\/button>/);
  assert.match(text, /It comes from login@revu\.lol\. Not there\? Check spam or Promotions, or resend\./);
  assert.match(text, /Only enter this code on revu\.lol or in the Revu app\. Revu will never ask you to share it\./);
  assert.match(text, /Enter the 8-character code we sent to\s+\.\s+Codes expire after 10 minutes\./);
  assert.match(text, /You don't need an account to review a game on revu\.lol\. Review a game/);
  assert.match(html, /<a href="\/terms">Terms<\/a>/);
  assert.match(html, /<a href="\/privacy">Privacy Policy<\/a>/);
  assert.match(html, /<title>Sign in - Revu<\/title>/);
});

test('The forms carry the agreed input attributes', () => {
  const email = html.match(/<input id="email-input"[^>]*>/)[0];
  for (const attr of ['type="email"', 'autocomplete="email"', 'inputmode="email"', 'maxlength="254"', 'placeholder="you@example.com"']) {
    assert.ok(email.includes(attr), attr);
  }
  const code = html.match(/<input id="code-input"[^>]*>/)[0];
  for (const attr of ['autocomplete="one-time-code"', 'autocapitalize="characters"', 'spellcheck="false"', 'maxlength="16"', 'placeholder="8-character code from email"']) {
    assert.ok(code.includes(attr), attr);
  }
  assert.match(html, /<form id="email-form"[^>]*novalidate/);
  assert.match(html, /<form id="code-form"[^>]*hidden/);
  assert.match(html, /role="status"/);
  assert.match(html, /role="alert"/);
  assert.match(html, /<div id="turnstile-container"><\/div>/);
});

test('Scripts: Turnstile explicit render, no inline script, no inline handlers, no download.js', () => {
  assert.match(html, /<script src="https:\/\/challenges\.cloudflare\.com\/turnstile\/v0\/api\.js\?render=explicit" async defer><\/script>/);
  for (const src of ['/account-api.js', '/sign-in.js', '/header-account.js']) {
    assert.match(html, new RegExp(`<script src="${src.replace('.', '\\.')}" defer></script>`));
  }
  assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/);
  for (const tag of html.match(/<[^>]+>/g)) assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, tag);
  assert.doesNotMatch(html, /javascript:/i);
  assert.doesNotMatch(html, /download\.js/);
  assert.doesNotMatch(html, /[–—]/);
  assert.doesNotMatch(script, /[–—]/);
  assert.doesNotMatch(script, /innerHTML/);
});

test('No third-party sign-in options outside the footer disclaimer', () => {
  const body = html.replace(/<footer[\s\S]*?<\/footer>/, '');
  assert.doesNotMatch(body, /Continue with/i);
  assert.doesNotMatch(body, /Sign in with (Google|Riot)/i);
  assert.doesNotMatch(body, /(class|id)="[^"]*(google|riot)[^"]*"/i);
});

test('A signed-in visitor goes straight to next', async () => {
  const run = page({ search: '?next=/account', routes: { 'GET /auth/web/me': response(200, { user: { email: 'a@b.co', created_at: 1 } }) } });
  await flush();
  assert.deepEqual(run.navigations, [['replace', '/account']]);
  const unsafe = page({ search: '?next=https://evil.com', routes: { 'GET /auth/web/me': response(200, { user: {} }) } });
  await flush();
  assert.deepEqual(unsafe.navigations, [['replace', '/my-clips']]);
});

test('No email provider disables both forms', async () => {
  const run = page({ routes: { 'GET /auth/web/providers': response(200, { providers: [] }) } });
  await flush();
  assert.equal(run.$('sign-in-error').textContent, 'Sign in is unavailable right now. Try again later.');
  for (const id of ['email-input', 'email-submit', 'have-code', 'code-input', 'code-submit', 'resend-code', 'go-back']) {
    assert.equal(run.$(id).disabled, true, id);
  }
  await submitEmail(run, 'a@b.co');
  assert.equal(run.requests.filter(r => r.path === '/auth/web/email/start').length, 0);
});

test('reason=session_expired shows its copy', async () => {
  const run = page({ search: '?reason=session_expired' });
  await flush();
  assert.equal(run.$('sign-in-status').textContent, 'Your session ended. Sign in again.');
  const other = page({ search: '?reason=<b>hi</b>' });
  await flush();
  assert.equal(other.$('sign-in-status').textContent, '');
});

test('An invalid email never reaches Turnstile or the server', async () => {
  const run = page();
  await flush();
  await submitEmail(run, 'not-an-email');
  assert.equal(run.$('sign-in-error').textContent, "That email address doesn't look valid.");
  assert.equal(run.turnstile.renders.length, 0);
  assert.equal(run.requests.filter(r => r.method === 'POST').length, 0);
});

test('Sending a code: Turnstile token, code step, countdown, then verify', async () => {
  const run = page({
    search: '?next=/account',
    routes: {
      'POST /auth/web/email/start': response(200, { sent: true }),
      'POST /auth/web/email/verify': body => (body.code === 'ABCD1234'
        ? response(200, { ok: true, new_account: true, user: { email: 'me@example.com', created_at: 1 } })
        : response(400, { error: 'invalid_or_expired_code' })),
    },
  });
  await flush();
  await submitEmail(run, '  Me@Example.com ');
  assert.equal(run.$('email-submit').textContent, 'Sending...');
  assert.equal(run.$('email-submit').disabled, true);
  assert.equal(run.turnstile.renders.length, 1);
  assert.equal(run.turnstile.options.action, 'web_signin');
  assert.equal(run.turnstile.options.sitekey, '0x4AAAAAADUmEL-Xamn7L2E0');
  assert.equal(run.turnstile.options.size, 'flexible');
  assert.equal(run.turnstile.options.appearance, 'interaction-only');
  assert.equal(run.turnstile.options.execution, 'execute');
  assert.equal(run.turnstile.executes, 1);

  run.turnstile.options['before-interactive-callback']();
  assert.equal(run.$('turnstile-container').classList.contains('is-interactive'), true);
  assert.equal(run.$('sign-in-status').textContent, 'Complete the quick check to continue.');
  run.turnstile.options.callback('token-1');
  await flush();
  assert.equal(run.$('turnstile-container').classList.contains('is-interactive'), false);
  assert.equal(run.turnstile.resets, 1);

  const start = run.requests.find(r => r.path === '/auth/web/email/start');
  assert.deepEqual(start.body, { email: 'me@example.com', turnstileToken: 'token-1' });
  assert.equal(run.$('email-form').hidden, true);
  assert.equal(run.$('code-form').hidden, false);
  assert.equal(run.$('code-email').textContent, 'me@example.com');
  assert.equal(run.document.activeElement, run.$('code-input'));
  assert.equal(run.$('resend-code').disabled, true);
  assert.match(run.$('resend-code').textContent, /^Resend code in (59|60)s$/);

  run.$('code-input').value = 'zz';
  run.$('code-form').dispatch('submit');
  await flush();
  assert.equal(run.$('sign-in-error').textContent, 'That code is invalid or expired. Request a new one.');
  assert.deepEqual(run.navigations, []);

  run.$('code-input').value = ' ';
  run.$('code-form').dispatch('submit');
  await flush();
  assert.equal(run.$('sign-in-error').textContent, 'Enter the code from your email.');

  run.$('code-input').value = 'abcd-1234';
  run.$('code-form').dispatch('submit');
  await flush();
  const verify = run.requests.filter(r => r.path === '/auth/web/email/verify').pop();
  assert.deepEqual(verify.body, { email: 'me@example.com', code: 'ABCD1234' });
  assert.deepEqual(run.navigations, [['replace', '/account?new=1']]);
  assert.equal(run.storage.map.get('revu.web.signedIn.v1'), '1');
  for (const [key, value] of run.storage.writes) {
    assert.equal(key, 'revu.web.signedIn.v1');
    assert.equal(value, '1');
  }
});

test('A throttled start still opens the code step with the server countdown', async () => {
  const run = page({ routes: { 'POST /auth/web/email/start': response(429, { error: 'rate_limit_email', retry_after: 42 }) } });
  await flush();
  await submitEmail(run, 'a@b.co');
  run.turnstile.options.callback('t');
  await flush();
  assert.equal(run.$('code-form').hidden, false);
  assert.equal(run.$('sign-in-status').textContent, 'We already sent a code to a@b.co. Check your email, or resend in 42s.');
  assert.match(run.$('resend-code').textContent, /^Resend code in (41|42)s$/);
});

test('Start errors show their copy and re-enable the button', async () => {
  const run = page({ routes: { 'POST /auth/web/email/start': response(502, { error: 'email_send_failed' }) } });
  await flush();
  await submitEmail(run, 'a@b.co');
  run.turnstile.options.callback('t');
  await flush();
  assert.equal(run.$('sign-in-error').textContent, "Couldn't send the email right now. Try again in a moment.");
  assert.equal(run.$('email-form').hidden, false);
  assert.equal(run.$('email-submit').disabled, false);
  assert.equal(run.$('email-submit').textContent, 'Send sign-in code');
});

test('A Turnstile failure shows the browser check copy and resets the widget', async () => {
  const run = page();
  await flush();
  await submitEmail(run, 'a@b.co');
  run.turnstile.options['error-callback']();
  await flush();
  assert.equal(run.$('sign-in-error').textContent, "We couldn't verify your browser. Try again.");
  assert.equal(run.turnstile.resets, 1);
  assert.equal(run.$('email-submit').disabled, false);
  assert.equal(run.requests.filter(r => r.path === '/auth/web/email/start').length, 0);

  const broken = page({ turnstile: { render() { throw new Error('bad sitekey'); }, execute() {}, reset() {} } });
  await flush();
  await submitEmail(broken, 'a@b.co');
  assert.equal(broken.$('sign-in-error').textContent, "We couldn't verify your browser. Try again.");
});

test('Already have a code, resend, and go back', async () => {
  let startStatus = 429;
  const run = page({
    routes: {
      'POST /auth/web/email/start': () => (startStatus === 429
        ? response(429, { error: 'rate_limit_email', retry_after: 900 })
        : response(200, { sent: true })),
    },
  });
  await flush();
  run.$('email-input').value = 'bad';
  run.$('have-code').click();
  assert.equal(run.$('sign-in-error').textContent, "That email address doesn't look valid.");
  assert.equal(run.$('code-form').hidden, true);

  run.$('email-input').value = 'A@B.co';
  run.$('have-code').click();
  assert.equal(run.$('code-form').hidden, false);
  assert.equal(run.$('code-email').textContent, 'a@b.co');
  assert.equal(run.$('resend-code').disabled, false);
  assert.equal(run.$('resend-code').textContent, 'Resend code');
  assert.equal(run.requests.filter(r => r.method === 'POST').length, 0);

  run.$('resend-code').click();
  await flush();
  run.turnstile.options.callback('t1');
  await flush();
  assert.equal(run.$('sign-in-error').textContent, 'We just sent a code to this email. Check your inbox, or try again in 15 min.');
  assert.equal(run.$('resend-code').disabled, true);
  assert.match(run.$('resend-code').textContent, /^Resend code in (899|900)s$/);

  run.$('go-back').click();
  assert.equal(run.$('email-form').hidden, false);
  assert.equal(run.$('code-form').hidden, true);
  assert.equal(run.$('email-input').value, 'a@b.co');

  startStatus = 200;
  run.$('have-code').click();
  run.$('resend-code').click();
  await flush();
  run.turnstile.options.callback('t2');
  await flush();
  const starts = run.requests.filter(r => r.path === '/auth/web/email/start');
  assert.equal(starts.length, 2);
  assert.equal(starts[1].body.turnstileToken, 't2');
  assert.equal(run.$('sign-in-status').textContent, 'We sent a new code to a@b.co.');
});

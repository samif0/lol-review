import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../account-api.js', import.meta.url), 'utf8');

function memoryStorage() {
  const map = new Map();
  const writes = [];
  return {
    map,
    writes,
    getItem: key => (map.has(key) ? map.get(key) : null),
    setItem: (key, value) => { writes.push([key, String(value)]); map.set(key, String(value)); },
    removeItem: key => { map.delete(key); },
  };
}

function throwingStorage() {
  const fail = () => { throw new Error('SecurityError'); };
  return { getItem: fail, setItem: fail, removeItem: fail };
}

function response(status, body, headers = {}) {
  const lower = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: name => lower[name.toLowerCase()] ?? null },
    json: async () => {
      if (body === undefined) throw new SyntaxError('no body');
      return body;
    },
  };
}

// Load account-api.js in a fresh context. `respond` maps each request to a
// response (or throws to simulate a network failure).
function load({ hostname = 'revu.lol', storage = memoryStorage(), respond } = {}) {
  const calls = [];
  const context = {
    location: { hostname },
    localStorage: storage,
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond ? respond(url, init) : response(200, {});
    },
  };
  vm.runInNewContext(source, context);
  return { api: context.RevuAccount, calls, storage };
}

test('API_BASE is production everywhere but localhost', () => {
  assert.equal(load({ hostname: 'revu.lol' }).api.API_BASE, 'https://clips.revu.lol');
  assert.equal(load({ hostname: 'www.revu.lol' }).api.API_BASE, 'https://clips.revu.lol');
  assert.equal(load({ hostname: '127.0.0.1' }).api.API_BASE, 'https://clips.revu.lol');
  assert.equal(load({ hostname: 'localhost' }).api.API_BASE, 'http://localhost:8787');
});

test('Turnstile site key matches app.js', () => {
  assert.equal(load({ hostname: 'revu.lol' }).api.TURNSTILE_SITE_KEY, '0x4AAAAAADUmEL-Xamn7L2E0');
  assert.equal(load({ hostname: 'localhost' }).api.TURNSTILE_SITE_KEY, '1x00000000000000000000AA');
  assert.equal(load().api.HINT_KEY, 'revu.web.signedIn.v1');
});

test('Every request is credentialed CORS with no-store, and writes carry the CSRF header', async () => {
  const { api, calls } = load();
  await api.me();
  await api.providers();
  await api.listClips();
  await api.startEmail('a@b.co', 'tok');
  await api.verifyEmail('a@b.co', 'ABCDEFGH');
  await api.deleteClip('Abc1234');
  await api.logout();
  await api.logoutAll();
  await api.deleteAccount('a@b.co');
  assert.equal(calls.length, 9);
  for (const { url, init } of calls) {
    assert.ok(url.startsWith('https://clips.revu.lol/auth/web/'), url);
    assert.equal(init.credentials, 'include');
    assert.equal(init.mode, 'cors');
    assert.equal(init.cache, 'no-store');
    if (init.method === 'GET') {
      assert.equal(init.headers['X-Revu-CSRF'], undefined);
    } else {
      assert.equal(init.headers['X-Revu-CSRF'], '1', `${init.method} ${url}`);
    }
    if (init.body !== undefined) {
      assert.equal(init.headers['Content-Type'], 'application/json');
      const parsed = JSON.parse(init.body);
      assert.equal(typeof parsed, 'object');
      assert.ok(parsed && !Array.isArray(parsed));
    } else {
      assert.equal(init.headers['Content-Type'], undefined);
    }
  }
  const byPath = Object.fromEntries(calls.map(c => [`${c.init.method} ${c.url.replace('https://clips.revu.lol', '')}`, c.init]));
  assert.ok(byPath['GET /auth/web/me']);
  assert.ok(byPath['GET /auth/web/providers']);
  assert.ok(byPath['GET /auth/web/clips']);
  assert.deepEqual(JSON.parse(byPath['POST /auth/web/email/start'].body), { email: 'a@b.co', turnstileToken: 'tok' });
  assert.deepEqual(JSON.parse(byPath['POST /auth/web/email/verify'].body), { email: 'a@b.co', code: 'ABCDEFGH' });
  assert.equal(byPath['DELETE /auth/web/clips/Abc1234'].body, undefined);
  assert.equal(byPath['POST /auth/web/logout'].body, undefined);
  assert.equal(byPath['POST /auth/web/logout-all'].body, undefined);
});

test('deleteAccount posts {email} to /auth/web/account/delete', async () => {
  const { api, calls } = load();
  await api.deleteAccount('Me@Example.com');
  assert.equal(calls[0].url, 'https://clips.revu.lol/auth/web/account/delete');
  assert.equal(calls[0].init.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].init.body), { email: 'Me@Example.com' });
});

test('request returns parsed data, status and retryAfter', async () => {
  const { api } = load({
    respond: url => {
      if (url.endsWith('/email/start')) return response(429, { error: 'rate_limit_email', retry_after: 900 }, { 'Retry-After': '60' });
      if (url.endsWith('/providers')) return response(429, { error: 'rate_limit_auth' }, { 'Retry-After': '60' });
      return response(500, undefined);
    },
  });
  const start = await api.startEmail('a@b.co', 't');
  assert.deepEqual({ ok: start.ok, status: start.status, retryAfter: start.retryAfter }, { ok: false, status: 429, retryAfter: 900 });
  assert.equal(start.data.error, 'rate_limit_email');
  const providers = await api.providers();
  assert.equal(providers.retryAfter, 60);
  const broken = await api.listClips();
  assert.equal(broken.status, 500);
  assert.deepEqual({ ...broken.data }, {});
});

test('A network throw resolves to the network error', async () => {
  const { api } = load({ respond: () => { throw new TypeError('Failed to fetch'); } });
  const res = await api.me();
  assert.equal(res.ok, false);
  assert.equal(res.status, 0);
  assert.equal(res.data.error, 'network');
  assert.equal(api.errorCopy(res.data.error), "Couldn't reach Revu. Check your connection and try again.");
});

test('Hint: set by me and verify on 200, cleared by any 401, logout, logoutAll and deleteAccount', async () => {
  let status = 200;
  const { api, storage } = load({ respond: () => response(status, status === 200 ? { ok: true } : { error: 'signed_out' }) });
  await api.me();
  assert.equal(storage.map.get('revu.web.signedIn.v1'), '1');
  assert.equal(api.hasHint(), true);

  status = 401;
  await api.listClips();
  assert.equal(api.hasHint(), false);

  status = 200;
  await api.verifyEmail('a@b.co', 'X');
  assert.equal(api.hasHint(), true);
  await api.logout();
  assert.equal(api.hasHint(), false);

  api.setHint(true);
  await api.logoutAll();
  assert.equal(api.hasHint(), false);

  api.setHint(true);
  status = 400;
  await api.deleteAccount('wrong@b.co');
  assert.equal(api.hasHint(), true, 'a failed delete keeps the hint');
  status = 200;
  await api.deleteAccount('a@b.co');
  assert.equal(api.hasHint(), false);

  status = 200;
  await api.providers();
  await api.startEmail('a@b.co', 't');
  assert.equal(api.hasHint(), false, 'providers and start never set the hint');
});

test('The only storage writes are the hint, and they never hold an email or token', async () => {
  const storage = memoryStorage();
  const token = 'a'.repeat(48);
  const { api } = load({
    storage,
    respond: () => response(200, { ok: true, new_account: true, user: { email: 'me@example.com', created_at: 1 }, token }),
  });
  await api.verifyEmail('me@example.com', 'ABCDEFGH');
  await api.me();
  await api.listClips();
  api.setHint(true);
  assert.ok(storage.writes.length > 0);
  for (const [key, value] of storage.writes) {
    assert.equal(key, 'revu.web.signedIn.v1');
    assert.equal(value, '1');
    assert.doesNotMatch(value, /@/);
    assert.doesNotMatch(value, /[0-9a-f]{48}/);
  }
});

test('A throwing localStorage breaks nothing', async () => {
  const { api } = load({ storage: throwingStorage(), respond: () => response(200, { user: { email: 'a@b.co' } }) });
  assert.doesNotThrow(() => api.setHint(true));
  assert.doesNotThrow(() => api.setHint(false));
  assert.equal(api.hasHint(), false);
  const res = await api.me();
  assert.equal(res.ok, true);
  await api.logout();

  const context = {
    location: { hostname: 'revu.lol' },
    fetch: async () => response(401, { error: 'signed_out' }),
  };
  Object.defineProperty(context, 'localStorage', { get() { throw new Error('SecurityError'); } });
  vm.runInNewContext(source, context);
  assert.equal(context.RevuAccount.hasHint(), false);
  const signedOut = await context.RevuAccount.me();
  assert.equal(signedOut.status, 401);
});

test('safeNext accepts exactly /my-clips and /account', () => {
  const { api } = load();
  assert.equal(api.safeNext('/my-clips'), '/my-clips');
  assert.equal(api.safeNext('/account'), '/account');
  for (const bad of ['//evil.com', 'https://evil.com', '/sign-in', 'javascript:alert(1)', '/my-clips?x=1', '', null, undefined, '/account/', '/ACCOUNT']) {
    assert.equal(api.safeNext(bad), '/my-clips', String(bad));
  }
});

test('afterSignInUrl appends ?new=1 only for true', () => {
  const { api } = load();
  assert.equal(api.afterSignInUrl('/account', true), '/account?new=1');
  assert.equal(api.afterSignInUrl('/account', false), '/account');
  assert.equal(api.afterSignInUrl('/my-clips', 'true'), '/my-clips');
  assert.equal(api.afterSignInUrl('//evil.com', true), '/my-clips?new=1');
  assert.equal(api.afterSignInUrl(null, undefined), '/my-clips');
});

test('errorCopy returns the exact contract strings', () => {
  const { api } = load();
  const expected = {
    invalid_email: "That email address doesn't look valid.",
    turnstile_required: "We couldn't verify your browser. Try again.",
    turnstile_failed: "We couldn't verify your browser. Try again.",
    rate_limit_auth: 'Too many attempts. Wait a minute and try again.',
    email_send_failed: "Couldn't send the email right now. Try again in a moment.",
    email_daily_cap: 'Revu has sent all the sign-in emails it can for today. Try again after midnight UTC.',
    code_required: 'Enter the code from your email.',
    invalid_or_expired_code: 'That code is invalid or expired. Request a new one.',
    sign_in_unavailable: 'Sign in is unavailable right now. Try again later.',
    confirm_mismatch: "That email doesn't match this account.",
    signed_out: 'Your session ended. Sign in again.',
    session_expired: 'Your session ended. Sign in again.',
    network: "Couldn't reach Revu. Check your connection and try again.",
  };
  for (const [code, copy] of Object.entries(expected)) assert.equal(api.errorCopy(code), copy, code);
  for (const unknown of ['server_error', 'not_found', 'toString', '__proto__', undefined, '']) {
    assert.equal(api.errorCopy(unknown), 'Something went wrong. Try again.', String(unknown));
  }
  const throttled = n => `We just sent a code to this email. Check your inbox, or try again in ${n} min.`;
  assert.equal(api.errorCopy('rate_limit_email', 60), throttled(1));
  assert.equal(api.errorCopy('rate_limit_email', 61), throttled(2));
  assert.equal(api.errorCopy('rate_limit_email', 900), throttled(15));
  assert.equal(api.errorCopy('rate_limit_email', 5), throttled(1));
  assert.equal(api.errorCopy('rate_limit_email', 0), throttled(1));
  assert.equal(api.errorCopy('rate_limit_email'), throttled(1));
  for (const copy of Object.values(expected)) assert.doesNotMatch(copy, /[–—]/);
});

test('formatDuration', () => {
  const { api } = load();
  assert.equal(api.formatDuration(null), '');
  assert.equal(api.formatDuration(undefined), '');
  assert.equal(api.formatDuration('abc'), '');
  assert.equal(api.formatDuration(-1), '');
  assert.equal(api.formatDuration(0), '0:00');
  assert.equal(api.formatDuration(5), '0:05');
  assert.equal(api.formatDuration(65.9), '1:05');
  assert.equal(api.formatDuration(3599), '59:59');
  assert.equal(api.formatDuration(3600), '1:00:00');
  assert.equal(api.formatDuration(3725), '1:02:05');
});

test('formatViews', () => {
  const { api } = load();
  assert.equal(api.formatViews(0), '0 views');
  assert.equal(api.formatViews(1), '1 view');
  assert.equal(api.formatViews(2), '2 views');
  assert.equal(api.formatViews(null), '0 views');
});

test('formatTimeLeft follows clip.html rounding', () => {
  const { api } = load();
  const now = 1_000_000;
  assert.equal(api.formatTimeLeft(now, now), 'Expired');
  assert.equal(api.formatTimeLeft(now - 10, now), 'Expired');
  assert.equal(api.formatTimeLeft(null, now), 'Expired');
  assert.equal(api.formatTimeLeft(now + 1, now), 'Expires in 1 min');
  assert.equal(api.formatTimeLeft(now + 61, now), 'Expires in 2 min');
  assert.equal(api.formatTimeLeft(now + 3599, now), 'Expires in 60 min');
  assert.equal(api.formatTimeLeft(now + 3600, now), 'Expires in 1 hr');
  assert.equal(api.formatTimeLeft(now + 7199, now), 'Expires in 1 hr');
  assert.equal(api.formatTimeLeft(now + 86399, now), 'Expires in 23 hr');
  assert.equal(api.formatTimeLeft(now + 86400, now), 'Expires in 1 day');
  assert.equal(api.formatTimeLeft(now + 2 * 86400 + 5, now), 'Expires in 2 days');
  assert.equal(api.formatTimeLeft(now + 3 * 86400, now), 'Expires in 3 days');
});

test('formatMemberSince', () => {
  const { api } = load();
  const expected = new Date(1760054400 * 1000).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });
  assert.equal(api.formatMemberSince(1760054400), expected);
  assert.match(expected, /^[A-Z][a-z]+ \d{1,2}, \d{4}$/);
  assert.equal(api.formatMemberSince(null), '');
});

test('Clip ids and URLs', () => {
  const { api } = load();
  assert.equal(api.isClipId('Abc1234'), true);
  for (const bad of ['Abc123', 'Abc12345', 'Abc-123', '../abcd', null, 1234567]) assert.equal(api.isClipId(bad), false, String(bad));
  assert.equal(api.clipShareUrl({ id: 'Abc1234', url: 'https://clips.revu.lol/Abc1234' }), 'https://clips.revu.lol/Abc1234');
  assert.equal(api.clipShareUrl({ id: 'Abc1234', url: 'https://evil.example/Abc1234' }), 'https://clips.revu.lol/Abc1234');
  assert.equal(api.clipShareUrl({ id: 'Abc1234', url: 'javascript:alert(1)' }), 'https://clips.revu.lol/Abc1234');
  assert.equal(api.clipShareUrl({ id: 'Abc1234' }), 'https://clips.revu.lol/Abc1234');
  assert.equal(api.watchHref('Abc1234'), '/clip?id=Abc1234');
});

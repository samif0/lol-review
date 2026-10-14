import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { flush, memoryStorage, read, response, runPage } from './fake-dom.mjs';

const html = read('account.html');
const script = read('account.js');
const text = html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
const USER = { email: 'Me@Example.com', created_at: 1760054400 };
const HINT = 'revu.web.signedIn.v1';

function helpers() {
  const context = {};
  vm.runInNewContext(script, context);
  return context.RevuAccountPage;
}

function page({ search = '', me = response(200, { user: USER, identities: [], session: { expires_at: 2 } }), routes = {}, storage } = {}) {
  return runPage({ html, script, search, pathname: '/account', storage, routes: { 'GET /auth/web/me': me, ...routes } });
}

test('canConfirmDelete is case- and space-insensitive', () => {
  const { canConfirmDelete } = helpers();
  assert.equal(canConfirmDelete('me@example.com', 'Me@Example.com'), true);
  assert.equal(canConfirmDelete('  ME@EXAMPLE.COM ', 'me@example.com'), true);
  assert.equal(canConfirmDelete('me@example.co', 'me@example.com'), false);
  assert.equal(canConfirmDelete('', ''), false);
  assert.equal(canConfirmDelete(null, 'me@example.com'), false);
});

test('The page has all five cards and the agreed copy', () => {
  assert.match(html, /<title>Account - Revu<\/title>/);
  assert.match(html, /<meta name="robots" content="noindex" \/>/);
  assert.match(html, /<h1[^>]*>Account<\/h1>/);
  for (const heading of ['Signed in as', 'How you sign in', 'Desktop app', 'Sign out', 'Delete account']) {
    assert.match(html, new RegExp(`<h2[^>]*>${heading}</h2>`), heading);
  }
  assert.match(text, /Email code/);
  assert.match(text, /Revu emails you a one-time code each time you sign in\. There is no password to leak or reset\./);
  assert.match(text, /The Revu desktop app signs in with the same email code\. Sign in there with\s+and the clips you share show up in My clips\./);
  assert.match(html, /<a class="btn-secondary" href="\/my-clips">My clips<\/a>/);
  assert.match(html, /href="\/">Get the desktop app<\/a>/);
  assert.match(html, /<button id="sign-out" class="btn-secondary" type="button">Sign out<\/button>/);
  assert.match(html, /<button id="sign-out-all" class="btn-link"[^>]*>Sign out everywhere on the web<\/button>/);
  assert.match(text, /Signs out every browser\. The desktop app stays signed in\./);
  assert.match(text, /Deletes your Revu account, your shared clips and every sign-in, including the desktop app\. Clips saved on your PC are not affected\. This can't be undone\./);
  assert.match(text, /Type your email to confirm/);
  assert.match(html, /<input id="delete-confirm"[^>]*type="email"[^>]*autocomplete="off"/);
  assert.match(html, /<button id="delete-account" class="btn-danger"[^>]*disabled>Delete account<\/button>/);
  assert.match(text, /We created a new Revu account for\s+\.\s+If the desktop app uses a different email, sign out and sign in with that one\./);
});

test('No inline scripts, dashes or connected-account UI', () => {
  for (const src of ['account-api', 'account', 'header-account']) assert.match(html, new RegExp(`<script src="/${src}\\.js" defer></script>`));
  assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/);
  for (const tag of html.match(/<[^>]+>/g)) assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, tag);
  assert.doesNotMatch(html, /[–—]/);
  assert.doesNotMatch(script, /[–—]/);
  assert.doesNotMatch(html, /Connected accounts/i);
  const body = html.replace(/<footer[\s\S]*?<\/footer>/, '');
  assert.doesNotMatch(body, /Google|Riot/);
  assert.doesNotMatch(script, /innerHTML/);
});

test('Signed out visitors are sent to sign in', async () => {
  const fresh = page({ me: response(401, { error: 'signed_out' }) });
  await flush();
  assert.deepEqual(fresh.navigations, [['replace', '/sign-in?next=/account']]);
  const stale = page({ me: response(401, { error: 'signed_out' }), storage: memoryStorage({ [HINT]: '1' }) });
  await flush();
  assert.deepEqual(stale.navigations, [['replace', '/sign-in?next=/account&reason=session_expired']]);
});

test('Fields are filled from me, and ?new=1 shows the notice once', async () => {
  const run = page({ search: '?new=1' });
  await flush();
  assert.equal(run.$('account-cards').hidden, false);
  assert.equal(run.$('state-loading').hidden, true);
  for (const id of ['account-email', 'identity-email', 'desktop-email', 'new-account-email']) {
    assert.equal(run.$(id).textContent, 'Me@Example.com', id);
  }
  const date = new Date(USER.created_at * 1000).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });
  assert.equal(run.$('member-since').textContent, `Member since ${date}`);
  assert.equal(run.$('new-account-notice').hidden, false);
  assert.deepEqual(run.replaced, ['/account']);
  assert.equal(run.storage.map.get(HINT), '1');

  const plain = page();
  await flush();
  assert.equal(plain.$('new-account-notice').hidden, true);
});

test('Errors loading the account show retry', async () => {
  let ok = false;
  const run = page({ me: () => (ok ? response(200, { user: USER }) : response(500, { error: 'server_error' })) });
  await flush();
  assert.equal(run.$('state-error').hidden, false);
  ok = true;
  run.$('retry').click();
  await flush();
  assert.equal(run.$('account-cards').hidden, false);
});

test('A slow earlier load cannot overwrite a newer one', async () => {
  let releaseFirst;
  let calls = 0;
  const run = page({
    me: () => {
      calls += 1;
      if (calls === 1) return new Promise(resolve => { releaseFirst = () => resolve(response(500, { error: 'server_error' })); });
      return response(200, { user: USER });
    },
  });
  await flush();
  run.$('retry').click();
  await flush();
  assert.equal(run.$('account-cards').hidden, false);
  releaseFirst();
  await flush();
  assert.equal(run.$('account-cards').hidden, false);
  assert.equal(run.$('state-error').hidden, true);
});

for (const [id, path] of [['sign-out', '/auth/web/logout'], ['sign-out-all', '/auth/web/logout-all']]) {
  test(`${id} posts ${path}, clears the hint and goes home`, async () => {
    const run = page({ routes: { [`POST ${path}`]: response(200, { ok: true }) } });
    await flush();
    run.$(id).click();
    assert.equal(run.$('sign-out').disabled, true);
    assert.equal(run.$('sign-out-all').disabled, true);
    await flush();
    const call = run.requests.find(r => r.path === path);
    assert.equal(call.method, 'POST');
    assert.equal(call.body, undefined);
    assert.equal(call.init.headers['X-Revu-CSRF'], '1');
    assert.equal(run.storage.map.has(HINT), false);
    assert.deepEqual(run.navigations, [['assign', '/']]);
  });
}

test('Sign out shows an error when Revu cannot be reached', async () => {
  const run = page({ routes: { 'POST /auth/web/logout': new TypeError('Failed to fetch') } });
  await flush();
  run.$('sign-out').click();
  await flush();
  assert.equal(run.$('sign-out-error').textContent, "Couldn't reach Revu. Check your connection and try again.");
  assert.equal(run.$('sign-out').disabled, false);
  assert.deepEqual(run.navigations, []);
});

test('Delete account: gated on the typed email, mismatch copy, then success', async () => {
  let reply = response(400, { error: 'confirm_mismatch' });
  const run = page({ routes: { 'POST /auth/web/account/delete': () => reply } });
  await flush();
  const input = run.$('delete-confirm');
  const button = run.$('delete-account');
  assert.equal(button.disabled, true);

  input.value = 'someone@else.com';
  input.dispatch('input');
  assert.equal(button.disabled, true);
  run.$('delete-form').dispatch('submit');
  await flush();
  assert.equal(run.requests.filter(r => r.path === '/auth/web/account/delete').length, 0);

  input.value = '  me@example.COM ';
  input.dispatch('input');
  assert.equal(button.disabled, false);
  run.$('delete-form').dispatch('submit');
  assert.equal(button.textContent, 'Deleting...');
  assert.equal(button.disabled, true);
  await flush();
  assert.equal(run.$('delete-error').textContent, "That email doesn't match this account.");
  assert.equal(button.textContent, 'Delete account');
  assert.equal(button.disabled, false);
  const call = run.requests.find(r => r.path === '/auth/web/account/delete');
  assert.deepEqual(call.body, { email: 'me@example.COM' });
  assert.equal(call.init.headers['X-Revu-CSRF'], '1');
  assert.equal(call.init.headers['Content-Type'], 'application/json');
  assert.equal(run.storage.map.get(HINT), '1');

  reply = response(200, { ok: true });
  run.$('delete-form').dispatch('submit');
  await flush();
  assert.equal(run.storage.map.has(HINT), false);
  assert.deepEqual(run.navigations, [['assign', '/']]);
});

test('Delete account: a 401 redirects to sign in', async () => {
  const run = page({ routes: { 'POST /auth/web/account/delete': response(401, { error: 'signed_out' }) } });
  await flush();
  run.$('delete-confirm').value = 'me@example.com';
  run.$('delete-confirm').dispatch('input');
  run.$('delete-form').dispatch('submit');
  await flush();
  assert.deepEqual(run.navigations, [['replace', '/sign-in?next=/account&reason=session_expired']]);
});

import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { flush, memoryStorage, read, response, runPage, textOf, walk } from './fake-dom.mjs';

const html = read('my-clips.html');
const script = read('my-clips.js');
const text = html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
const NOW = 1_800_000_000;

function helpers() {
  const context = { location: { hostname: 'revu.lol' } };
  vm.runInNewContext(read('account-api.js'), context);
  vm.runInNewContext(script, context);
  return context.RevuMyClips;
}

function clip(overrides = {}) {
  return {
    id: 'Abc1234',
    url: 'https://clips.revu.lol/Abc1234',
    title: 'Baron steal',
    champion: 'Lee Sin',
    duration_s: 42,
    size_bytes: 1000,
    created_at: NOW - 3600,
    expires_at: NOW + 2 * 86400 + 60,
    view_count: 3,
    status: 'ready',
    narrated: true,
    has_transcript: true,
    ...overrides,
  };
}

test('cardModel for a ready clip', () => {
  const model = helpers().cardModel(clip(), NOW);
  assert.equal(model.id, 'Abc1234');
  assert.equal(model.title, 'Baron steal');
  assert.equal(model.champion, 'Lee Sin');
  assert.equal(model.length, '0:42');
  assert.deepEqual([...model.badges], ['Narrated', 'Transcript']);
  assert.equal(model.views, '3 views');
  assert.equal(model.ready, true);
  assert.equal(model.timeLeft, 'Expires in 2 days');
  assert.equal(model.expired, false);
  assert.equal(model.canWatch, true);
  assert.equal(model.canCopy, true);
  assert.equal(model.watchHref, '/clip?id=Abc1234');
  assert.equal(model.shareUrl, 'https://clips.revu.lol/Abc1234');
});

test('cardModel fallbacks', () => {
  const model = helpers().cardModel(clip({ title: '   ', champion: null, duration_s: null, view_count: 1, narrated: false, has_transcript: false }), NOW);
  assert.equal(model.title, 'Untitled clip');
  assert.equal(model.champion, '');
  assert.equal(model.length, '');
  assert.equal(model.views, '1 view');
  assert.deepEqual([...model.badges], []);
  assert.equal(helpers().cardModel(clip({ title: null }), NOW).title, 'Untitled clip');
  assert.equal(helpers().cardModel(clip({ title: '  Teamfight  ' }), NOW).title, 'Teamfight');
});

test('An uploading clip has no time left and no Watch or Copy', () => {
  const model = helpers().cardModel(clip({ status: 'uploading', expires_at: NOW + 600, narrated: false, has_transcript: false }), NOW);
  assert.deepEqual([...model.badges], ['Uploading']);
  assert.equal(model.ready, false);
  assert.equal(model.timeLeft, '');
  assert.equal(model.expired, false);
  assert.equal(model.canWatch, false);
  assert.equal(model.canCopy, false);
});

test('An expired clip says so and cannot be watched or copied', () => {
  const model = helpers().cardModel(clip({ expires_at: NOW }), NOW);
  assert.equal(model.expired, true);
  assert.equal(model.timeLeft, 'Expired');
  assert.equal(model.canWatch, false);
  assert.equal(model.canCopy, false);
});

test('A bad url is replaced by the canonical share URL, and bad ids are not usable', () => {
  const { cardModel } = helpers();
  assert.equal(cardModel(clip({ url: 'https://evil.example/x' }), NOW).shareUrl, 'https://clips.revu.lol/Abc1234');
  assert.equal(cardModel(clip({ url: 'https://clips.revu.lol/Abc1234/../x' }), NOW).shareUrl, 'https://clips.revu.lol/Abc1234');
  const bad = cardModel(clip({ id: '../../x' }), NOW);
  assert.equal(bad.canWatch, false);
  assert.equal(bad.canCopy, false);
});

test('deleteOutcome mapping', () => {
  const { deleteOutcome } = helpers();
  assert.equal(deleteOutcome(200), 'removed');
  assert.equal(deleteOutcome(404), 'removed');
  assert.equal(deleteOutcome(401), 'signed_out');
  for (const status of [0, 403, 429, 500]) assert.equal(deleteOutcome(status), 'error');
});

test('The page has the agreed copy and no inline scripts', () => {
  assert.match(html, /<meta name="robots" content="noindex" \/>/);
  assert.match(html, /<title>My clips - Revu<\/title>/);
  assert.match(html, /<h1[^>]*>My clips<\/h1>/);
  assert.match(text, /Shared clips and games stay up for 3 days, then Revu deletes them automatically\./);
  assert.match(text, /We created a new Revu account for\s+\.\s+If the desktop app uses a different email, sign out and sign in with that one\. Dismiss/);
  assert.match(html, /No shared clips for <span id="empty-email"><\/span> right now\./);
  assert.match(text, /Clips you share from the Revu desktop app while signed in with this email show up here for 3 days\./);
  assert.match(html, /<a class="btn-secondary" href="\/">Get the desktop app<\/a>/);
  assert.match(text, /Loading your clips\.\.\./);
  assert.match(text, /Couldn't load your clips\. Retry/);
  assert.match(html, /<ul id="clip-grid" class="clip-grid"/);
  assert.match(html, /class="visually-hidden" role="status"/);
  assert.match(html, /<a class="btn-secondary" href="\/account">Account<\/a>/);
  for (const src of ['account-api', 'my-clips', 'header-account']) assert.match(html, new RegExp(`<script src="/${src}\\.js" defer></script>`));
  assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/);
  for (const tag of html.match(/<[^>]+>/g)) assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, tag);
  assert.doesNotMatch(html, /[–—]/);
  assert.doesNotMatch(script, /[–—]/);
  assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML/);
});

function page({ search = '', me = response(200, { user: { email: 'me@example.com', created_at: 1 } }), clips = [clip()], routes = {}, storage, extra } = {}) {
  return runPage({
    html,
    script,
    search,
    pathname: '/my-clips',
    storage,
    extra,
    routes: {
      'GET /auth/web/me': me,
      'GET /auth/web/clips': Array.isArray(clips) ? response(200, { clips }) : clips,
      ...routes,
    },
  });
}

const cardsOf = run => run.$('clip-grid').children;
const buttonIn = (el, label) => walk(el).find(n => n.tagName === 'BUTTON' && n.textContent === label && !n.hidden);
const linkIn = (el, label) => walk(el).find(n => n.tagName === 'A' && n.textContent === label);

test('Signed out visitors are sent to sign in', async () => {
  const fresh = page({ me: response(401, { error: 'signed_out' }) });
  await flush();
  assert.deepEqual(fresh.navigations, [['replace', '/sign-in?next=/my-clips']]);

  const stale = page({ me: response(401, { error: 'signed_out' }), storage: memoryStorage({ 'revu.web.signedIn.v1': '1' }) });
  await flush();
  assert.deepEqual(stale.navigations, [['replace', '/sign-in?next=/my-clips&reason=session_expired']]);
  assert.equal(stale.storage.map.has('revu.web.signedIn.v1'), false);

  const clipsOnly = page({ clips: response(401, { error: 'signed_out' }) });
  await flush();
  assert.deepEqual(clipsOnly.navigations, [['replace', '/sign-in?next=/my-clips']]);
});

test('Server errors show the retry panel', async () => {
  let fail = true;
  const run = page({ clips: undefined, routes: { 'GET /auth/web/clips': () => (fail ? response(500, { error: 'server_error' }) : response(200, { clips: [] })) } });
  await flush();
  assert.equal(run.$('state-error').hidden, false);
  assert.equal(run.$('state-loading').hidden, true);
  fail = false;
  run.$('retry').click();
  await flush();
  assert.equal(run.$('state-error').hidden, true);
  assert.equal(run.$('state-empty').hidden, false);
  assert.equal(run.$('empty-email').textContent, 'me@example.com');
  assert.equal(run.$('clip-count').textContent, '0 clips');
});

test('Cards render with textContent, and the new-account notice clears the param', async () => {
  const hostile = '<img src=x onerror=alert(1)>';
  const run = page({
    search: '?new=1',
    clips: [clip({ title: hostile }), clip({ id: 'Upl0ad1', status: 'uploading', expires_at: Math.floor(Date.now() / 1000) + 600, narrated: false, has_transcript: false })],
  });
  await flush();
  assert.equal(run.$('signed-in-email').textContent, 'me@example.com');
  assert.equal(run.$('signed-in-line').hidden, false);
  assert.equal(run.$('new-account-notice').hidden, false);
  assert.equal(run.$('new-account-email').textContent, 'me@example.com');
  assert.deepEqual(run.replaced, ['/my-clips']);
  assert.equal(run.$('clip-count').textContent, '2 clips');
  assert.equal(run.$('clip-grid').hidden, false);

  const [ready, uploading] = cardsOf(run);
  assert.equal(ready.tagName, 'LI');
  assert.equal(ready.className, 'clip-card card');
  assert.equal(walk(ready).find(n => n.tagName === 'H2').textContent, hostile);
  const readyText = textOf(ready);
  assert.match(readyText, /Lee Sin/);
  assert.match(readyText, /0:42/);
  assert.match(readyText, /3 views/);
  assert.match(readyText, /Narrated Transcript/);
  assert.equal(walk(ready).filter(n => n.className === 'clip-pill').length, 2);
  assert.equal(linkIn(ready, 'Watch').href, '/clip?id=Abc1234');
  assert.ok(buttonIn(ready, 'Copy link'));
  assert.ok(buttonIn(ready, 'Delete'));

  assert.match(textOf(uploading), /Uploading/);
  assert.doesNotMatch(textOf(uploading), /Expires/);
  assert.equal(linkIn(uploading, 'Watch').hidden, true);
  assert.equal(buttonIn(uploading, 'Copy link'), undefined);
  assert.equal(buttonIn(uploading, 'Delete'), undefined);
  assert.equal(walk(uploading).find(n => n.className === 'clip-card-actions').hidden, true);

  run.$('new-account-dismiss').click();
  assert.equal(run.$('new-account-notice').hidden, true);
});

test('Without ?new=1 the notice stays hidden', async () => {
  const run = page();
  await flush();
  assert.equal(run.$('new-account-notice').hidden, true);
  assert.deepEqual(run.replaced, []);
});

test('Copy link: clipboard success and the manual fallback', async () => {
  const writes = [];
  const ok = page({ extra: { navigator: { clipboard: { writeText: async value => { writes.push(value); } } } } });
  await flush();
  const card = cardsOf(ok)[0];
  buttonIn(card, 'Copy link').click();
  await flush();
  assert.deepEqual(writes, ['https://clips.revu.lol/Abc1234']);
  assert.ok(buttonIn(card, 'Copied'));
  assert.equal(ok.$('live').textContent, 'Link copied.');
  const reset = ok.timers.find(t => t.ms === 2000);
  reset.fn();
  assert.ok(buttonIn(card, 'Copy link'));

  const denied = page({ extra: { navigator: { clipboard: { writeText: async () => { throw new Error('denied'); } } } } });
  await flush();
  const deniedCard = cardsOf(denied)[0];
  buttonIn(deniedCard, 'Copy link').click();
  await flush();
  const box = walk(deniedCard).find(n => n.className === 'clip-card-copy');
  assert.equal(box.hidden, false);
  assert.match(textOf(box), /Press Ctrl\+C to copy\./);
  const input = walk(box).find(n => n.tagName === 'INPUT');
  assert.equal(input.value, 'https://clips.revu.lol/Abc1234');
  assert.equal(input.readOnly, true);
  assert.equal(input.selected, true);

  const none = page({ extra: { navigator: {} } });
  await flush();
  buttonIn(cardsOf(none)[0], 'Copy link').click();
  await flush();
  assert.equal(walk(cardsOf(none)[0]).find(n => n.className === 'clip-card-copy').hidden, false);
});

test('Delete: confirm, cancel, error, then remove down to the empty state', async () => {
  let status = 500;
  const run = page({
    clips: [clip(), clip({ id: 'Second1', title: 'Second' })],
    routes: { 'DELETE /auth/web/clips/Abc1234': () => response(status, status === 200 ? { ok: true } : { error: 'server_error' }), 'DELETE /auth/web/clips/Second1': response(404, { error: 'not_found' }) },
  });
  await flush();
  const card = cardsOf(run)[0];
  buttonIn(card, 'Delete').click();
  const confirm = walk(card).find(n => n.className === 'clip-card-confirm');
  assert.equal(confirm.hidden, false);
  assert.match(textOf(confirm), /Delete this shared clip\? The link stops working for everyone\. The clip stays on your PC\./);
  assert.equal(buttonIn(confirm, 'Delete clip').className, 'btn-danger');

  buttonIn(confirm, 'Cancel').click();
  assert.equal(confirm.hidden, true);
  assert.equal(run.requests.filter(r => r.method === 'DELETE').length, 0);

  buttonIn(card, 'Delete').click();
  const confirmButton = buttonIn(confirm, 'Delete clip');
  confirmButton.click();
  assert.equal(confirmButton.textContent, 'Deleting...');
  assert.equal(confirmButton.disabled, true);
  await flush();
  assert.equal(confirmButton.disabled, false);
  assert.match(textOf(card), /Couldn't delete this clip\. Try again\./);
  const del = run.requests.find(r => r.method === 'DELETE');
  assert.equal(del.path, '/auth/web/clips/Abc1234');
  assert.equal(del.init.headers['X-Revu-CSRF'], '1');
  assert.equal(del.body, undefined);

  status = 200;
  confirmButton.click();
  await flush();
  assert.equal(cardsOf(run).length, 1);
  assert.equal(run.$('live').textContent, 'Clip deleted.');
  assert.equal(run.$('clip-count').textContent, '1 clip');

  const second = cardsOf(run)[0];
  buttonIn(second, 'Delete').click();
  buttonIn(second, 'Delete clip').click();
  await flush();
  assert.equal(cardsOf(run).length, 0);
  assert.equal(run.$('clip-count').textContent, '0 clips');
  assert.equal(run.$('state-empty').hidden, false);
  assert.equal(run.$('clip-grid').hidden, true);
});

test('A 401 on delete sends the user to sign in with session_expired', async () => {
  const run = page({ routes: { 'DELETE /auth/web/clips/Abc1234': response(401, { error: 'signed_out' }) } });
  await flush();
  const card = cardsOf(run)[0];
  buttonIn(card, 'Delete').click();
  buttonIn(card, 'Delete clip').click();
  await flush();
  assert.deepEqual(run.navigations, [['replace', '/sign-in?next=/my-clips&reason=session_expired']]);
});

test('Time left is recomputed when the tab becomes visible', async () => {
  const realNow = Date.now;
  const start = 1_900_000_000_000;
  Date.now = () => start;
  try {
    // The page runs in its own realm, so hand it this realm's Date to steer.
    const run = page({ clips: [clip({ expires_at: start / 1000 + 7200 })], extra: { Date } });
    await flush();
    const card = cardsOf(run)[0];
    assert.match(textOf(card), /Expires in 2 hr/);
    assert.equal(run.timers.length, 0, 'no interval or timer while idle');

    Date.now = () => start + 7200 * 1000;
    run.document.visibilityState = 'hidden';
    run.document.dispatch('visibilitychange');
    assert.match(textOf(card), /Expires in 2 hr/);

    run.document.visibilityState = 'visible';
    run.document.dispatch('visibilitychange');
    assert.match(textOf(card), /Expired/);
    const actions = walk(card).find(n => n.className === 'clip-card-actions');
    assert.equal(actions.hidden, true);
  } finally {
    Date.now = realNow;
  }
});

function game(overrides = {}) {
  return clip({
    id: 'Game123', kind: 'vod', url: 'https://revu.lol/v/Game123', title: 'Jinx vs Caitlyn', champion: 'Jinx',
    duration_s: 1834, view_count: 0, narrated: false, has_transcript: false, quality: '1080p', has_notes: true,
    ...overrides,
  });
}

test('cardModel for a shared full game', () => {
  const { cardModel, countText } = helpers();
  const model = cardModel(game(), NOW);
  assert.equal(model.kind, 'vod');
  assert.equal(model.title, 'Jinx vs Caitlyn');
  assert.equal(model.champion, '', 'the title already names the champion');
  assert.equal(model.length, '30:34');
  assert.deepEqual([...model.badges], ['Full game', '1080p', 'Includes notes']);
  assert.equal(model.views, '', 'games carry no view count');
  assert.equal(model.watchHref, 'https://revu.lol/v/Game123');
  assert.equal(model.shareUrl, 'https://revu.lol/v/Game123');
  assert.equal(model.remove.action, 'Stop sharing');
  assert.equal(model.remove.confirm, 'Stop sharing this game?');
  assert.equal(model.remove.confirmAction, 'Stop sharing');
  const plain = cardModel(game({ title: null, champion: 'Jinx', quality: '720p', has_notes: false }), NOW);
  assert.equal(plain.title, 'Full game');
  assert.equal(plain.champion, 'Jinx');
  assert.deepEqual([...plain.badges], ['Full game', '720p']);
  // Shares from 4.3 carry the frame rate with the resolution.
  assert.deepEqual([...cardModel(game({ quality: '1080p60', has_notes: false }), NOW).badges], ['Full game', '1080p60']);
  assert.deepEqual([...cardModel(game({ quality: '720p30', has_notes: false }), NOW).badges], ['Full game', '720p30']);
  assert.deepEqual([...cardModel(game({ status: 'uploading', quality: 'huge' }), NOW).badges], ['Full game', 'Includes notes', 'Uploading']);
  assert.deepEqual([...cardModel(game({ quality: '1080p144', has_notes: false }), NOW).badges], ['Full game']);
  // A bad url falls back to the canonical watch link; a clip url never passes for a game.
  for (const url of ['https://evil.example/v/Game123', 'https://clips.revu.lol/Game123', 'http://localhost:8788/v/Game123', 'javascript:alert(1)']) {
    assert.equal(cardModel(game({ url }), NOW).shareUrl, 'https://revu.lol/v/Game123', url);
  }
  assert.equal(cardModel(clip({ kind: 'clip', quality: null, has_notes: false }), NOW).remove.action, 'Delete');
  assert.equal(countText([{ kind: 'clip' }, { kind: 'clip' }]), '2 clips');
  assert.equal(countText([{ kind: 'clip' }, { kind: 'vod' }]), '1 clip, 1 game');
  assert.equal(countText([{ kind: 'vod' }, { kind: 'vod' }]), '2 games');
  assert.equal(countText([]), '0 clips');
});

test('a local page accepts a loopback game link from wrangler pages dev', () => {
  const context = { location: { hostname: 'localhost' } };
  vm.runInNewContext(read('account-api.js'), context);
  vm.runInNewContext(script, context);
  const { cardModel } = context.RevuMyClips;
  assert.equal(cardModel(game({ url: 'http://localhost:8788/v/Game123' }), NOW).shareUrl, 'http://localhost:8788/v/Game123');
});

test('Game cards: badges, Watch opens the game link, and Stop sharing confirms then deletes', async () => {
  const run = page({
    clips: [clip(), game()],
    routes: { 'DELETE /auth/web/clips/Game123': response(200, { ok: true }) },
  });
  await flush();
  assert.equal(run.$('clip-count').textContent, '1 clip, 1 game');
  const card = cardsOf(run)[1];
  const cardText = textOf(card);
  assert.match(cardText, /Jinx vs Caitlyn/);
  assert.match(cardText, /Full game 1080p Includes notes/);
  assert.match(cardText, /30:34/);
  assert.doesNotMatch(cardText, /views?\b/);
  assert.equal(linkIn(card, 'Watch').href, 'https://revu.lol/v/Game123');
  assert.equal(buttonIn(card, 'Delete'), undefined);
  buttonIn(card, 'Stop sharing').click();
  const confirm = walk(card).find(n => n.className === 'clip-card-confirm');
  assert.equal(confirm.hidden, false);
  assert.match(textOf(confirm), /^Stop sharing this game\? Stop sharing Cancel$/);
  buttonIn(confirm, 'Stop sharing').click();
  assert.equal(walk(confirm).find(n => n.tagName === 'BUTTON' && n.className === 'btn-danger').textContent, 'Stopping...');
  await flush();
  assert.deepEqual(run.requests.filter(r => r.method === 'DELETE').map(r => r.path), ['/auth/web/clips/Game123']);
  assert.equal(run.$('live').textContent, 'Stopped sharing this game.');
  assert.equal(run.$('clip-count').textContent, '1 clip');
  assert.equal(cardsOf(run).length, 1);
});

test('A failed Stop sharing keeps the game and says so', async () => {
  const run = page({ clips: [game()], routes: { 'DELETE /auth/web/clips/Game123': response(500, { error: 'server_error' }) } });
  await flush();
  const card = cardsOf(run)[0];
  buttonIn(card, 'Stop sharing').click();
  const confirm = walk(card).find(n => n.className === 'clip-card-confirm');
  buttonIn(confirm, 'Stop sharing').click();
  await flush();
  assert.match(textOf(card), /Couldn't stop sharing this game\. Try again\./);
  assert.equal(buttonIn(confirm, 'Stop sharing').disabled, false);
  assert.equal(run.$('clip-count').textContent, '1 game');
});

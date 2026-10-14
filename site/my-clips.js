/*
 * My clips page (my-clips.html): the signed-in user's shared clips and games, with
 * watch, copy link and delete. Everything is built with createElement and
 * textContent; clip fields from the server are never parsed as HTML.
 *
 * cardModel and deleteOutcome are pure and exported for tests. DOM wiring
 * only runs in a browser.
 */
(function (root) {
  'use strict';

  var api = root.RevuAccount;

  // Shared full games (kind 'vod') sit in the same list. They play on revu.lol/v/<id>,
  // carry no view count, and "Stop sharing" uses the same delete as a clip.
  function cardModel(clip, nowSec) {
    var vod = clip.kind === 'vod';
    var ready = clip.status === 'ready';
    var expired = ready && !(Number(clip.expires_at) > nowSec);
    var usable = ready && !expired && api.isClipId(clip.id);
    var title = typeof clip.title === 'string' ? clip.title.trim() : '';
    var champion = typeof clip.champion === 'string' ? clip.champion.trim() : '';
    var badges = [];
    if (vod) {
      badges.push('Full game');
      if (clip.quality === '1080p' || clip.quality === '720p') badges.push(clip.quality);
      if (clip.has_notes === true) badges.push('Includes notes');
    } else {
      if (clip.narrated === true) badges.push('Narrated');
      if (clip.has_transcript === true) badges.push('Transcript');
    }
    if (!ready) badges.push('Uploading');
    var shareUrl = vod ? api.vodShareUrl(clip) : api.clipShareUrl(clip);
    return {
      id: clip.id,
      kind: vod ? 'vod' : 'clip',
      title: title || (vod ? 'Full game' : 'Untitled clip'),
      // A game's title already names its champion ("Jinx vs Caitlyn").
      champion: vod && title ? '' : champion,
      length: api.formatDuration(clip.duration_s),
      badges: badges,
      views: vod ? '' : api.formatViews(clip.view_count),
      ready: ready,
      timeLeft: ready ? api.formatTimeLeft(clip.expires_at, nowSec) : '',
      expired: expired,
      canWatch: usable,
      canCopy: usable,
      watchHref: vod ? shareUrl : api.watchHref(clip.id),
      shareUrl: shareUrl,
      remove: vod ? VOD_REMOVE : CLIP_REMOVE
    };
  }

  var CLIP_REMOVE = {
    action: 'Delete', confirm: 'Delete this shared clip? The link stops working for everyone. The clip stays on your PC.',
    confirmAction: 'Delete clip', busy: 'Deleting...', done: 'Clip deleted.', failed: "Couldn't delete this clip. Try again."
  };
  var VOD_REMOVE = {
    action: 'Stop sharing', confirm: 'Stop sharing this game?', confirmAction: 'Stop sharing', busy: 'Stopping...',
    done: 'Stopped sharing this game.', failed: "Couldn't stop sharing this game. Try again."
  };

  function countText(models) {
    var games = models.filter(function (m) { return m.kind === 'vod'; }).length;
    var clips = models.length - games;
    var clipText = clips === 1 ? '1 clip' : clips + ' clips';
    if (!games) return clipText;
    var gameText = games === 1 ? '1 game' : games + ' games';
    return clips ? clipText + ', ' + gameText : gameText;
  }

  function deleteOutcome(status) {
    if (status === 200 || status === 404) return 'removed';
    if (status === 401) return 'signed_out';
    return 'error';
  }

  root.RevuMyClips = { cardModel: cardModel, deleteOutcome: deleteOutcome, countText: countText };

  if (typeof document === 'undefined') return;

  var params = new URLSearchParams(location.search);
  var hadHint = api.hasHint();
  var cards = [];
  // Only the newest load may update the page (Retry can overlap a slow one).
  var loadSeq = 0;
  var title = document.getElementById('page-title');
  var countPill = document.getElementById('clip-count');
  var signedInLine = document.getElementById('signed-in-line');
  var signedInEmail = document.getElementById('signed-in-email');
  var notice = document.getElementById('new-account-notice');
  var noticeEmail = document.getElementById('new-account-email');
  var dismiss = document.getElementById('new-account-dismiss');
  var emptyEmail = document.getElementById('empty-email');
  var panels = {
    loading: document.getElementById('state-loading'),
    empty: document.getElementById('state-empty'),
    error: document.getElementById('state-error'),
    list: document.getElementById('clip-grid')
  };
  var grid = panels.list;
  var live = document.getElementById('live');
  var retry = document.getElementById('retry');

  function nowSec() { return Math.floor(Date.now() / 1000); }

  function announce(text) {
    live.textContent = '';
    live.textContent = text;
  }

  function show(name) {
    Object.keys(panels).forEach(function (key) { panels[key].hidden = key !== name; });
  }

  function signIn() {
    location.replace(hadHint ? '/sign-in?next=/my-clips&reason=session_expired' : '/sign-in?next=/my-clips');
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function button(className, text) {
    var node = el('button', className, text);
    node.type = 'button';
    return node;
  }

  function updateCount() {
    countPill.textContent = countText(cards);
    countPill.hidden = false;
    show(cards.length === 0 ? 'empty' : 'list');
  }

  function buildCard(clip) {
    var model = cardModel(clip, nowSec());
    var li = el('li', 'clip-card card');
    li.appendChild(el('h2', null, model.title));
    if (model.champion) li.appendChild(el('p', 'clip-card-champion', model.champion));

    var meta = el('p', 'clip-card-meta');
    if (model.length) meta.appendChild(el('span', null, model.length));
    if (model.views) meta.appendChild(el('span', null, model.views));
    var timeLeft = el('span');
    meta.appendChild(timeLeft);
    li.appendChild(meta);

    var badges = el('div', 'clip-card-badges');
    model.badges.forEach(function (badge) { badges.appendChild(el('span', 'clip-pill', badge)); });
    li.appendChild(badges);

    var actions = el('div', 'clip-card-actions');
    var watch = el('a', 'btn-secondary', 'Watch');
    watch.href = model.watchHref;
    var copy = button('btn-secondary', 'Copy link');
    var del = button('btn-danger', model.remove.action);
    actions.appendChild(watch);
    actions.appendChild(copy);
    actions.appendChild(del);
    li.appendChild(actions);

    var copyBox = el('div', 'clip-card-copy');
    copyBox.hidden = true;
    var copyInput = el('input');
    copyInput.type = 'text';
    copyInput.readOnly = true;
    copyInput.spellcheck = false;
    copyInput.setAttribute('aria-label', model.kind === 'vod' ? 'Game link' : 'Clip link');
    copyBox.appendChild(el('span', null, 'Press Ctrl+C to copy.'));
    copyBox.appendChild(copyInput);
    li.appendChild(copyBox);

    var confirm = el('div', 'clip-card-confirm');
    confirm.hidden = true;
    confirm.appendChild(el('p', null, model.remove.confirm));
    var confirmRow = el('div', 'clip-card-actions');
    var confirmDelete = button('btn-danger', model.remove.confirmAction);
    var cancel = button('btn-secondary', 'Cancel');
    confirmRow.appendChild(confirmDelete);
    confirmRow.appendChild(cancel);
    confirm.appendChild(confirmRow);
    li.appendChild(confirm);

    var error = el('p', 'clip-card-error');
    li.appendChild(error);

    var copyTimer = null;
    var card = { li: li, update: update, kind: model.kind };

    function update(now) {
      var m = cardModel(clip, now);
      timeLeft.textContent = m.timeLeft;
      timeLeft.hidden = !m.timeLeft;
      // Uploading and expired rows get no actions at all: Delete waits until
      // the desktop finishes the upload, like Watch and Copy link.
      watch.hidden = !m.canWatch;
      copy.hidden = !m.canCopy;
      del.hidden = !m.canWatch;
      if (!m.canWatch) {
        actions.hidden = true;
        confirm.hidden = true;
        copyBox.hidden = true;
      }
    }

    copy.addEventListener('click', function () {
      var url = cardModel(clip, nowSec()).shareUrl;
      var clipboard = root.navigator && root.navigator.clipboard;
      var write = clipboard && typeof clipboard.writeText === 'function'
        ? Promise.resolve().then(function () { return clipboard.writeText(url); })
        : Promise.reject(new Error('clipboard_unavailable'));
      write.then(function () {
        copyBox.hidden = true;
        copy.textContent = 'Copied';
        announce('Link copied.');
        clearTimeout(copyTimer);
        copyTimer = setTimeout(function () { copy.textContent = 'Copy link'; }, 2000);
      }, function () {
        copyInput.value = url;
        copyBox.hidden = false;
        copyInput.focus();
        copyInput.select();
      });
    });

    del.addEventListener('click', function () {
      error.textContent = '';
      actions.hidden = true;
      copyBox.hidden = true;
      confirm.hidden = false;
      cancel.focus();
    });

    cancel.addEventListener('click', function () {
      confirm.hidden = true;
      actions.hidden = false;
      update(nowSec());
      del.focus();
    });

    confirmDelete.addEventListener('click', function () {
      confirmDelete.disabled = true;
      cancel.disabled = true;
      confirmDelete.textContent = model.remove.busy;
      error.textContent = '';
      api.deleteClip(clip.id).then(function (res) {
        var outcome = deleteOutcome(res.status);
        if (outcome === 'removed') {
          li.remove();
          cards = cards.filter(function (c) { return c !== card; });
          announce(model.remove.done);
          updateCount();
          title.focus();
          return;
        }
        if (outcome === 'signed_out') {
          signIn();
          return;
        }
        confirmDelete.disabled = false;
        cancel.disabled = false;
        confirmDelete.textContent = model.remove.confirmAction;
        error.textContent = model.remove.failed;
      });
    });

    update(nowSec());
    return card;
  }

  function render(clips) {
    while (grid.firstChild) grid.removeChild(grid.firstChild);
    cards = clips.map(buildCard);
    cards.forEach(function (card) { grid.appendChild(card.li); });
    updateCount();
  }

  function showNewAccount(email) {
    if (params.get('new') !== '1') return;
    noticeEmail.textContent = email;
    notice.hidden = false;
    params.delete('new');
    try { history.replaceState(null, '', '/my-clips'); } catch (e) { /* keep the param */ }
  }

  function load() {
    var seq = ++loadSeq;
    show('loading');
    Promise.all([api.me(), api.listClips()]).then(function (results) {
      if (seq !== loadSeq) return;
      var me = results[0];
      var list = results[1];
      if (me.status === 401 || list.status === 401) {
        signIn();
        return;
      }
      var user = me.ok && me.data.user;
      if (!user || !list.ok) {
        show('error');
        return;
      }
      // A 200 from me means a live session, so a later 401 means it ended.
      hadHint = true;
      var email = typeof user.email === 'string' ? user.email : '';
      signedInEmail.textContent = email;
      signedInLine.hidden = !email;
      emptyEmail.textContent = email;
      showNewAccount(email);
      render(Array.isArray(list.data.clips) ? list.data.clips : []);
    });
  }

  dismiss.addEventListener('click', function () { notice.hidden = true; });
  retry.addEventListener('click', load);
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState !== 'visible') return;
    var now = nowSec();
    cards.forEach(function (card) { card.update(now); });
  });

  load();
})(globalThis);

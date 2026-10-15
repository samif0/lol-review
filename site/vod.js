/*
 * Shared full game watch page (vod.html, served at /v/<id> through _redirects): the page
 * itself and the views it shares. vod-lib.js (RevuVod) holds the pure helpers, vod-player.js
 * (RevuVodPlayer) the HLS engines and play tokens, vod-live.js (RevuVodLive) the controller
 * for a game that plays while it uploads; all three load first.
 *
 * /vod-meta/<id> gives the expiry, quality, format and the sharer's markers document.
 *   - An MP4 share (no format) streams from /vod-file/<id> once its upload is complete.
 *     Until then the page shows "Still uploading, N%" and polls (every 4 s for a minute,
 *     then 10 s, 30 s while the upload has stalled). The player has preload="none", so a
 *     play counts only when the viewer presses play.
 *   - An HLS share (format "hls") goes to RevuVodLive: it plays from 0:00 while the rest
 *     uploads, and the moments past the uploaded edge wait for it.
 * Polling never runs while the tab is hidden, and asks again as soon as it is shown.
 * Marker and note times are GAME seconds; the lane maps them to video time.
 * Everything the server sends is set with textContent, never parsed as HTML.
 */
(function (root) {
  'use strict';

  if (typeof document === 'undefined') return;

  var V = root.RevuVod;
  var id = V.parseId(location.pathname, location.search);
  var API = V.apiBase(location.hostname, location.search);
  var $ = function (name) { return document.getElementById(name); };
  var panels = { loading: $('state-loading'), missing: $('state-missing'), uploading: $('state-uploading'), game: $('state-game') };
  var video = $('vod-video');
  var playerMsg = $('vod-player-msg');
  var retryButton = $('vod-retry');
  var banner = $('vod-expiry');
  var lane = $('vod-lane');
  var track = $('vod-lane-track');
  var uploaded = $('vod-lane-uploaded');
  var playhead = $('vod-lane-playhead');
  var notesPanel = $('vod-notes');
  var notesList = $('vod-notes-list');
  var meta = null;
  var live = null;
  var expiryTimer = null;
  var rechecking = false;
  var pollTimer = null;
  var pollStart = 0;
  var pollStalled = false;
  var pollDue = false;
  var onRetry = null;
  var laneLength = 0;
  var marks = [];
  var noteEntries = [];

  function show(name) {
    Object.keys(panels).forEach(function (key) { panels[key].hidden = key !== name; });
  }

  function stopPlayback() {
    try { video.pause(); } catch (e) { /* nothing is playing */ }
    try { video.removeAttribute('src'); video.load(); } catch (e) { /* the element is already empty */ }
  }

  function stopExpiry() {
    if (expiryTimer) clearInterval(expiryTimer);
    expiryTimer = null;
  }

  function showState(name) {
    var state = V.STATES[name] || V.STATES.missing;
    $('state-title').textContent = state.title;
    $('state-copy').textContent = state.copy;
    stopExpiry();
    stopPolling();
    if (live) live.stop();
    else if (meta) stopPlayback();
    show('missing');
  }

  // Polling.

  function stopPolling() {
    if (pollTimer) clearTimeout(pollTimer);
    pollTimer = null;
    pollDue = false;
  }

  // One poll at a time. A hidden tab stops asking, and asks again as soon as it is shown.
  function schedulePoll(delay) {
    if (pollTimer) clearTimeout(pollTimer);
    pollDue = false;
    pollTimer = setTimeout(function () {
      pollTimer = null;
      if (document.hidden) { pollDue = true; return; }
      poll();
    }, delay);
  }

  function poll() {
    pollDue = false;
    fetchMeta().then(route, function (error) {
      if (live) { live.pollFailed(error); return; }
      // Gone (stopped, discarded or past its upload window): say so. Anything else may pass.
      if (error && error.status === 404) showState('missing');
      else schedulePoll(V.pollDelay(Date.now() - pollStart, pollStalled));
    });
  }

  document.addEventListener('visibilitychange', function () {
    if (!document.hidden && pollDue) poll();
  });

  function fetchMeta() {
    return fetch(API + '/vod-meta/' + encodeURIComponent(id), { cache: 'no-store' }).then(function (res) {
      if (!res.ok) {
        var error = new Error('vod-meta ' + res.status);
        error.status = res.status;
        throw error;
      }
      return res.json();
    });
  }

  // Every meta reply lands here: an HLS share goes to its controller, an MP4 upload keeps
  // waiting, anything else renders.
  function route(fresh) {
    if (V.isHls(fresh) || live) {
      if (!live) live = root.RevuVodLive.create(livePage());
      live.update(fresh);
      return;
    }
    if (V.metaState(fresh) !== 'uploading') {
      stopPolling();
      render(fresh);
      return;
    }
    if (!pollStart) pollStart = Date.now();
    pollStalled = fresh.stalled === true;
    var words = V.uploadText(fresh);
    waiting({ game: V.uploadGame(fresh), title: words.title, copy: words.copy, percent: V.uploadPercent(fresh) });
    schedulePoll(V.pollDelay(Date.now() - pollStart, pollStalled));
  }

  // Views.

  // The waiting page: the game's name once the sharer's app has sent it, a title, the copy
  // (a live region) and, when percent is a number, the upload meter.
  function waiting(view) {
    $('upload-game-title').textContent = view.game ? view.game.title : '';
    $('upload-game-meta').textContent = view.game ? view.game.meta : '';
    $('upload-game-meta').hidden = !(view.game && view.game.meta);
    $('upload-game').hidden = !view.game;
    $('upload-title').textContent = view.title;
    // The copy is a live region: rewrite it only when it says something new.
    if ($('upload-copy').textContent !== view.copy) $('upload-copy').textContent = view.copy;
    var meter = $('upload-meter');
    meter.hidden = typeof view.percent !== 'number';
    if (typeof view.percent === 'number') {
      $('upload-bar').style.width = view.percent + '%';
      meter.setAttribute('aria-valuenow', String(view.percent));
    }
    document.title = view.title + ' - Revu';
    show('uploading');
  }

  function playerMessage(message, retry) {
    playerMsg.textContent = message;
    playerMsg.hidden = !message;
    onRetry = message && retry ? retry : null;
    retryButton.hidden = !onRetry;
    panels.game.classList.toggle('is-blocked', !!message);
  }

  retryButton.addEventListener('click', function () { if (onRetry) onRetry(); });

  function gameTitle() {
    return V.titleOf(V.gameOf(meta));
  }

  function renderHeader() {
    var game = V.gameOf(meta);
    $('vod-title').textContent = V.titleOf(game);
    var parts = V.metaParts(game);
    var line = $('vod-meta');
    line.textContent = parts.join(' · ');
    line.hidden = parts.length === 0;
    var quality = $('vod-quality');
    // "1080p60" once the sharer's app sends the frame rate; older shares say "1080p".
    quality.textContent = /^(1080|720)p(30|60)?$/.test(meta.quality) ? meta.quality : '';
    quality.hidden = !quality.textContent;
    $('vod-id').textContent = id;
  }

  function renderExpiry() {
    var line = V.expiryText(meta.expires_at, Date.now());
    if (!line) { showState('missing'); return; }
    banner.textContent = line;
    banner.hidden = false;
  }

  // The game page: header, title, the expiry banner and its timer. False once the link has expired.
  function renderGame(fresh) {
    meta = fresh;
    renderHeader();
    document.title = gameTitle() + ' - Revu';
    show('game');
    stopExpiry();
    renderExpiry();
    if (panels.missing.hidden === false) return false;
    expiryTimer = setInterval(renderExpiry, 60000);
    return true;
  }

  // A moment past the uploaded edge stays a button: pressing it arms "jump there when it
  // arrives" (aria-pressed), and its name says when it should arrive.
  function markMoment(button, item, label) {
    var suffix = V.pendingSuffix(item);
    button.classList.toggle('is-pending', !!item.pending);
    button.classList.toggle('is-armed', !!item.armed);
    if (item.pending) button.setAttribute('aria-pressed', item.armed ? 'true' : 'false');
    else button.removeAttribute('aria-pressed');
    if (label) button.setAttribute('aria-label', label + suffix);
    return suffix;
  }

  // Ticks are rebuilt only when the moments change; otherwise they update in place, so
  // keyboard focus survives every upload update.
  function drawLane(items, choose) {
    var same = items.length === marks.length && items.every(function (item, i) { return item.vt === marks[i].item.vt && item.aria === marks[i].item.aria; });
    if (!same) {
      while (track.firstChild) track.removeChild(track.firstChild);
      track.appendChild(uploaded);
      track.appendChild(playhead);
      marks = items.map(function (item) {
        var entry = { button: document.createElement('button'), item: item, choose: choose };
        entry.button.type = 'button';
        entry.button.className = 'vod-mark' + (item.tone ? ' is-' + item.tone : '');
        entry.button.addEventListener('click', function () { entry.choose(entry.item); });
        track.appendChild(entry.button);
        return entry;
      });
    }
    marks.forEach(function (entry, i) {
      var item = items[i];
      entry.item = item;
      entry.choose = choose;
      entry.button.style.left = item.left + '%';
      var suffix = markMoment(entry.button, item, item.aria);
      entry.button.title = item.title + (suffix ? ' (' + suffix.slice(2) + ')' : '');
    });
  }

  function drawNotes(rows, choose) {
    var same = rows.length === noteEntries.length && rows.every(function (row, i) { return row.vt === noteEntries[i].row.vt && row.text === noteEntries[i].row.text; });
    if (!same) {
      while (notesList.firstChild) notesList.removeChild(notesList.firstChild);
      noteEntries = rows.map(function (row) {
        var li = document.createElement('li');
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'vod-note';
        var time = document.createElement('span');
        time.className = 'vod-note-time';
        time.textContent = row.time;
        var kind = document.createElement('span');
        kind.className = 'vod-note-kind';
        kind.textContent = row.kind;
        button.appendChild(time);
        button.appendChild(kind);
        if (row.text) {
          var body = document.createElement('span');
          body.className = 'vod-note-text';
          body.textContent = row.text;
          button.appendChild(body);
        }
        var entry = { button: button, row: row, choose: choose, suffix: null };
        button.addEventListener('click', function () { entry.choose(entry.row); });
        li.appendChild(button);
        notesList.appendChild(li);
        return entry;
      });
    }
    noteEntries.forEach(function (entry, i) {
      entry.row = rows[i];
      entry.choose = choose;
      // A note keeps its text as its name; a pending one gets a visually hidden suffix.
      var suffix = markMoment(entry.button, entry.row, '');
      if (suffix && !entry.suffix) {
        entry.suffix = document.createElement('span');
        entry.suffix.className = 'visually-hidden';
        entry.button.appendChild(entry.suffix);
      }
      if (entry.suffix) entry.suffix.textContent = suffix;
      entry.button.title = suffix ? entry.row.time + ' ' + entry.row.kind + ' (' + suffix.slice(2) + ')' : '';
    });
    notesPanel.hidden = rows.length === 0;
    panels.game.classList.toggle('has-notes', rows.length > 0);
  }

  // The lane and the notes. While an HLS game uploads, `live` is { edge, rate, armed } and the
  // lane spans `length`, the whole game, even without moments (its fill shows the upload).
  function drawMoments(o) {
    var doc = meta && meta.meta;
    laneLength = Number(o.length) || 0;
    var items = V.laneItems(doc, laneLength, o.live);
    drawLane(items, o.choose);
    drawNotes(V.noteRows(doc, o.live), o.choose);
    lane.hidden = items.length === 0 && !o.live;
    updatePlayhead();
  }

  function updatePlayhead() {
    var pct = laneLength > 0 ? Math.max(0, Math.min(100, ((video.currentTime || 0) / laneLength) * 100)) : 0;
    playhead.style.left = pct + '%';
  }

  // MP4 shares.

  function duration() {
    return isFinite(video.duration) && video.duration > 0 ? video.duration : Number(meta && meta.duration_s) || 0;
  }

  function seek(item) {
    try { video.currentTime = item.vt; } catch (e) { /* not seekable yet */ }
    var playing = video.play();
    if (playing && typeof playing.catch === 'function') playing.catch(function () {});
  }

  function drawMp4() {
    drawMoments({ length: duration(), live: null, choose: seek });
  }

  // A failed load or seek can be the daily play cap, a pause, or the link ending.
  function recheck() {
    if (rechecking) return;
    rechecking = true;
    fetchMeta().then(function (fresh) {
      var state = V.metaState(fresh);
      if (state === 'paused' || state === 'limited') playerMessage(V.STATES[state].copy);
      else playerMessage(V.UNPLAYABLE);
    }, function () {
      showState('missing');
    }).then(function () { rechecking = false; });
  }

  function render(fresh) {
    if (V.metaState(fresh) !== 'ok') { meta = fresh; showState(V.metaState(fresh)); return; }
    if (!renderGame(fresh)) return;
    video.addEventListener('loadedmetadata', drawMp4);
    video.addEventListener('timeupdate', updatePlayhead);
    video.addEventListener('error', recheck);
    video.src = API + '/vod-file/' + encodeURIComponent(id);
    drawMp4();
  }

  // HLS shares.

  function livePage() {
    return {
      video: video, doc: document, api: API, id: id, $: $,
      fetch: function (url, init) { return fetch(url, init); },
      fetchMeta: fetchMeta, poll: schedulePoll, stopPolling: stopPolling,
      online: function () { return root.navigator ? root.navigator.onLine : undefined; },
      onOnline: function (fn) { if (typeof root.addEventListener === 'function') root.addEventListener('online', fn, { once: true }); },
      showState: showState, waiting: waiting, renderGame: renderGame,
      header: function (fresh) { meta = fresh; renderHeader(); },
      gameTitle: gameTitle, moments: drawMoments, playhead: updatePlayhead, message: playerMessage
    };
  }

  if (!id) {
    showState('missing');
    return;
  }
  fetchMeta().then(route, function () { showState('missing'); });
})(globalThis);

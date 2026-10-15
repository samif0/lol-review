/*
 * Shared full game watch page (vod.html, served at /v/<id> through _redirects): the page
 * itself and the views it shares. vod-lib.js (RevuVod) holds the pure helpers, vod-player.js
 * (RevuVodPlayer) the HLS engines and play tokens, vod-live.js (RevuVodLive) the controller
 * for a game that plays while it uploads, vod-side.js (RevuVodSide) the side column; all of
 * them load first.
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
 *
 * The side column (vod-side.js) shows Notes or Events: the viewer's own bookmarks and clips,
 * the sharer's notes, and the game's events with a filter.
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
  var fill = $('vod-lane-fill');
  var tip = $('vod-lane-tip');
  var axis = $('vod-lane-axis');
  var axisKey = '';
  var lastMoments = null;
  var liveEdge = null;
  var controls = null;
  var ink = null;
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
  var rangeEl = $('vod-lane-range');
  // The side column (vod-side.js): notes, the viewer's own and the sharer's, and the events.
  var side = root.RevuVodSide ? root.RevuVodSide.attach({ root: root, doc: document, video: video, $: $, V: V, id: id,
    offset: function () { return V.offsetOf(meta && meta.meta); },
    length: function () { return laneLength; },
    redraw: function () { if (lastMoments) drawMoments(lastMoments); } }) : null;
  var markMoment = root.RevuVodSide ? function (button, item, label) { return root.RevuVodSide.markMoment(V, button, item, label); }
    : function () { return ''; };

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
    attachControls();
    if (side) side.start();
    return true;
  }

  // The app-style transport and the pen (vod-controls.js, vod-ink.js), once. Before the first
  // start of a game that plays while it uploads, Play presses the page's own Play button, which
  // mints the token and counts the play.
  function attachControls() {
    if (controls || !root.RevuVodControls) return;
    var playButton = $('vod-play');
    ink = root.RevuVodInk ? root.RevuVodInk.attach({ doc: document, video: video, stage: $('vod-stage'),
      onChange: function (s) { if (controls) controls.penPressed(s.active); } }) : null;
    controls = root.RevuVodControls.attach({
      doc: document, video: video, deck: $('vod-deck'), bar: $('vod-bar'), track: track, fill: fill, playhead: playhead,
      tip: tip, ink: ink,
      length: function () { return laneLength; },
      edge: function () { return liveEdge; },
      started: function () { return playButton.hidden; },
      start: function () { playButton.click(); }
    });
  }

  // Ticks are rebuilt only when the moments change; otherwise they update in place, so
  // keyboard focus survives every upload update.
  function drawLane(items, choose) {
    var same = items.length === marks.length && items.every(function (item, i) { return item.vt === marks[i].item.vt && item.aria === marks[i].item.aria; });
    if (!same) {
      while (track.firstChild) track.removeChild(track.firstChild);
      [uploaded, rangeEl, fill, playhead, tip].forEach(function (el) { if (el) track.appendChild(el); });
      marks = items.map(function (item) {
        var entry = { button: document.createElement('button'), item: item, choose: choose };
        entry.button.type = 'button';
        // A stem in the app's style: its color by the event kind, its height by the tier.
        entry.button.className = 'vod-mark is-' + item.tone + ' tier-' + item.tier;
        entry.button.addEventListener('click', function () { entry.choose(entry.item); });
        entry.button.addEventListener('mousedown', keepFocus);
        track.appendChild(entry.button);
        return entry;
      });
    }
    marks.forEach(function (entry, i) {
      var item = items[i];
      entry.item = item;
      entry.choose = choose;
      entry.button.style.left = item.left + '%';
      // A code that would crowd its row (or a minor stem's) shows on hover, as in the app.
      entry.button.setAttribute('data-code', item.code);
      entry.button.classList.toggle('is-unlabeled', !!item.code && !item.labeled);
      var suffix = markMoment(entry.button, item, item.aria);
      entry.button.title = item.title + (suffix ? ' (' + suffix.slice(2) + ')' : '');
    });
  }

  // A click on a moment leaves the keyboard where it was: Space then pauses the game it
  // jumped to instead of pressing the moment again. Tab and Enter still reach every moment.
  function keepFocus(ev) { ev.preventDefault(); }

  function span(className, value) {
    var el = document.createElement('span');
    el.className = className;
    if (value !== undefined) el.textContent = value;
    return el;
  }

  // Clock ticks under the lane (game time), redrawn only when they change.
  function drawAxis(doc) {
    var ticks = V.axisTicks(laneLength, V.offsetOf(doc));
    var key = ticks.map(function (t) { return t.label + '@' + t.left.toFixed(2); }).join(',');
    if (key === axisKey) return;
    axisKey = key;
    while (axis.firstChild) axis.removeChild(axis.firstChild);
    ticks.forEach(function (t) {
      var tick = span('vod-lane-tick', t.label);
      tick.style.left = t.left + '%';
      axis.appendChild(tick);
    });
  }

  // The lane (the seek bar), the events and the notes. While an HLS game uploads, `live` is
  // { edge, rate, armed } and the lane spans `length`, the whole game (its fill shows the upload).
  function drawMoments(o) {
    lastMoments = o;
    var doc = meta && meta.meta && typeof meta.meta === 'object' ? meta.meta : null;
    laneLength = Number(o.length) || 0;
    liveEdge = o.live ? Number(o.live.edge) : null;
    // Shown before it is measured: a hidden lane is 0 px wide.
    lane.hidden = !(laneLength > 0);
    // A clip plays to its end and pauses there (vod-side.js).
    var choose = side ? side.playing(o.choose) : o.choose;
    // The viewer's own notes join the sharer's on the lane.
    var items = V.laneItems(side ? side.laneDoc(doc) : doc, laneLength, o.live, labelGap());
    drawLane(items, choose);
    drawAxis(doc);
    var shown = side ? side.draw({ doc: doc, live: o.live, items: items, choose: choose }) : false;
    panels.game.classList.toggle('has-notes', shown);
    updatePlayhead();
    if (controls) controls.paint();
  }

  // A code needs about 30 px: a narrow lane (a phone) shows fewer of them, and every moment
  // is still in the list. A window that changes size draws them again.
  function labelGap() {
    var width = track.clientWidth || 0;
    return width > 0 ? Math.max(V.LABEL_GAP, (30 / width) * 100) : V.LABEL_GAP;
  }

  if (typeof root.addEventListener === 'function') {
    root.addEventListener('resize', function () { if (lastMoments) drawMoments(lastMoments); });
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
    offerPlay();
    drawMp4();
  }

  // Nothing loads before the first play (preload="none"), so the picture is dark until then:
  // the big Play button over it, as for a game that plays while it uploads.
  function offerPlay() {
    var button = $('vod-play');
    var hide = function () { button.hidden = true; };
    $('vod-play-label').textContent = 'Play';
    button.hidden = false;
    button.addEventListener('click', function () {
      hide();
      var playing = video.play();
      if (playing && typeof playing.catch === 'function') playing.catch(function () {});
    });
    video.addEventListener('play', hide);
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
      gameTitle: gameTitle, moments: drawMoments, playhead: updatePlayhead, message: playerMessage,
      drawing: function () { return !!(ink && ink.active()); }
    };
  }

  if (!id) {
    showState('missing');
    return;
  }

  fetchMeta().then(route, function () { showState('missing'); });
})(globalThis);

/*
 * Shared full game watch page (vod.html, served at /v/<id> through _redirects).
 *
 * The video and its metadata come from clips.revu.lol: /vod-meta/<id> gives the
 * expiry, the quality and the sharer's markers document, /vod-file/<id> streams
 * the MP4. Marker and note times are GAME seconds; the share copy keeps the
 * recording's timeline, so video time is max(0, t - gameTimeAtVideoStart).
 *
 * The sharer gets the link as soon as their upload starts. While it runs,
 * /vod-meta answers status "uploading" with its progress and, once the sharer's
 * app has sent it, the markers document: the page names the game, shows the
 * progress, polls (every 4 s for a minute, then 10 s, 30 s while the upload has
 * stalled, never while the tab is hidden) and starts the player once the game
 * is ready.
 *
 * Everything the server sends is set with textContent, never parsed as HTML.
 * The pure helpers live on RevuVod for tests; DOM wiring only runs in a browser.
 */
(function (root) {
  'use strict';

  var ID = /^[0-9A-Za-z]{7}$/;
  var PROD_API = 'https://clips.revu.lol';
  var ROLES = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', MID: 'Mid', BOTTOM: 'Bot', ADC: 'Bot', UTILITY: 'Support', SUPPORT: 'Support' };
  var RESULTS = { win: 'Win', loss: 'Loss', remake: 'Remake' };
  var TONES = ['win', 'loss', 'gold', 'neutral'];
  var STATES = {
    missing: { title: 'Game not available', copy: "This shared game isn't available. Shared games stay up for 3 days." },
    paused: { title: 'Playback paused', copy: 'Playback is paused right now. Try again later.' },
    limited: { title: 'Playback limit reached', copy: 'This game has been watched a lot today. Try again tomorrow.' }
  };
  var UNPLAYABLE = "This video couldn't be played.";
  var UPLOADING_COPY = 'This game plays here as soon as the upload finishes. You can leave this page open.';
  var STALLED_COPY = "The upload hasn't moved in a while. If it doesn't continue soon, ask whoever sent you this link to share the game again.";

  function params(search) {
    try { return new URLSearchParams(search || ''); } catch (e) { return new URLSearchParams(''); }
  }

  // /v/<id> first; ?id= for the plain vod.html route.
  function parseId(pathname, search) {
    var match = /^\/v\/([0-9A-Za-z]{7})\/?$/.exec(String(pathname || ''));
    if (match) return match[1];
    var id = (params(search).get('id') || '').trim();
    return ID.test(id) ? id : '';
  }

  // The proxy override exists for local dev (wrangler dev) ONLY: honored only while
  // this page is served from localhost, and only for a loopback proxy, so a crafted
  // link can never make revu.lol fetch or play anything but clips.revu.lol.
  function apiBase(hostname, search) {
    var override = (params(search).get('proxy') || '').replace(/\/+$/, '');
    var local = /^(localhost|127\.0\.0\.1)$/.test(String(hostname || ''));
    return local && /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(override) ? override : PROD_API;
  }

  function clock(seconds) {
    var total = Math.max(0, Math.floor(Number(seconds) || 0));
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var ss = (s < 10 ? '0' : '') + s;
    return h > 0 ? h + ':' + (m < 10 ? '0' : '') + m + ':' + ss : m + ':' + ss;
  }

  function text(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function titleOf(game) {
    var champion = text(game && game.champion);
    var opponent = text(game && game.opponentChampion);
    if (champion && opponent) return champion + ' vs ' + opponent;
    if (champion) return champion + ' game';
    return 'Shared game';
  }

  function roleLabel(role) {
    var value = text(role);
    return Object.prototype.hasOwnProperty.call(ROLES, value.toUpperCase()) ? ROLES[value.toUpperCase()] : value;
  }

  // 'YYYY-MM-DD' is the date the game was played; shown as written, in no time zone.
  function formatDate(ymd, locale) {
    var match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text(ymd));
    if (!match) return '';
    var date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
    try {
      return new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' }).format(date);
    } catch (e) {
      return match[0];
    }
  }

  // Role, result, game length and date, in that order; missing parts are skipped.
  function metaParts(game, locale) {
    var parts = [];
    if (!game) return parts;
    var role = roleLabel(game.role);
    if (role) parts.push(role);
    if (Object.prototype.hasOwnProperty.call(RESULTS, game.result)) parts.push(RESULTS[game.result]);
    if (Number(game.durationS) > 0) parts.push(clock(game.durationS));
    var date = formatDate(game.playedAt, locale);
    if (date) parts.push(date);
    return parts;
  }

  // The game object of a meta reply's markers document, or null.
  function gameOf(meta) {
    var doc = meta && meta.meta && typeof meta.meta === 'object' ? meta.meta : null;
    return doc && doc.game && typeof doc.game === 'object' ? doc.game : null;
  }

  // While the upload runs: the game's title and meta line once the sharer's app
  // has sent its details, else null (there is nothing to name yet).
  function uploadGame(meta, locale) {
    var game = gameOf(meta);
    if (!game) return null;
    var parts = metaParts(game, locale);
    if (!text(game.champion) && parts.length === 0) return null;
    return { title: titleOf(game), meta: parts.join(' · ') };
  }

  function metaState(meta) {
    if (!meta || typeof meta !== 'object') return 'missing';
    if (meta.serving === false) return 'paused';
    if (meta.status === 'uploading') return 'uploading';
    if (meta.play_limited === true) return 'limited';
    return 'ok';
  }

  // The proxy counts parts as they start, retries included, so its count is an
  // estimate: never 100% until the game is actually ready.
  function uploadPercent(meta) {
    var progress = meta && meta.progress;
    var parts = Number(progress && progress.parts);
    var total = Number(progress && progress.part_count);
    if (!(parts > 0) || !(total > 0)) return 0;
    return Math.max(0, Math.min(99, Math.floor((parts / total) * 100)));
  }

  function uploadText(meta) {
    var percent = uploadPercent(meta);
    return meta && meta.stalled === true
      ? { title: 'Upload paused at ' + percent + '%', copy: STALLED_COPY }
      : { title: 'Still uploading, ' + percent + '%', copy: UPLOADING_COPY };
  }

  // How long to wait before asking again: quick at first, slower after a
  // minute, and slow while the upload has stalled.
  function pollDelay(elapsedMs, stalled) {
    if (stalled) return 30000;
    return Number(elapsedMs) < 60000 ? 4000 : 10000;
  }

  function formatTime(unixSeconds, nowMs, options) {
    var ms = Number(unixSeconds) * 1000;
    var opts = ms - nowMs < 6 * 86400000
      ? { weekday: 'long', hour: 'numeric', minute: '2-digit' }
      : { month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit' };
    if (options && options.timeZone) opts.timeZone = options.timeZone;
    try {
      return new Intl.DateTimeFormat(options && options.locale, opts).format(new Date(ms));
    } catch (e) {
      return new Date(ms).toLocaleString();
    }
  }

  // '' once the link has expired.
  function expiryText(expiresAt, nowMs, options) {
    var remaining = Number(expiresAt) * 1000 - Number(nowMs);
    if (!(remaining > 0)) return '';
    if (remaining < 3600000) return 'This link expires in less than an hour.';
    if (remaining < 86400000) {
      var hours = Math.floor(remaining / 3600000);
      return 'This link expires in ' + hours + (hours === 1 ? ' hour.' : ' hours.');
    }
    return 'This link expires ' + formatTime(expiresAt, nowMs, options) + '.';
  }

  function offsetOf(doc) {
    var value = Number(doc && doc.gameTimeAtVideoStart);
    return isFinite(value) ? value : 0;
  }

  function videoTime(t, offset) {
    return Math.max(0, Number(t) - offset);
  }

  function noteKind(kind) {
    return kind === 'clip' ? 'Clip' : 'Bookmark';
  }

  // Lane ticks for the markers and, when the sharer included them, the notes.
  function laneItems(doc, duration) {
    var items = [];
    var length = Number(duration);
    if (!doc || !(length > 0)) return items;
    var offset = offsetOf(doc);
    var add = function (entry, isNote) {
      if (!entry || !isFinite(Number(entry.t))) return;
      var vt = videoTime(entry.t, offset);
      if (vt > length) return;
      var label = isNote ? noteKind(entry.kind) : (text(entry.label) || text(entry.kind) || 'Moment');
      var tone = isNote ? 'note' : (TONES.indexOf(entry.tone) !== -1 ? entry.tone : '');
      items.push({ note: isNote, vt: vt, left: (vt / length) * 100, label: label, tone: tone,
        aria: label + ' at ' + clock(entry.t), title: clock(entry.t) + ' ' + label });
    };
    (Array.isArray(doc.markers) ? doc.markers : []).forEach(function (m) { add(m, false); });
    (Array.isArray(doc.notes) ? doc.notes : []).forEach(function (n) { add(n, true); });
    return items;
  }

  function noteRows(doc) {
    if (!doc || !Array.isArray(doc.notes)) return [];
    var offset = offsetOf(doc);
    return doc.notes.filter(function (n) { return n && isFinite(Number(n.t)); }).map(function (n) {
      return { vt: videoTime(n.t, offset), time: clock(n.t), kind: noteKind(n.kind), text: text(n.text) };
    });
  }

  root.RevuVod = {
    parseId: parseId, apiBase: apiBase, clock: clock, titleOf: titleOf, roleLabel: roleLabel, formatDate: formatDate,
    metaParts: metaParts, metaState: metaState, expiryText: expiryText, laneItems: laneItems, noteRows: noteRows,
    uploadPercent: uploadPercent, uploadText: uploadText, uploadGame: uploadGame, pollDelay: pollDelay,
    STATES: STATES, UNPLAYABLE: UNPLAYABLE, UPLOADING_COPY: UPLOADING_COPY, STALLED_COPY: STALLED_COPY
  };

  if (typeof document === 'undefined') return;

  var id = parseId(location.pathname, location.search);
  var API = apiBase(location.hostname, location.search);
  var $ = function (name) { return document.getElementById(name); };
  var panels = { loading: $('state-loading'), missing: $('state-missing'), uploading: $('state-uploading'), game: $('state-game') };
  var video = $('vod-video');
  var playerMsg = $('vod-player-msg');
  var banner = $('vod-expiry');
  var lane = $('vod-lane');
  var track = $('vod-lane-track');
  var playhead = $('vod-lane-playhead');
  var notesPanel = $('vod-notes');
  var notesList = $('vod-notes-list');
  var meta = null;
  var expiryTimer = null;
  var rechecking = false;
  var pollTimer = null;
  var pollStart = 0;
  var pollStalled = false;
  var pollDue = false;

  function show(name) {
    Object.keys(panels).forEach(function (key) { panels[key].hidden = key !== name; });
  }

  function stopPlayback() {
    try { video.pause(); } catch (e) { /* nothing is playing */ }
    try { video.removeAttribute('src'); video.load(); } catch (e) { /* the element is already empty */ }
  }

  function showState(name) {
    var state = STATES[name] || STATES.missing;
    $('state-title').textContent = state.title;
    $('state-copy').textContent = state.copy;
    if (expiryTimer) clearInterval(expiryTimer);
    expiryTimer = null;
    stopPolling();
    if (meta) stopPlayback();
    show('missing');
  }

  // ── Still uploading ──

  function stopPolling() {
    if (pollTimer) clearTimeout(pollTimer);
    pollTimer = null;
    pollDue = false;
  }

  function showUploading(fresh) {
    var percent = uploadPercent(fresh);
    var words = uploadText(fresh);
    var game = uploadGame(fresh);
    $('upload-game-title').textContent = game ? game.title : '';
    $('upload-game-meta').textContent = game ? game.meta : '';
    $('upload-game-meta').hidden = !(game && game.meta);
    $('upload-game').hidden = !game;
    $('upload-title').textContent = words.title;
    // The copy is a live region: rewrite it only when it says something new.
    if ($('upload-copy').textContent !== words.copy) $('upload-copy').textContent = words.copy;
    $('upload-bar').style.width = percent + '%';
    $('upload-meter').setAttribute('aria-valuenow', String(percent));
    document.title = words.title + ' - Revu';
    show('uploading');
  }

  // A hidden tab stops asking, and asks again as soon as it is shown.
  function schedulePoll() {
    if (pollTimer) clearTimeout(pollTimer);
    pollTimer = setTimeout(function () {
      pollTimer = null;
      if (document.hidden) { pollDue = true; return; }
      poll();
    }, pollDelay(Date.now() - pollStart, pollStalled));
  }

  function poll() {
    pollDue = false;
    fetchMeta().then(route, function (error) {
      // Gone (stopped, discarded or past its upload window): say so. Anything else may pass.
      if (error && error.status === 404) showState('missing');
      else schedulePoll();
    });
  }

  // Every meta reply lands here: still uploading keeps waiting, anything else renders.
  function route(fresh) {
    if (metaState(fresh) !== 'uploading') {
      stopPolling();
      render(fresh);
      return;
    }
    if (!pollStart) pollStart = Date.now();
    pollStalled = fresh.stalled === true;
    showUploading(fresh);
    schedulePoll();
  }

  document.addEventListener('visibilitychange', function () {
    if (!document.hidden && pollDue) poll();
  });

  function playerMessage(message) {
    playerMsg.textContent = message;
    playerMsg.hidden = !message;
    panels.game.classList.toggle('is-blocked', !!message);
  }

  function duration() {
    return isFinite(video.duration) && video.duration > 0 ? video.duration : Number(meta && meta.duration_s) || 0;
  }

  function seek(vt) {
    try { video.currentTime = vt; } catch (e) { /* not seekable yet */ }
    var playing = video.play();
    if (playing && typeof playing.catch === 'function') playing.catch(function () {});
  }

  function renderLane() {
    var doc = meta && meta.meta;
    var items = laneItems(doc, duration());
    while (track.firstChild) track.removeChild(track.firstChild);
    track.appendChild(playhead);
    items.forEach(function (item) {
      var mark = document.createElement('button');
      mark.type = 'button';
      mark.className = 'vod-mark' + (item.tone ? ' is-' + item.tone : '');
      mark.style.left = item.left + '%';
      mark.setAttribute('aria-label', item.aria);
      mark.title = item.title;
      mark.addEventListener('click', function () { seek(item.vt); });
      track.appendChild(mark);
    });
    lane.hidden = items.length === 0;
    updatePlayhead();
  }

  function updatePlayhead() {
    var length = duration();
    var pct = length > 0 ? Math.max(0, Math.min(100, ((video.currentTime || 0) / length) * 100)) : 0;
    playhead.style.left = pct + '%';
  }

  function renderNotes() {
    var rows = noteRows(meta && meta.meta);
    while (notesList.firstChild) notesList.removeChild(notesList.firstChild);
    rows.forEach(function (row) {
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
      button.addEventListener('click', function () { seek(row.vt); });
      li.appendChild(button);
      notesList.appendChild(li);
    });
    notesPanel.hidden = rows.length === 0;
    panels.game.classList.toggle('has-notes', rows.length > 0);
  }

  function renderExpiry() {
    var line = expiryText(meta.expires_at, Date.now());
    if (!line) { showState('missing'); return; }
    banner.textContent = line;
    banner.hidden = false;
  }

  function renderHeader() {
    var game = gameOf(meta);
    var title = titleOf(game);
    $('vod-title').textContent = title;
    document.title = title + ' - Revu';
    var parts = metaParts(game);
    var line = $('vod-meta');
    line.textContent = parts.join(' · ');
    line.hidden = parts.length === 0;
    var quality = $('vod-quality');
    // "1080p60" once the sharer's app sends the frame rate; older shares say "1080p".
    quality.textContent = /^(1080|720)p(30|60)?$/.test(meta.quality) ? meta.quality : '';
    quality.hidden = !quality.textContent;
    $('vod-id').textContent = id;
  }

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

  // A failed load or seek can be the daily play cap, a pause, or the link ending.
  function recheck() {
    if (rechecking) return;
    rechecking = true;
    fetchMeta().then(function (fresh) {
      var state = metaState(fresh);
      if (state === 'paused' || state === 'limited') playerMessage(STATES[state].copy);
      else playerMessage(UNPLAYABLE);
    }, function () {
      showState('missing');
    }).then(function () { rechecking = false; });
  }

  function render(fresh) {
    meta = fresh;
    var state = metaState(meta);
    if (state !== 'ok') { showState(state); return; }
    renderHeader();
    renderNotes();
    show('game');
    renderExpiry();
    if (panels.missing.hidden === false) return;
    expiryTimer = setInterval(renderExpiry, 60000);
    video.addEventListener('loadedmetadata', renderLane);
    video.addEventListener('timeupdate', updatePlayhead);
    video.addEventListener('error', recheck);
    video.src = API + '/vod-file/' + encodeURIComponent(id);
    renderLane();
  }

  if (!id) {
    showState('missing');
    return;
  }
  fetchMeta().then(route, function () { showState('missing'); });
})(globalThis);

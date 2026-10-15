/*
 * Pure helpers for the shared full game page (vod.html at /v/<id>): ids, text, meta
 * states, the moments lane and, for a game that plays while it uploads (format "hls"),
 * what the page says, how often it asks /vod-meta and when it waits at the uploaded edge.
 * No DOM: vod.js and vod-live.js wire them up, tests/ runs them directly. Times in markers
 * and notes are GAME seconds; video time is max(0, t - gameTimeAtVideoStart).
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
    limited: { title: 'Playback limit reached', copy: 'This game has been watched a lot today. Try again tomorrow.' },
    stopped: { title: 'Sharing stopped', copy: 'The sharer stopped sharing this game.' }
  };
  var UNPLAYABLE = "This video couldn't be played.";
  var UNSUPPORTED = "This browser can't play shared games. Try a recent version of Chrome, Edge, Firefox or Safari.";
  var OFFLINE = "You're offline. The game picks up again when you're back online.";
  var UPLOADING_COPY = 'This game plays here as soon as the upload finishes. You can leave this page open.';
  var STALLED_COPY = "The upload hasn't moved in a while. If it doesn't continue soon, ask whoever sent you this link to share the game again.";
  // An HLS share that can't play yet, by how long it has waited (waitingKind).
  var WAITING = {
    soon: { title: 'Starting soon', copy: 'This game starts here in a few seconds, while the rest uploads. You can leave this page open.' },
    longer: { title: 'Taking longer than usual', copy: 'This game starts here as soon as the upload picks up. You can leave this page open.' },
    notstarted: { title: "The upload hasn't started", copy: 'The sharer may have closed Revu.' }
  };
  // A moment can play once this much video after it is uploaded.
  var MOMENT_LEAD_S = 2;
  // This close to the uploaded edge, playback has caught up with the upload.
  var EDGE_NEAR_S = 2;
  // After catching up with a paused, stalled or slow upload, loading starts again once
  // this much more is uploaded (one target duration).
  var CAUGHT_UP_RESUME_S = 6;
  // The proxy's last commit this long ago, and the share not paused: the upload has stalled.
  var STALL_AFTER_S = 120;

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

  // A finite number, or null (the proxy sends null for "not known yet").
  function num(value) {
    return typeof value === 'number' && isFinite(value) ? value : null;
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

  // A share published as HLS segments (format "hls") can play while it uploads; anything
  // else is the single MP4 of 4.2.0, which plays once it is complete.
  function isHls(meta) {
    return !!meta && meta.format === 'hls';
  }

  // An HLS share is finished once its row is no longer uploading (ended, or ended early).
  function hlsEnded(meta) {
    return isHls(meta) && (meta.status !== 'uploading' || meta.ended === true);
  }

  // A pause and the play cap win over everything (a new play could not start anyway; a
  // viewer who is already watching is never cut off, see vod-live.js). An HLS share that is
  // still uploading is 'live' once the proxy says it is playable, 'waiting' before.
  function metaState(meta) {
    if (!meta || typeof meta !== 'object') return 'missing';
    if (meta.serving === false) return 'paused';
    if (meta.play_limited === true) return 'limited';
    if (isHls(meta)) {
      if (hlsEnded(meta)) return 'ok';
      return meta.playable === true ? 'live' : 'waiting';
    }
    if (meta.status === 'uploading') return 'uploading';
    return 'ok';
  }

  // The uploaded edge of an HLS share, in video seconds.
  function edgeOf(meta) {
    var media = num(meta && meta.media_seconds);
    return media !== null && media > 0 ? media : 0;
  }

  // Media seconds uploaded per wall-clock second (the proxy's average), or null.
  function rateOf(meta) {
    var rate = num(meta && meta.ingest_x);
    return rate !== null && rate >= 0 ? rate : null;
  }

  function pausedOf(meta) {
    return !!meta && meta.paused_reason === 'game';
  }

  function stalledOf(meta) {
    var age = num(meta && meta.last_commit_age_s);
    return !pausedOf(meta) && age !== null && age >= STALL_AFTER_S;
  }

  // An HLS share that can't play yet: under 30 s 'soon', then 'longer'; three minutes with
  // no commit at all is 'notstarted'. Ages come from the proxy's clock (meta.now).
  function waitingKind(meta, nowS) {
    var commitAge = num(meta && meta.last_commit_age_s);
    var now = num(meta && meta.now);
    var age = commitAge !== null ? commitAge : (now !== null ? now : Number(nowS)) - Number(meta && meta.created_at);
    if (!(age >= 30)) return 'soon';
    return age < 180 || commitAge !== null ? 'longer' : 'notstarted';
  }

  // The proxy counts parts as they start, retries included, so its count is an
  // estimate: never 100% until the game is actually ready. An HLS share counts media
  // seconds against its length.
  function uploadPercent(meta) {
    if (isHls(meta)) return livePercent(edgeOf(meta), meta.duration_s);
    var progress = meta && meta.progress;
    var parts = Number(progress && progress.parts);
    var total = Number(progress && progress.part_count);
    if (!(parts > 0) || !(total > 0)) return 0;
    return Math.max(0, Math.min(99, Math.floor((parts / total) * 100)));
  }

  function livePercent(edge, length) {
    var e = Number(edge), l = Number(length);
    if (!(e > 0) || !(l > 0)) return 0;
    return Math.max(0, Math.min(99, Math.floor((e / l) * 100)));
  }

  function uploadText(meta) {
    var percent = uploadPercent(meta);
    return meta && meta.stalled === true
      ? { title: 'Upload paused at ' + percent + '%', copy: STALLED_COPY }
      : { title: 'Still uploading, ' + percent + '%', copy: UPLOADING_COPY };
  }

  // An MP4 upload: how long to wait before asking again. Quick at first, slower after a
  // minute, and slow while the upload has stalled.
  function pollDelay(elapsedMs, stalled) {
    if (stalled) return 30000;
    return Number(elapsedMs) < 60000 ? 4000 : 10000;
  }

  // An HLS share, by phase: 'waiting' (not playable: 2 s for a minute, 10 s until 10 min,
  // then 30 s), 'ready' (playable, not playing), 'playing' (hls.js reloads the playlist),
  // 'hold' (caught up with the upload, loading stopped). 0: don't ask.
  function hlsPollDelay(phase, elapsedMs) {
    if (phase === 'waiting') return elapsedMs < 60000 ? 2000 : elapsedMs < 600000 ? 10000 : 30000;
    if (phase === 'ready') return 10000;
    if (phase === 'hold') return 15000;
    if (phase === 'playing') return 30000;
    return 0;
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

  function timeOfDay(ms, options) {
    var opts = { hour: 'numeric', minute: '2-digit' };
    if (options && options.timeZone) opts.timeZone = options.timeZone;
    try {
      return new Intl.DateTimeFormat(options && options.locale, opts).format(new Date(ms));
    } catch (e) {
      return new Date(ms).toLocaleTimeString();
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

  // Minutes until a moment at video time vt can play, from the upload rate; null when
  // unknown (no rate yet, or the upload is paused or stalled).
  function etaMinutes(vt, edge, rate) {
    if (!(Number(rate) > 0)) return null;
    var seconds = (Number(vt) + MOMENT_LEAD_S - Number(edge)) / Number(rate);
    return seconds > 0 ? Math.max(1, Math.ceil(seconds / 60)) : null;
  }

  // While the game uploads (live given): is the moment past the uploaded edge, when will it
  // arrive, and is it the one the viewer asked to jump to.
  function liveFields(item, live) {
    item.pending = !!live && item.vt + MOMENT_LEAD_S > Number(live.edge);
    item.eta = item.pending ? etaMinutes(item.vt, live.edge, live.rate) : null;
    item.armed = item.pending && typeof live.armed === 'number' && Math.abs(live.armed - item.vt) < 0.001;
    return item;
  }

  // What a pending moment adds to its name: ', not uploaded yet, about 3 min'.
  function pendingSuffix(item) {
    if (!item || !item.pending) return '';
    return ', not uploaded yet' + (item.eta ? ', about ' + item.eta + ' min' : '');
  }

  // Lane ticks for the markers and, when the sharer included them, the notes. While the
  // game uploads, `duration` is the whole game's length and `live` is { edge, rate, armed }.
  function laneItems(doc, duration, live) {
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
      items.push(liveFields({ note: isNote, vt: vt, left: (vt / length) * 100, label: label, tone: tone,
        aria: label + ' at ' + clock(entry.t), title: clock(entry.t) + ' ' + label }, live));
    };
    (Array.isArray(doc.markers) ? doc.markers : []).forEach(function (m) { add(m, false); });
    (Array.isArray(doc.notes) ? doc.notes : []).forEach(function (n) { add(n, true); });
    return items;
  }

  function noteRows(doc, live) {
    if (!doc || !Array.isArray(doc.notes)) return [];
    var offset = offsetOf(doc);
    return doc.notes.filter(function (n) { return n && isFinite(Number(n.t)); }).map(function (n) {
      var row = { vt: videoTime(n.t, offset), time: clock(n.t), kind: noteKind(n.kind), text: text(n.text) };
      row.aria = row.kind + ' at ' + row.time;
      return liveFields(row, live);
    });
  }

  function jumpText(item) {
    return 'Jumps to ' + item.aria + ' when it arrives' + (item.eta ? ', in about ' + item.eta + ' min.' : '.');
  }

  // Playing while it uploads.

  // Seconds to wait before playing from `position` runs to the end without catching the
  // upload: (left to upload) / rate - (left to play). 0 when the upload keeps ahead.
  function smoothWait(duration, edge, rate, position) {
    var d = Number(duration), e = Number(edge), r = Number(rate), p = Number(position) || 0;
    if (!(r > 0) || !(r < 1) || !(d > e)) return 0;
    return Math.max(0, (d - e) / r - (d - p));
  }

  // Seconds buffered ahead of t in a TimeRanges, 0 when t is not inside a buffered range.
  function bufferedAhead(buffered, t) {
    var at = Number(t) || 0;
    for (var i = 0; buffered && i < buffered.length; i += 1) {
      if (buffered.start(i) <= at + 0.25 && buffered.end(i) > at) return buffered.end(i) - at;
    }
    return 0;
  }

  // Whether to stop loading at the uploaded edge, and how much lead to build first.
  // s: { lead, rate, rebufferOn, paused, stalled }. Slower than playback with the rebuffer
  // policy on: pause until 30 s ahead (60 s below 0.7x). Paused, stalled, or slow with the
  // policy off: wait for the next bit. A fast upload is left to hls.js.
  function holdAtEdge(s) {
    if (!(s.lead < EDGE_NEAR_S)) return null;
    var slow = typeof s.rate === 'number' && s.rate < 1;
    if (slow && s.rebufferOn) return { kind: 'rebuffer', lead: s.rate < 0.7 ? 60 : 30 };
    if (slow || s.paused || s.stalled) return { kind: 'caughtup', lead: CAUGHT_UP_RESUME_S };
    return null;
  }

  function line(kind, value) {
    return { kind: kind, text: value, before: value, action: '', after: '' };
  }

  // What the line under the player says for a playable HLS share.
  // s: { gone, ended, truncated, paused, stalled, started, hold, atEdge, edge, duration,
  //      rate, position, rebufferOn, nowMs }
  function liveLine(s, options) {
    var edge = clock(s.edge);
    if (s.gone) return line('stopped', STATES.stopped.copy);
    if (s.ended) {
      return s.truncated ? line('truncated', 'The sharer stopped before the end. Shared up to ' + edge + '.')
        : line('ended', 'The whole game is here.');
    }
    if (s.paused) return line('paused', 'The sharer started another game. You can watch up to ' + edge + '; the rest uploads after it.');
    if (s.stalled) return line('stalled', 'The upload has stalled. You can watch up to ' + edge + '.');
    var wait = s.rebufferOn ? smoothWait(s.duration, s.edge, s.rate, s.position) : 0;
    if (s.hold === 'rebuffer' || wait > 0) {
      // now + wait stays put while the upload and the clock both move, so the time shown is steady.
      var minutes = Math.max(1, Math.round(wait / 60));
      var before = 'The upload is slower than playback. Smooth from about ' + timeOfDay(Number(s.nowMs) + wait * 1000, options) +
        ' (in ' + minutes + ' min), or ';
      return { kind: 'slow', text: before + 'play now with pauses.', before: before, action: 'play now with pauses', after: '.' };
    }
    if (s.hold === 'caughtup' || s.atEdge) return line('caughtup', 'Waiting for more of the game to upload');
    var length = clock(s.duration);
    if (!s.started) return line('ready', 'Ready from the start. ' + edge + ' of ' + length + ' uploaded so far');
    return line('watchable', 'Watchable up to ' + edge + ' of ' + length + ' · still uploading');
  }

  root.RevuVod = {
    parseId: parseId, apiBase: apiBase, clock: clock, titleOf: titleOf, roleLabel: roleLabel, formatDate: formatDate,
    metaParts: metaParts, gameOf: gameOf, uploadGame: uploadGame, isHls: isHls, hlsEnded: hlsEnded, metaState: metaState,
    edgeOf: edgeOf, rateOf: rateOf, pausedOf: pausedOf, stalledOf: stalledOf, waitingKind: waitingKind,
    uploadPercent: uploadPercent, livePercent: livePercent, uploadText: uploadText, pollDelay: pollDelay,
    hlsPollDelay: hlsPollDelay, expiryText: expiryText, timeOfDay: timeOfDay, etaMinutes: etaMinutes,
    pendingSuffix: pendingSuffix, laneItems: laneItems, noteRows: noteRows, jumpText: jumpText,
    smoothWait: smoothWait, bufferedAhead: bufferedAhead, holdAtEdge: holdAtEdge, liveLine: liveLine,
    STATES: STATES, WAITING: WAITING, UNPLAYABLE: UNPLAYABLE, UNSUPPORTED: UNSUPPORTED, OFFLINE: OFFLINE,
    UPLOADING_COPY: UPLOADING_COPY, STALLED_COPY: STALLED_COPY, MOMENT_LEAD_S: MOMENT_LEAD_S, EDGE_NEAR_S: EDGE_NEAR_S,
    CAUGHT_UP_RESUME_S: CAUGHT_UP_RESUME_S, STALL_AFTER_S: STALL_AFTER_S
  };
})(globalThis);

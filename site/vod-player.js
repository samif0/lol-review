/*
 * Playback engines and play tokens for a shared full game published as HLS (format "hls").
 * vod-live.js decides when to start and what the page says; this file only plays.
 *
 *   hls.js 1.7.3, light build, self-hosted under /vendor/ and pinned by SRI: wherever
 *     MediaSource or ManagedMediaSource plays H.264 High + AAC (desktop browsers, Android,
 *     iPadOS, iOS 17.1+), while the game uploads and after.
 *   The browser's own HLS player only for a FINISHED game, on Safari (ManagedMediaSource
 *     and native HLS) or where MediaSource is missing (iOS before 17.1, which waits for
 *     the finished game). Chromium's own HLS player is never used: on a growing playlist it
 *     skips segments, can't seek and raises no error.
 *
 * A play is counted only when the viewer starts one: POST /vod-play/<id> (no body, no custom
 * headers) mints a token, and the playlist URL is built from the checked id and that token
 * only. hls.js runs with autoStartLoad off, so attaching it fetches nothing.
 */
(function (root) {
  'use strict';

  var HLS_SRC = '/vendor/hls-1.7.3/hls.light.min.js';
  // The sha384 SRI of the vendored file, filled in by whoever vendors hls.js (see
  // site/README.md; tests/vendor.test.mjs checks it against the file). While it is empty
  // the loader refuses to load anything and the page says the game couldn't be played.
  var HLS_INTEGRITY = '';
  var HLS_MIME = 'application/vnd.apple.mpegurl';
  // The share copy: H.264 High (level 4.2 covers 1080p60) and AAC-LC.
  var CODECS = 'video/mp4; codecs="avc1.64002a,mp4a.40.2"';
  var ID = /^[0-9A-Za-z]{7}$/;
  var TOKEN = /^\d{10}\.[A-Za-z0-9_-]{22}$/;
  var INTEGRITY = /^sha384-[A-Za-z0-9+/]{64}$/;
  // A 403 this soon after a mint means a fresh token would not help either.
  var REMINT_MIN_MS = 60000;

  function supports(source) {
    try { return !!source && typeof source.isTypeSupported === 'function' && source.isTypeSupported(CODECS); } catch (e) { return false; }
  }

  function capsOf(win, video) {
    var native = '';
    try { native = video && typeof video.canPlayType === 'function' ? video.canPlayType(HLS_MIME) : ''; } catch (e) { native = ''; }
    return { mse: supports(win && win.MediaSource), mms: supports(win && win.ManagedMediaSource), native: !!native };
  }

  // The browser's own HLS player is trusted only where hls.js says it is reliable: Safari
  // (it has ManagedMediaSource) and browsers with no MediaSource at all. Chromium reports
  // native HLS too, but has MediaSource and no ManagedMediaSource.
  function nativeTrusted(caps) {
    return !!caps.native && (caps.mms || !caps.mse);
  }

  // 'hlsjs', 'native', 'wait' (no MediaSource: play natively once the game is finished)
  // or 'none' (the unsupported copy).
  function choose(caps, finished) {
    if (caps.mse || caps.mms) return finished && nativeTrusted(caps) ? 'native' : 'hlsjs';
    if (caps.native) return finished ? 'native' : 'wait';
    return 'none';
  }

  // Can play() start by itself after an earlier click on the page? Chromium and Firefox
  // honour that sticky activation; Safari wants the press itself, so it shows the button.
  function autoplayAfterClick(caps) {
    return !caps.mms && !nativeTrusted(caps);
  }

  function hlsConfig() {
    return {
      autoStartLoad: false,        // nothing loads, and no play counts, before play or a picked moment
      startPosition: 0,            // an uploading game starts at its beginning, not near its live edge
      enableWorker: false,         // fMP4 needs no transmuxing, and the CSP has worker-src 'none'
      lowLatencyMode: false,
      liveDurationInfinity: false, // duration = what is uploaded: the scrubber and seekable stop there
      maxBufferLength: 30,
      backBufferLength: 90         // the default keeps every watched second in memory
    };
  }

  function mintUrl(api, id) {
    return ID.test(id) ? api + '/vod-play/' + id : '';
  }

  // The only way a playlist URL is made: a checked id and a checked token, nothing from a reply.
  function playlistUrl(api, id, token) {
    return ID.test(id) && TOKEN.test(String(token)) ? api + '/vod-hls/' + id + '/' + token + '/index.m3u8' : '';
  }

  // A refused mint: 429 the play cap, 503 playback off, 404 gone or not playable yet.
  function mintKind(status, code) {
    if (status === 429) return 'limited';
    if (status === 503) return 'paused';
    if (status === 404) return code === 'not_ready' ? 'notready' : 'gone';
    return 'unplayable';
  }

  // A failed media request. hls.js never retries a 4xx; it retries a 5xx, so a 503 (playback
  // switched off) is also reported while it retries.
  function errorKind(status, online) {
    if (status === 429) return 'limited';
    if (status === 503) return 'paused';
    if (status === 404) return 'gone';
    if (!status && online === false) return 'offline';
    return 'unplayable';
  }

  function edgeOf(details) {
    var edge = Number(details && details.edge);
    if (!isFinite(edge)) edge = Number(details && details.totalduration);
    return isFinite(edge) ? edge : 0;
  }

  function fail(kind, during, status) {
    return { kind: kind, fatal: true, during: during, status: status };
  }

  var api = { HLS_SRC: HLS_SRC, HLS_INTEGRITY: HLS_INTEGRITY, HLS_MIME: HLS_MIME, CODECS: CODECS, TOKEN: TOKEN,
    capsOf: capsOf, nativeTrusted: nativeTrusted, choose: choose, autoplayAfterClick: autoplayAfterClick,
    hlsConfig: hlsConfig, mintUrl: mintUrl, playlistUrl: playlistUrl, mintKind: mintKind, errorKind: errorKind, edgeOf: edgeOf };
  root.RevuVodPlayer = api;

  if (typeof document === 'undefined') return;

  var loading = null;

  // hls.js loads once, from this origin, with SRI (script-src 'self' covers it). No pin, no load.
  function loadHls(doc) {
    if (loading) return loading;
    if (!INTEGRITY.test(HLS_INTEGRITY)) return Promise.reject(new Error('hls.js is not pinned'));
    loading = new Promise(function (resolve, reject) {
      var script = doc.createElement('script');
      script.src = HLS_SRC;
      script.integrity = HLS_INTEGRITY;
      script.async = true;
      script.onload = function () {
        if (typeof root.Hls === 'function') { resolve(root.Hls); return; }
        loading = null;
        reject(new Error('hls.js did not load'));
      };
      script.onerror = function () { loading = null; reject(new Error('hls.js did not load')); };
      doc.head.appendChild(script);
    });
    return loading;
  }

  function playQuietly(video) {
    try {
      var playing = video.play();
      if (playing && typeof playing.catch === 'function') playing.catch(function () {});
    } catch (e) { /* the page shows its Play button */ }
  }

  /*
   * create(video, { kind: 'hlsjs'|'native', api, id, doc, fetch, online, onEdge(edge, ended), onProblem(info) })
   * info: { kind: 'limited'|'paused'|'gone'|'notready'|'offline'|'unplayable'|'unsupported', fatal, during, status }
   * Returns { kind, begin(at), stop(), resume(at), destroy() }. Nothing is fetched before begin.
   */
  function create(video, o) {
    var self = { kind: o.kind };
    var Hls = null, hls = null;
    var started = false, destroyed = false, want = false, sourced = false, parsed = false;
    var url = '', startAt = 0, recovered = 0, mintedAt = -Infinity, seekOnLoad = false;

    function problem(info) { if (!destroyed) o.onProblem(info); }

    function online() { try { return o.online(); } catch (e) { return undefined; } }

    function mint() {
      mintedAt = Date.now();
      return o.fetch(mintUrl(o.api, o.id), { method: 'POST', cache: 'no-store' }).then(function (res) {
        return res.json().then(null, function () { return null; }).then(function (body) {
          if (!res.ok) throw fail(mintKind(res.status, body && body.error), 'mint', res.status);
          var next = playlistUrl(o.api, o.id, body && body.token);
          if (!next) throw fail('unplayable', 'mint', res.status);
          return next;
        });
      }, function () {
        throw fail(online() === false ? 'offline' : 'unplayable', 'mint');
      });
    }

    function minted(error) {
      problem(error && error.kind ? error : fail('unplayable', 'mint'));
    }

    // hls.js: the playlist once there is a token, then MANIFEST_PARSED starts loading.
    function load() {
      if (!hls || !url || sourced || destroyed) return;
      sourced = true;
      hls.loadSource(url);
    }

    // A rejected token (403): mint again and load at the current time. A new URL makes
    // hls.js re-attach the media element, so playback starts again from there.
    function remint() {
      if (Date.now() - mintedAt < REMINT_MIN_MS) { problem(fail('unplayable', 'media', 403)); return; }
      var at = video.currentTime || startAt;
      var playing = !video.paused;
      mint().then(function (next) {
        if (destroyed || !hls) return;
        url = next;
        startAt = at;
        parsed = false;
        want = true;
        hls.loadSource(url);
        if (playing) playQuietly(video);
      }, minted);
    }

    function onError(event, data) {
      if (!data || destroyed) return;
      var status = data.response && typeof data.response.code === 'number' ? data.response.code : undefined;
      var network = data.type === Hls.ErrorTypes.NETWORK_ERROR;
      if (!data.fatal) {
        if (network && status >= 500) problem({ kind: errorKind(status, online()), fatal: false, during: 'media', status: status });
        return;
      }
      if (network && status === 403) { remint(); return; }
      if (data.type === Hls.ErrorTypes.MEDIA_ERROR && recovered < 1) { recovered += 1; hls.recoverMediaError(); return; }
      problem({ kind: errorKind(status, online()), fatal: true, during: 'media', status: status });
    }

    // Attached means a MediaSource on the element and nothing fetched: play() may come first.
    function attachHls(H) {
      if (destroyed) return false;
      if (typeof H.isSupported !== 'function' || !H.isSupported()) { problem(fail('unsupported', 'load')); return false; }
      Hls = H;
      hls = new H(hlsConfig());
      hls.on(H.Events.MANIFEST_PARSED, function () {
        parsed = true;
        if (want && !destroyed) hls.startLoad(startAt);
      });
      hls.on(H.Events.LEVEL_UPDATED, function (event, data) {
        var details = data && data.details;
        if (details && !destroyed) o.onEdge(edgeOf(details), details.live === false);
      });
      hls.on(H.Events.ERROR, onError);
      hls.attachMedia(video);
      return true;
    }

    function startNative() {
      video.addEventListener('error', function () { if (started) problem(fail('unplayable', 'media')); });
      if (startAt > 0) {
        seekOnLoad = true;
        video.addEventListener('loadedmetadata', function () {
          if (!seekOnLoad) return;
          seekOnLoad = false;
          try { video.currentTime = startAt; } catch (e) { /* plays from the start */ }
        });
      }
      video.src = url;
    }

    // hls.js failed to load (no pin, the network, or an SRI mismatch): never fall back to
    // the browser's own player here; the page offers Try again. Nothing is minted for a
    // player that can't run.
    var ready = self.kind === 'hlsjs'
      ? loadHls(o.doc).then(attachHls, function () { problem(fail('unplayable', 'load')); return false; })
      : Promise.resolve(true);

    // The first play or a picked moment: mint (this counts the play), then load from `at`.
    self.begin = function (at) {
      if (started || destroyed) return;
      started = true;
      want = true;
      startAt = Number(at) > 0 ? Number(at) : 0;
      ready.then(function (ok) {
        if (!ok || destroyed) return;
        mint().then(function (next) {
          if (destroyed) return;
          url = next;
          if (self.kind === 'native') startNative(); else load();
        }, minted);
      });
    };

    // Stop and start fetching (hls.js only: playlist reloads and segments).
    self.stop = function () {
      if (!want || destroyed) return;
      want = false;
      if (hls && parsed) hls.stopLoad();
    };
    self.resume = function (at) {
      if (!started || want || destroyed) return;
      want = true;
      startAt = Number(at) > 0 ? Number(at) : 0;
      if (hls && parsed) hls.startLoad(startAt);
    };

    self.destroy = function () {
      if (destroyed) return;
      destroyed = true;
      if (hls) { try { hls.destroy(); } catch (e) { /* already gone */ } hls = null; }
      try { video.pause(); } catch (e) { /* nothing is playing */ }
      try { video.removeAttribute('src'); video.load(); } catch (e) { /* the element is already empty */ }
    };

    return self;
  }

  api.loadHls = loadHls;
  api.create = create;
})(globalThis);

/*
 * Read-ahead for a shared full game published as HLS. vod-player.js plays the game and owns the
 * token; this file loads before it.
 *
 * hls.js fetches one segment (about two seconds of the game) at a time, and each one waits far
 * longer for the storage's first byte than its bytes take to arrive. A skip past the buffer, a
 * jump, or 2x and 3x on a long route all wait on that first byte. Read-ahead fetches the
 * segments the viewer is about to need side by side, into the browser's own cache (a segment is
 * private, max-age=3600), where hls.js then finds them at once. No second copy is kept here.
 *
 *   segmentsBetween  the segments that hold a span of video seconds (pure)
 *   spans            what is wanted, most wanted first (pure): the second under a resting
 *                    pointer, the seconds around the playhead (skips both ways, 2x and 3x; hls.js
 *                    would fetch the 30 s ahead anyway), then the game's moments, nearest first
 *   queue            fetches them: a few side by side, fewer on a slow line, moments only while
 *                    the buffer is full, each segment at most once in 50 minutes
 *   attach           asks again as the viewer plays, skips, jumps and points at the lane
 *
 * Only what hls.js itself may fetch is fetched (vod-player.js warm): the same token, this
 * share's own segment URLs, only while loading runs. So nothing before the first play, at the
 * uploaded edge or after a stop, and no play is counted (only minting a token counts one).
 */
(function (root) {
  'use strict';

  var SEGMENT = /^\d{1,5}\.m4s$/;
  var WARM = {
    FIRST_S: 12,          // straight ahead first: the next skips forward land here
    BACK_S: 10,           // then just behind: a skip back after a jump
    AHEAD_S: 30,          // then where the longest skip (30 s) lands, and the rest hls.js buffers ahead
    BEHIND_S: 20,         // and a little further back, for a second skip back
    MOMENT_S: 1,          // a jump this close to a segment's end needs the next segment too
    MOMENTS: 60,
    EVERY_MS: 1000,       // while playing
    SETTLE_MS: 80,        // a drag along the lane seeks many times: wait for it to rest
    HOVER_MS: 100,        // the pointer rests on the lane: the jump under it may come next
    AGAIN_MS: 50 * 60000, // a browser keeps a segment for an hour
    LANES: 2,             // fetches side by side at first, then by how fast the bytes arrive
    LANES_MAX: 6,
    SLOW_S: 1,            // a segment's bytes took this long: one lane fewer
    FAST_S: 0.35,         // ... or this short: one lane more
    REST_MS: 20000,       // after a refusal, a failure, or a line too slow for even one lane
    PER_10S: 80,          // with hls.js's own requests, well under the proxy's 200 per 10 s rate rule
    IDLE_LEAD_S: 15,      // the game's moments wait until this much is buffered ahead
    IDLE_LANES: 3
  };

  function segmentEnd(fragment) {
    return Number(fragment && fragment.start) + Number(fragment && fragment.duration);
  }

  // The segments of `fragments` (hls.js's list: start, duration, url) that hold the video
  // seconds from..to, nearest `from` first (so to < from walks backwards). Only this share's
  // own "<base><n>.m4s" URLs come back: nothing else is ever fetched on a playlist's word.
  function segmentsBetween(fragments, base, from, to) {
    var out = [];
    var a = Number(from), b = Number(to);
    if (!fragments || !fragments.length || !base || !isFinite(a) || !isFinite(b)) return out;
    var lo = Math.min(a, b), hi = Math.max(a, b);
    var i = 0, j = fragments.length;
    while (i < j) {
      var mid = (i + j) >> 1;
      if (segmentEnd(fragments[mid]) > lo) j = mid; else i = mid + 1;
    }
    for (; i < fragments.length; i += 1) {
      var start = Number(fragments[i].start);
      if (!(start < hi || start <= lo)) break;
      var url = String(fragments[i].url || '');
      if (url.indexOf(base) === 0 && SEGMENT.test(url.slice(base.length))) out.push({ url: url, start: start, end: segmentEnd(fragments[i]) });
    }
    return b < a ? out.reverse() : out;
  }

  // What to fetch, most wanted first: [{ from, to, idle }] in video seconds.
  function spans(at, hoverAt, moments) {
    var p = Number(at) || 0;
    var out = [];
    if (typeof hoverAt === 'number' && isFinite(hoverAt)) out.push({ from: hoverAt, to: hoverAt + WARM.MOMENT_S });
    out.push({ from: p, to: p + WARM.FIRST_S }, { from: p, to: p - WARM.BACK_S }, { from: p + WARM.AHEAD_S, to: p + WARM.AHEAD_S + WARM.MOMENT_S },
      { from: p + WARM.FIRST_S, to: p + WARM.AHEAD_S }, { from: p - WARM.BACK_S, to: p - WARM.BEHIND_S });
    (moments || []).slice().sort(function (x, y) { return Math.abs(x - p) - Math.abs(y - p); }).slice(0, WARM.MOMENTS)
      .forEach(function (vt) { out.push({ from: vt, to: vt + WARM.MOMENT_S, idle: true }); });
    return out;
  }

  /*
   * queue({ root, video, fetch }) returns
   *   set(entries, skip)  entries: [{ url, start, end, idle }], most wanted first; replaces what
   *                       was queued. `skip` is the URL hls.js is fetching right now.
   *   fetched(url)        hls.js fetched this one itself: it is in the browser's cache too
   *   stop()              loading stopped: forget the queue
   *   reset()             a new token: every segment has a new address
   *   destroy()
   */
  function queue(o) {
    var win = o.root, video = o.video;
    var list = [], fetchedAt = {}, running = 0, lanes = WARM.LANES, restUntil = 0, starts = [], timer = null, alive = true;
    var aborter = typeof win.AbortController === 'function' ? new win.AbortController() : null;

    function bufferedAhead() {
      var at = video.currentTime || 0, b = video.buffered;
      for (var i = 0; b && i < b.length; i += 1) {
        if (b.start(i) <= at + 0.25 && b.end(i) > at) return b.end(i) - at;
      }
      return 0;
    }

    function inBuffer(entry) {
      var b = video.buffered;
      for (var i = 0; b && i < b.length; i += 1) {
        if (b.start(i) <= entry.start + 0.1 && b.end(i) >= entry.end - 0.1) return true;
      }
      return false;
    }

    function clear() {
      list = [];
      if (timer !== null) { win.clearTimeout(timer); timer = null; }
    }

    function later(ms) {
      if (timer !== null) win.clearTimeout(timer);
      timer = win.setTimeout(function () { timer = null; pump(); }, ms);
    }

    function withinRate(t) {
      while (starts.length && t - starts[0] >= 10000) starts.shift();
      return starts.length < WARM.PER_10S;
    }

    // A slow line gets fewer lanes, down to none for a while: read-ahead must never take the
    // bytes of the segment that plays next.
    function pace(bytes, seconds) {
      if (!(bytes > 200000)) return;
      if (seconds > WARM.SLOW_S) {
        lanes = Math.max(0, lanes - 1);
        if (!lanes) restUntil = Date.now() + WARM.REST_MS;
      } else if (seconds < WARM.FAST_S && lanes < WARM.LANES_MAX) lanes += 1;
    }

    function fetchSegment(url) {
      var gotAt = 0;
      var init = { priority: 'low' }; // behind the segment hls.js is fetching, where the browser can
      if (aborter) init.signal = aborter.signal;
      fetchedAt[url] = Date.now();
      starts.push(Date.now());
      running += 1;
      o.fetch(url, init).then(function (res) {
        if (!res || !res.ok) throw new Error('refused');
        gotAt = Date.now();
        // The cache keeps a segment only once its body has been read to the end.
        return typeof res.arrayBuffer === 'function' ? res.arrayBuffer() : null;
      }).then(function (body) {
        pace(body ? body.byteLength : 0, (Date.now() - gotAt) / 1000);
      }, function () {
        // Refused or failed: hls.js reports real problems; read-ahead only stops for a while.
        delete fetchedAt[url];
        clear();
        restUntil = Date.now() + WARM.REST_MS;
      }).then(function () {
        running -= 1;
        pump();
      });
    }

    function pump() {
      if (timer !== null) { win.clearTimeout(timer); timer = null; }
      if (!alive || !list.length) return;
      var t = Date.now();
      if (t < restUntil) { later(restUntil - t); return; }
      if (!lanes) lanes = 1;
      for (;;) {
        var full = bufferedAhead() >= WARM.IDLE_LEAD_S;
        var next = -1;
        for (var i = 0; i < list.length && next < 0; i += 1) {
          if (list[i].idle ? full && running < WARM.IDLE_LANES : running < lanes) next = i;
        }
        if (next < 0) break;
        if (!withinRate(t)) { later(1000); return; }
        fetchSegment(list.splice(next, 1)[0].url);
      }
      // The moments wait for a full buffer: look again in a moment.
      if (list.length && !running) later(1000);
    }

    return {
      set: function (entries, skip) {
        var seen = {}, t = Date.now();
        list = [];
        (alive ? entries || [] : []).forEach(function (entry) {
          if (seen[entry.url] || entry.url === skip || inBuffer(entry)) return;
          if (fetchedAt[entry.url] !== undefined && t - fetchedAt[entry.url] < WARM.AGAIN_MS) return;
          seen[entry.url] = true;
          list.push({ url: entry.url, idle: entry.idle === true });
        });
        pump();
      },
      fetched: function (url) { if (url) fetchedAt[url] = Date.now(); },
      stop: clear,
      reset: function () { clear(); fetchedAt = {}; },
      destroy: function () {
        alive = false;
        clear();
        if (aborter) { try { aborter.abort(); } catch (e) { /* nothing in flight */ } }
      }
    };
  }

  /*
   * attach({ root, video, warm(spans), moments() }) returns { hover(vt) }.
   * warm(null) says whether read-ahead may run now; warm(spans) queues them. moments() gives
   * the video seconds of the game's uploaded moments. hover(vt) is the second under the pointer
   * on the lane, or null once it leaves.
   */
  function attach(o) {
    var win = o.root, video = o.video;
    var hoverAt = null, hoverTimer = null, settleTimer = null, askedAt = -Infinity;

    // A phone on mobile data, or a browser asked to save data, fetches only what it plays.
    function sparing() {
      var connection = win.navigator && win.navigator.connection;
      return !!(connection && (connection.saveData === true || connection.type === 'cellular'));
    }

    function can() { return !sparing() && o.warm(null) === true; }

    function ask() {
      if (!can()) return;
      askedAt = Date.now();
      o.warm(spans(video.currentTime || 0, hoverAt, o.moments()));
    }

    function soon() {
      if (!can()) return;
      if (settleTimer !== null) win.clearTimeout(settleTimer);
      settleTimer = win.setTimeout(function () { settleTimer = null; ask(); }, WARM.SETTLE_MS);
    }

    function hover(vt) {
      hoverAt = typeof vt === 'number' && isFinite(vt) ? vt : null;
      if (hoverTimer !== null) { win.clearTimeout(hoverTimer); hoverTimer = null; }
      if (hoverAt === null || !can()) return;
      hoverTimer = win.setTimeout(function () { hoverTimer = null; ask(); }, WARM.HOVER_MS);
    }

    video.addEventListener('playing', ask);
    video.addEventListener('timeupdate', function () { if (Date.now() - askedAt >= WARM.EVERY_MS) ask(); });
    video.addEventListener('seeking', soon);

    return { hover: hover };
  }

  root.RevuVodWarm = { WARM: WARM, segmentsBetween: segmentsBetween, spans: spans, queue: queue, attach: attach };
})(globalThis);

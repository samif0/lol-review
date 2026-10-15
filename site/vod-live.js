/*
 * The controller for a shared full game published as HLS (format "hls"). vod.js hands it
 * every /vod-meta reply and the page's shared views; it decides what the page shows, how
 * often /vod-meta is asked, when the player starts and when it waits at the uploaded edge.
 *
 *   waiting   not playable yet: "Starting soon" and a Play when ready toggle; polls every
 *             2 s for a minute, 10 s until 10 min, then 30 s
 *   ready     playable, not started: a Play from the start button over the player; polls
 *             every 10 s so the uploaded edge and the moments move before anyone presses play
 *   playing   hls.js reloads the playlist; /vod-meta every 30 s for the rest
 *   hold      caught up with the upload: loading stops and /vod-meta is asked every 15 s.
 *             With an upload slower than playback (and the rebuffer policy on) playback
 *             pauses until it has a 30 s lead (60 s below 0.7x)
 *   finished  the game plays like any video; a game that ended early says so
 *
 * Nothing is asked while the tab is hidden (vod.js holds the poll), and a paused player in a
 * hidden tab stops loading too. A play is counted only when the viewer starts one.
 */
(function (root) {
  'use strict';

  var V = root.RevuVod;
  var P = root.RevuVodPlayer;
  // Line kinds a screen reader hears when the line changes to them.
  var ANNOUNCE = { ready: 1, slow: 1, caughtup: 1, paused: 1, stalled: 1, ended: 1, truncated: 1, stopped: 1 };

  /*
   * page: { video, doc, api, id, $(id), fetch(url, init), fetchMeta(), poll(ms), stopPolling(),
   *   online(), onOnline(fn), showState(name), waiting(view), renderGame(meta), header(meta),
   *   gameTitle(), moments({ length, live, choose }), playhead(), message(text, retry) }
   */
  function create(page) {
    var video = page.video, doc = page.doc, $ = page.$;
    var overlay = $('vod-play'), overlayLabel = $('vod-play-label');
    var lineEl = $('vod-live'), lineBefore = $('vod-live-text'), lineAction = $('vod-live-action'), lineAfter = $('vod-live-after');
    var jumpEl = $('vod-jump'), jumpText = $('vod-jump-text'), jumpCancel = $('vod-jump-cancel');
    var sayEl = $('vod-live-say'), toggleRow = $('vod-when-ready-row'), toggle = $('vod-when-ready');
    var fill = $('vod-lane-uploaded');
    var caps = P.capsOf(root, video);

    var meta = null, kind = '', engine = null;
    var mode = '';               // 'waiting', 'wait' (no MediaSource), 'game', 'blocked' or 'done'
    var started = false, startAt = 0, resumeAt = null;
    var ended = false, sawLive = false, gone = false, atEdge = false;
    var hlsEdge = 0, hold = null, rebufferOn = true, armed = null, whenReady = false, endKnown = true;
    var waitSince = 0, softAt = -Infinity, said = { kind: '', last: '', at: -Infinity };

    function now() { return Date.now(); }

    function playQuietly(onRefused) {
      var result;
      try { result = video.play(); } catch (e) { result = null; }
      if (result && typeof result.then === 'function') {
        result.then(null, function (error) { if (onRefused && video.paused && error && error.name === 'NotAllowedError') onRefused(); });
      }
    }

    function say(message) {
      if (message && sayEl.textContent !== message) sayEl.textContent = message;
    }

    function edge() { return Math.max(V.edgeOf(meta), hlsEdge); }

    function lead() { return edge() - (video.currentTime || 0); }

    function laneLength() {
      if (ended && isFinite(video.duration) && video.duration > 0) return video.duration;
      return Number(meta && meta.duration_s) || edge();
    }

    // Polling.

    function phase() {
      if (mode === 'waiting') return 'waiting';
      if (mode === 'wait') return meta && meta.playable === true ? 'ready' : 'waiting';
      if (mode !== 'game' || ended || gone) return '';
      if (!started) return 'ready';
      return hold ? 'hold' : 'playing';
    }

    function schedule() {
      var delay = V.hlsPollDelay(phase(), now() - waitSince);
      if (delay) page.poll(delay); else page.stopPolling();
    }

    // What the page shows.

    function showOverlay(at) {
      overlayLabel.textContent = at > 0 ? 'Play' : 'Play from the start';
      overlay.hidden = false;
      video.controls = false;
    }

    function hideOverlay() {
      overlay.hidden = true;
      video.controls = true;
    }

    function renderMoments() {
      if (mode !== 'game') return;
      var live = ended || gone ? null : { edge: edge(), rate: V.pausedOf(meta) || V.stalledOf(meta) ? null : V.rateOf(meta),
        armed: armed ? armed.vt : null };
      var length = laneLength();
      page.moments({ length: length, live: live, choose: choose });
      fill.hidden = !live;
      fill.style.width = live && length > 0 ? Math.min(100, (live.edge / length) * 100) + '%' : '0%';
      jumpEl.hidden = !armed;
      if (armed) jumpText.textContent = V.jumpText(armed);
    }

    function lineStatus() {
      if (mode !== 'game' || !meta) return null;
      // A game finished before this page opened says nothing unless it ended early; one that
      // just finished says so once /vod-meta has told whether it ended early.
      if (ended && (!endKnown || (!sawLive && meta.truncated !== true))) return null;
      return V.liveLine({ gone: gone, ended: ended, truncated: meta.truncated === true, paused: V.pausedOf(meta),
        stalled: V.stalledOf(meta), started: started, hold: hold && hold.kind, atEdge: atEdge, edge: edge(),
        duration: Number(meta.duration_s) || edge(), rate: V.rateOf(meta), position: started ? video.currentTime : startAt,
        rebufferOn: rebufferOn, nowMs: now() });
    }

    function renderLine() {
      var status = lineStatus();
      lineEl.hidden = !status;
      if (!status) return;
      if (lineBefore.textContent !== status.before) lineBefore.textContent = status.before;
      lineAction.hidden = !status.action;
      if (lineAction.textContent !== status.action) lineAction.textContent = status.action;
      if (lineAfter.textContent !== status.after) lineAfter.textContent = status.after;
      if (status.kind !== said.kind && ANNOUNCE[status.kind] && (status.kind !== said.last || now() - said.at >= 10000)) {
        say(status.text);
        said.last = status.kind;
        said.at = now();
      }
      said.kind = status.kind;
      if (!started && !ended) doc.title = 'Ready to watch · ' + page.gameTitle();
    }

    function render() {
      renderMoments();
      renderLine();
    }

    // Engine.

    function newEngine() {
      if (engine) engine.destroy();
      engine = P.create(video, { kind: kind, api: page.api, id: page.id, doc: doc, fetch: page.fetch, online: page.online,
        onEdge: onEdge, onProblem: onProblem });
    }

    // Loading runs unless playback waits at the edge, the game is gone, or a paused player
    // sits in a hidden tab.
    function syncLoad() {
      if (!engine || !started) return;
      if (hold || gone || (doc.hidden && video.paused)) engine.stop();
      else engine.resume(video.currentTime || startAt);
    }

    // The first start: from a press (overlay, a moment, Try again) or by itself (Play when
    // ready, a moment that arrived). The engine mints the token, which counts the play.
    function start(at) {
      if (mode !== 'game' || !engine) return;
      startAt = at > 0 ? at : 0;
      hideOverlay();
      if (!started) {
        started = true;
        doc.title = page.gameTitle() + ' - Revu';
        engine.begin(startAt);
        schedule();
        render();
      }
      playQuietly(function () { showOverlay(startAt); });
    }

    // No press to lean on: Chromium and Firefox may start after an earlier click; Safari shows the button.
    function autoStart(at) {
      if (P.autoplayAfterClick(caps)) start(at);
      else { startAt = at > 0 ? at : 0; showOverlay(startAt); }
    }

    function jumpTo(vt, pressed) {
      if (!started) { if (pressed) start(vt); else autoStart(vt); return; }
      try { video.currentTime = vt; } catch (e) { /* not seekable yet */ }
      hold = null;
      syncLoad();
      schedule();
      playQuietly();
      render();
    }

    // A moment on the lane or in the notes. One that isn't uploaded yet arms "jump there when
    // it arrives"; pressing it again cancels.
    function choose(item) {
      if (mode !== 'game' || !engine) return;
      if (item.pending && !ended) {
        armed = armed && armed.vt === item.vt ? null : item;
        if (armed) say(V.jumpText(item));
        renderMoments();
        return;
      }
      armed = null;
      jumpTo(item.vt, true);
    }

    function checkArmed() {
      if (!armed || (!ended && armed.vt + V.MOMENT_LEAD_S > edge())) return;
      var item = armed;
      armed = null;
      jumpTo(item.vt, false);
    }

    // What playing from here would do at the uploaded edge (hls.js only; null: keep loading).
    function nextHold() {
      if (ended || gone || !engine || engine.kind !== 'hlsjs') return null;
      return V.holdAtEdge({ lead: lead(), rate: V.rateOf(meta), rebufferOn: rebufferOn, paused: V.pausedOf(meta),
        stalled: V.stalledOf(meta) });
    }

    // Stop loading at the uploaded edge when the upload can't keep up; start again once it has.
    function checkHold() {
      if (mode !== 'game' || !engine || !started || gone) return;
      if (hold) {
        if (ended || lead() >= hold.lead) release();
        return;
      }
      if (video.paused) return;
      var next = nextHold();
      if (!next) return;
      hold = next;
      if (hold.kind === 'rebuffer') video.pause();
      syncLoad();
      schedule();
      renderLine();
    }

    function release() {
      var was = hold;
      hold = null;
      syncLoad();
      schedule();
      if (was && was.kind === 'rebuffer') playQuietly();
      renderLine();
    }

    // "Play now with pauses": no more waiting for a lead. At the edge, playback goes on as
    // soon as the next bit arrives.
    function playNow() {
      rebufferOn = false;
      if (!started) { start(startAt); return; }
      hold = nextHold();
      syncLoad();
      schedule();
      playQuietly();
      renderLine();
    }

    function onEdge(at, endlist) {
      if (mode !== 'game') return;
      if (at > hlsEdge) hlsEdge = at;
      if (endlist && !ended) {
        endKnown = false;
        finished();
        page.fetchMeta().then(function (fresh) { endKnown = true; update(fresh); },
          function () { endKnown = true; renderLine(); });
        return;
      }
      checkArmed();
      checkHold();
      render();
    }

    // The upload is complete (or ended early): everything uploaded can play.
    function finished() {
      ended = true;
      if (!started) {
        var next = P.choose(caps, true);
        if (next !== kind) { kind = next; newEngine(); }
      }
      if (hold) release();
      checkArmed();
      schedule();
      render();
    }

    // Problems.

    function block(message, canRetry) {
      if (engine) { engine.destroy(); engine = null; }
      mode = canRetry ? 'blocked' : 'done';
      hold = null;
      armed = null;
      overlay.hidden = true;
      lineEl.hidden = true;
      jumpEl.hidden = true;
      fill.hidden = true;
      doc.title = page.gameTitle() + ' - Revu';
      page.stopPolling();
      page.message(message, canRetry ? retry : null);
    }

    // Before any play the page itself becomes the message (as for an MP4 share).
    function refuse(state) {
      if (engine) { engine.destroy(); engine = null; }
      mode = 'done';
      page.showState(state);
    }

    function goneNow() {
      if (!started || mode !== 'game') { refuse('stopped'); return; }
      if (gone) return;
      gone = true;
      armed = null;
      syncLoad();
      page.stopPolling();
      render();
      checkGone();
    }

    // A stopped share keeps playing what is buffered, then says it stopped.
    function checkGone() {
      if (!gone || mode !== 'game') return;
      if (V.bufferedAhead(video.buffered, video.currentTime) > 0.5 && !video.ended) return;
      block(V.STATES.stopped.copy, false);
    }

    // Ask /vod-meta why something failed. soft: hls.js is still retrying, so only a definite
    // answer (playback switched off, or the share gone) changes the page.
    function recheck(soft) {
      page.fetchMeta().then(function (fresh) {
        if (mode !== 'game') return;
        if (fresh.serving === false) block(V.STATES.paused.copy, true);
        else if (!soft) block(V.UNPLAYABLE, true);
      }, function (error) {
        if (mode !== 'game') return;
        if (error && error.status === 404) goneNow();
        else if (!soft) block(page.online() === false ? V.OFFLINE : V.UNPLAYABLE, true);
      });
    }

    function onProblem(info) {
      if (mode !== 'game') return;
      if (!info.fatal) {
        // hls.js retries a 5xx; one soft recheck tells a switched-off share from a blip.
        if (now() - softAt >= 30000) { softAt = now(); recheck(true); }
        return;
      }
      if (info.kind === 'limited') block(V.STATES.limited.copy, false);
      else if (info.kind === 'unsupported') block(V.UNSUPPORTED, false);
      else if (info.kind === 'offline') { block(V.OFFLINE, true); page.onOnline(function () { if (mode === 'blocked') retry(); }); }
      else if (info.kind === 'paused') block(V.STATES.paused.copy, true);
      else if (info.kind === 'gone') { if (info.during === 'mint') recheck(false); else goneNow(); }
      else if (info.kind === 'notready') page.fetchMeta().then(update, function () { block(V.UNPLAYABLE, true); });
      else if (info.during === 'load') block(V.UNPLAYABLE, true);
      else recheck(false);
    }

    // "Try again" (a press): a fresh engine on a fresh reply, playing from where the viewer was.
    function retry() {
      if (mode !== 'blocked') return;
      resumeAt = started ? (video.currentTime || startAt) : startAt;
      page.message('');
      mode = 'retrying';
      page.fetchMeta().then(function (fresh) {
        if (mode !== 'retrying') return;
        started = false;
        hold = null;
        gone = false;
        hlsEdge = 0;
        mode = '';
        update(fresh);
      }, function (error) {
        if (mode !== 'retrying') return;
        mode = 'blocked';
        if (error && error.status === 404) refuse('stopped');
        else page.message(page.online() === false ? V.OFFLINE : V.UNPLAYABLE, retry);
      });
    }

    // Meta replies.

    function showWaiting() {
      if (!waitSince) waitSince = now();
      if (kind === 'wait') {
        mode = 'wait';
        var words = V.uploadText(meta);
        page.waiting({ game: V.uploadGame(meta), title: words.title, copy: words.copy, percent: V.uploadPercent(meta) });
        toggleRow.hidden = true;
      } else {
        mode = 'waiting';
        var wait = V.WAITING[V.waitingKind(meta, now() / 1000)];
        page.waiting({ game: V.uploadGame(meta), title: wait.title, copy: wait.copy, percent: null });
        toggleRow.hidden = false;
        // The game is seconds from playable: fetch the player while waiting.
        P.loadHls(doc).then(null, function () { /* the game page says so if it stays unavailable */ });
      }
      schedule();
    }

    function enterGame() {
      mode = 'game';
      if (!page.renderGame(meta)) { mode = 'done'; return; }
      if (kind === 'none') { block(V.UNSUPPORTED, false); return; }
      newEngine();
      showOverlay(startAt);
      render();
      if (resumeAt !== null) { var at = resumeAt; resumeAt = null; start(at); }
      else if (whenReady) autoStart(0);
      schedule();
    }

    function update(fresh) {
      if (mode === 'done' || mode === 'blocked' || mode === 'retrying') return;
      if (!fresh || typeof fresh !== 'object') { schedule(); return; }
      var state = V.metaState(fresh);
      // The cap only refuses new plays: whoever is watching keeps watching.
      if (state === 'limited' && started) state = V.hlsEnded(fresh) ? 'ok' : 'live';
      if (state === 'paused' || state === 'limited' || state === 'missing') {
        // On the game page it is the player's message; before that, the whole page.
        if (state !== 'missing' && (mode === 'game' || resumeAt !== null)) block(V.STATES[state].copy, state === 'paused');
        else refuse(state);
        return;
      }
      meta = fresh;
      if (state === 'live') sawLive = true;
      if (mode === 'game') {
        page.header(meta);
        if (state === 'ok' && !ended) { finished(); return; }
        checkArmed();
        checkHold();
        render();
        schedule();
        return;
      }
      ended = state === 'ok';
      kind = P.choose(caps, ended);
      if (kind !== 'none' && (state === 'waiting' || (kind === 'wait' && !ended))) { showWaiting(); return; }
      enterGame();
    }

    // Wiring.

    overlay.addEventListener('click', function () { start(startAt); if (typeof video.focus === 'function') video.focus(); });
    lineAction.addEventListener('click', playNow);
    jumpCancel.addEventListener('click', function () { armed = null; renderMoments(); });
    toggle.addEventListener('change', function () { whenReady = !!toggle.checked; });
    video.addEventListener('play', function () {
      if (mode !== 'game' || !engine) return;
      if (!started) { start(video.currentTime || startAt); return; }
      // Play pressed while the page waits for a lead: at the edge, the viewer would rather play
      // now with pauses; further back, the policy stays on.
      if (hold && hold.kind === 'rebuffer') {
        if (lead() < V.EDGE_NEAR_S) rebufferOn = false;
        hold = nextHold();
        schedule();
      }
      syncLoad();
      renderLine();
    });
    video.addEventListener('pause', syncLoad);
    video.addEventListener('playing', function () { if (mode !== 'game') return; atEdge = false; hideOverlay(); renderLine(); });
    video.addEventListener('waiting', function () {
      if (mode !== 'game') return;
      atEdge = !ended && lead() < V.EDGE_NEAR_S;
      checkGone();
      checkHold();
      renderLine();
    });
    video.addEventListener('timeupdate', function () {
      if (mode !== 'game') return;
      page.playhead();
      checkGone();
      checkHold();
      renderLine();
    });
    video.addEventListener('seeking', function () { if (mode === 'game') { atEdge = false; checkHold(); syncLoad(); } });
    video.addEventListener('ended', checkGone);
    video.addEventListener('durationchange', function () { if (mode === 'game' && ended) renderMoments(); });
    doc.addEventListener('visibilitychange', syncLoad);

    return {
      update: update,
      // A /vod-meta poll failed: 404 is a stopped share; anything else asks again later.
      pollFailed: function (error) {
        if (mode === 'done' || mode === 'blocked' || mode === 'retrying') return;
        if (error && error.status === 404) goneNow(); else schedule();
      },
      // The page became a message (the link expired, or a refusal): stop everything.
      stop: function () {
        if (engine) { engine.destroy(); engine = null; }
        mode = 'done';
      }
    };
  }

  root.RevuVodLive = { create: create };
})(globalThis);

/*
 * The shared game page's player controls (vod.html), shaped like the desktop app's VOD
 * reviewer (desktop/ui/vodtransport.js): step back and forward by a step that Up and Down
 * change (1, 2, 5, 10 or 30 s), play and pause, the time with the step, speed, mute, the pen
 * and full screen, with the same keys. The moments lane under the picture is the seek bar:
 * press or drag anywhere on it. While a game uploads, a seek stops at the uploaded edge.
 *
 * RevuVodControls.attach({ doc, video, deck, bar, track, fill, playhead, tip, length, edge, start, ink })
 * builds the bar into `bar` and keeps the lane's fill and playhead with the picture, a seek
 * included. `length()` is the lane's length in seconds, `edge()` the uploaded
 * edge (null once everything is uploaded), `start()` presses the page's own Play when the game
 * hasn't started (it mints the token and counts the play), `ink` is RevuVodInk's handle.
 * clock, nextIndex and seekTarget are pure and exported for the tests.
 */
(function (root) {
  'use strict';

  var STEP_CHOICES = [1, 2, 5, 10, 30];
  var RATE_CHOICES = [0.25, 0.5, 1, 1.5, 2, 3];
  var FRAME = 1 / 60;
  var EDGE_MARGIN = 0.5;
  var SVG_NS = 'http://www.w3.org/2000/svg';
  // The desktop transport's icons (desktop/ui/vodtransport.js), plus the pen.
  var ICONS = {
    back: { paths: ['M11 6l-6 6 6 6', 'M19 6l-6 6 6 6'] },
    forward: { paths: ['M13 6l6 6-6 6', 'M5 6l6 6-6 6'] },
    play: { paths: ['M7 5v14l11-7z'], fill: true },
    pause: { paths: ['M8 5v14', 'M16 5v14'] },
    volume: { paths: ['M4 9h4l5-4v14l-5-4H4z', 'M16.5 8.5a5 5 0 0 1 0 7', 'M19 6a8.5 8.5 0 0 1 0 12'] },
    muted: { paths: ['M4 9h4l5-4v14l-5-4H4z', 'M17 9.5l5 5', 'M22 9.5l-5 5'] },
    enlarge: { paths: ['M8 3H3v5', 'M16 3h5v5', 'M3 16v5h5', 'M21 16v5h-5'] },
    restore: { paths: ['M3 8h5V3', 'M21 8h-5V3', 'M3 16h5v5', 'M21 16h-5v5'] },
    pen: { paths: ['M4 20l4.5-1L19 8.5 15.5 5 5 15.5z', 'M13.5 7l3.5 3.5'] }
  };

  function clock(s) {
    s = Math.max(0, Math.floor(Number(s) || 0));
    var h = Math.floor(s / 3600);
    var m = Math.floor((s % 3600) / 60);
    var sec = String(s % 60).padStart(2, '0');
    return h > 0 ? h + ':' + String(m).padStart(2, '0') + ':' + sec : m + ':' + sec;
  }

  // The next index in a list of choices, kept in range.
  function nextIndex(index, delta, count) {
    return Math.max(0, Math.min(count - 1, index + delta));
  }

  // Where a seek lands: inside the game, and never past what has uploaded so far.
  function seekTarget(t, length, edge) {
    var end = length > 0 ? length : Infinity;
    if (edge !== null && edge !== undefined && isFinite(edge)) end = Math.min(end, Math.max(0, edge - EDGE_MARGIN));
    return Math.max(0, Math.min(end, Number(t) || 0));
  }

  function editable(target) {
    if (!target || !target.tagName) return false;
    var tag = String(target.tagName).toUpperCase();
    return tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA' || target.isContentEditable === true;
  }

  function attach(o) {
    var doc = o.doc;
    var video = o.video;
    var deck = o.deck;
    var bar = o.bar;
    var track = o.track;
    var length = o.length;
    var edge = typeof o.edge === 'function' ? o.edge : function () { return null; };
    var ink = o.ink || null;
    var stepIndex = 2;

    function svg(name) {
      var spec = ICONS[name];
      var el = doc.createElementNS(SVG_NS, 'svg');
      var attrs = { viewBox: '0 0 24 24', width: '16', height: '16', 'aria-hidden': 'true', focusable: 'false',
        fill: spec.fill ? 'currentColor' : 'none', stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round',
        'stroke-linejoin': 'round' };
      Object.keys(attrs).forEach(function (key) { el.setAttribute(key, attrs[key]); });
      spec.paths.forEach(function (d) {
        var path = doc.createElementNS(SVG_NS, 'path');
        path.setAttribute('d', d);
        el.appendChild(path);
      });
      return el;
    }

    function setIcon(button, name) {
      if (button.dataset.icon === name) return;
      button.dataset.icon = name;
      var old = button.querySelector('svg');
      var fresh = svg(name);
      if (old) button.replaceChild(fresh, old);
      else button.insertBefore(fresh, button.firstChild);
    }

    function tbtn(name, label, title, keyHint, onPress) {
      var b = doc.createElement('button');
      b.type = 'button';
      b.className = 'tbtn';
      b.setAttribute('aria-label', label);
      b.title = title;
      setIcon(b, name);
      if (keyHint) {
        var kbd = doc.createElement('kbd');
        kbd.setAttribute('aria-hidden', 'true');
        kbd.textContent = keyHint;
        b.appendChild(kbd);
      }
      b.addEventListener('click', onPress);
      // A click leaves the keyboard where it was, so Space still plays and pauses.
      b.addEventListener('mousedown', function (ev) { ev.preventDefault(); });
      return b;
    }

    while (bar.firstChild) bar.removeChild(bar.firstChild);
    var back = tbtn('back', 'Back one step', 'Back one step (Left). Up and Down change the step.', '←', function () { step(-1); });
    var play = tbtn('play', 'Play', 'Play or pause (Space or K)', null, function () { togglePlay(); });
    play.classList.add('play');
    var fwd = tbtn('forward', 'Forward one step', 'Forward one step (Right). Up and Down change the step.', '→', function () { step(1); });
    var time = doc.createElement('span');
    time.className = 'ttime';
    var spring = doc.createElement('span');
    spring.className = 'tspring';
    var rate = doc.createElement('select');
    rate.className = 'vp-rate-sel';
    rate.setAttribute('aria-label', 'Playback speed');
    rate.title = 'Playback speed (- slower, + faster)';
    RATE_CHOICES.forEach(function (value) {
      var option = doc.createElement('option');
      option.value = String(value);
      option.textContent = value + '×';
      if (value === 1) option.selected = true;
      rate.appendChild(option);
    });
    rate.addEventListener('change', function () { video.playbackRate = Number(rate.value) || 1; });
    var mute = tbtn('volume', 'Mute', 'Mute (M)', null, function () { video.muted = !video.muted; });
    var pen = tbtn('pen', 'Draw on the video', 'Draw on the video (D)', 'D', function () { if (ink) ink.toggle(); });
    pen.classList.add('tpen');
    var full = tbtn('enlarge', 'Full screen', 'Full screen (F)', null, function () { toggleFull(); });
    [back, play, fwd, time, spring, rate, mute, pen, full].forEach(function (el) { bar.appendChild(el); });
    if (!ink) pen.hidden = true;

    function duration() {
      var d = Number(length()) || 0;
      if (d > 0) return d;
      return isFinite(video.duration) && video.duration > 0 ? video.duration : 0;
    }

    function readout() {
      while (time.firstChild) time.removeChild(time.firstChild);
      time.appendChild(doc.createTextNode(clock(video.currentTime) + ' / ' + clock(duration())));
      var stepTag = doc.createElement('span');
      stepTag.className = 'tstep';
      stepTag.textContent = 'step ' + STEP_CHOICES[stepIndex] + 's';
      time.appendChild(stepTag);
    }

    function paint() {
      var d = duration();
      var pct = (d > 0 ? Math.max(0, Math.min(1, (video.currentTime || 0) / d)) : 0) * 100 + '%';
      if (o.fill) o.fill.style.width = pct;
      if (o.playhead) o.playhead.style.left = pct;
      readout();
    }

    function glyphs() {
      setIcon(play, video.paused ? 'play' : 'pause');
      play.setAttribute('aria-label', video.paused ? 'Play' : 'Pause');
      setIcon(mute, video.muted ? 'muted' : 'volume');
      mute.setAttribute('aria-label', video.muted ? 'Unmute' : 'Mute');
      var isFull = fullElement() === deck;
      setIcon(full, isFull ? 'restore' : 'enlarge');
      full.setAttribute('aria-label', isFull ? 'Exit full screen' : 'Full screen');
      deck.classList.toggle('is-full', isFull);
    }

    function quietly(promise) { if (promise && typeof promise.catch === 'function') promise.catch(function () {}); }

    function started() { return typeof o.started === 'function' ? o.started() : true; }

    function togglePlay() {
      if (!started()) { if (typeof o.start === 'function') o.start(); return; }
      if (video.paused) quietly(video.play());
      else video.pause();
    }

    function seekTo(t) {
      if (!started()) return;
      try { video.currentTime = seekTarget(t, duration(), edge()); } catch (e) { /* not seekable yet */ }
      paint();
    }

    function step(direction) { seekTo((video.currentTime || 0) + direction * STEP_CHOICES[stepIndex]); }

    function frameStep(direction) {
      if (!video.paused) video.pause();
      seekTo((video.currentTime || 0) + direction * FRAME);
    }

    function setRate(delta) {
      var current = RATE_CHOICES.indexOf(Number(rate.value));
      var next = RATE_CHOICES[nextIndex(current < 0 ? 2 : current, delta, RATE_CHOICES.length)];
      rate.value = String(next);
      video.playbackRate = next;
    }

    function fullElement() { return doc.fullscreenElement || doc.webkitFullscreenElement || null; }

    function toggleFull() {
      if (fullElement()) {
        quietly(doc.exitFullscreen ? doc.exitFullscreen() : doc.webkitExitFullscreen && doc.webkitExitFullscreen());
        return;
      }
      if (deck.requestFullscreen) quietly(deck.requestFullscreen());
      else if (deck.webkitRequestFullscreen) deck.webkitRequestFullscreen();
      else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen();
    }

    // The seek bar: press or drag anywhere on the lane; a moment's own button still jumps.
    var dragging = null;
    function atPointer(ev) {
      var r = track.getBoundingClientRect();
      var f = r.width > 0 ? Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width)) : 0;
      return f * duration();
    }
    // The chip under the lane: the time under the pointer, or the moment it is on ("Dragon at 5:12").
    function showTip(ev) {
      if (!o.tip || !(duration() > 0)) return;
      var r = track.getBoundingClientRect();
      var f = r.width > 0 ? Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width)) : 0;
      var mark = ev.target && ev.target.closest ? ev.target.closest('.vod-mark') : null;
      o.tip.textContent = (mark && mark.getAttribute('aria-label')) || clock(f * duration());
      o.tip.style.left = f * 100 + '%';
      o.tip.hidden = false;
    }
    track.addEventListener('pointerdown', function (ev) {
      if (ev.button > 0 || (ev.target && ev.target.closest && ev.target.closest('.vod-mark'))) return;
      if (!started()) return;
      dragging = { id: ev.pointerId };
      try { track.setPointerCapture(ev.pointerId); } catch (e) { /* not capturable */ }
      seekTo(atPointer(ev));
      showTip(ev);
    });
    track.addEventListener('pointermove', function (ev) {
      showTip(ev);
      if (dragging) seekTo(atPointer(ev));
    });
    function endDrag() { dragging = null; }
    track.addEventListener('pointerup', endDrag);
    track.addEventListener('pointercancel', endDrag);
    track.addEventListener('pointerleave', function () { if (o.tip && !dragging) o.tip.hidden = true; });

    ['timeupdate', 'durationchange', 'loadedmetadata', 'seeked'].forEach(function (name) { video.addEventListener(name, paint); });
    ['play', 'pause', 'volumechange'].forEach(function (name) { video.addEventListener(name, glyphs); });
    ['fullscreenchange', 'webkitfullscreenchange'].forEach(function (name) { doc.addEventListener(name, function () { glyphs(); if (ink) ink.layout(); }); });
    video.addEventListener('ratechange', function () { rate.value = String(video.playbackRate); });
    // A click on the picture plays and pauses, as on the desktop (not while drawing).
    video.addEventListener('click', function () { if (!(ink && ink.active())) togglePlay(); });

    doc.addEventListener('keydown', function (ev) {
      if (ev.defaultPrevented || editable(ev.target)) return;
      if (ink && ink.key(ev)) { ev.preventDefault(); return; }
      if (ev.ctrlKey || ev.metaKey || ev.altKey) return;
      var k = ev.key;
      var handled = true;
      if (k === ' ' || k === 'k' || k === 'K') {
        // Space on a focused button presses that button instead.
        if (k === ' ' && ev.target && ev.target.tagName === 'BUTTON') return;
        togglePlay();
      } else if (k === 'ArrowLeft') step(-1);
      else if (k === 'ArrowRight') step(1);
      else if (k === 'ArrowUp' || k === 'ArrowDown') { stepIndex = nextIndex(stepIndex, k === 'ArrowUp' ? 1 : -1, STEP_CHOICES.length); readout(); }
      else if (k === ',') frameStep(-1);
      else if (k === '.') frameStep(1);
      else if (k === '-' || k === '_') setRate(-1);
      else if (k === '=' || k === '+') setRate(1);
      else if (k === 'm' || k === 'M') video.muted = !video.muted;
      else if (k === 'f' || k === 'F') toggleFull();
      else if ((k === 'd' || k === 'D') && ink) ink.toggle();
      else handled = false;
      if (handled) ev.preventDefault();
    });

    paint();
    glyphs();
    return {
      paint: paint,
      penPressed: function (on) { pen.setAttribute('aria-pressed', String(!!on)); deck.classList.toggle('is-drawing', !!on); },
      step: step,
      seekTo: seekTo,
      togglePlay: togglePlay
    };
  }

  root.RevuVodControls = { attach: attach, clock: clock, nextIndex: nextIndex, seekTarget: seekTarget,
    STEP_CHOICES: STEP_CHOICES, RATE_CHOICES: RATE_CHOICES };
})(typeof window !== 'undefined' ? window : this);

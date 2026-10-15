/*
 * Coach tools on the shared clip page (clip.html): the pen (vod-ink.js) over the clip, with D
 * and the Draw button, and review notes (review-notes.js): bookmarks (B) and clips inside the
 * clip (I, then O), kept in this browser and sent on as a link with Copy link with notes. A
 * clip plays to its end and pauses there.
 *
 * The player itself is clip.html's inline script, pinned by hash in _headers; this script only
 * adds to it. Its keys run first (a capture listener on the window), so while the pen is out
 * the pen's keys (C clears, [ and ] change the line) never reach the player's (C captions,
 * [ and ] speed), and nothing typed in a note does either.
 */
(function (root) {
  'use strict';

  if (typeof document === 'undefined') return;
  var N = root.RevuNotes;
  var Ui = root.RevuNotesUi;
  var Ink = root.RevuVodInk;
  var $ = function (name) { return document.getElementById(name); };
  var video = $('clip-video');
  var page = $('state-clip');
  if (!video || !page) return;

  // The clip's id as clip.html reads it (?id=, else a 7-character path), when it is one.
  function clipId() {
    var id = '';
    try { id = (new URLSearchParams(location.search).get('id') || '').trim(); } catch (e) { id = ''; }
    if (!id) id = String(location.pathname || '').replace(/^\/+|\/+$/g, '');
    return /^[0-9A-Za-z]{7}$/.test(id) ? id : '';
  }

  function clock(seconds) {
    var s = Math.max(0, Math.floor(Number(seconds) || 0));
    var h = Math.floor(s / 3600);
    var m = Math.floor((s % 3600) / 60);
    var sec = String(s % 60).padStart(2, '0');
    return h > 0 ? h + ':' + String(m).padStart(2, '0') + ':' + sec : m + ':' + sec;
  }

  function length(seconds) {
    var s = Math.max(0, Math.round(Number(seconds) || 0));
    return s < 60 ? s + 's' : clock(s);
  }

  function isEditable(target) {
    if (Ui) return Ui.editable(target);
    var tag = target && target.tagName ? String(target.tagName).toUpperCase() : '';
    return tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA' || !!(target && target.isContentEditable);
  }

  function duration() {
    return isFinite(video.duration) && video.duration > 0 ? video.duration : 0;
  }

  function keepFocus(ev) { ev.preventDefault(); }

  function span(className, text) {
    var el = document.createElement('span');
    el.className = className;
    el.textContent = text;
    return el;
  }

  // The pen.
  var stage = document.querySelector('.clip-video-wrap');
  var drawButton = $('clip-draw');
  var ink = null;
  if (Ink && stage && drawButton) {
    ink = Ink.attach({ doc: document, video: video, stage: stage, onChange: function (s) {
      drawButton.setAttribute('aria-pressed', String(s.active));
      stage.classList.toggle('is-drawing', s.active);
    } });
    drawButton.hidden = false;
    drawButton.addEventListener('click', function () { ink.toggle(); });
    drawButton.addEventListener('mousedown', keepFocus);
  }

  // Review notes.
  var id = clipId();
  var notesPanel = $('clip-notes');
  var key = N && Ui && id ? N.storageKey('c', id) : '';
  var storage = key ? N.browserStorage(root) : null;
  var mine = [];
  var composer = null;
  var sharer = null;
  var clipStop = null;

  function persist() {
    N.save(storage, key, mine, Date.now());
  }

  function status(message, actionLabel, action) {
    return Ui.statusLine(document, $('clip-notes-status'), message, actionLabel, action);
  }

  function changed() {
    if (sharer) sharer.refresh();
    drawRows();
    drawMarks();
  }

  function play(note) {
    clipStop = note.end !== null ? { from: note.t, to: note.end } : null;
    try { video.currentTime = note.t; } catch (e) { /* not seekable yet */ }
    var playing = video.play();
    if (playing && typeof playing.catch === 'function') playing.catch(function () {});
  }

  // A clip plays to its end and pauses there; a seek away from it lets the video play on.
  function stopClip() {
    if (!clipStop) return;
    var t = video.currentTime || 0;
    if (t < clipStop.from - 1 || t > clipStop.to + 2) { clipStop = null; return; }
    if (t >= clipStop.to) {
      clipStop = null;
      video.pause();
    }
  }

  function remove(note) {
    var at = mine.indexOf(note);
    if (at < 0) return;
    mine = mine.slice(0, at).concat(mine.slice(at + 1));
    persist();
    var undo = status('Note deleted.', 'Undo', function () {
      mine = N.merge(mine, [note]);
      persist();
      status('');
      changed();
    });
    changed();
    if (undo && typeof undo.focus === 'function') undo.focus();
  }

  function closeIcon() {
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    var attrs = { viewBox: '0 0 24 24', width: '14', height: '14', 'aria-hidden': 'true', focusable: 'false', fill: 'none',
      stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round' };
    Object.keys(attrs).forEach(function (name) { svg.setAttribute(name, attrs[name]); });
    var path = document.createElementNS(ns, 'path');
    path.setAttribute('d', 'M6 6l12 12M18 6L6 18');
    svg.appendChild(path);
    return svg;
  }

  // The notes, as rows: time, kind, a clip's length and the text; a press plays the note.
  function drawRows() {
    var list = $('clip-mine-list');
    while (list.firstChild) list.removeChild(list.firstChild);
    mine.forEach(function (note) {
      var kind = note.end === null ? 'Bookmark' : 'Clip';
      var li = document.createElement('li');
      var row = document.createElement('button');
      row.type = 'button';
      row.className = 'vod-note';
      row.appendChild(span('vod-note-time', clock(note.t)));
      row.appendChild(span('vod-note-kind', kind));
      if (note.end !== null) row.appendChild(span('vod-note-length', length(note.end - note.t)));
      if (note.text) row.appendChild(span('vod-note-text', note.text));
      row.addEventListener('click', function () { play(note); });
      row.addEventListener('mousedown', keepFocus);
      var del = document.createElement('button');
      del.type = 'button';
      del.className = 'vod-delete';
      del.title = 'Delete';
      del.setAttribute('aria-label', 'Delete ' + kind.toLowerCase() + ' at ' + clock(note.t));
      del.appendChild(closeIcon());
      del.addEventListener('click', function () { remove(note); });
      li.appendChild(row);
      li.appendChild(del);
      list.appendChild(li);
    });
    $('clip-mine-empty').hidden = mine.length > 0 || !!(composer && composer.draft());
  }

  // A diamond on the track per note, once the clip's length is known.
  function drawMarks() {
    var box = $('clip-track-marks');
    if (!box) return;
    while (box.firstChild) box.removeChild(box.firstChild);
    var d = duration();
    if (!(d > 0)) return;
    mine.forEach(function (note) {
      if (note.t > d) return;
      var mark = document.createElement('span');
      mark.className = 'clip-track-mark' + (note.end !== null ? ' is-clip' : '');
      mark.style.left = (note.t / d) * 100 + '%';
      box.appendChild(mark);
    });
  }

  // The clip being made, on the track: from its start to the playhead, then to its end.
  function drawRange(draft) {
    var band = $('clip-track-range');
    if (!band) return;
    var d = duration();
    var on = !!draft && draft.kind === 'clip' && draft.t !== null && d > 0;
    band.hidden = !on;
    if (!on) return;
    var to = draft.end !== null ? draft.end : video.currentTime || 0;
    var from = Math.max(0, Math.min(draft.t, to)) / d;
    var width = Math.max(0.004, Math.min(1, Math.max(draft.t, to) / d) - from);
    band.style.left = 'calc(10px + (100% - 20px) * ' + from + ')';
    band.style.width = 'calc((100% - 20px) * ' + width + ')';
  }

  if (key && notesPanel) {
    composer = Ui.composer({
      els: { form: $('clip-composer'), when: $('clip-composer-when'), text: $('clip-composer-text'), cancel: $('clip-composer-cancel'),
        clip: $('clip-add-clip'), clipLabel: $('clip-add-clip-label'), clipKey: $('clip-add-clip-key'), hint: $('clip-composer-hint') },
      now: function () { return video.currentTime || 0; },
      clock: clock,
      pause: function () { if (!video.paused) video.pause(); },
      onSave: function (note) {
        mine = N.merge(mine, [note]);
        persist();
        status('');
        changed();
      },
      onDraft: function (draft) {
        $('clip-mine-empty').hidden = mine.length > 0 || !!draft;
        drawRange(draft);
      }
    });
    var bookmarkButton = $('clip-add-bookmark');
    var clipButton = $('clip-add-clip');
    bookmarkButton.addEventListener('click', function () { composer.bookmark(); });
    clipButton.addEventListener('click', function () { composer.clipButton(); });
    [bookmarkButton, clipButton].forEach(function (button) { button.addEventListener('mousedown', keepFocus); });
    sharer = Ui.sharer({
      els: { box: $('clip-share'), copy: $('clip-copy-notes'), status: $('clip-copy-status'), link: $('clip-copy-link') },
      href: function () { return location.href; },
      notes: function () { return mine; },
      clipboard: root.navigator && root.navigator.clipboard,
      setTimeout: function (fn, ms) { return setTimeout(fn, ms); },
      clearTimeout: function (handle) { clearTimeout(handle); }
    });
    notesPanel.hidden = false;
    ['loadedmetadata', 'durationchange'].forEach(function (name) { video.addEventListener(name, drawMarks); });
    video.addEventListener('timeupdate', function () {
      stopClip();
      drawRange(composer.draft());
    });
    N.prune(storage, Date.now());
    N.adopt({ storage: storage, key: key, href: location.href, nowMs: Date.now(), replace: function (url) {
      try { root.history.replaceState(root.history.state, '', url); } catch (e) { /* no history to rewrite */ }
    } }).then(function (result) {
      mine = result.notes;
      status(N.adoptedText(result));
      changed();
    });
  }

  // Keys, before the player's. Typing in a note reaches only the composer (Enter saves, Esc
  // drops it); otherwise the pen's keys come first while it is out, then D, then B, I and O.
  root.addEventListener('keydown', function (ev) {
    if (page.hidden || ev.defaultPrevented) return;
    var handled = false;
    if (isEditable(ev.target)) {
      handled = !!composer && composer.key(ev);
    } else if (ink && ink.key(ev)) {
      handled = true;
    } else if (ink && !ev.ctrlKey && !ev.metaKey && !ev.altKey && (ev.key === 'd' || ev.key === 'D')) {
      if (!ev.repeat) ink.toggle();
      handled = true;
    } else if (composer && composer.key(ev)) {
      handled = true;
    }
    if (handled) {
      ev.preventDefault();
      ev.stopPropagation();
    }
  }, true);
})(typeof window !== 'undefined' ? window : globalThis);

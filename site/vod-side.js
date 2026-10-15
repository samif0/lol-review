/*
 * The shared game page's side column (vod.html): Notes or Events, one at a time.
 *   - Notes: the viewer's own bookmarks and clips (B, and I then O, as in the app's reviewer;
 *     review-notes.js keeps them in this browser and puts them in a link, review-notes-ui.js
 *     runs the composer), then the sharer's, under Player notes.
 *   - Events: the game's moments, time, color and name, with a filter (All, Deaths, Kills,
 *     Objectives) when there is more than one kind.
 * A clip, the viewer's or the sharer's, plays to its end and pauses there. A note saved while
 * the pen's ink is on screen keeps the drawing: choosing it (its row or its diamond on the
 * lane) opens its first frame, paused, with the drawing, and playing on clears it as it clears
 * any ink. A link with notes (#notes=...) adds its notes when the page opens.
 *
 * RevuVodSide.attach({ root, doc, video, $, V, id, offset(), length(), redraw(), ink() }) returns
 * { start, laneDoc, draw, playing }: vod.js calls start() once the game shows, puts laneDoc(doc)
 * on its lane, wraps each moment's action with playing(choose) and passes its lane items to
 * draw(); ink() is the pen (vod-ink.js) once it is attached. markMoment is shared with the
 * lane. Server text is set with textContent only.
 */
(function (root) {
  'use strict';

  // A moment past the uploaded edge stays a button: pressing it arms "jump there when it
  // arrives" (aria-pressed), and its name says when it should arrive.
  function markMoment(V, button, item, label) {
    var suffix = V.pendingSuffix(item);
    button.classList.toggle('is-pending', !!item.pending);
    button.classList.toggle('is-armed', !!item.armed);
    if (item.pending) button.setAttribute('aria-pressed', item.armed ? 'true' : 'false');
    else button.removeAttribute('aria-pressed');
    if (label) button.setAttribute('aria-label', label + suffix);
    return suffix;
  }

  // A click leaves the keyboard where it was: Space then pauses the game instead of pressing
  // the row again. Tab and Enter still reach every row.
  function keepFocus(ev) { ev.preventDefault(); }

  function attach(o) {
    var doc = o.doc;
    var win = o.root;
    var video = o.video;
    var V = o.V;
    var $ = o.$;
    var N = win.RevuNotes;
    var Ui = win.RevuNotesUi;
    var key = N && Ui && o.id ? N.storageKey('v', o.id) : '';
    var storage = key ? N.browserStorage(win) : null;
    var mine = [];
    var entries = { notes: [], mine: [], events: [] };
    var composer = null;
    var sharer = null;
    var tab = 'notes';
    var shown = { notes: false, events: false };
    var eventFilter = 'all';
    var filterButtons = null;
    var clipStop = null;
    var inkWait = null;
    var rangeEl = $('vod-lane-range');
    var mineEmpty = $('vod-mine-empty');

    function span(className, value) {
      var el = doc.createElement('span');
      el.className = className;
      if (value !== undefined) el.textContent = value;
      return el;
    }

    // A list of rows, rebuilt only when its rows change so keyboard focus survives every upload
    // update. `parts(row)` gives a row's spans, `name(row)` the name its tooltip gives, and
    // `extra(li, entry)`, when given, adds to a row (the delete button).
    function drawRows(list, was, rows, choose, name, parts, extra) {
      var same = rows.length === was.length && rows.every(function (row, i) {
        var old = was[i].row;
        return row.vt === old.vt && row.aria === old.aria && row.text === old.text && row.length === old.length &&
          !!row.drawn === !!old.drawn;
      });
      var now = was;
      if (!same) {
        while (list.firstChild) list.removeChild(list.firstChild);
        now = rows.map(function (row) {
          var li = doc.createElement('li');
          var button = doc.createElement('button');
          button.type = 'button';
          button.className = 'vod-note';
          parts(row).forEach(function (el) { button.appendChild(el); });
          var entry = { button: button, row: row, choose: choose, suffix: null };
          button.addEventListener('click', function () { entry.choose(entry.row); });
          button.addEventListener('mousedown', keepFocus);
          li.appendChild(button);
          if (extra) extra(li, entry);
          list.appendChild(li);
          return entry;
        });
      }
      now.forEach(function (entry, i) {
        entry.row = rows[i];
        entry.choose = choose;
        // A row keeps its text as its name; a pending one gets a visually hidden suffix.
        var suffix = markMoment(V, entry.button, entry.row, '');
        if (suffix && !entry.suffix) {
          entry.suffix = span('visually-hidden');
          entry.button.appendChild(entry.suffix);
        }
        if (entry.suffix) entry.suffix.textContent = suffix;
        entry.button.title = suffix ? entry.row.time + ' ' + name(entry.row) + ' (' + suffix.slice(2) + ')' : '';
      });
      return now;
    }

    // A note row: its time, its kind, a clip's length, the pen's mark when it has a drawing,
    // then its text on a line of its own.
    function noteParts(row) {
      var parts = [span('vod-note-time', row.time), span('vod-note-kind', row.kind)];
      if (row.length) parts.push(span('vod-note-length', row.length));
      if (row.drawn) parts.push(Ui.inkMark(doc));
      if (row.text) parts.push(span('vod-note-text', row.text));
      return parts;
    }

    function kindOf(row) { return row.kind; }

    function closeIcon() {
      if (typeof doc.createElementNS !== 'function') return span('vod-delete-x', 'x');
      var ns = 'http://www.w3.org/2000/svg';
      var svg = doc.createElementNS(ns, 'svg');
      var attrs = { viewBox: '0 0 24 24', width: '14', height: '14', 'aria-hidden': 'true', focusable: 'false', fill: 'none',
        stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round' };
      Object.keys(attrs).forEach(function (name) { svg.setAttribute(name, attrs[name]); });
      var path = doc.createElementNS(ns, 'path');
      path.setAttribute('d', 'M6 6l12 12M18 6L6 18');
      svg.appendChild(path);
      return svg;
    }

    // The viewer's own notes, each with a delete button beside it.
    function drawMine(rows, choose) {
      entries.mine = drawRows($('vod-mine-list'), entries.mine, rows, choose, kindOf, noteParts, function (li, entry) {
        var remove = doc.createElement('button');
        remove.type = 'button';
        remove.className = 'vod-delete';
        remove.title = 'Delete';
        remove.setAttribute('aria-label', 'Delete ' + entry.row.kind.toLowerCase() + ' at ' + entry.row.time);
        remove.appendChild(closeIcon());
        remove.addEventListener('click', function () { removeNote(entry.row.note); });
        li.appendChild(remove);
      });
      mineEmpty.hidden = rows.length > 0 || !!(composer && composer.draft());
    }

    // Events to watch, as in the app's reviewer: time, the event's color and its name.
    function drawEvents(items, choose) {
      entries.events = drawRows($('vod-events-list'), entries.events, items, choose, function (item) { return item.label; }, function (item) {
        var dot = span('vod-event-dot is-' + item.tone);
        dot.setAttribute('aria-hidden', 'true');
        return [span('vod-note-time', item.time), dot, span('vod-note-kind', item.label)];
      });
    }

    // The events filter, built once: All, Deaths, Kills, Objectives, each with its count.
    function buildFilter() {
      if (filterButtons) return filterButtons;
      var box = $('vod-event-filter');
      filterButtons = V.EVENT_GROUPS.map(function (group) {
        var button = doc.createElement('button');
        button.type = 'button';
        button.className = 'vod-filter-btn';
        var count = span('vod-filter-count');
        button.appendChild(span('vod-filter-label', group.label));
        button.appendChild(count);
        button.addEventListener('click', function () {
          eventFilter = group.id;
          o.redraw();
        });
        button.addEventListener('mousedown', keepFocus);
        box.appendChild(button);
        return { id: group.id, button: button, count: count };
      });
      return filterButtons;
    }

    function drawEventList(events, choose) {
      var counts = V.groupCounts(events);
      if (!(counts[eventFilter] > 0)) eventFilter = 'all';
      var kinds = ['death', 'kill', 'objective'].filter(function (group) { return counts[group] > 0; }).length;
      buildFilter().forEach(function (f) {
        f.button.hidden = f.id !== 'all' && !(counts[f.id] > 0);
        f.button.setAttribute('aria-pressed', String(f.id === eventFilter));
        f.count.textContent = String(counts[f.id] || 0);
      });
      // A filter that could not narrow the list (one kind of event) stays out of the way.
      $('vod-event-filter').hidden = kinds < 2;
      drawEvents(eventFilter === 'all' ? events : events.filter(function (item) { return item.group === eventFilter; }), choose);
      $('vod-events').hidden = events.length === 0;
    }

    // Notes or Events, one at a time; with only one of them, no switch.
    function renderTabs() {
      var showing = !shown.notes ? 'events' : !shown.events ? 'notes' : tab;
      $('vod-side-tabs').hidden = !(shown.notes && shown.events);
      $('vod-panel-notes').hidden = showing !== 'notes';
      $('vod-panel-events').hidden = showing !== 'events';
      $('vod-tab-notes').setAttribute('aria-pressed', String(showing === 'notes'));
      $('vod-tab-events').setAttribute('aria-pressed', String(showing === 'events'));
    }

    function selectTab(name) {
      tab = name;
      renderTabs();
    }

    [['vod-tab-notes', 'notes'], ['vod-tab-events', 'events']].forEach(function (pair) {
      var button = $(pair[0]);
      button.addEventListener('click', function () { selectTab(pair[1]); });
      button.addEventListener('mousedown', keepFocus);
    });

    // The viewer's notes in the sharer's notes' shape, on the same clock.
    function mineDoc(gameDoc) {
      return { gameTimeAtVideoStart: V.offsetOf(gameDoc), notes: mine.map(function (n) {
        return { t: n.t, end: n.end, kind: n.end === null ? 'bookmark' : 'clip', text: n.text };
      }) };
    }

    // The markers document the lane draws: the sharer's, with the viewer's notes added.
    function laneDoc(gameDoc) {
      if (!mine.length) return gameDoc;
      var theirs = gameDoc && Array.isArray(gameDoc.notes) ? gameDoc.notes : [];
      return Object.assign({}, gameDoc || {}, { notes: theirs.concat(mineDoc(gameDoc).notes) });
    }

    // The side column for this draw: the sharer's notes, the viewer's, the events, the switch.
    // Returns whether the column shows. d: { doc, live, items, choose }
    function draw(d) {
      var rows = V.noteRows(d.doc, d.live);
      entries.notes = drawRows($('vod-notes-list'), entries.notes, rows, d.choose, kindOf, noteParts);
      $('vod-notes-block').hidden = rows.length === 0;
      var mineRows = V.noteRows(mineDoc(d.doc), d.live);
      mineRows.forEach(function (row, i) {
        row.note = mine[i];
        row.drawn = !!mine[i].ink;
      });
      drawMine(mineRows, d.choose);
      var events = d.items.filter(function (item) { return !item.note; });
      drawEventList(events, d.choose);
      shown.notes = !!composer || rows.length > 0;
      shown.events = events.length > 0;
      $('vod-notes-count').textContent = rows.length + mineRows.length > 0 ? String(rows.length + mineRows.length) : '';
      $('vod-events-count').textContent = events.length > 0 ? String(events.length) : '';
      renderTabs();
      drawRange(composer ? composer.draft() : null);
      var on = shown.notes || shown.events;
      $('vod-notes').hidden = !on;
      return on;
    }

    function persist() {
      N.save(storage, key, mine, Date.now());
    }

    function changed() {
      if (sharer) sharer.refresh();
      o.redraw();
    }

    function status(message, actionLabel, action) {
      return Ui ? Ui.statusLine(doc, $('vod-notes-status'), message, actionLabel, action) : null;
    }

    function removeNote(note) {
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

    // The clip being made, on the lane: from its start to its end, or to the playhead until O.
    function drawRange(draft) {
      if (!rangeEl) return;
      var length = Number(o.length()) || 0;
      var on = !!draft && draft.kind === 'clip' && draft.t !== null && length > 0;
      rangeEl.hidden = !on;
      if (!on) return;
      var offset = Number(o.offset()) || 0;
      var from = Math.max(0, draft.t - offset);
      var to = draft.end !== null ? Math.max(0, draft.end - offset) : video.currentTime || 0;
      var start = Math.min(from, to);
      var end = Math.max(from, to);
      rangeEl.style.left = Math.min(100, (start / length) * 100) + '%';
      rangeEl.style.width = Math.max(0.3, Math.min(100, ((end - start) / length) * 100)) + '%';
    }

    // A clip plays to its end and pauses there; a seek away from it lets the game play on.
    function stopClip() {
      if (!clipStop) return;
      var t = video.currentTime || 0;
      if (t < clipStop.from - 1 || t > clipStop.to + 2) { clipStop = null; return; }
      if (t >= clipStop.to) {
        clipStop = null;
        video.pause();
      }
    }

    video.addEventListener('timeupdate', stopClip);

    // The drawing saved with one of the viewer's notes: on its row, or found by its time for its
    // diamond on the lane.
    function inkOf(item) {
      if (item.note && typeof item.note === 'object') return item.note.ink || null;
      if (item.note !== true) return null;
      var offset = Number(o.offset()) || 0;
      var near = function (a, b) { return a === b || (a !== null && b !== null && Math.abs(a - b) < 0.05); };
      for (var i = 0; i < mine.length; i += 1) {
        var n = mine[i];
        if (n.ink && near(Math.max(0, n.t - offset), item.vt) && near(n.end === null ? null : Math.max(0, n.end - offset), item.vtEnd)) {
          return n.ink;
        }
      }
      return null;
    }

    // The pen (vod-ink.js), once vod.js has attached it.
    function pen() {
      var handle = typeof o.ink === 'function' ? o.ink() : null;
      return handle && typeof handle.show === 'function' && typeof handle.strokes === 'function' ? handle : null;
    }

    // The game paused where it is, with a note's drawing over it.
    function showInk(ink) {
      var handle = pen();
      if (!handle) return;
      if (!video.paused) video.pause();
      handle.show(N.inkStrokes(ink));
    }

    // A moment with a drawing that isn't uploaded yet: the drawing shows once the game jumps there.
    video.addEventListener('seeked', function () {
      if (!inkWait || Math.abs((video.currentTime || 0) - inkWait.vt) >= 1) return;
      var ink = inkWait.ink;
      inkWait = null;
      showInk(ink);
    });

    // A clip plays to its end and pauses there. A note with a drawing opens paused on its first
    // frame (the page's own seek starts the game or plays it there; the pause follows at once),
    // with the drawing; playing on clears the drawing, and a clip still stops at its end.
    function playing(choose) {
      return function (item) {
        clipStop = typeof item.vtEnd === 'number' && item.vtEnd > item.vt ? { from: item.vt, to: item.vtEnd } : null;
        var ink = inkOf(item);
        inkWait = ink && item.pending ? { vt: item.vt, ink: ink } : null;
        choose(item);
        if (ink && !item.pending) showInk(ink);
      };
    }

    // Once the game shows: Bookmark (B), Clip (I, then O), the composer, Copy link with notes,
    // and the keys (after the transport's, so the pen's keys win while it is out).
    function start() {
      if (composer || !key) return;
      $('vod-review').hidden = false;
      composer = Ui.composer({
        els: { form: $('vod-composer'), when: $('vod-composer-when'), text: $('vod-composer-text'), cancel: $('vod-composer-cancel'),
          clip: $('vod-add-clip'), clipLabel: $('vod-add-clip-label'), clipKey: $('vod-add-clip-key'), hint: $('vod-composer-hint') },
        now: function () { return (video.currentTime || 0) + (Number(o.offset()) || 0); },
        clock: V.clock,
        pause: function () { if (!video.paused) video.pause(); },
        open: function () { selectTab('notes'); },
        ink: function () {
          var handle = pen();
          return handle ? N.packInk(handle.strokes()) : null;
        },
        onSave: function (note) {
          mine = N.merge(mine, [note]);
          persist();
          status('');
          changed();
        },
        onDraft: function (draft) {
          mineEmpty.hidden = mine.length > 0 || !!draft;
          drawRange(draft);
        }
      });
      var bookmarkButton = $('vod-add-bookmark');
      var clipButton = $('vod-add-clip');
      bookmarkButton.addEventListener('click', function () { composer.bookmark(); });
      clipButton.addEventListener('click', function () { composer.clipButton(); });
      [bookmarkButton, clipButton].forEach(function (button) { button.addEventListener('mousedown', keepFocus); });
      sharer = Ui.sharer({
        els: { box: $('vod-share'), copy: $('vod-copy-notes'), status: $('vod-copy-status'), link: $('vod-copy-link') },
        href: function () { return win.location ? win.location.href : ''; },
        notes: function () { return mine; },
        clipboard: win.navigator && win.navigator.clipboard,
        setTimeout: function (fn, ms) { return win.setTimeout(fn, ms); },
        clearTimeout: function (handle) { win.clearTimeout(handle); }
      });
      doc.addEventListener('keydown', function (ev) {
        if (ev.defaultPrevented) return;
        if (composer.key(ev)) ev.preventDefault();
      });
      video.addEventListener('timeupdate', function () { drawRange(composer.draft()); });
    }

    // This browser's notes for the game, and a link's (#notes=...), which then leave the address bar.
    if (key) {
      N.prune(storage, Date.now());
      N.adopt({ storage: storage, key: key, href: win.location ? win.location.href : '', nowMs: Date.now(), replace: function (url) {
        try { win.history.replaceState(win.history.state, '', url); } catch (e) { /* no history to rewrite */ }
      } }).then(function (result) {
        mine = result.notes;
        status(N.adoptedText(result));
        changed();
      });
    }

    return { start: start, laneDoc: laneDoc, draw: draw, playing: playing, notes: function () { return mine.slice(); } };
  }

  root.RevuVodSide = { attach: attach, markMoment: markMoment };
})(typeof window !== 'undefined' ? window : globalThis);

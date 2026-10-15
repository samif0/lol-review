/*
 * Ink on the shared game page (vod.html) and the shared clip page (clip.html): screen-pen
 * drawing over the video, for a coach walking someone through a game. The pen sends nothing:
 * the ink lives in this page until it is cleared, or until the video plays on (unless Keep ink
 * is pinned). Starting a stroke pauses the video. A note saved while ink is on screen keeps a
 * copy (strokes(), kept by review-notes.js), and choosing that note shows it again (show()).
 *
 * Strokes are vectors in the picture's own coordinates (0..1 of the video frame, not of the
 * element), so a stroke stays on the same spot of the game when the player resizes or goes
 * full screen. Line widths are a share of the picture's height for the same reason.
 *
 * RevuVodInk.attach({ doc, video, stage, onChange }) adds the canvas and the tool palette to
 * `stage` and returns { setActive, toggle, active, clear, undo, hasInk, key, layout, strokes,
 * show }. frameRect, toFrame, hitStroke and arrowHead are pure and exported for the tests.
 */
(function (root) {
  'use strict';

  var TOOLS = [
    { id: 'pen', label: 'Pen', key: 'p' },
    { id: 'highlighter', label: 'Highlighter', key: 'h' },
    { id: 'arrow', label: 'Arrow', key: 'a' },
    { id: 'rect', label: 'Box', key: 'r' },
    { id: 'ellipse', label: 'Circle', key: 'o' },
    { id: 'eraser', label: 'Eraser', key: 'e' }
  ];
  var COLORS = ['red', 'yellow', 'green', 'blue', 'white'];
  var COLOR_NAMES = { red: 'Red', yellow: 'Yellow', green: 'Green', blue: 'Blue', white: 'White' };
  // Line widths as a share of the picture's height, so a stroke scales with the player.
  var SIZES = [0.004, 0.008, 0.016];
  var SIZE_NAMES = ['Thin', 'Medium', 'Thick'];
  var MIN_STEP = 0.002;
  var ERASE_RADIUS = 0.02;
  var SVG_NS = 'http://www.w3.org/2000/svg';
  // Monochrome stroked icons, drawn like the transport's (24 box, 1.8 stroke, round ends).
  var ICONS = {
    pen: ['M4 20l4.5-1L19 8.5 15.5 5 5 15.5z', 'M13.5 7l3.5 3.5'],
    highlighter: ['M5 20h7', 'M9 16l-2-2 9-9 4 4-9 9z', 'M7 14l-2 3h4'],
    arrow: ['M5 19L19 5', 'M10 5h9v9'],
    rect: ['M4 6h16v12H4z'],
    ellipse: ['M3 12a9 6.5 0 1 0 18 0a9 6.5 0 1 0-18 0'],
    eraser: ['M8 20h12', 'M4.5 15.5l9-9 6 6-7.5 7.5H8.5z', 'M9.5 10.5l6 6'],
    undo: ['M9 14L4 9l5-5', 'M4 9h10.5a5.5 5.5 0 0 1 0 11H11'],
    clear: ['M4 7h16', 'M9 7V4h6v3', 'M6.5 7l1 13h9l1-13'],
    keep: ['M12 16v5', 'M8 3h8l-1 6 3 3.5H6L9 9z'],
    close: ['M6 6l12 12', 'M18 6L6 18']
  };

  // ── Geometry (pure) ────────────────────────────────────────────────────────

  // Where the picture sits inside an element of size w x h that shows a vw x vh video
  // contained (letterboxed). Without the video's size the whole element is the frame.
  function frameRect(w, h, vw, vh) {
    if (!(w > 0 && h > 0)) return { x: 0, y: 0, w: 0, h: 0 };
    if (!(vw > 0 && vh > 0)) return { x: 0, y: 0, w: w, h: h };
    var scale = Math.min(w / vw, h / vh);
    var fw = vw * scale;
    var fh = vh * scale;
    return { x: (w - fw) / 2, y: (h - fh) / 2, w: fw, h: fh };
  }

  // A point in element pixels to frame coordinates, kept on the picture.
  function toFrame(px, py, rect) {
    if (!(rect.w > 0 && rect.h > 0)) return null;
    var clamp = function (v) { return Math.max(0, Math.min(1, v)); };
    return [clamp((px - rect.x) / rect.w), clamp((py - rect.y) / rect.h)];
  }

  function distanceToSegment(p, a, b) {
    var dx = b[0] - a[0];
    var dy = b[1] - a[1];
    var len = dx * dx + dy * dy;
    var t = len > 0 ? Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len)) : 0;
    var x = a[0] + t * dx - p[0];
    var y = a[1] + t * dy - p[1];
    return Math.sqrt(x * x + y * y);
  }

  // True when the point (frame coordinates) touches the stroke's line.
  function hitStroke(stroke, p, radius) {
    var pts = stroke.points;
    var reach = radius + stroke.size / 2;
    if (stroke.tool === 'rect') {
      var a = pts[0];
      var b = pts[pts.length - 1];
      var corners = [a, [b[0], a[1]], b, [a[0], b[1]], a];
      for (var i = 1; i < corners.length; i++) if (distanceToSegment(p, corners[i - 1], corners[i]) <= reach) return true;
      return false;
    }
    if (stroke.tool === 'ellipse') {
      var c0 = pts[0];
      var c1 = pts[pts.length - 1];
      var cx = (c0[0] + c1[0]) / 2;
      var cy = (c0[1] + c1[1]) / 2;
      var rx = Math.abs(c1[0] - c0[0]) / 2;
      var ry = Math.abs(c1[1] - c0[1]) / 2;
      for (var k = 0; k < 48; k++) {
        var t0 = (k / 48) * 2 * Math.PI;
        var t1 = ((k + 1) / 48) * 2 * Math.PI;
        if (distanceToSegment(p, [cx + rx * Math.cos(t0), cy + ry * Math.sin(t0)], [cx + rx * Math.cos(t1), cy + ry * Math.sin(t1)]) <= reach) return true;
      }
      return false;
    }
    var line = stroke.tool === 'arrow' ? [pts[0], pts[pts.length - 1]] : pts;
    if (line.length === 1) return distanceToSegment(p, line[0], line[0]) <= reach;
    for (var j = 1; j < line.length; j++) if (distanceToSegment(p, line[j - 1], line[j]) <= reach) return true;
    return false;
  }

  // The two back corners of an arrowhead at `to`, `size` long (same units as the points).
  function arrowHead(from, to, size) {
    var angle = Math.atan2(to[1] - from[1], to[0] - from[0]);
    var spread = Math.PI / 7;
    return [
      [to[0] - size * Math.cos(angle - spread), to[1] - size * Math.sin(angle - spread)],
      [to[0] - size * Math.cos(angle + spread), to[1] - size * Math.sin(angle + spread)]
    ];
  }

  // A stroke to show, as strokes() gives it ({ tool, color, size: an index into SIZES, points }),
  // checked: a tool that draws, a color and a size from the palette, points on the picture. A
  // shape keeps its two corners. Null when it can't be drawn.
  function inkStroke(s) {
    if (!s || !Array.isArray(s.points) || s.tool === 'eraser' || COLORS.indexOf(s.color) < 0) return null;
    var tool = TOOLS.filter(function (t) { return t.id === s.tool; })[0];
    if (!tool || typeof s.size !== 'number' || !SIZES[s.size]) return null;
    var onPicture = function (v) { return typeof v === 'number' && isFinite(v) ? Math.max(0, Math.min(1, v)) : NaN; };
    var points = s.points.map(function (p) { return Array.isArray(p) ? [onPicture(p[0]), onPicture(p[1])] : [NaN, NaN]; })
      .filter(function (p) { return !isNaN(p[0]) && !isNaN(p[1]); });
    if (tool.id !== 'pen' && tool.id !== 'highlighter') points = points.length > 1 ? [points[0], points[points.length - 1]] : [];
    return points.length ? { tool: tool.id, color: s.color, size: SIZES[s.size], points: points } : null;
  }

  // ── The page part ──────────────────────────────────────────────────────────

  function icon(doc, name) {
    var svg = doc.createElementNS(SVG_NS, 'svg');
    var attrs = { viewBox: '0 0 24 24', width: '18', height: '18', 'aria-hidden': 'true', focusable: 'false', fill: 'none',
      stroke: 'currentColor', 'stroke-width': '1.8', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' };
    Object.keys(attrs).forEach(function (key) { svg.setAttribute(key, attrs[key]); });
    (ICONS[name] || []).forEach(function (d) {
      var path = doc.createElementNS(SVG_NS, 'path');
      path.setAttribute('d', d);
      svg.appendChild(path);
    });
    return svg;
  }

  function attach(o) {
    var doc = o.doc;
    var video = o.video;
    var stage = o.stage;
    var win = doc.defaultView || root;
    var onChange = typeof o.onChange === 'function' ? o.onChange : function () {};

    var canvas = doc.createElement('canvas');
    canvas.className = 'vod-ink';
    canvas.setAttribute('aria-hidden', 'true');
    var ctx = canvas.getContext ? canvas.getContext('2d') : null;
    var bar = doc.createElement('div');
    bar.className = 'vod-ink-bar';
    bar.setAttribute('role', 'toolbar');
    bar.setAttribute('aria-label', 'Drawing tools');
    bar.hidden = true;
    stage.appendChild(canvas);
    stage.appendChild(bar);

    var state = { active: false, tool: 'pen', color: 'red', size: 1, keep: false };
    var strokes = [];
    var actions = [];
    var drawing = null;
    var rect = { x: 0, y: 0, w: 0, h: 0 };
    var box = { w: 0, h: 0, left: 0, top: 0, dpr: 1 };
    var frame = 0;
    var palette = {};

    function inkColor(name) {
      var value = '';
      try { value = win.getComputedStyle(doc.documentElement).getPropertyValue('--ink-' + name).trim(); } catch (e) { /* no styles */ }
      return value || name;
    }

    function button(cls, label, title, iconName, onPress) {
      var b = doc.createElement('button');
      b.type = 'button';
      b.className = cls;
      b.setAttribute('aria-label', label);
      b.title = title;
      if (iconName) b.appendChild(icon(doc, iconName));
      b.addEventListener('click', onPress);
      // A click leaves the keyboard where it was, so Space still plays and pauses.
      b.addEventListener('mousedown', function (ev) { ev.preventDefault(); });
      return b;
    }

    function group(label) {
      var g = doc.createElement('div');
      g.className = 'vod-ink-group';
      g.setAttribute('role', 'group');
      g.setAttribute('aria-label', label);
      bar.appendChild(g);
      return g;
    }

    var tools = group('Tool');
    TOOLS.forEach(function (tool) {
      palette['tool-' + tool.id] = tools.appendChild(button('vod-ink-btn', tool.label, tool.label + ' (' + tool.key.toUpperCase() + ')', tool.id,
        function () { setTool(tool.id); }));
    });
    var colors = group('Color');
    COLORS.forEach(function (name, i) {
      var b = button('vod-ink-swatch is-' + name, COLOR_NAMES[name], COLOR_NAMES[name] + ' (' + (i + 1) + ')', null, function () { setColor(name); });
      palette['color-' + name] = colors.appendChild(b);
    });
    var sizes = group('Size');
    SIZES.forEach(function (_, i) {
      var b = button('vod-ink-size is-' + i, SIZE_NAMES[i], SIZE_NAMES[i] + ' line ([ and ] change it)', null, function () { setSize(i); });
      b.appendChild(doc.createElement('span'));
      palette['size-' + i] = sizes.appendChild(b);
    });
    var actionsGroup = group('Ink');
    palette.undo = actionsGroup.appendChild(button('vod-ink-btn', 'Undo', 'Undo (Ctrl+Z)', 'undo', function () { undo(); }));
    palette.clear = actionsGroup.appendChild(button('vod-ink-btn', 'Clear ink', 'Clear ink (C)', 'clear', function () { clear(); }));
    palette.keep = actionsGroup.appendChild(button('vod-ink-btn', 'Keep ink while the video plays',
      'Keep ink while the video plays (off: it clears when the video plays)', 'keep', function () { state.keep = !state.keep; refresh(); }));
    palette.done = bar.appendChild(button('vod-ink-btn vod-ink-done', 'Stop drawing', 'Stop drawing (Esc or D)', 'close', function () { setActive(false); }));

    function refresh() {
      TOOLS.forEach(function (tool) { palette['tool-' + tool.id].setAttribute('aria-pressed', String(state.tool === tool.id)); });
      COLORS.forEach(function (name) { palette['color-' + name].setAttribute('aria-pressed', String(state.color === name)); });
      SIZES.forEach(function (_, i) { palette['size-' + i].setAttribute('aria-pressed', String(state.size === i)); });
      palette.keep.setAttribute('aria-pressed', String(state.keep));
      palette.undo.disabled = actions.length === 0;
      palette.clear.disabled = strokes.length === 0;
      canvas.classList.toggle('is-active', state.active);
      canvas.classList.toggle('is-erasing', state.active && state.tool === 'eraser');
      bar.hidden = !state.active;
      onChange({ active: state.active, hasInk: strokes.length > 0 });
    }

    function layout() {
      var w = stage.clientWidth || 0;
      var h = stage.clientHeight || 0;
      var dpr = Math.max(1, Math.min(3, win.devicePixelRatio || 1));
      if (w !== box.w || h !== box.h || dpr !== box.dpr) {
        box.w = w;
        box.h = h;
        box.dpr = dpr;
        canvas.width = Math.round(w * dpr);
        canvas.height = Math.round(h * dpr);
      }
      rect = frameRect(w, h, video.videoWidth, video.videoHeight);
      draw();
    }

    function px(p) { return [(rect.x + p[0] * rect.w) * box.dpr, (rect.y + p[1] * rect.h) * box.dpr]; }

    function paint(stroke) {
      var pts = stroke.points;
      if (!pts.length) return;
      var width = Math.max(1, stroke.size * rect.h * box.dpr);
      ctx.save();
      ctx.strokeStyle = inkColor(stroke.color);
      ctx.fillStyle = ctx.strokeStyle;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.lineWidth = stroke.tool === 'highlighter' ? width * 3 : width;
      if (stroke.tool === 'highlighter') ctx.globalAlpha = 0.35;
      var a = px(pts[0]);
      var b = px(pts[pts.length - 1]);
      ctx.beginPath();
      if (stroke.tool === 'rect') {
        ctx.rect(Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.abs(b[0] - a[0]), Math.abs(b[1] - a[1]));
      } else if (stroke.tool === 'ellipse') {
        ctx.ellipse((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, Math.abs(b[0] - a[0]) / 2, Math.abs(b[1] - a[1]) / 2, 0, 0, 2 * Math.PI);
      } else if (stroke.tool === 'arrow') {
        ctx.moveTo(a[0], a[1]);
        ctx.lineTo(b[0], b[1]);
        var head = arrowHead(a, b, Math.max(width * 4, 12 * box.dpr));
        ctx.moveTo(head[0][0], head[0][1]);
        ctx.lineTo(b[0], b[1]);
        ctx.lineTo(head[1][0], head[1][1]);
      } else if (pts.length === 1) {
        ctx.arc(a[0], a[1], ctx.lineWidth / 2, 0, 2 * Math.PI);
        ctx.fill();
        ctx.restore();
        return;
      } else {
        // Midpoint smoothing: a quadratic curve through each point keeps fast strokes round.
        ctx.moveTo(a[0], a[1]);
        for (var i = 1; i < pts.length - 1; i++) {
          var p = px(pts[i]);
          var q = px(pts[i + 1]);
          ctx.quadraticCurveTo(p[0], p[1], (p[0] + q[0]) / 2, (p[1] + q[1]) / 2);
        }
        ctx.lineTo(b[0], b[1]);
      }
      ctx.stroke();
      ctx.restore();
    }

    function draw() {
      frame = 0;
      if (!ctx) return;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      if (!(rect.w > 0)) return;
      strokes.forEach(paint);
      if (drawing) paint(drawing);
    }

    function soon() {
      if (frame) return;
      frame = win.requestAnimationFrame ? win.requestAnimationFrame(draw) : setTimeout(draw, 16);
    }

    function point(ev) {
      var r = canvas.getBoundingClientRect();
      return toFrame(ev.clientX - r.left, ev.clientY - r.top, rect);
    }

    function eraseAt(p) {
      var removed = [];
      for (var i = strokes.length - 1; i >= 0; i--) {
        if (hitStroke(strokes[i], p, ERASE_RADIUS)) removed.push({ stroke: strokes.splice(i, 1)[0], index: i });
      }
      if (removed.length) {
        actions.push({ kind: 'erase', removed: removed });
        soon();
        refresh();
      }
    }

    canvas.addEventListener('pointerdown', function (ev) {
      if (!state.active || ev.button > 0) return;
      layout();
      var p = point(ev);
      if (!p) return;
      ev.preventDefault();
      if (!video.paused) video.pause();
      try { canvas.setPointerCapture(ev.pointerId); } catch (e) { /* not capturable */ }
      if (state.tool === 'eraser') {
        drawing = { erasing: true };
        eraseAt(p);
        return;
      }
      drawing = { tool: state.tool, color: state.color, size: SIZES[state.size], points: [p] };
      soon();
    });
    canvas.addEventListener('pointermove', function (ev) {
      if (!drawing) return;
      var p = point(ev);
      if (!p) return;
      if (drawing.erasing) { eraseAt(p); return; }
      var pts = drawing.points;
      var last = pts[pts.length - 1];
      if (drawing.tool === 'pen' || drawing.tool === 'highlighter') {
        if (Math.abs(p[0] - last[0]) + Math.abs(p[1] - last[1]) >= MIN_STEP) pts.push(p);
      } else {
        pts[1] = p;
      }
      soon();
    });
    function finish() {
      if (!drawing) return;
      var stroke = drawing;
      drawing = null;
      if (!stroke.erasing) {
        var shape = stroke.tool !== 'pen' && stroke.tool !== 'highlighter';
        if (!(shape && stroke.points.length < 2)) {
          strokes.push(stroke);
          actions.push({ kind: 'add', stroke: stroke });
        }
      }
      soon();
      refresh();
    }
    canvas.addEventListener('pointerup', finish);
    canvas.addEventListener('pointercancel', finish);
    canvas.addEventListener('lostpointercapture', finish);

    function clear() {
      if (!strokes.length) return;
      actions.push({ kind: 'clear', strokes: strokes });
      strokes = [];
      soon();
      refresh();
    }

    function undo() {
      var last = actions.pop();
      if (!last) return;
      if (last.kind === 'add') {
        var i = strokes.lastIndexOf(last.stroke);
        if (i >= 0) strokes.splice(i, 1);
      } else if (last.kind === 'erase') {
        last.removed.slice().reverse().forEach(function (r) { strokes.splice(Math.min(r.index, strokes.length), 0, r.stroke); });
      } else if (last.kind === 'clear') {
        strokes = last.strokes.concat(strokes);
      } else if (last.kind === 'show') {
        strokes = last.strokes;
      }
      soon();
      refresh();
    }

    // The ink on screen, for a note to keep: [{ tool, color, size, points }], size an index into
    // SIZES and points in picture coordinates (0..1).
    function exportStrokes() {
      return strokes.map(function (s) {
        return { tool: s.tool, color: s.color, size: Math.max(0, SIZES.indexOf(s.size)),
          points: s.points.map(function (p) { return [p[0], p[1]]; }) };
      });
    }

    // A note's drawing (in strokes()'s shape) in place of the ink on screen, which Undo brings
    // back. The page pauses the video first; playing on clears it as it clears any ink, unless
    // Keep ink is pinned.
    function show(list) {
      var next = (Array.isArray(list) ? list : []).map(inkStroke).filter(Boolean);
      if (!next.length && !strokes.length) return;
      actions.push({ kind: 'show', strokes: strokes });
      strokes = next;
      layout();
      refresh();
    }

    function setTool(id) { state.tool = id; refresh(); }
    function setColor(name) { state.color = name; if (state.tool === 'eraser') state.tool = 'pen'; refresh(); }
    function setSize(i) { state.size = Math.max(0, Math.min(SIZES.length - 1, i)); refresh(); }

    function setActive(on) {
      state.active = !!on;
      if (state.active) layout();
      refresh();
    }

    // The video playing on clears the ink, unless Keep ink is pinned. A play the page pauses
    // again at once (opening a note with its drawing) is not playing on.
    video.addEventListener('play', function () { if (!state.keep && !drawing && !video.paused) clear(); });
    ['loadedmetadata', 'resize'].forEach(function (name) { video.addEventListener(name, layout); });
    if (typeof win.ResizeObserver === 'function') new win.ResizeObserver(layout).observe(stage);
    else if (typeof win.addEventListener === 'function') win.addEventListener('resize', layout);
    doc.addEventListener('fullscreenchange', layout);

    // Keys while drawing (the page's keyboard asks here first). True when the key was used.
    function key(ev) {
      if (!state.active) return false;
      var k = String(ev.key || '').toLowerCase();
      if ((ev.ctrlKey || ev.metaKey) && k === 'z') { undo(); return true; }
      if (ev.ctrlKey || ev.metaKey || ev.altKey) return false;
      if (k === 'escape') { setActive(false); return true; }
      if (k === 'c' || k === 'delete' || k === 'backspace') { clear(); return true; }
      if (k === '[') { setSize(state.size - 1); return true; }
      if (k === ']') { setSize(state.size + 1); return true; }
      var digit = Number(k);
      if (digit >= 1 && digit <= COLORS.length) { setColor(COLORS[digit - 1]); return true; }
      for (var i = 0; i < TOOLS.length; i++) if (TOOLS[i].key === k) { setTool(TOOLS[i].id); return true; }
      return false;
    }

    refresh();
    return {
      setActive: setActive,
      toggle: function () { setActive(!state.active); },
      active: function () { return state.active; },
      clear: clear,
      undo: undo,
      hasInk: function () { return strokes.length > 0; },
      key: key,
      layout: layout,
      strokes: exportStrokes,
      show: show,
      state: function () { return { tool: state.tool, color: state.color, size: state.size, keep: state.keep, strokes: strokes.length }; }
    };
  }

  root.RevuVodInk = { attach: attach, frameRect: frameRect, toFrame: toFrame, hitStroke: hitStroke, arrowHead: arrowHead,
    TOOLS: TOOLS, COLORS: COLORS, SIZES: SIZES };
})(typeof window !== 'undefined' ? window : this);

/*
 * Review notes on a shared game (vod.html) or a shared clip (clip.html): the bookmarks and
 * clips a coach makes while watching, kept in this browser for that share and sent on as a
 * link. A link carries its notes after the # (#notes=...), the part of a URL a browser never
 * sends to a server; the page that opens it adds them to that browser's notes for the share.
 * Nothing here is uploaded: the notes live in this browser's storage and in the links made
 * from it.
 *
 * A note is { t, end, text }: t, and end for a clip, in the page's own clock (game seconds
 * on vod.html, seconds into the clip on clip.html); a bookmark's end is null. A note saved
 * while the pen's ink was on screen also has ink: its drawing, in the compact form below.
 *
 * This file is the data: cleaning, drawings, links (encode, decode), storage, and adopt(),
 * which reads a share's notes and adds a link's. review-notes-ui.js runs the composer and Copy
 * link with notes. Everything here is pure or takes its storage as an argument, for the tests.
 */
(function (root) {
  'use strict';

  var MAX_NOTES = 100;
  var MAX_TEXT = 500;
  // Longer than any League game.
  var MAX_T = 6 * 3600;
  // A clip shorter than this is a bookmark.
  var MIN_CLIP = 1;
  // A link's notes, as written after #notes=, and once inflated.
  var MAX_PAYLOAD = 24000;
  var MAX_JSON = 200000;
  // A share's notes leave this browser's storage this long after they last changed (a shared
  // game or clip is kept for 3 days).
  var KEEP_MS = 14 * 86400000;
  var PREFIX = 'revu.notes.';
  // A note's drawing (vod-ink.js's strokes), as storage and links keep it: one array per stroke,
  // [tool, color, size, x1, y1, x2, y2, ...]. Tool and color are indexes into these lists, size
  // an index into the pen's three widths, and the points are whole thousandths of the picture
  // (0..1000, from its left and its top). The lists are the format: only ever add to their ends.
  var INK_TOOLS = ['pen', 'highlighter', 'arrow', 'rect', 'ellipse'];
  var INK_COLORS = ['red', 'yellow', 'green', 'blue', 'white'];
  var INK_SIZES = 3;
  var INK_SCALE = 1000;
  // A note keeps at most this many strokes, points in a stroke, and points in all, so a link
  // with drawings stays short. A freehand line first loses the points that bend it by less than
  // INK_TOLERANCE thousandths, then more until it fits.
  var MAX_STROKES = 32;
  var MAX_POINTS = 64;
  var MAX_INK_POINTS = 256;
  var INK_TOLERANCE = 2;
  // Line breaks stay; other control characters and the bidirectional overrides (which could
  // make a note read differently from what it says) go.
  var STRIP = /[\u0000-\u0008\u000b-\u001f\u007f\u202a-\u202e\u2066-\u2069]/g;

  function tenth(value) {
    return Math.round(Number(value) * 10) / 10;
  }

  function cleanText(value) {
    if (typeof value !== 'string') return '';
    var text = value.replace(/\r\n?/g, '\n').replace(/\t/g, ' ').replace(STRIP, '').replace(/\n{3,}/g, '\n\n').trim();
    var chars = Array.from(text);
    return chars.length > MAX_TEXT ? chars.slice(0, MAX_TEXT).join('').trim() : text;
  }

  // Drawings.

  // A whole number from 0 up to, not including, `below`.
  function whole(value, below) {
    return typeof value === 'number' && value % 1 === 0 && value >= 0 && value < below;
  }

  // Arrows, boxes and circles are two corners; pen and highlighter lines any number of points.
  function isShape(tool) {
    return INK_TOOLS[tool] !== 'pen' && INK_TOOLS[tool] !== 'highlighter';
  }

  // One stroke from storage or a link, or null: a known tool, color and size, whole points on
  // the picture, a shape's two corners, and a line's first MAX_POINTS points.
  function cleanStroke(stroke) {
    if (!Array.isArray(stroke) || stroke.length < 5 || stroke.length % 2 === 0) return null;
    if (!whole(stroke[0], INK_TOOLS.length) || !whole(stroke[1], INK_COLORS.length) || !whole(stroke[2], INK_SIZES)) return null;
    if (isShape(stroke[0]) && stroke.length !== 7) return null;
    var out = stroke.slice(0, 3 + MAX_POINTS * 2);
    for (var i = 3; i < out.length; i += 1) if (!whole(out[i], INK_SCALE + 1)) return null;
    return out;
  }

  // A note's drawing from storage or a link, or null when it has none: its strokes, up to
  // MAX_STROKES of them and MAX_INK_POINTS points. Anything that isn't a stroke is left out.
  function cleanInk(ink) {
    if (!Array.isArray(ink)) return null;
    var out = [];
    var total = 0;
    for (var i = 0; i < ink.length && out.length < MAX_STROKES; i += 1) {
      var stroke = cleanStroke(ink[i]);
      if (!stroke) continue;
      total += (stroke.length - 3) / 2;
      if (total > MAX_INK_POINTS) break;
      out.push(stroke);
    }
    return out.length ? out : null;
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

  // The fewest of a line's points that keep it within `tolerance` of where it was drawn
  // (Ramer-Douglas-Peucker), first and last kept.
  function simplify(points, tolerance) {
    if (points.length < 3) return points;
    var keep = [];
    keep[0] = keep[points.length - 1] = true;
    var spans = [[0, points.length - 1]];
    while (spans.length) {
      var span = spans.pop();
      var far = -1;
      var most = tolerance;
      for (var i = span[0] + 1; i < span[1]; i += 1) {
        var d = distanceToSegment(points[i], points[span[0]], points[span[1]]);
        if (d > most) { most = d; far = i; }
      }
      if (far >= 0) {
        keep[far] = true;
        spans.push([span[0], far], [far, span[1]]);
      }
    }
    return points.filter(function (p, i) { return keep[i]; });
  }

  // At most `most` of a line's points, evenly spaced, first and last kept: a long scribble is
  // thinned before it is simplified, which takes time that grows with its points squared.
  function thin(points, most) {
    if (points.length <= most) return points;
    var out = [];
    for (var i = 0; i < most; i += 1) out.push(points[Math.round((i * (points.length - 1)) / (most - 1))]);
    return out;
  }

  // A line simplified at `tolerance`, then harder until it has at most `most` points. Each pass
  // works on the last one's points.
  function fewer(points, tolerance, most) {
    var out = simplify(points, tolerance);
    for (var t = tolerance * 2; out.length > most; t *= 2) out = simplify(out, t);
    return out;
  }

  // One of the pen's strokes in thousandths, or null: a known tool, color and size, each point
  // on the picture once in a row, a shape's two corners.
  function penLine(s) {
    if (!s || !Array.isArray(s.points)) return null;
    var head = [INK_TOOLS.indexOf(s.tool), INK_COLORS.indexOf(s.color), s.size];
    if (head[0] < 0 || head[1] < 0 || !whole(head[2], INK_SIZES)) return null;
    var thousandths = function (v) { return Math.round(Math.max(0, Math.min(1, v)) * INK_SCALE); };
    var points = [];
    s.points.forEach(function (p) {
      if (!Array.isArray(p) || typeof p[0] !== 'number' || typeof p[1] !== 'number' || !isFinite(p[0]) || !isFinite(p[1])) return;
      var q = [thousandths(p[0]), thousandths(p[1])];
      var last = points[points.length - 1];
      if (!last || last[0] !== q[0] || last[1] !== q[1]) points.push(q);
    });
    var shape = isShape(head[0]);
    if (shape) points = points.length > 1 ? [points[0], points[points.length - 1]] : [];
    return points.length ? { head: head, points: shape ? points : thin(points, MAX_POINTS * 8), shape: shape } : null;
  }

  // The pen's strokes (vod-ink.js strokes(): { tool, color, size, points } in 0..1 of the
  // picture) as a note keeps them, or null when there are none. The first MAX_STROKES strokes
  // are kept, their lines simplified harder until the drawing fits MAX_INK_POINTS.
  function packInk(strokes) {
    var lines = (Array.isArray(strokes) ? strokes : []).map(penLine).filter(Boolean).slice(0, MAX_STROKES);
    var total = Infinity;
    // Each pass works on the last one's points, twice as loosely.
    for (var tolerance = INK_TOLERANCE; total > MAX_INK_POINTS && tolerance <= INK_SCALE * 4; tolerance *= 2) {
      total = 0;
      for (var i = 0; i < lines.length; i += 1) {
        if (!lines[i].shape) lines[i].points = fewer(lines[i].points, tolerance, MAX_POINTS);
        total += lines[i].points.length;
      }
    }
    return cleanInk(lines.map(function (line) {
      var stroke = line.head.slice();
      line.points.forEach(function (p) { stroke.push(p[0], p[1]); });
      return stroke;
    }));
  }

  // A note's drawing as the pen shows it (vod-ink.js show()): [{ tool, color, size, points }].
  function inkStrokes(ink) {
    return (cleanInk(ink) || []).map(function (s) {
      var points = [];
      for (var i = 3; i < s.length; i += 2) points.push([s[i] / INK_SCALE, s[i + 1] / INK_SCALE]);
      return { tool: INK_TOOLS[s[0]], color: INK_COLORS[s[1]], size: s[2], points: points };
    });
  }

  // Notes.

  // One note in range, or null: t from 0 to MAX_T, a clip's end at least MIN_CLIP after t
  // (closer is a bookmark), times to a tenth of a second. A drawing that can't be read is left
  // out; the note stays.
  function cleanNote(note) {
    if (!note || typeof note !== 'object') return null;
    var t = tenth(note.t);
    if (typeof note.t !== 'number' || !isFinite(t) || t < 0 || t > MAX_T) return null;
    var end = null;
    if (note.end !== null && note.end !== undefined) {
      end = tenth(note.end);
      if (typeof note.end !== 'number' || !isFinite(end) || end > MAX_T) return null;
      if (end - t < MIN_CLIP) end = null;
    }
    var out = { t: t, end: end, text: cleanText(note.text) };
    var ink = cleanInk(note.ink);
    if (ink) out.ink = ink;
    return out;
  }

  // Two notes are the same when their times, words and drawings are.
  function noteKey(note) {
    return note.t + '|' + (note.end === null ? '' : note.end) + '|' + note.text + (note.ink ? '|' + JSON.stringify(note.ink) : '');
  }

  // Clean notes in time order, each once, at most MAX_NOTES.
  function clean(list) {
    if (!Array.isArray(list)) return [];
    var seen = {};
    var out = [];
    list.forEach(function (raw) {
      var note = cleanNote(raw);
      if (!note) return;
      var key = noteKey(note);
      if (seen[key]) return;
      seen[key] = true;
      out.push(note);
    });
    out.sort(function (a, b) { return a.t - b.t || (a.end === null ? -1 : a.end) - (b.end === null ? -1 : b.end); });
    return out.slice(0, MAX_NOTES);
  }

  function merge(mine, incoming) {
    return clean([].concat(mine || [], incoming || []));
  }

  // A note as a link writes it: [t, text] for a bookmark, [t, end, text] for a clip, and
  // [t, end or null, text, ink] for either with a drawing.
  function pack(notes) {
    return JSON.stringify(clean(notes).map(function (n) {
      if (n.ink) return [n.t, n.end, n.text, n.ink];
      return n.end === null ? [n.t, n.text] : [n.t, n.end, n.text];
    }));
  }

  function unpack(json) {
    var rows;
    try { rows = JSON.parse(json); } catch (e) { return null; }
    if (!Array.isArray(rows)) return null;
    return clean(rows.map(function (row) {
      if (!Array.isArray(row)) return null;
      if (row.length === 2) return { t: row[0], end: null, text: row[1] };
      if (row.length === 3) return { t: row[0], end: row[1], text: row[2] };
      if (row.length === 4) return { t: row[0], end: row[1], text: row[2], ink: row[3] };
      return null;
    }));
  }

  function toBase64Url(bytes) {
    var binary = '';
    for (var i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return root.btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function fromBase64Url(value) {
    var base = value.replace(/-/g, '+').replace(/_/g, '/');
    var binary = root.atob(base + '==='.slice((base.length + 3) % 4));
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
    return bytes;
  }

  function canCompress() {
    return typeof root.CompressionStream === 'function' && typeof root.DecompressionStream === 'function' &&
      typeof root.Blob === 'function' && typeof root.Response === 'function';
  }

  function deflate(bytes) {
    var stream = new root.Blob([bytes]).stream().pipeThrough(new root.CompressionStream('deflate-raw'));
    return new root.Response(stream).arrayBuffer().then(function (buffer) { return new Uint8Array(buffer); });
  }

  // Inflates at most MAX_JSON bytes; a link that inflates past that is refused.
  function inflate(bytes) {
    var reader = new root.Blob([bytes]).stream().pipeThrough(new root.DecompressionStream('deflate-raw')).getReader();
    var chunks = [];
    var size = 0;
    function next() {
      return reader.read().then(function (part) {
        if (part.done) {
          var out = new Uint8Array(size);
          var at = 0;
          chunks.forEach(function (chunk) { out.set(chunk, at); at += chunk.length; });
          return out;
        }
        size += part.value.length;
        if (size > MAX_JSON) {
          reader.cancel();
          throw new Error('The notes in this link are too long.');
        }
        chunks.push(part.value);
        return next();
      });
    }
    return next();
  }

  // The notes as a link writes them, at once: 'j' and the JSON in base64url.
  function encodeNow(notes) {
    return 'j' + toBase64Url(new root.TextEncoder().encode(pack(notes)));
  }

  // The shorter of encodeNow and, where the browser can compress, 'z' and the deflated JSON.
  function encode(notes) {
    var plain = encodeNow(notes);
    if (!canCompress()) return Promise.resolve(plain);
    return deflate(new root.TextEncoder().encode(pack(notes))).then(function (bytes) {
      var packed = 'z' + toBase64Url(bytes);
      return packed.length < plain.length ? packed : plain;
    }, function () { return plain; });
  }

  // A link's notes, or null when they can't be read.
  function decode(payload) {
    var value = typeof payload === 'string' ? payload : '';
    if (!/^[jz][A-Za-z0-9_-]*$/.test(value) || value.length > MAX_PAYLOAD) return Promise.resolve(null);
    var bytes;
    try { bytes = fromBase64Url(value.slice(1)); } catch (e) { return Promise.resolve(null); }
    var json = value[0] === 'j' ? Promise.resolve(bytes) : canCompress() ? inflate(bytes) : Promise.reject(new Error('no inflate'));
    return json.then(function (raw) {
      return unpack(new root.TextDecoder('utf-8', { fatal: true }).decode(raw));
    }).catch(function () { return null; });
  }

  // What follows #notes= in a URL's fragment ('' when there is none).
  function fromHash(hash) {
    var match = /(?:^#|&)notes=([^&]*)/.exec(String(hash || ''));
    return match ? match[1] : '';
  }

  function withoutHash(href) {
    var value = String(href || '');
    var at = value.indexOf('#');
    return at < 0 ? value : value.slice(0, at);
  }

  function linkFor(href, payload) {
    return withoutHash(href) + '#notes=' + payload;
  }

  // Storage: one entry per share, { v, saved, notes }. Every call survives storage that is
  // missing, full or blocked (a private window): the notes then last as long as the page.

  // This browser's local storage, or null where it is missing or blocked. Only review notes use
  // it, under PREFIX: nothing that names or follows the viewer.
  function browserStorage(win) {
    try { return (win && win.localStorage) || null; } catch (e) { return null; }
  }

  function storageKey(scope, id) {
    return PREFIX + scope + '.' + id;
  }

  function load(storage, key) {
    try {
      var raw = storage ? storage.getItem(key) : null;
      var data = raw ? JSON.parse(raw) : null;
      return data && Array.isArray(data.notes) ? clean(data.notes) : [];
    } catch (e) {
      return [];
    }
  }

  function save(storage, key, notes, nowMs) {
    try {
      if (!storage) return false;
      if (!notes.length) storage.removeItem(key);
      else storage.setItem(key, JSON.stringify({ v: 1, saved: Number(nowMs) || 0, notes: clean(notes) }));
      return true;
    } catch (e) {
      return false;
    }
  }

  // Drops every share's notes that last changed more than KEEP_MS ago.
  function prune(storage, nowMs) {
    try {
      if (!storage) return;
      var stale = [];
      for (var i = 0; i < storage.length; i += 1) {
        var key = storage.key(i);
        if (!key || key.indexOf(PREFIX) !== 0) continue;
        var data = null;
        try { data = JSON.parse(storage.getItem(key) || 'null'); } catch (e) { data = null; }
        if (!data || !(Number(nowMs) - Number(data.saved) < KEEP_MS)) stale.push(key);
      }
      stale.forEach(function (key) { storage.removeItem(key); });
    } catch (e) { /* storage blocked */ }
  }

  // This browser's notes for a share, plus the ones in the page's link (then dropped from the
  // address bar, so a reload does not add a note the viewer has since deleted). Resolves
  // { notes, added, unreadable }. o: { storage, key, href, replace(url), nowMs }
  function adopt(o) {
    var mine = load(o.storage, o.key);
    var href = String(o.href || '');
    var payload = href.indexOf('#') < 0 ? '' : fromHash(href.slice(href.indexOf('#')));
    if (!payload) return Promise.resolve({ notes: mine, added: 0, unreadable: false });
    return decode(payload).then(function (incoming) {
      if (typeof o.replace === 'function') o.replace(withoutHash(o.href));
      if (!incoming) return { notes: mine, added: 0, unreadable: true };
      var merged = merge(mine, incoming);
      save(o.storage, o.key, merged, o.nowMs);
      return { notes: merged, added: merged.length - mine.length, unreadable: false };
    });
  }

  // What the status line says once a link's notes are read: '' when the link had none.
  function adoptedText(result) {
    if (result.added > 0) return result.added === 1 ? 'Added 1 note from the link.' : 'Added ' + result.added + ' notes from the link.';
    return result.unreadable ? "The notes in this link couldn't be read." : '';
  }

  root.RevuNotes = {
    clean: clean, cleanNote: cleanNote, cleanText: cleanText, merge: merge, pack: pack, unpack: unpack, tenth: tenth,
    cleanInk: cleanInk, packInk: packInk, inkStrokes: inkStrokes, simplify: simplify,
    encode: encode, encodeNow: encodeNow, decode: decode, fromHash: fromHash, linkFor: linkFor, withoutHash: withoutHash,
    storageKey: storageKey, browserStorage: browserStorage, load: load, save: save, prune: prune, adopt: adopt,
    adoptedText: adoptedText,
    MAX_NOTES: MAX_NOTES, MAX_TEXT: MAX_TEXT, MAX_PAYLOAD: MAX_PAYLOAD, MIN_CLIP: MIN_CLIP, KEEP_MS: KEEP_MS, PREFIX: PREFIX,
    INK_TOOLS: INK_TOOLS, INK_COLORS: INK_COLORS, INK_SCALE: INK_SCALE, MAX_STROKES: MAX_STROKES, MAX_POINTS: MAX_POINTS,
    MAX_INK_POINTS: MAX_INK_POINTS
  };
})(typeof window !== 'undefined' ? window : globalThis);

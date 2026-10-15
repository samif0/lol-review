/*
 * Review notes on a shared game (vod.html) or a shared clip (clip.html): the bookmarks and
 * clips a coach makes while watching, kept in this browser for that share and sent on as a
 * link. A link carries its notes after the # (#notes=...), the part of a URL a browser never
 * sends to a server; the page that opens it adds them to that browser's notes for the share.
 * Nothing here is uploaded: the notes live in this browser's storage and in the links made
 * from it.
 *
 * A note is { t, end, text }: t, and end for a clip, in the page's own clock (game seconds
 * on vod.html, seconds into the clip on clip.html); a bookmark's end is null.
 *
 * This file is the data: cleaning, links (encode, decode), storage, and adopt(), which reads a
 * share's notes and adds a link's. review-notes-ui.js runs the composer and Copy link with
 * notes. Everything here is pure or takes its storage as an argument, for the tests.
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

  // One note in range, or null: t from 0 to MAX_T, a clip's end at least MIN_CLIP after t
  // (closer is a bookmark), times to a tenth of a second.
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
    return { t: t, end: end, text: cleanText(note.text) };
  }

  function noteKey(note) {
    return note.t + '|' + (note.end === null ? '' : note.end) + '|' + note.text;
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

  // A note as a link writes it: [t, text] for a bookmark, [t, end, text] for a clip.
  function pack(notes) {
    return JSON.stringify(clean(notes).map(function (n) { return n.end === null ? [n.t, n.text] : [n.t, n.end, n.text]; }));
  }

  function unpack(json) {
    var rows;
    try { rows = JSON.parse(json); } catch (e) { return null; }
    if (!Array.isArray(rows)) return null;
    return clean(rows.map(function (row) {
      if (!Array.isArray(row)) return null;
      if (row.length === 2) return { t: row[0], end: null, text: row[1] };
      if (row.length === 3) return { t: row[0], end: row[1], text: row[2] };
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
    encode: encode, encodeNow: encodeNow, decode: decode, fromHash: fromHash, linkFor: linkFor, withoutHash: withoutHash,
    storageKey: storageKey, browserStorage: browserStorage, load: load, save: save, prune: prune, adopt: adopt,
    adoptedText: adoptedText,
    MAX_NOTES: MAX_NOTES, MAX_TEXT: MAX_TEXT, MAX_PAYLOAD: MAX_PAYLOAD, MIN_CLIP: MIN_CLIP, KEEP_MS: KEEP_MS, PREFIX: PREFIX
  };
})(typeof window !== 'undefined' ? window : globalThis);

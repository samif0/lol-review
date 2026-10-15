/*
 * Review notes on the page (review-notes.js has the data): the composer and Copy link with
 * notes, shared by the shared game page (vod-side.js) and the shared clip page
 * (clip-coach.js).
 *
 * RevuNotesUi.composer(o) runs the app reviewer's keys over the page's form: B bookmarks the
 * moment on screen, I starts a clip and O ends it, Enter saves and Esc drops the draft.
 * RevuNotesUi.sharer(o) runs Copy link with notes. statusLine() writes the quiet line over the
 * notes, with an Undo when one is given.
 */
(function (root) {
  'use strict';

  var N = root.RevuNotes;

  function editable(target) {
    if (!target || !target.tagName) return false;
    var tag = String(target.tagName).toUpperCase();
    return tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA' || target.isContentEditable === true;
  }

  // What the composer says above its field.
  function whenText(draft, clock) {
    if (!draft) return '';
    if (draft.kind === 'bookmark') return 'Bookmark at ' + clock(draft.t) + '.';
    if (draft.t === null) return 'Press I to start a clip, then O to end it.';
    if (draft.end === null) return 'Clip from ' + clock(draft.t) + '. Press O to end it.';
    return 'Clip from ' + clock(draft.t) + ' to ' + clock(draft.end) + '.';
  }

  /*
   * The composer over the page's form. o: { els: { form, when, text, cancel, clip, clipLabel,
   * clipKey, hint }, now() (the page's clock now), clock(t), pause(), open() (show the notes),
   * onSave(note), onDraft(draft) }. The draft is null or { kind, t, end }.
   */
  function composer(o) {
    var els = o.els;
    var draft = null;

    function render() {
      els.form.hidden = !draft;
      els.when.textContent = whenText(draft, o.clock);
      var ending = !!draft && draft.kind === 'clip' && draft.t !== null && draft.end === null;
      if (els.clipLabel) els.clipLabel.textContent = ending ? 'End clip' : 'Clip';
      if (els.clipKey) els.clipKey.textContent = ending ? 'O' : 'I';
      if (els.clip) {
        els.clip.title = ending ? 'End the clip here (O)' : 'Start a clip here (I), then end it with O';
        els.clip.setAttribute('aria-pressed', String(ending));
      }
      if (typeof o.onDraft === 'function') o.onDraft(draft);
    }

    function hint(message) {
      if (els.hint) {
        els.hint.textContent = message || '';
        els.hint.hidden = !message;
      }
    }

    function now() {
      var t = Number(o.now());
      return isFinite(t) && t > 0 ? N.tenth(t) : 0;
    }

    function focusText() {
      if (typeof o.pause === 'function') o.pause();
      if (typeof els.text.focus === 'function') els.text.focus();
    }

    function start(next) {
      var opening = !draft;
      if (!draft || draft.kind !== next.kind) els.text.value = '';
      draft = next;
      hint('');
      if (opening && typeof o.open === 'function') o.open();
      render();
    }

    function bookmark() {
      start({ kind: 'bookmark', t: now(), end: null });
      focusText();
    }

    function clipIn() {
      start({ kind: 'clip', t: now(), end: null });
    }

    function clipOut() {
      if (!draft || draft.kind !== 'clip' || draft.t === null) {
        start({ kind: 'clip', t: null, end: null });
        return;
      }
      var at = now();
      var from = Math.min(draft.t, at);
      var to = Math.max(draft.t, at);
      if (to - from < N.MIN_CLIP) {
        hint('Play on, then press O to end the clip.');
        return;
      }
      draft = { kind: 'clip', t: from, end: to };
      hint('');
      render();
      focusText();
    }

    // The clip button: start, then end.
    function clipButton() {
      if (draft && draft.kind === 'clip' && draft.t !== null && draft.end === null) clipOut();
      else clipIn();
    }

    function close() {
      draft = null;
      els.text.value = '';
      hint('');
      render();
      if (typeof els.text.blur === 'function') els.text.blur();
    }

    function save() {
      if (!draft) return false;
      if (draft.kind === 'clip' && draft.t === null) {
        hint('Press I to start the clip first.');
        return false;
      }
      if (draft.kind === 'clip' && draft.end === null) {
        clipOut();
        if (!draft || draft.end === null) return false;
      }
      var note = N.cleanNote({ t: draft.t, end: draft.end, text: els.text.value });
      if (!note) return false;
      o.onSave(note);
      close();
      return true;
    }

    els.form.addEventListener('submit', function (ev) { ev.preventDefault(); save(); });
    if (els.cancel) els.cancel.addEventListener('click', close);

    // True when the key was the composer's. The page's own keys (play, seek, the pen) come first.
    function key(ev) {
      if (ev.isComposing) return false;
      var k = String(ev.key || '');
      if (ev.target === els.text) {
        if (k === 'Enter' && !ev.shiftKey && !ev.altKey) { if (!ev.repeat) save(); return true; }
        if (k === 'Escape') { close(); return true; }
        return false;
      }
      if (editable(ev.target) || ev.ctrlKey || ev.metaKey || ev.altKey) return false;
      var lower = k.toLowerCase();
      if (lower === 'escape' && draft) { close(); return true; }
      if (lower !== 'b' && lower !== 'i' && lower !== 'o') return false;
      if (!ev.repeat) {
        if (lower === 'b') bookmark();
        else if (lower === 'i') clipIn();
        else clipOut();
      }
      return true;
    }

    render();
    return {
      bookmark: bookmark, clipIn: clipIn, clipOut: clipOut, clipButton: clipButton, save: save, cancel: close, key: key,
      draft: function () { return draft ? { kind: draft.kind, t: draft.t, end: draft.end } : null; }
    };
  }

  /*
   * Copy link with notes. o: { els: { box, copy, status, link }, href() (the page's address),
   * notes(), clipboard (navigator.clipboard), setTimeout, clearTimeout }. The link is worked
   * out whenever the notes change, so a press copies at once (Safari copies only inside the
   * press itself).
   */
  function sharer(o) {
    var els = o.els;
    var payload = '';
    var round = 0;
    var timer = null;
    var wait = typeof o.setTimeout === 'function' ? o.setTimeout : function () { return 0; };
    var unwait = typeof o.clearTimeout === 'function' ? o.clearTimeout : function () {};

    function say(message) {
      els.status.textContent = message;
    }

    function refresh() {
      var notes = o.notes();
      els.box.hidden = notes.length === 0;
      els.link.hidden = true;
      payload = '';
      if (!notes.length) return;
      var mine = ++round;
      try {
        N.encode(notes).then(function (value) { if (mine === round) payload = value; }, function () {});
      } catch (e) { /* no encoder yet: the press works the link out */ }
    }

    function copy() {
      var notes = o.notes();
      if (!notes.length) return;
      var link = '';
      try { link = N.linkFor(o.href(), payload || N.encodeNow(notes)); } catch (e) { say("This browser can't make the link."); return; }
      var fallback = function () {
        say('Copy this link:');
        els.link.value = link;
        els.link.hidden = false;
        if (typeof els.link.focus === 'function') els.link.focus();
        if (typeof els.link.select === 'function') els.link.select();
      };
      try {
        if (!o.clipboard || typeof o.clipboard.writeText !== 'function') throw new Error('no clipboard');
        o.clipboard.writeText(link).then(function () {
          els.link.hidden = true;
          say('Link copied. Anyone with it sees these notes.');
          if (timer) unwait(timer);
          timer = wait(function () { say(''); }, 6000);
        }, fallback);
      } catch (e) {
        fallback();
      }
    }

    els.copy.addEventListener('click', copy);
    refresh();
    return { refresh: refresh, copy: copy, link: function () { return payload ? N.linkFor(o.href(), payload) : ''; } };
  }

  // The quiet line over the notes ("Added 2 notes from the link."), with an optional button
  // (Undo) after it. Returns the button, or null.
  function statusLine(doc, box, message, actionLabel, action) {
    while (box.firstChild) box.removeChild(box.firstChild);
    box.textContent = '';
    if (!message) return null;
    var words = doc.createElement('span');
    words.textContent = message;
    box.appendChild(words);
    if (!actionLabel) return null;
    var button = doc.createElement('button');
    button.type = 'button';
    button.className = 'vod-undo';
    button.textContent = actionLabel;
    button.addEventListener('click', action);
    box.appendChild(button);
    return button;
  }

  root.RevuNotesUi = { composer: composer, sharer: sharer, statusLine: statusLine, whenText: whenText, editable: editable };
})(typeof window !== 'undefined' ? window : globalThis);

/*
 * Pure helpers for the web review tool on the home page (app.js): review
 * notes kept per match in localStorage, the user-facing error copy, and the
 * match list title. No DOM, so tests/web-review.test.mjs runs it in node:vm.
 */
(function (root) {
  'use strict';

  var NOTES_KEY = 'revu.web.notes.v1';
  var NOTES_LIMIT = 200;

  function readAll(store) {
    try {
      var parsed = JSON.parse(store.getItem(NOTES_KEY) || '{}');
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
    } catch (e) {
      return {};
    }
  }

  /** The notes saved for one match id, or ''. */
  function notesFor(store, matchId) {
    if (!store || !matchId) return '';
    var entry = readAll(store)[matchId];
    return entry && typeof entry.text === 'string' ? entry.text : '';
  }

  /** Save (or, when empty, forget) one match's notes; keeps the newest 200 matches. */
  function saveNotes(store, matchId, text, now) {
    if (!store || !matchId) return;
    var all = readAll(store);
    if (text) all[matchId] = { text: String(text), at: Number(now) || 0 };
    else delete all[matchId];
    var ids = Object.keys(all);
    if (ids.length > NOTES_LIMIT) {
      ids.sort(function (a, b) { return (all[b].at || 0) - (all[a].at || 0); });
      ids.slice(NOTES_LIMIT).forEach(function (id) { delete all[id]; });
    }
    try {
      store.setItem(NOTES_KEY, JSON.stringify(all));
    } catch (e) {
      // Quota or blocked storage: the notes stay in the textarea only.
    }
  }

  /**
   * Older versions kept one global note for every match. Move it onto the
   * match it was last shown with (only if that match has no notes yet), so
   * nothing typed is lost and it stops following the user to other games.
   */
  function migrateLegacyNotes(store, legacyText, matchId, now) {
    if (!legacyText || !matchId || notesFor(store, matchId)) return;
    saveNotes(store, matchId, legacyText, now);
  }

  var COPY = {
    turnstile: "We couldn't verify your browser. Try again.",
    session_busy: 'Too many lookups from your network. Wait a few minutes and try again.',
    session: "Couldn't start a lookup. Try again in a moment.",
    network: "Couldn't reach Revu. Check your connection and try again.",
    expired: 'Your lookup session ended. Try again.',
    busy: 'Too many lookups right now. Wait a few seconds and try again.',
    timeout: 'Riot took too long to answer. Try again.',
    riot: 'Riot lookups are unavailable right now. Try again later.'
  };

  /** Classify a thrown error from the session or fetch step. */
  function failureFromError(err) {
    var message = String((err && err.message) || '');
    if (message.indexOf('turnstile') === 0) return 'turnstile';
    if (message === 'session_mint_failed_429') return 'session_busy';
    if (message.indexOf('session_mint') === 0) return 'session';
    return 'network';
  }

  /** Classify a non-OK proxy response (404 is handled by the caller). */
  function failureFromStatus(status) {
    if (status === 401) return 'expired';
    if (status === 429) return 'busy';
    if (status === 504) return 'timeout';
    return 'riot';
  }

  function errorCopy(failure) {
    return COPY[failure] || COPY.riot;
  }

  /** The list holds every queue, so it is titled as recent games, not ranked. */
  function matchesTitle(name) {
    return (name || 'Player') + "'s recent games";
  }

  root.RevuWebReview = {
    NOTES_KEY: NOTES_KEY,
    notesFor: notesFor,
    saveNotes: saveNotes,
    migrateLegacyNotes: migrateLegacyNotes,
    failureFromError: failureFromError,
    failureFromStatus: failureFromStatus,
    errorCopy: errorCopy,
    matchesTitle: matchesTitle
  };
})(this);

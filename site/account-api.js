/*
 * Revu web accounts: the one client for clips.revu.lol/auth/web/*.
 *
 * A classic script shared by sign-in.js, my-clips.js and account.js. It
 * touches no DOM, so tests run it in node:vm with stubbed globals.
 *
 * The session lives in an HttpOnly cookie that only clips.revu.lol can see.
 * This file never sees the token. The only thing it stores is a "signed in"
 * hint ('1') so pages can pick the right copy before the network answers;
 * no token, email or user id is ever written to storage.
 */
(function (root) {
  'use strict';

  var hostname = '';
  try { hostname = String((root.location && root.location.hostname) || ''); } catch (e) { hostname = ''; }

  var API_BASE = hostname === 'localhost' ? 'http://localhost:8787' : 'https://clips.revu.lol';
  // Same keys as app.js: Cloudflare's always-pass test key on local dev.
  var TURNSTILE_SITE_KEY = (hostname === 'localhost' || hostname === '127.0.0.1')
    ? '1x00000000000000000000AA'
    : '0x4AAAAAADUmEL-Xamn7L2E0';
  var HINT_KEY = 'revu.web.signedIn.v1';
  var NEXT_PATHS = ['/my-clips', '/account'];
  var CLIP_ID = /^[0-9A-Za-z]{7}$/;
  var CLIP_URL = /^https:\/\/clips\.revu\.lol\/[0-9A-Za-z]{7}$/;
  // Shared full games play on revu.lol/v/<id>. wrangler pages dev serves the same
  // page on a loopback port, which is accepted only while this page is local too.
  var VOD_URL = /^https:\/\/revu\.lol\/v\/[0-9A-Za-z]{7}$/;
  var DEV_VOD_URL = /^http:\/\/(localhost|127\.0\.0\.1):\d{1,5}\/v\/[0-9A-Za-z]{7}$/;
  var LOCAL_PAGE = hostname === 'localhost' || hostname === '127.0.0.1';

  var COPY = {
    invalid_email: "That email address doesn't look valid.",
    turnstile_required: "We couldn't verify your browser. Try again.",
    turnstile_failed: "We couldn't verify your browser. Try again.",
    rate_limit_auth: 'Too many attempts. Wait a minute and try again.',
    email_send_failed: "Couldn't send the email right now. Try again in a moment.",
    email_daily_cap: 'Revu has sent all the sign-in emails it can for today. Try again after midnight UTC.',
    code_required: 'Enter the code from your email.',
    invalid_or_expired_code: 'That code is invalid or expired. Request a new one.',
    sign_in_unavailable: 'Sign in is unavailable right now. Try again later.',
    confirm_mismatch: "That email doesn't match this account.",
    signed_out: 'Your session ended. Sign in again.',
    session_expired: 'Your session ended. Sign in again.',
    network: "Couldn't reach Revu. Check your connection and try again."
  };
  var GENERIC_COPY = 'Something went wrong. Try again.';

  function storage() {
    try { return root.localStorage || null; } catch (e) { return null; }
  }

  function setHint(on) {
    try {
      var store = storage();
      if (!store) return;
      if (on) store.setItem(HINT_KEY, '1');
      else store.removeItem(HINT_KEY);
    } catch (e) { /* storage blocked: the hint is only a convenience */ }
  }

  function hasHint() {
    try {
      var store = storage();
      return !!store && store.getItem(HINT_KEY) === '1';
    } catch (e) {
      return false;
    }
  }

  function toSeconds(value) {
    if (value === null || value === undefined || value === '') return null;
    var n = Number(value);
    return isFinite(n) && n >= 0 ? n : null;
  }

  function headerValue(res, name) {
    try {
      return res && res.headers && typeof res.headers.get === 'function' ? res.headers.get(name) : null;
    } catch (e) {
      return null;
    }
  }

  // Every call to /auth/web/* goes through here. Resolves, never rejects.
  function request(method, path, body) {
    var headers = {};
    if (method !== 'GET') headers['X-Revu-CSRF'] = '1';
    var init = {
      method: method,
      credentials: 'include',
      mode: 'cors',
      cache: 'no-store',
      headers: headers
    };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    var pending;
    try {
      pending = Promise.resolve(root.fetch(API_BASE + path, init));
    } catch (e) {
      pending = Promise.reject(e);
    }
    return pending.then(function (res) {
      return Promise.resolve()
        .then(function () { return res.json(); })
        .catch(function () { return {}; })
        .then(function (parsed) {
          var data = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
          var retryAfter = toSeconds(data.retry_after);
          if (retryAfter === null) retryAfter = toSeconds(headerValue(res, 'Retry-After'));
          if (res.status === 401) setHint(false);
          return { ok: !!res.ok, status: res.status, data: data, retryAfter: retryAfter };
        });
    }, function () {
      return { ok: false, status: 0, data: { error: 'network' }, retryAfter: null };
    });
  }

  function hintOn200(res) {
    if (res.status === 200) setHint(true);
    return res;
  }

  function hintOff(res) {
    setHint(false);
    return res;
  }

  function me() { return request('GET', '/auth/web/me').then(hintOn200); }
  function providers() { return request('GET', '/auth/web/providers'); }
  function startEmail(email, turnstileToken) {
    return request('POST', '/auth/web/email/start', { email: email, turnstileToken: turnstileToken });
  }
  function verifyEmail(email, code) {
    return request('POST', '/auth/web/email/verify', { email: email, code: code }).then(hintOn200);
  }
  function listClips() { return request('GET', '/auth/web/clips'); }
  function deleteClip(id) { return request('DELETE', '/auth/web/clips/' + encodeURIComponent(id)); }
  function logout() { return request('POST', '/auth/web/logout').then(hintOff); }
  function logoutAll() { return request('POST', '/auth/web/logout-all').then(hintOff); }
  function deleteAccount(email) {
    return request('POST', '/auth/web/account/delete', { email: email }).then(function (res) {
      if (res.status === 200) setHint(false);
      return res;
    });
  }

  function safeNext(raw) {
    return NEXT_PATHS.indexOf(raw) !== -1 ? raw : '/my-clips';
  }

  function afterSignInUrl(next, newAccount) {
    return safeNext(next) + (newAccount === true ? '?new=1' : '');
  }

  function errorCopy(code, retryAfter) {
    if (code === 'rate_limit_email') {
      var seconds = toSeconds(retryAfter);
      if (seconds === null) seconds = 60;
      var minutes = Math.max(1, Math.ceil(seconds / 60));
      return 'We just sent a code to this email. Check your inbox, or try again in ' + minutes + ' min.';
    }
    return Object.prototype.hasOwnProperty.call(COPY, code) ? COPY[code] : GENERIC_COPY;
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function formatDuration(sec) {
    if (sec === null || sec === undefined || sec === '') return '';
    var n = Number(sec);
    if (!isFinite(n) || n < 0) return '';
    var total = Math.floor(n);
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    return h > 0 ? h + ':' + pad2(m) + ':' + pad2(s) : m + ':' + pad2(s);
  }

  function formatViews(n) {
    var count = Math.max(0, Math.floor(Number(n) || 0));
    return count === 1 ? '1 view' : count + ' views';
  }

  function formatTimeLeft(expiresAt, nowSec) {
    var rem = (Number(expiresAt) || 0) - (Number(nowSec) || 0);
    if (!(rem > 0)) return 'Expired';
    if (rem < 3600) return 'Expires in ' + Math.max(1, Math.ceil(rem / 60)) + ' min';
    if (rem < 86400) return 'Expires in ' + Math.floor(rem / 3600) + ' hr';
    var days = Math.floor(rem / 86400);
    return days === 1 ? 'Expires in 1 day' : 'Expires in ' + days + ' days';
  }

  function formatMemberSince(sec) {
    var n = Number(sec);
    if (!isFinite(n) || n <= 0) return '';
    return new Date(n * 1000).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });
  }

  function isClipId(id) {
    return typeof id === 'string' && CLIP_ID.test(id);
  }

  function clipShareUrl(clip) {
    var url = clip && clip.url;
    if (typeof url === 'string' && CLIP_URL.test(url)) return url;
    return 'https://clips.revu.lol/' + (clip && clip.id);
  }

  function watchHref(id) {
    return '/clip?id=' + id;
  }

  function vodShareUrl(share) {
    var url = share && share.url;
    if (typeof url === 'string' && (VOD_URL.test(url) || (LOCAL_PAGE && DEV_VOD_URL.test(url)))) return url;
    return 'https://revu.lol/v/' + (share && share.id);
  }

  root.RevuAccount = {
    API_BASE: API_BASE,
    TURNSTILE_SITE_KEY: TURNSTILE_SITE_KEY,
    HINT_KEY: HINT_KEY,
    request: request,
    me: me,
    providers: providers,
    startEmail: startEmail,
    verifyEmail: verifyEmail,
    listClips: listClips,
    deleteClip: deleteClip,
    logout: logout,
    logoutAll: logoutAll,
    deleteAccount: deleteAccount,
    setHint: setHint,
    hasHint: hasHint,
    safeNext: safeNext,
    afterSignInUrl: afterSignInUrl,
    errorCopy: errorCopy,
    formatDuration: formatDuration,
    formatViews: formatViews,
    formatTimeLeft: formatTimeLeft,
    formatMemberSince: formatMemberSince,
    isClipId: isClipId,
    clipShareUrl: clipShareUrl,
    watchHref: watchHref,
    vodShareUrl: vodShareUrl
  };
})(globalThis);

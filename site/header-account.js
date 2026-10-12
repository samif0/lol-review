/*
 * Header account link. Every page with a [data-header-account] link loads
 * this deferred script. It makes no network calls: it reads the "signed in"
 * hint that account-api.js keeps in localStorage and swaps "Sign in" for
 * "My clips" when the hint is set. The pages themselves still check the real
 * session, so a stale hint only changes this one label.
 */
(function () {
  'use strict';

  var HINT_KEY = 'revu.web.signedIn.v1';

  function signedInHint() {
    try {
      return window.localStorage.getItem(HINT_KEY) === '1';
    } catch (e) {
      return false;
    }
  }

  function samePath(a, b) {
    var strip = function (p) { return String(p || '').replace(/\.html$/, '').replace(/\/+$/, '') || '/'; };
    return strip(a) === strip(b);
  }

  var links = document.querySelectorAll('[data-header-account]');
  if (!links.length) return;
  var signedIn = signedInHint();
  var path = window.location.pathname;

  for (var i = 0; i < links.length; i++) {
    var link = links[i];
    if (signedIn) {
      link.textContent = 'My clips';
      link.setAttribute('href', '/my-clips');
    }
    var target = link.getAttribute('href');
    var current = samePath(path, target) ||
      (link.textContent === 'Sign in' && samePath(path, '/sign-in'));
    if (current) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
})();

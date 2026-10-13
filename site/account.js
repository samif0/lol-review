/*
 * Account page (account.html): who is signed in, sign out, sign out every
 * browser, and self-serve account deletion. Fields are filled with
 * textContent only. canConfirmDelete is exported for tests; DOM wiring only
 * runs in a browser.
 */
(function (root) {
  'use strict';

  function canConfirmDelete(typed, email) {
    var a = String(typed === null || typed === undefined ? '' : typed).trim().toLowerCase();
    var b = String(email === null || email === undefined ? '' : email).trim().toLowerCase();
    return a.length > 0 && a === b;
  }

  root.RevuAccountPage = { canConfirmDelete: canConfirmDelete };

  if (typeof document === 'undefined') return;

  var api = root.RevuAccount;
  var params = new URLSearchParams(location.search);
  var hadHint = api.hasHint();
  var email = '';
  var pending = false;
  // Only the newest load may update the page (Retry can overlap a slow one).
  var loadSeq = 0;

  function byId(id) { return document.getElementById(id); }

  var loading = byId('state-loading');
  var failed = byId('state-error');
  var cards = byId('account-cards');
  var notice = byId('new-account-notice');
  var signOut = byId('sign-out');
  var signOutAll = byId('sign-out-all');
  var signOutError = byId('sign-out-error');
  var deleteForm = byId('delete-form');
  var deleteInput = byId('delete-confirm');
  var deleteButton = byId('delete-account');
  var deleteError = byId('delete-error');

  function signIn() {
    location.replace(hadHint ? '/sign-in?next=/account&reason=session_expired' : '/sign-in?next=/account');
  }

  function show(which) {
    loading.hidden = which !== 'loading';
    failed.hidden = which !== 'error';
    cards.hidden = which !== 'cards';
  }

  function setPending(on) {
    pending = on;
    signOut.disabled = on;
    signOutAll.disabled = on;
    deleteInput.disabled = on;
    deleteButton.disabled = on || !canConfirmDelete(deleteInput.value, email);
  }

  function fill(user) {
    email = typeof user.email === 'string' ? user.email : '';
    byId('account-email').textContent = email;
    byId('identity-email').textContent = email;
    byId('desktop-email').textContent = email;
    var since = api.formatMemberSince(user.created_at);
    byId('member-since').textContent = since ? 'Member since ' + since : '';
    if (params.get('new') === '1') {
      byId('new-account-email').textContent = email;
      notice.hidden = false;
      try { history.replaceState(null, '', '/account'); } catch (e) { /* keep the param */ }
    }
    deleteButton.disabled = !canConfirmDelete(deleteInput.value, email);
  }

  function load() {
    var seq = ++loadSeq;
    show('loading');
    api.me().then(function (res) {
      if (seq !== loadSeq) return;
      if (res.status === 401) {
        signIn();
        return;
      }
      if (!res.ok || !res.data.user) {
        show('error');
        return;
      }
      // A 200 means a live session, so a later 401 means it ended.
      hadHint = true;
      fill(res.data.user);
      show('cards');
    });
  }

  function leave(call) {
    if (pending) return;
    signOutError.textContent = '';
    setPending(true);
    call().then(function (res) {
      if (res.ok || res.status === 401) {
        api.setHint(false);
        location.assign('/');
        return;
      }
      setPending(false);
      signOutError.textContent = api.errorCopy(res.data.error, res.retryAfter);
    });
  }

  signOut.addEventListener('click', function () { leave(api.logout); });
  signOutAll.addEventListener('click', function () { leave(api.logoutAll); });

  deleteInput.addEventListener('input', function () {
    deleteError.textContent = '';
    deleteButton.disabled = pending || !canConfirmDelete(deleteInput.value, email);
  });

  deleteForm.addEventListener('submit', function (event) {
    event.preventDefault();
    if (pending || !canConfirmDelete(deleteInput.value, email)) return;
    deleteError.textContent = '';
    setPending(true);
    deleteButton.textContent = 'Deleting...';
    api.deleteAccount(deleteInput.value.trim()).then(function (res) {
      if (res.status === 200) {
        api.setHint(false);
        location.assign('/');
        return;
      }
      if (res.status === 401) {
        signIn();
        return;
      }
      deleteButton.textContent = 'Delete account';
      setPending(false);
      deleteError.textContent = api.errorCopy(res.data.error, res.retryAfter);
    });
  });

  byId('new-account-dismiss').addEventListener('click', function () { notice.hidden = true; });
  byId('retry').addEventListener('click', load);

  load();
})(globalThis);

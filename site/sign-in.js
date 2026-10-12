/*
 * Sign-in page (sign-in.html): email, then a one-time code.
 *
 * Step 1 posts the email with a Turnstile token; step 2 verifies the code
 * and moves on to the allowlisted next page. All network calls go through
 * account-api.js. Neither the email nor the code is written to storage.
 * The pure helpers at the top are exported for tests; DOM wiring only runs
 * in a browser.
 */
(function (root) {
  'use strict';

  var EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
  var DEFAULT_RESEND = 60;
  var TURNSTILE_TIMEOUT_MS = 30000;
  var TURNSTILE_WAIT_MS = 10000;
  var CHECK_COPY = 'Complete the quick check to continue.';

  function normalizeEmail(value) {
    return String(value === null || value === undefined ? '' : value).trim().toLowerCase();
  }

  function normalizeCode(value) {
    return String(value === null || value === undefined ? '' : value)
      .replace(/[\s-]+/g, '')
      .toUpperCase()
      .slice(0, 16);
  }

  function isValidEmail(email) {
    return email.length >= 3 && email.length <= 254 && EMAIL_RE.test(email);
  }

  function resendDelay(retryAfter) {
    var n = Number(retryAfter);
    return retryAfter !== null && retryAfter !== undefined && isFinite(n) && n > 0 ? Math.ceil(n) : DEFAULT_RESEND;
  }

  function startOutcome(res) {
    if (res && res.status === 200) return 'code_step';
    if (res && res.status === 429 && res.data && res.data.error === 'rate_limit_email') return 'code_step_throttled';
    return 'error';
  }

  root.RevuSignIn = {
    normalizeEmail: normalizeEmail,
    normalizeCode: normalizeCode,
    resendDelay: resendDelay,
    startOutcome: startOutcome
  };

  if (typeof document === 'undefined') return;

  var api = root.RevuAccount;
  var params = new URLSearchParams(location.search);
  var next = api.safeNext(params.get('next'));
  var reason = params.get('reason');

  var statusEl = document.getElementById('sign-in-status');
  var errorEl = document.getElementById('sign-in-error');
  var emailForm = document.getElementById('email-form');
  var emailInput = document.getElementById('email-input');
  var emailSubmit = document.getElementById('email-submit');
  var haveCode = document.getElementById('have-code');
  var codeForm = document.getElementById('code-form');
  var codeEmail = document.getElementById('code-email');
  var codeInput = document.getElementById('code-input');
  var codeSubmit = document.getElementById('code-submit');
  var resendBtn = document.getElementById('resend-code');
  var goBack = document.getElementById('go-back');
  var turnstileBox = document.getElementById('turnstile-container');

  var email = '';
  var busy = false;
  var unavailable = false;
  var countdownTimer = null;
  var countdownEnd = 0;
  var widgetId = null;
  var pendingSolve = null;

  function setStatus(text) { statusEl.textContent = text || ''; }
  function setError(text) { errorEl.textContent = text || ''; }

  // Turnstile: one widget for the page, executed once per code request and
  // reset after every use so each token is fresh and single use.
  function turnstileReady() {
    return new Promise(function (resolve) {
      var start = Date.now();
      (function tick() {
        if (root.turnstile) return resolve(true);
        if (Date.now() - start > TURNSTILE_WAIT_MS) return resolve(false);
        setTimeout(tick, 100);
      })();
    });
  }

  function settleSolve(err, token) {
    var solve = pendingSolve;
    if (!solve) return;
    pendingSolve = null;
    clearTimeout(solve.timer);
    turnstileBox.classList.remove('is-interactive');
    if (statusEl.textContent === CHECK_COPY) setStatus('');
    try { root.turnstile.reset(widgetId); } catch (e) { /* widget gone */ }
    if (err) solve.reject(err);
    else solve.resolve(token);
  }

  function ensureWidget() {
    if (widgetId !== null) return;
    widgetId = root.turnstile.render(turnstileBox, {
      sitekey: api.TURNSTILE_SITE_KEY,
      action: 'web_signin',
      size: 'flexible',
      appearance: 'interaction-only',
      execution: 'execute',
      callback: function (token) { settleSolve(null, token); },
      'error-callback': function () { settleSolve(new Error('turnstile_error')); return true; },
      'expired-callback': function () { settleSolve(new Error('turnstile_expired')); },
      'timeout-callback': function () { settleSolve(new Error('turnstile_timeout')); },
      'before-interactive-callback': function () {
        turnstileBox.classList.add('is-interactive');
        setStatus(CHECK_COPY);
      }
    });
  }

  function turnstileToken() {
    return turnstileReady().then(function (ready) {
      if (!ready || !root.turnstile) throw new Error('turnstile_unavailable');
      return new Promise(function (resolve, reject) {
        pendingSolve = {
          resolve: resolve,
          reject: reject,
          timer: setTimeout(function () { settleSolve(new Error('turnstile_timeout')); }, TURNSTILE_TIMEOUT_MS)
        };
        try {
          ensureWidget();
          root.turnstile.execute(widgetId);
        } catch (e) {
          settleSolve(e);
        }
      });
    });
  }

  // Resolves to an account-api result, or {turnstile: true} when the check
  // could not produce a token.
  function requestCode(address) {
    return turnstileToken().then(function (token) {
      return api.startEmail(address, token);
    }, function () {
      return { turnstile: true };
    });
  }

  function stopCountdown() {
    if (countdownTimer !== null) clearTimeout(countdownTimer);
    countdownTimer = null;
    resendBtn.disabled = unavailable;
    resendBtn.textContent = 'Resend code';
  }

  function tickCountdown() {
    var left = Math.ceil((countdownEnd - Date.now()) / 1000);
    if (left <= 0) {
      stopCountdown();
      return;
    }
    resendBtn.disabled = true;
    resendBtn.textContent = 'Resend code in ' + left + 's';
    countdownTimer = setTimeout(tickCountdown, 1000);
  }

  function startCountdown(seconds) {
    if (countdownTimer !== null) clearTimeout(countdownTimer);
    countdownEnd = Date.now() + seconds * 1000;
    tickCountdown();
  }

  function showCodeStep() {
    codeEmail.textContent = email;
    emailForm.hidden = true;
    codeForm.hidden = false;
    codeInput.value = '';
    codeInput.focus();
  }

  function showEmailStep() {
    stopCountdown();
    codeForm.hidden = true;
    emailForm.hidden = false;
    emailInput.value = email;
    setError('');
    setStatus('');
    emailInput.focus();
  }

  function setDisabled(form, disabled) {
    var controls = form.querySelectorAll('input, button');
    for (var i = 0; i < controls.length; i++) controls[i].disabled = disabled;
  }

  function readEmail() {
    var value = normalizeEmail(emailInput.value);
    if (!isValidEmail(value)) {
      setError(api.errorCopy('invalid_email'));
      emailInput.focus();
      return null;
    }
    return value;
  }

  emailForm.addEventListener('submit', function (event) {
    event.preventDefault();
    if (busy || unavailable) return;
    setError('');
    var address = readEmail();
    if (!address) return;
    busy = true;
    emailSubmit.disabled = true;
    emailSubmit.textContent = 'Sending...';
    requestCode(address).then(function (res) {
      if (res.turnstile) {
        setError(api.errorCopy('turnstile_failed'));
        return;
      }
      var outcome = startOutcome(res);
      if (outcome === 'error') {
        setError(api.errorCopy(res.data.error, res.retryAfter));
        return;
      }
      email = address;
      showCodeStep();
      if (outcome === 'code_step') {
        setStatus('We sent a code to ' + email + '.');
        startCountdown(DEFAULT_RESEND);
      } else {
        var wait = resendDelay(res.retryAfter);
        setStatus('We already sent a code to ' + email + '. Check your email, or resend in ' + wait + 's.');
        startCountdown(wait);
      }
    }).then(function () {
      busy = false;
      emailSubmit.disabled = unavailable;
      emailSubmit.textContent = 'Send sign-in code';
    });
  });

  haveCode.addEventListener('click', function () {
    if (busy || unavailable) return;
    setError('');
    var address = readEmail();
    if (!address) return;
    email = address;
    stopCountdown();
    showCodeStep();
    setStatus('Enter the code we sent to ' + email + '.');
  });

  resendBtn.addEventListener('click', function () {
    if (busy || unavailable || countdownTimer !== null) return;
    busy = true;
    setError('');
    resendBtn.disabled = true;
    resendBtn.textContent = 'Sending...';
    requestCode(email).then(function (res) {
      busy = false;
      stopCountdown();
      if (res.turnstile) {
        setError(api.errorCopy('turnstile_failed'));
        return;
      }
      var outcome = startOutcome(res);
      if (outcome === 'code_step') {
        setStatus('We sent a new code to ' + email + '.');
        startCountdown(DEFAULT_RESEND);
      } else if (outcome === 'code_step_throttled') {
        setError(api.errorCopy('rate_limit_email', res.retryAfter));
        startCountdown(resendDelay(res.retryAfter));
      } else {
        setError(api.errorCopy(res.data.error, res.retryAfter));
      }
    });
  });

  goBack.addEventListener('click', function () {
    if (busy) return;
    showEmailStep();
  });

  codeForm.addEventListener('submit', function (event) {
    event.preventDefault();
    if (busy || unavailable) return;
    setError('');
    var code = normalizeCode(codeInput.value);
    if (!code) {
      setError(api.errorCopy('code_required'));
      codeInput.focus();
      return;
    }
    busy = true;
    codeSubmit.disabled = true;
    codeSubmit.textContent = 'Verifying...';
    api.verifyEmail(email, code).then(function (res) {
      if (res.status === 200) {
        api.setHint(true);
        setStatus('Signed in.');
        location.replace(api.afterSignInUrl(next, res.data.new_account));
        return;
      }
      busy = false;
      codeSubmit.disabled = false;
      codeSubmit.textContent = 'Verify code';
      setError(api.errorCopy(res.data.error, res.retryAfter));
      codeInput.focus();
    });
  });

  Promise.all([api.providers(), api.me()]).then(function (results) {
    var providers = results[0];
    var me = results[1];
    if (me.ok) {
      location.replace(next);
      return;
    }
    if (providers.status === 0) {
      setError(api.errorCopy('network'));
    } else {
      var list = providers.ok && Array.isArray(providers.data.providers) ? providers.data.providers : [];
      if (list.indexOf('email') === -1) {
        unavailable = true;
        setDisabled(emailForm, true);
        setDisabled(codeForm, true);
        setError(api.errorCopy('sign_in_unavailable'));
      }
    }
    if (reason === 'session_expired') setStatus(api.errorCopy('session_expired'));
  });
})(globalThis);

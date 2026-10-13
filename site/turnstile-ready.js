// Reserve the callback name before Turnstile's script loads, so the
// async loader has something to invoke. app.js will reassign once it
// initializes its solveTurnstile promise.
window.__turnstileLoaded = false;
window.onTurnstileReady = function () { window.__turnstileLoaded = true; };

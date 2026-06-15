(function () {
  var root = document.documentElement;
  if (!root) return;

  // Keep the rail trace on the same timeline across full-page navigations.
  // Without this, every .html load restarts nav-trace at 0%, which reads as a
  // quick vertical jump in the fixed rail even though the rail box is stable.
  var traceDurationMs = 5400;
  var phaseMs = Date.now() % traceDurationMs;
  root.style.setProperty('--nav-trace-delay', '-' + phaseMs + 'ms');
})();

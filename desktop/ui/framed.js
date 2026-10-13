// Synchronous (classic, NOT a module) <head> script — runs BEFORE the body paints.
// Every content page lives inside the persistent app shell's <iframe> (index.html),
// which owns the ONE navigation rail and the live auto-show. Mark <html> so CSS
// drops the rail gutter. A content page opened on its own (a browser preview or an
// old link) has no navigation, so reopen it inside the shell at the same page.
(function () {
  var framed;
  try { framed = window.self !== window.top; }
  // Cross-origin access to window.top throws → framed by a different origin;
  // treat as framed (defensive; in-app the shell is same-origin so this won't hit).
  catch (_) { framed = true; }
  if (framed) {
    document.documentElement.classList.add('framed');
    return;
  }
  var file = location.pathname.split('/').pop() || 'dashboard.html';
  location.replace('index.html#' + file + location.search + location.hash);
})();

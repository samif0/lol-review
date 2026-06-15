// Synchronous (classic, NOT a module) <head> script — runs BEFORE the body paints.
// When this page is loaded inside the persistent app shell's <iframe> (index.html),
// mark <html> so CSS hides this page's own nav rail and drops the rail gutter: the
// shell owns the ONE persistent rail, and the rail must never live inside a document
// that reloads. Standalone (top-level) load keeps its own static rail as a fallback.
(function () {
  try {
    if (window.self !== window.top) {
      document.documentElement.classList.add('framed');
    }
  } catch (_) {
    // Cross-origin access to window.top throws → we're framed by a different origin;
    // treat as framed (defensive; in-app the shell is same-origin so this won't hit).
    document.documentElement.classList.add('framed');
  }
})();

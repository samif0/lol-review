(function () {
  var root = document.documentElement;
  if (!root) return;

  // Identify the fixed outer shell before its first paint. Other top-level pages
  // remain scrollable; framed.js identifies content documents separately.
  var file = (location.pathname.split('/').pop() || '').toLowerCase();
  if (!file || file === 'index.html') root.classList.add('app-root');
  root.style.colorScheme = 'dark';
})();

// Synchronous classic <head> script, first on every page and before any stylesheet.
// It marks the shell and applies the saved appearance to <html> before the first
// paint: data-theme, data-mode (resolved dark or light), data-mode-pref, data-page,
// data-daypart and the color scheme. ES5 only; it never throws.
// The rules mirror ui/appearance.mjs; tests/appearance.test.mjs keeps them equal.
//
// Sources, strongest first: a choice passed to reapply() or read from a storage event,
// a preview override (?appearance=<theme>-<mode>, never stored), then in a frame the
// shell's own attributes, then the saved revu.appearance entry, then the defaults.
// The shell is the one authority for System mode and the time of day: it listens to
// Windows and the clock and forwards every change to the frame.
(function () {
  var root = document.documentElement;
  if (!root) return;
  var KEY = 'revu.appearance', DEFAULT_THEME = 'warm', DEFAULT_MODE = 'dark';
  var THEMES = { warm: 1, neutral: 1, journal: 1, aura: 1 };
  var MODES = { system: 1, dark: 1, light: 1 };
  var DAYPARTS = { morning: 1, day: 1, evening: 1, night: 1 };
  var SERIF = ['instrument-sans', 'source-serif-4-roman'];
  var FONTS = { warm: SERIF, neutral: ['geist'], journal: SERIF, aura: SERIF };
  // The @font-face family of each preloaded file (appearance.css), for loads after parsing.
  var FAMILY = { 'instrument-sans': 'Instrument Sans', 'source-serif-4-roman': 'Source Serif 4', geist: 'Geist' };
  var preloaded = {}, chosen = null, current = null;
  function has(set, value) { return typeof value === 'string' && set[value] === 1; }

  // data-page is the file name without .html; '/' (the shell under clean URLs) is index.
  var page = String(location.pathname || '').split('/').pop().toLowerCase().replace(/\.html$/, '') || 'index';
  root.setAttribute('data-page', page);
  // Identify the fixed outer shell before its first paint. Other top-level pages
  // remain scrollable; framed.js identifies content documents separately.
  if (page === 'index') root.classList.add('app-root');
  var framed;
  try { framed = window.self !== window.top; } catch (_) { framed = true; }

  function normalize(value) {
    var source = value && typeof value === 'object' ? value : {};
    return { theme: has(THEMES, source.theme) ? source.theme : DEFAULT_THEME,
      mode: has(MODES, source.mode) ? source.mode : DEFAULT_MODE };
  }
  function parse(raw) {
    var value = null;
    try { value = JSON.parse(raw); } catch (_) { value = null; }
    return normalize(value);
  }
  function stored() {
    var raw = null;
    try { raw = window.localStorage.getItem(KEY); } catch (_) { raw = null; }
    return parse(raw);
  }
  function override() {
    var pattern = /[?&]appearance=([a-z]+)-([a-z]+)(?=[&#]|$)/i, match = null;
    try { match = pattern.exec(String(location.search || '')) || pattern.exec(String(location.hash || '')); } catch (_) { match = null; }
    if (!match) return null;
    var theme = match[1].toLowerCase(), mode = match[2].toLowerCase();
    return has(THEMES, theme) && has(MODES, mode) ? { theme: theme, mode: mode } : null;
  }
  function daypartFor(hour) {
    var value = Number(hour);
    if (!isFinite(value)) return 'evening';
    var at = ((Math.floor(value) % 24) + 24) % 24;
    if (at >= 5 && at < 11) return 'morning';
    if (at >= 11 && at < 17) return 'day';
    if (at >= 17 && at < 21) return 'evening';
    return 'night';
  }
  function now() { try { return daypartFor(new Date().getHours()); } catch (_) { return 'evening'; } }
  function topAttribute(name) {
    if (!framed) return null;
    try { return window.top.document.documentElement.getAttribute(name); } catch (_) { return null; }
  }
  function prefersLight() {
    try { return !!window.matchMedia('(prefers-color-scheme: light)').matches; } catch (_) { return false; }
  }
  // A framed page shows exactly what the shell shows once the shell has a theme.
  function mirror() {
    var theme = topAttribute('data-theme');
    if (!has(THEMES, theme)) return null;
    var mode = topAttribute('data-mode') === 'light' ? 'light' : 'dark';
    var pref = topAttribute('data-mode-pref'), daypart = topAttribute('data-daypart');
    return { theme: theme, mode: mode, pref: has(MODES, pref) ? pref : mode,
      daypart: has(DAYPARTS, daypart) ? daypart : now() };
  }
  function resolve(choice) {
    var mode = choice.mode;
    if (mode === 'system') {
      var top = topAttribute('data-mode');
      mode = top === 'dark' || top === 'light' ? top : (prefersLight() ? 'light' : 'dark');
    }
    return { theme: choice.theme, mode: mode, pref: choice.mode, daypart: now() };
  }
  function compute() {
    var choice = chosen || override();
    if (!choice) {
      var shown = mirror();
      if (shown) return shown;
      choice = stored();
    }
    return resolve(choice);
  }
  // While <head> is parsing, a <link rel=preload> fetches the faces before the first paint.
  // A later theme change (reapply, a storage event) asks the font set directly: a preload
  // link added then often goes unused and Chromium warns about it.
  function preload(theme) {
    var list = FONTS[theme] || [];
    var parsed = document.readyState === 'interactive' || document.readyState === 'complete';
    for (var i = 0; i < list.length; i++) {
      if (preloaded[list[i]]) continue;
      preloaded[list[i]] = true;
      try {
        if (parsed) {
          var loading = document.fonts && document.fonts.load ? document.fonts.load('400 1em "' + FAMILY[list[i]] + '"') : null;
          if (loading && loading.then) loading.then(null, function () { /* the stylesheet fetches it when used */ });
          continue;
        }
        var link = document.createElement('link');
        link.setAttribute('rel', 'preload');
        link.setAttribute('as', 'font');
        link.setAttribute('type', 'font/woff2');
        link.setAttribute('crossorigin', '');
        link.setAttribute('href', './fonts/' + list[i] + '.woff2');
        (document.head || root).appendChild(link);
      } catch (_) { /* a missing preload only costs a later font fetch */ }
    }
  }
  function get() {
    var state = current || {};
    return { theme: state.theme, mode: state.mode, pref: state.pref, daypart: state.daypart, page: page };
  }
  function apply(state) {
    var changed = !current || current.theme !== state.theme || current.mode !== state.mode
      || current.pref !== state.pref || current.daypart !== state.daypart;
    root.setAttribute('data-theme', state.theme);
    root.setAttribute('data-mode', state.mode);
    root.setAttribute('data-mode-pref', state.pref);
    root.setAttribute('data-daypart', state.daypart);
    try { root.style.colorScheme = state.mode; } catch (_) { /* attributes still drive the CSS */ }
    preload(state.theme);
    current = state;
    return changed;
  }
  function refresh() {
    var state = compute();
    if (apply(state)) {
      try { window.dispatchEvent(new window.CustomEvent('revu:appearance', { detail: get() })); } catch (_) { /* no listeners */ }
    }
    if (!framed) {
      try {
        var frame = document.getElementById('app-frame');
        var api = frame && frame.contentWindow && frame.contentWindow.__revuAppearance;
        if (api && typeof api.reapply === 'function') api.reapply({ theme: state.theme, mode: state.pref });
      } catch (_) { /* the frame mirrors the shell when its next page loads */ }
    }
    return get();
  }
  // reapply(next): apply {theme, mode} now (Settings, or the shell forwarding);
  // reapply(): drop any held choice and read the sources again.
  function reapply(next) {
    chosen = next && typeof next === 'object' ? normalize(next) : null;
    return refresh();
  }

  try { apply(compute()); } catch (_) { /* the CSS defaults to Warm dark */ }
  window.__revuAppearance = { get: get, reapply: reapply };
  try {
    window.addEventListener('storage', function (event) {
      if (!event || (event.key !== KEY && event.key !== null)) return;
      // The current saved value, not event.newValue: two saves in one task can deliver
      // their events after both writes, and the older event must not win.
      chosen = event.key === KEY && typeof event.newValue === 'string' ? stored() : null;
      refresh();
    });
  } catch (_) { /* storage events are optional */ }
  if (framed) return;
  try {
    var scheme = window.matchMedia('(prefers-color-scheme: light)');
    if (scheme.addEventListener) scheme.addEventListener('change', refresh);
    else if (scheme.addListener) scheme.addListener(refresh);
  } catch (_) { /* no live Windows mode */ }
  try { window.setInterval(refresh, 300000); } catch (_) { /* no daypart refresh */ }
})();

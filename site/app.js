(() => {
  const STORAGE_KEY = 'revu.web.simple.v1';
  const MATCH_CACHE_KEY = 'revu.web.match-cache.v1';
  const RECENT_KEY = 'revu.web.recent.v1';
  const MATCH_CACHE_LIMIT = 200;
  const RECENT_LIMIT = 5;
  const VIDEO_EXTENSIONS = new Set(['mp4', 'webm', 'mkv', 'mov', 'avi']);
  let DDRAGON_VERSION = '16.10.1';
  const IS_LOCAL = ['localhost', '127.0.0.1'].includes(window.location.hostname);
  const PROXY_URL = IS_LOCAL
    ? 'http://localhost:8787'
    : 'https://revu-proxy.lol-review.workers.dev';
  // Local dev uses Cloudflare's always-pass test key so we don't depend on
  // the production site key's hostname allowlist. Production gets the real key.
  const TURNSTILE_SITE_KEY = IS_LOCAL
    ? '1x00000000000000000000AA'
    : '0x4AAAAAADUmEL-Xamn7L2E0';
  const SESSION_KEY = 'revu.web.session.v1';
  const MATCH_COUNT = 20;
  const QUEUE_FILTERS = { all: null, solo: 420, flex: 440 };
  const ROLE_LABEL = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', BOTTOM: 'ADC', UTILITY: 'Support' };
  const QUEUE_LABEL = { 420: 'Ranked Solo/Duo', 440: 'Ranked Flex', 400: 'Normal Draft', 430: 'Normal Blind', 450: 'ARAM' };

  const demoMatches = [
    {
      id: 'NA1_1234567890',
      champion: "Kai'Sa",
      championKey: 'Kaisa',
      role: 'ADC',
      result: 'loss',
      kills: 7,
      deaths: 6,
      assists: 8,
      cs: 214,
      durationSeconds: 1935,
      queue: 'Ranked Solo/Duo',
      queueKey: 'solo',
      playedAt: 'Today',
    },
    {
      id: 'NA1_1234567881',
      champion: 'Ahri',
      championKey: 'Ahri',
      role: 'Mid',
      result: 'win',
      kills: 9,
      deaths: 3,
      assists: 11,
      cs: 232,
      durationSeconds: 1724,
      queue: 'Ranked Solo/Duo',
      queueKey: 'solo',
      playedAt: 'Yesterday',
    },
    {
      id: 'NA1_1234567817',
      champion: 'Jinx',
      championKey: 'Jinx',
      role: 'ADC',
      result: 'loss',
      kills: 5,
      deaths: 8,
      assists: 6,
      cs: 198,
      durationSeconds: 2102,
      queue: 'Ranked Flex',
      queueKey: 'flex',
      playedAt: '2 days ago',
    },
    {
      id: 'NA1_1234567798',
      champion: 'Thresh',
      championKey: 'Thresh',
      role: 'Support',
      result: 'win',
      kills: 2,
      deaths: 4,
      assists: 22,
      cs: 38,
      durationSeconds: 1840,
      queue: 'Ranked Solo/Duo',
      queueKey: 'solo',
      playedAt: '3 days ago',
    },
    {
      id: 'NA1_1234567760',
      champion: 'Lee Sin',
      championKey: 'LeeSin',
      role: 'Jungle',
      result: 'win',
      kills: 11,
      deaths: 5,
      assists: 9,
      cs: 162,
      durationSeconds: 1989,
      queue: 'Ranked Solo/Duo',
      queueKey: 'solo',
      playedAt: '4 days ago',
    },
  ];

  let state = loadState();
  let matchCache = loadMatchCache();
  let loadedMatches = [];
  let loadInflight = false;
  let currentObjectUrl = '';
  let timerHandle = 0;
  let timerStartedAt = 0;
  let timerBaseSeconds = 0;
  let currentFilter = 'all';

  fetch('https://ddragon.leagueoflegends.com/api/versions.json')
    .then((r) => r.json())
    .then((v) => { if (Array.isArray(v) && v.length) DDRAGON_VERSION = v[0]; })
    .catch(() => {});

  const el = {
    searchScreen: document.querySelector('#search-screen'),
    matchScreen: document.querySelector('#match-screen'),
    reviewScreen: document.querySelector('#review-screen'),
    playerForm: document.querySelector('#player-form'),
    riotId: document.querySelector('#riot-id'),
    region: document.querySelector('#region'),
    searchStatus: document.querySelector('#search-status'),
    matchesTitle: document.querySelector('#matches-title'),
    matchList: document.querySelector('#match-list'),
    matchesCount: document.querySelector('#matches-count'),
    matchFilters: document.querySelector('.match-filters'),
    recentChips: document.querySelector('#recent-chips'),
    backToSearch: document.querySelector('#back-to-search'),
    backToMatches: document.querySelector('#back-to-matches'),
    reviewEyebrow: document.querySelector('#review-eyebrow'),
    reviewTitle: document.querySelector('#review-title'),
    reviewMeta: document.querySelector('#review-meta'),
    reviewResult: document.querySelector('#review-result'),
    clearFile: document.querySelector('#clear-file'),
    dropZone: document.querySelector('#drop-zone'),
    fileInput: document.querySelector('#file-input'),
    dropKicker: document.querySelector('#drop-kicker'),
    dropTitle: document.querySelector('#drop-title'),
    dropCopy: document.querySelector('#drop-copy'),
    video: document.querySelector('#video-player'),
    viewerShell: document.querySelector('.viewer-shell'),
    vodControls: document.querySelector('#vod-controls'),
    vodPlay: document.querySelector('#vod-play'),
    vodPlayIcon: document.querySelector('#vod-play-icon'),
    vodBack: document.querySelector('#vod-back'),
    vodFwd: document.querySelector('#vod-fwd'),
    vodCurrent: document.querySelector('#vod-current'),
    vodDuration: document.querySelector('#vod-duration'),
    vodTrack: document.querySelector('#vod-track'),
    vodFill: document.querySelector('#vod-fill'),
    vodThumb: document.querySelector('#vod-thumb'),
    vodSpeed: document.querySelector('#vod-speed'),
    vodSpeedLabel: document.querySelector('#vod-speed-label'),
    vodMute: document.querySelector('#vod-mute'),
    vodMuteIcon: document.querySelector('#vod-mute-icon'),
    vodFs: document.querySelector('#vod-fs'),
    vodHelp: document.querySelector('#vod-help'),
    vodHelpPanel: document.querySelector('#vod-help-panel'),
    roflMode: document.querySelector('#rofl-mode'),
    clock: document.querySelector('#clock'),
    timerToggle: document.querySelector('#timer-toggle'),
    jumpBack: document.querySelector('#jump-back'),
    jumpForward: document.querySelector('#jump-forward'),
    notes: document.querySelector('#review-notes'),
    exportReview: document.querySelector('#export-review'),
  };

  const SPEEDS = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

  hydrate();
  bind();
  renderRecent();
  render();

  function defaultState() {
    return {
      riotId: '',
      region: 'na1',
      stage: 'search',
      puuid: '',
      canonicalRiotId: '',
      selectedMatchId: '',
      media: { kind: 'none', name: '', size: 0 },
      clockSeconds: 0,
      notes: '',
    };
  }

  function loadState() {
    try {
      return { ...defaultState(), ...JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}'), stage: 'search' };
    } catch {
      return defaultState();
    }
  }

  function saveState() {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  }

  function loadMatchCache() {
    try { return JSON.parse(localStorage.getItem(MATCH_CACHE_KEY) || '{}'); }
    catch { return {}; }
  }

  function saveMatchCache() {
    const ids = Object.keys(matchCache);
    if (ids.length > MATCH_CACHE_LIMIT) {
      // Keep most-recent by gameEndTimestamp
      const sorted = ids
        .map((id) => [id, matchCache[id]?.endTs || 0])
        .sort((a, b) => b[1] - a[1])
        .slice(0, MATCH_CACHE_LIMIT);
      const next = {};
      for (const [id] of sorted) next[id] = matchCache[id];
      matchCache = next;
    }
    try { localStorage.setItem(MATCH_CACHE_KEY, JSON.stringify(matchCache)); }
    catch {} // Quota exceeded — silently keep in-memory
  }

  function loadRecent() {
    try {
      const v = JSON.parse(localStorage.getItem(RECENT_KEY) || '[]');
      return Array.isArray(v) ? v : [];
    } catch { return []; }
  }

  function pushRecent(riotId, region) {
    const entries = loadRecent().filter((e) => !(e.riotId === riotId && e.region === region));
    entries.unshift({ riotId, region });
    const trimmed = entries.slice(0, RECENT_LIMIT);
    try { localStorage.setItem(RECENT_KEY, JSON.stringify(trimmed)); } catch {}
  }

  function renderRecent() {
    const entries = loadRecent();
    // Clear existing chips but preserve the label.
    el.recentChips.querySelectorAll('.chip').forEach((c) => c.remove());
    if (entries.length === 0) {
      el.recentChips.hidden = true;
      return;
    }
    el.recentChips.hidden = false;
    for (const entry of entries) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'chip';
      chip.dataset.recent = entry.riotId;
      chip.dataset.region = entry.region;
      chip.textContent = `${entry.riotId} · ${entry.region.toUpperCase()}`;
      el.recentChips.append(chip);
    }
  }

  function hydrate() {
    el.riotId.value = state.riotId;
    el.region.value = state.region;
    el.notes.value = state.notes;
  }

  function bind() {
    el.playerForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      state.riotId = el.riotId.value.trim();
      state.region = el.region.value;
      if (!state.riotId) {
        setSearchStatus('Enter your Riot ID to search.', 'error');
        return;
      }
      if (!/.+#.+/.test(state.riotId)) {
        setSearchStatus('Riot ID must be gameName#tagLine.', 'error');
        return;
      }
      await lookupAccount();
    });

    el.riotId.addEventListener('input', () => {
      state.riotId = el.riotId.value.trim();
      saveState();
    });
    el.region.addEventListener('change', () => {
      state.region = el.region.value;
      saveState();
    });

    el.recentChips.addEventListener('click', (event) => {
      const chip = event.target.closest('[data-recent]');
      if (!chip) return;
      el.riotId.value = chip.dataset.recent;
      state.riotId = chip.dataset.recent;
      if (chip.dataset.region) {
        el.region.value = chip.dataset.region;
        state.region = chip.dataset.region;
      }
      saveState();
      el.playerForm.requestSubmit();
    });

    el.backToSearch.addEventListener('click', () => {
      resetViewer();
      state.stage = 'search';
      saveState();
      render();
    });
    el.backToMatches.addEventListener('click', () => {
      resetViewer();
      state.stage = 'matches';
      saveState();
      render();
    });

    el.matchList.addEventListener('click', (event) => {
      const card = event.target.closest('.match-card');
      if (!card) return;
      if (card.dataset.matchId !== state.selectedMatchId) {
        resetViewer();
      }
      state.selectedMatchId = card.dataset.matchId;
      state.stage = 'review';
      state.clockSeconds = 0;
      saveState();
      render();
    });

    el.matchFilters.addEventListener('click', (event) => {
      const chip = event.target.closest('.filter-chip');
      if (!chip) return;
      currentFilter = chip.dataset.filter;
      el.matchFilters.querySelectorAll('.filter-chip').forEach((c) => {
        c.classList.toggle('is-active', c === chip);
      });
      renderMatches();
    });

    el.dropZone.addEventListener('dragover', (event) => {
      event.preventDefault();
      el.dropZone.classList.add('is-dragover');
    });
    el.dropZone.addEventListener('dragleave', () => {
      el.dropZone.classList.remove('is-dragover');
    });
    el.dropZone.addEventListener('drop', (event) => {
      event.preventDefault();
      el.dropZone.classList.remove('is-dragover');
      const file = event.dataTransfer.files && event.dataTransfer.files[0];
      if (file) handleFile(file);
    });
    el.fileInput.addEventListener('change', () => {
      const file = el.fileInput.files && el.fileInput.files[0];
      if (file) handleFile(file);
      el.fileInput.value = '';
    });
    el.clearFile.addEventListener('click', clearFile);

    el.timerToggle.addEventListener('click', toggleTimer);
    el.jumpBack.addEventListener('click', () => setClock(state.clockSeconds - 3));
    el.jumpForward.addEventListener('click', () => setClock(state.clockSeconds + 3));

    el.vodPlay.addEventListener('click', togglePlay);

    el.video.addEventListener('click', () => {
      if (state.media.kind === 'video') togglePlay();
    });
    el.vodBack.addEventListener('click', () => seekVideo(-3));
    el.vodFwd.addEventListener('click', () => seekVideo(3));
    el.vodSpeed.addEventListener('click', () => cycleSpeed(1));
    el.vodMute.addEventListener('click', toggleMute);
    el.vodFs.addEventListener('click', toggleFullscreen);
    el.vodHelp.addEventListener('click', () => {
      el.vodHelpPanel.hidden = !el.vodHelpPanel.hidden;
    });

    el.vodTrack.addEventListener('click', onScrubClick);
    el.vodTrack.addEventListener('mousedown', onScrubDrag);

    el.video.addEventListener('play', () => {
      el.viewerShell.classList.add('is-playing');
      el.vodPlayIcon.innerHTML = '&#10074;&#10074;';
    });
    el.video.addEventListener('pause', () => {
      el.viewerShell.classList.remove('is-playing');
      el.vodPlayIcon.innerHTML = '&#9654;';
    });
    el.video.addEventListener('volumechange', () => {
      el.vodMuteIcon.innerHTML = el.video.muted || el.video.volume === 0
        ? '&#x1F507;'
        : '&#x1F50A;';
    });
    el.video.addEventListener('ratechange', updateSpeedLabel);
    el.video.addEventListener('loadedmetadata', () => {
      el.vodDuration.textContent = formatTime(el.video.duration);
      updateVodProgress();
    });
    el.video.addEventListener('timeupdate', () => {
      state.clockSeconds = Math.floor(el.video.currentTime || 0);
      updateClock();
      updateVodProgress();
      saveState();
    });
    el.notes.addEventListener('input', () => {
      state.notes = el.notes.value;
      saveState();
    });
    el.exportReview.addEventListener('click', exportMarkdown);

    document.addEventListener('keydown', onKey);
  }

  function onKey(event) {
    if (event.target.matches('input, textarea, select')) return;

    if (state.stage === 'review') {
      const step = event.shiftKey ? 10 : 3;
      if (event.key === 'Escape') {
        event.preventDefault();
        el.backToMatches.click();
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        if (state.media.kind === 'video') seekVideo(-step);
        else setClock(state.clockSeconds - step);
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        if (state.media.kind === 'video') seekVideo(step);
        else setClock(state.clockSeconds + step);
      } else if (event.key === 'ArrowUp') {
        event.preventDefault();
        if (state.media.kind === 'video') cycleSpeed(1);
      } else if (event.key === 'ArrowDown') {
        event.preventDefault();
        if (state.media.kind === 'video') cycleSpeed(-1);
      } else if (event.key === ' ') {
        event.preventDefault();
        if (state.media.kind === 'video') togglePlay();
        else if (state.media.kind === 'rofl') toggleTimer();
      } else if (event.key === '[') {
        event.preventDefault();
        if (state.media.kind === 'video') cycleSpeed(-1);
      } else if (event.key === ']') {
        event.preventDefault();
        if (state.media.kind === 'video') cycleSpeed(1);
      } else if (event.key === 'f' || event.key === 'F') {
        if (state.media.kind === 'video') {
          event.preventDefault();
          toggleFullscreen();
        }
      } else if (event.key === 'm' || event.key === 'M') {
        if (state.media.kind === 'video') {
          event.preventDefault();
          toggleMute();
        }
      } else if (event.key === '?') {
        event.preventDefault();
        el.vodHelpPanel.hidden = !el.vodHelpPanel.hidden;
      } else if (/^[0-9]$/.test(event.key) && state.media.kind === 'video' && el.video.duration) {
        event.preventDefault();
        const pct = Number(event.key) / 10;
        el.video.currentTime = el.video.duration * pct;
      }
    } else if (state.stage === 'matches') {
      if (event.key === 'Escape') {
        event.preventDefault();
        el.backToSearch.click();
      }
    } else if (state.stage === 'search') {
      if (event.key === '/' && document.activeElement !== el.riotId) {
        event.preventDefault();
        el.riotId.focus();
      }
    }
  }

  // ── Turnstile + session JWT ─────────────────────────────────────
  //
  // First Riot ID search triggers Turnstile (invisible 99% of the time);
  // we POST the Turnstile token to /web/session and get back a 30-min JWT.
  // The JWT is cached in sessionStorage and used for all /web/* calls.
  // On 401 (expired), we re-solve once.

  let turnstileReadyPromise = null;
  let turnstileWidgetId = null;

  function turnstileReady() {
    if (turnstileReadyPromise) return turnstileReadyPromise;
    turnstileReadyPromise = new Promise((resolve) => {
      if (window.turnstile) return resolve();
      const start = Date.now();
      const tick = () => {
        if (window.turnstile) return resolve();
        if (Date.now() - start > 10000) return resolve(); // hard fallback
        setTimeout(tick, 100);
      };
      tick();
    });
    return turnstileReadyPromise;
  }

  async function solveTurnstile() {
    await turnstileReady();
    if (!window.turnstile) {
      throw new Error('turnstile_script_unavailable');
    }
    const container = document.querySelector('#turnstile-container');
    return new Promise((resolve, reject) => {
      // Render a fresh widget for each solve so we always get a single-use token.
      if (turnstileWidgetId !== null) {
        try { window.turnstile.remove(turnstileWidgetId); } catch {}
        turnstileWidgetId = null;
      }
      container.classList.remove('is-interactive');
      const widgetId = window.turnstile.render(container, {
        sitekey: TURNSTILE_SITE_KEY,
        size: 'invisible',
        callback: (token) => {
          container.classList.remove('is-interactive');
          resolve(token);
        },
        'error-callback': () => {
          container.classList.remove('is-interactive');
          reject(new Error('turnstile_error'));
        },
        'expired-callback': () => {
          container.classList.remove('is-interactive');
          reject(new Error('turnstile_expired'));
        },
        'before-interactive-callback': () => {
          // Cloudflare escalated to an interactive challenge. Surface the
          // widget so the user can complete it.
          container.classList.add('is-interactive');
        },
      });
      turnstileWidgetId = widgetId;
      try { window.turnstile.execute(widgetId); } catch (err) { reject(err); }
    });
  }

  function loadSession() {
    try {
      const v = JSON.parse(sessionStorage.getItem(SESSION_KEY) || 'null');
      if (!v || typeof v.token !== 'string' || typeof v.expiresAt !== 'number') return null;
      // Refresh slightly before expiry to avoid mid-flight 401s.
      if (v.expiresAt - 60_000 < Date.now()) return null;
      return v;
    } catch { return null; }
  }

  function saveSession(token, expiresInSeconds) {
    const expiresAt = Date.now() + expiresInSeconds * 1000;
    try {
      sessionStorage.setItem(SESSION_KEY, JSON.stringify({ token, expiresAt }));
    } catch {}
  }

  function clearSession() {
    try { sessionStorage.removeItem(SESSION_KEY); } catch {}
  }

  async function ensureSession() {
    const cached = loadSession();
    if (cached) return cached.token;
    const turnstileToken = await solveTurnstile();
    const res = await fetch(new URL('/web/session', PROXY_URL), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ turnstileToken }),
    });
    if (!res.ok) {
      throw new Error('session_mint_failed_' + res.status);
    }
    const data = await res.json();
    if (!data.token || typeof data.expiresIn !== 'number') {
      throw new Error('session_mint_malformed');
    }
    saveSession(data.token, data.expiresIn);
    return data.token;
  }

  async function proxyFetch(path, params) {
    const url = new URL(path, PROXY_URL);
    if (params) {
      for (const [k, v] of Object.entries(params)) {
        if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
      }
    }
    for (let attempt = 0; attempt < 2; attempt++) {
      const token = await ensureSession();
      const res = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (res.status === 401 && attempt === 0) {
        clearSession();
        continue;
      }
      return res;
    }
    throw new Error('proxy_fetch_unreachable');
  }

  async function lookupAccount() {
    setSearchStatus('Looking up Riot ID…', 'inflight');
    const submit = el.playerForm.querySelector('button[type=submit]');
    if (submit) submit.disabled = true;
    try {
      const res = await proxyFetch('/web/account', {
        riotId: state.riotId,
        region: state.region,
      });
      if (!res.ok) {
        let detail = '';
        try { detail = (await res.json()).status?.message || ''; } catch {}
        if (res.status === 404) {
          setSearchStatus(`No account found for ${state.riotId} in ${state.region.toUpperCase()}.`, 'error');
        } else if (res.status === 401 || res.status === 403) {
          setSearchStatus('Could not verify session. Please try again.', 'error');
        } else if (res.status === 429) {
          setSearchStatus('Rate limited. Wait a second and retry.', 'error');
        } else {
          setSearchStatus(`Lookup failed (${res.status})${detail ? ': ' + detail : ''}.`, 'error');
        }
        return;
      }
      const data = await res.json();
      state.puuid = data.puuid;
      state.canonicalRiotId = `${data.gameName}#${data.tagLine}`;
      state.stage = 'matches';
      loadedMatches = [];
      saveState();
      pushRecent(state.canonicalRiotId, state.region);
      renderRecent();
      render();
      loadMatches();
    } catch (err) {
      setSearchStatus(`Could not reach proxy at ${PROXY_URL}. Is wrangler dev running?`, 'error');
    } finally {
      if (submit) submit.disabled = false;
    }
  }

  async function loadMatches() {
    if (loadInflight) return;
    if (!state.puuid) return;
    loadInflight = true;
    setMatchesProgress('Fetching match list…');
    try {
      const queueId = QUEUE_FILTERS[currentFilter];
      const res = await proxyFetch('/web/matches', {
        puuid: state.puuid,
        region: state.region,
        count: MATCH_COUNT,
        queue: queueId || undefined,
      });
      if (!res.ok) {
        setMatchesProgress(`Match list failed (${res.status})`, 'error');
        return;
      }
      const ids = await res.json();
      if (!Array.isArray(ids) || ids.length === 0) {
        setMatchesProgress('No matches found.', 'error');
        return;
      }

      // Seed with cache hits up front so cards appear instantly.
      loadedMatches = ids.map((id) => matchCache[id] || null).filter(Boolean);
      renderMatches();

      let done = loadedMatches.length;
      const total = ids.length;
      setMatchesProgress(`Loading ${done} of ${total}…`);

      for (const id of ids) {
        if (matchCache[id]) continue;
        const mapped = await fetchAndMapMatch(id);
        if (mapped) {
          matchCache[id] = mapped;
          loadedMatches.push(mapped);
          // Keep sorted newest-first
          loadedMatches.sort((a, b) => b.endTs - a.endTs);
          saveMatchCache();
          renderMatches();
        }
        done += 1;
        setMatchesProgress(`Loading ${done} of ${total}…`);
      }

      setMatchesProgress(`${loadedMatches.length} games`);
    } catch (err) {
      setMatchesProgress('Could not reach proxy.', 'error');
    } finally {
      loadInflight = false;
    }
  }

  async function fetchAndMapMatch(matchId) {
    for (let attempt = 0; attempt < 4; attempt++) {
      const res = await proxyFetch('/web/match/' + matchId, { region: state.region });
      if (res.status === 429) {
        const retry = parseInt(res.headers.get('Retry-After') || '1', 10);
        await sleep(Math.max(500, retry * 1000));
        continue;
      }
      if (!res.ok) return null;
      const match = await res.json();
      return mapMatch(match);
    }
    return null;
  }

  function mapMatch(match) {
    const me = (match.info?.participants || []).find((p) => p.puuid === state.puuid);
    if (!me) return null;
    const queueId = match.info.queueId;
    return {
      id: match.metadata.matchId,
      champion: me.championName,
      championKey: me.championName, // Data Dragon uses PascalCase keys
      role: ROLE_LABEL[me.teamPosition] || me.teamPosition || '-',
      result: me.win ? 'win' : 'loss',
      kills: me.kills,
      deaths: me.deaths,
      assists: me.assists,
      cs: (me.totalMinionsKilled || 0) + (me.neutralMinionsKilled || 0),
      durationSeconds: match.info.gameDuration,
      queue: QUEUE_LABEL[queueId] || `Queue ${queueId}`,
      queueKey: queueId === 420 ? 'solo' : queueId === 440 ? 'flex' : 'other',
      playedAt: formatRelative(match.info.gameEndTimestamp),
      endTs: match.info.gameEndTimestamp,
    };
  }

  function formatRelative(ms) {
    const diff = Date.now() - (ms || 0);
    const days = Math.floor(diff / 86400000);
    if (days <= 0) return 'Today';
    if (days === 1) return 'Yesterday';
    if (days < 7) return `${days} days ago`;
    if (days < 30) return `${Math.floor(days / 7)} weeks ago`;
    if (days < 365) return `${Math.floor(days / 30)} months ago`;
    return `${Math.floor(days / 365)} years ago`;
  }

  function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  function setMatchesProgress(message, tone) {
    if (!el.matchesCount) return;
    el.matchesCount.textContent = message;
    el.matchesCount.dataset.tone = tone || 'idle';
  }

  function setSearchStatus(message, tone) {
    el.searchStatus.textContent = message;
    el.searchStatus.dataset.tone = tone || 'idle';
  }

  function render() {
    el.searchScreen.hidden = state.stage !== 'search';
    el.matchScreen.hidden = state.stage !== 'matches';
    el.reviewScreen.hidden = state.stage !== 'review';

    if (state.stage === 'matches') renderMatches();
    if (state.stage === 'review') renderReview();
  }

  function renderMatches() {
    const name = state.canonicalRiotId || state.riotId || 'player';
    el.matchesTitle.textContent = `${name}'s ranked games`;
    el.matchList.innerHTML = '';

    const source = loadedMatches.length > 0 || state.puuid ? loadedMatches : demoMatchesForRegion();
    const filtered = source.filter((m) => {
      if (currentFilter === 'all') return true;
      if (currentFilter === 'win') return m.result === 'win';
      if (currentFilter === 'loss') return m.result === 'loss';
      if (currentFilter === 'solo') return m.queueKey === 'solo';
      if (currentFilter === 'flex') return m.queueKey === 'flex';
      return true;
    });

    // Only overwrite the progress pill when we're not actively loading.
    if (!loadInflight) {
      el.matchesCount.textContent = `${filtered.length} ${filtered.length === 1 ? 'game' : 'games'}`;
      el.matchesCount.dataset.tone = 'idle';
    }

    for (const match of filtered) {
      el.matchList.append(buildMatchCard(match));
    }
  }

  function buildMatchCard(match) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = `match-card ${match.result}`;
    card.dataset.matchId = match.id;

    const square = document.createElement('div');
    square.className = 'champion-square';
    square.style.backgroundImage = `url(https://ddragon.leagueoflegends.com/cdn/${DDRAGON_VERSION}/img/champion/${match.championKey}.png)`;
    card.append(square);

    const identity = document.createElement('div');
    identity.className = 'match-identity';
    const champ = document.createElement('p');
    champ.className = 'champion';
    champ.textContent = match.champion;
    const role = document.createElement('span');
    role.className = 'match-role';
    role.textContent = `${match.role} // ${match.queue}`;
    identity.append(champ, role);
    card.append(identity);

    const stats = document.createElement('div');
    stats.className = 'match-stats';
    const kda = document.createElement('span');
    kda.className = 'kda';
    kda.textContent = `${match.kills} / ${match.deaths} / ${match.assists}`;
    const kdaDetail = document.createElement('span');
    kdaDetail.className = 'kda-detail';
    const kdaRatio = ((match.kills + match.assists) / Math.max(1, match.deaths)).toFixed(2);
    kdaDetail.textContent = `${kdaRatio} KDA // ${match.cs} CS`;
    stats.append(kda, kdaDetail);
    card.append(stats);

    const metaCol = document.createElement('div');
    metaCol.className = 'match-meta-col';
    const dur = document.createElement('span');
    dur.className = 'queue';
    dur.textContent = formatTime(match.durationSeconds);
    const played = document.createElement('span');
    played.textContent = match.playedAt;
    metaCol.append(dur, played);
    card.append(metaCol);

    const tag = document.createElement('span');
    tag.className = `result-tag ${match.result}`;
    tag.textContent = match.result === 'win' ? 'WIN' : 'LOSS';
    card.append(tag);

    const chev = document.createElement('span');
    chev.className = 'match-chevron';
    chev.textContent = '>';
    card.append(chev);

    const brackets = ['tl', 'tr', 'bl', 'br'];
    for (const corner of brackets) {
      const b = document.createElement('span');
      b.className = `bracket bracket-${corner}`;
      card.append(b);
    }

    return card;
  }

  function renderReview() {
    const match = selectedMatch();
    if (!match) {
      state.stage = 'matches';
      render();
      return;
    }

    el.reviewEyebrow.textContent = `${match.queue} // ${match.playedAt}`;
    el.reviewTitle.textContent = match.champion;
    el.reviewMeta.textContent = `${match.role} // ${match.kills}/${match.deaths}/${match.assists} // ${formatTime(match.durationSeconds)} // ${match.id}`;
    el.reviewResult.textContent = match.result === 'win' ? 'WIN' : 'LOSS';
    el.reviewResult.className = `result-tag ${match.result}`;
    el.notes.value = state.notes;
    renderMedia();
    updateClock();
  }

  function renderMedia() {
    const kind = state.media.kind;
    const hasVideo = kind === 'video' && currentObjectUrl;
    const hasRofl = kind === 'rofl';

    el.video.hidden = !hasVideo;
    el.vodControls.hidden = !hasVideo;
    el.vodHelp.hidden = !hasVideo;
    if (!hasVideo) el.vodHelpPanel.hidden = true;
    el.dropZone.hidden = hasVideo || hasRofl;
    el.roflMode.hidden = !hasRofl;
    el.clearFile.hidden = kind === 'none';

    if (kind === 'none') {
      el.dropKicker.textContent = 'Attach POV';
      el.dropTitle.textContent = 'Drop ROFL or video';
      el.dropCopy.textContent = 'ROFL opens in League; videos play here. Files are local only and are not uploaded.';
      return;
    }

    if (hasRofl) {
      el.dropTitle.textContent = state.media.name;
    }
  }

  function handleFile(file) {
    revokeObjectUrl();
    stopTimer();
    const extension = getExtension(file.name);

    if (extension === 'rofl') {
      state.media = { kind: 'rofl', name: file.name, size: file.size };
      state.clockSeconds = 0;
    } else if (file.type.startsWith('video/') || VIDEO_EXTENSIONS.has(extension)) {
      currentObjectUrl = URL.createObjectURL(file);
      el.video.src = currentObjectUrl;
      state.media = { kind: 'video', name: file.name, size: file.size };
      state.clockSeconds = 0;
    } else {
      state.media = { kind: 'none', name: '', size: 0 };
    }

    saveState();
    renderMedia();
  }

  function clearFile() {
    resetViewer();
    saveState();
    renderMedia();
  }

  function resetViewer() {
    revokeObjectUrl();
    stopTimer();
    state.media = defaultState().media;
    state.clockSeconds = 0;
  }

  function revokeObjectUrl() {
    if (currentObjectUrl) {
      URL.revokeObjectURL(currentObjectUrl);
      currentObjectUrl = '';
    }
    el.video.removeAttribute('src');
    el.video.load();
  }

  function demoMatchesForRegion() {
    const prefix = state.region.toUpperCase();
    return demoMatches.map((match, index) => ({
      ...match,
      id: `${prefix}_${1234567890 - index * 19}`,
    }));
  }

  function selectedMatch() {
    const source = loadedMatches.length > 0 ? loadedMatches : demoMatchesForRegion();
    return source.find((match) => match.id === state.selectedMatchId) || source[0];
  }

  function toggleTimer() {
    if (timerHandle) {
      stopTimer();
      return;
    }
    timerBaseSeconds = state.clockSeconds;
    timerStartedAt = performance.now();
    el.timerToggle.textContent = 'Pause';
    timerHandle = window.setInterval(() => {
      const elapsed = Math.floor((performance.now() - timerStartedAt) / 1000);
      setClock(timerBaseSeconds + elapsed);
    }, 250);
  }

  function stopTimer() {
    if (timerHandle) {
      window.clearInterval(timerHandle);
      timerHandle = 0;
    }
    el.timerToggle.textContent = 'Start';
  }

  function setClock(seconds) {
    state.clockSeconds = Math.max(0, Math.floor(seconds));
    saveState();
    updateClock();
  }

  function updateClock() {
    el.clock.textContent = formatTime(state.clockSeconds);
  }

  function togglePlay() {
    if (el.video.paused) el.video.play();
    else el.video.pause();
  }

  function seekVideo(deltaSeconds) {
    if (!el.video.duration) return;
    const next = Math.max(0, Math.min(el.video.duration, (el.video.currentTime || 0) + deltaSeconds));
    el.video.currentTime = next;
  }

  function cycleSpeed(direction) {
    const current = el.video.playbackRate;
    let idx = SPEEDS.indexOf(current);
    if (idx === -1) idx = SPEEDS.indexOf(1);
    const next = Math.max(0, Math.min(SPEEDS.length - 1, idx + direction));
    el.video.playbackRate = SPEEDS[next];
  }

  function updateSpeedLabel() {
    const rate = el.video.playbackRate;
    el.vodSpeedLabel.innerHTML = `${rate.toFixed(rate % 1 === 0 ? 1 : 2)}×`;
  }

  function toggleMute() {
    el.video.muted = !el.video.muted;
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else {
      el.viewerShell.requestFullscreen().catch(() => {});
    }
  }

  function updateVodProgress() {
    if (!el.video.duration) return;
    const pct = (el.video.currentTime / el.video.duration) * 100;
    el.vodFill.style.width = `${pct}%`;
    el.vodThumb.style.left = `${pct}%`;
    el.vodCurrent.textContent = formatTime(el.video.currentTime);
  }

  function onScrubClick(event) {
    if (!el.video.duration) return;
    const rect = el.vodTrack.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
    el.video.currentTime = el.video.duration * pct;
  }

  function onScrubDrag(event) {
    event.preventDefault();
    const move = (e) => onScrubClick(e);
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }

  function exportMarkdown() {
    const match = selectedMatch();
    const lines = [
      '# Revu Web Review',
      '',
      `- Player: ${state.riotId || 'Unknown'}`,
      `- Region: ${state.region.toUpperCase()}`,
      `- Match: ${match.id}`,
      `- Champion: ${match.champion} (${match.role})`,
      `- Result: ${match.result}`,
      `- KDA: ${match.kills}/${match.deaths}/${match.assists}`,
      `- Duration: ${formatTime(match.durationSeconds)}`,
      `- Media: ${state.media.kind}${state.media.name ? ` (${state.media.name})` : ''}`,
      '',
      '## Notes',
      state.notes || '',
      '',
    ];

    const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `revu-review-${match.id}.md`;
    document.body.append(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  function getExtension(name) {
    const match = /\.([^.]+)$/.exec(name || '');
    return match ? match[1].toLowerCase() : '';
  }

  function formatTime(seconds) {
    const safe = Math.max(0, Math.floor(Number(seconds) || 0));
    const minutes = Math.floor(safe / 60);
    const secs = safe % 60;
    return `${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  }
})();

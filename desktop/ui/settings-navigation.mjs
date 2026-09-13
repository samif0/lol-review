const normalize = value => String(value || '').normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase()
  .replace(/[^\p{L}\p{N}]+/gu, ' ').trim();

export function findSettings(categories, query) {
  const terms = normalize(query).split(/\s+/).filter(Boolean);
  return categories.map(category => ({
    id: category.id,
    cards: category.cards.filter(card => {
      const searchable = normalize(`${category.title} ${card.text}`);
      return terms.every(term => searchable.includes(term));
    }).map(card => card.id),
  }));
}

// Refreshing a saved card must not discard an edit in another, hidden category.
// Values are the exact displayed strings/booleans, including incomplete input.
export function preserveSettingsDraft(current, baseline, savedFields = []) {
  const saved = new Set(savedFields);
  return Object.fromEntries(Object.entries(current).filter(([field, value]) => !saved.has(field)
    && Object.hasOwn(baseline, field) && !Object.is(value, baseline[field])));
}

export function initializeSettingsNavigation({ document = globalThis.document, scope = globalThis.window } = {}) {
  const search = document.getElementById('settings-search');
  if (!search) return;
  const sections = [...document.querySelectorAll('[data-settings-section]')];
  const buttons = [...document.querySelectorAll('[data-settings-category]')];
  const categories = sections.map(section => ({ id: section.dataset.settingsSection,
    title: section.querySelector('h2')?.textContent || '',
    cards: [...section.querySelectorAll('[data-settings-card]')].map(card => ({ id: card.id,
      text: `${card.dataset.search || ''} ${[...card.querySelectorAll('h3,summary,label,.set-row-h,.set-row-s,.settings-card-description')]
        .map(element => element.textContent).join(' ')}` })),
  }));
  const status = document.getElementById('settings-search-status');
  const empty = document.getElementById('settings-search-empty');
  const clear = document.getElementById('settings-search-clear');
  const openedBySearch = new Set();
  const hash = scope.location.hash.replace(/^#settings-/, '');
  let selected = categories.some(category => category.id === hash) ? hash : 'recording';
  const apply = () => {
    const query = search.value.trim();
    const searching = normalize(query).length > 0;
    const matches = findSettings(categories, query);
    for (const detail of openedBySearch) detail.open = false;
    openedBySearch.clear();
    let resultCount = 0;
    for (const section of sections) {
      const category = section.dataset.settingsSection;
      const ids = new Set(matches.find(match => match.id === category)?.cards || []);
      section.hidden = searching ? ids.size === 0 : category !== selected;
      for (const card of section.querySelectorAll('[data-settings-card]')) {
        card.hidden = searching && !ids.has(card.id);
        if (!card.hidden && searching) {
          resultCount++;
          const details = card.matches('details') ? [card] : [...card.querySelectorAll('details')];
          for (const detail of details) if (!detail.open) { detail.open = true; openedBySearch.add(detail); }
        }
      }
    }
    for (const button of buttons) {
      const id = button.dataset.settingsCategory;
      const count = matches.find(match => match.id === id)?.cards.length || 0;
      button.hidden = searching && count === 0;
      button.classList.toggle('is-current', !searching && id === selected);
      if (!searching && id === selected) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
      const badge = button.querySelector('[data-search-count]');
      if (badge) { badge.hidden = !searching; badge.textContent = String(count); }
    }
    clear.hidden = !search.value;
    status.hidden = !searching;
    status.textContent = searching ? `${resultCount} ${resultCount === 1 ? 'setting group' : 'setting groups'} found` : '';
    empty.hidden = !searching || resultCount > 0;
  };
  search.addEventListener('input', apply);
  search.addEventListener('keydown', event => {
    if (event.key === 'Escape') { search.value = ''; apply(); }
  });
  clear.addEventListener('click', () => { search.value = ''; apply(); search.focus(); });
  document.getElementById('settings-search-reset')?.addEventListener('click', () => { search.value = ''; apply(); search.focus(); });
  const select = id => {
    if (!categories.some(category => category.id === id)) return;
    selected = id; search.value = '';
    scope.history.replaceState(null, '', `#settings-${selected}`);
    apply();
    sections.find(section => section.dataset.settingsSection === selected)?.querySelector('h2')?.focus({ preventScroll: true });
  };
  for (const button of buttons) button.addEventListener('click', () => select(button.dataset.settingsCategory));
  for (const link of document.querySelectorAll('[data-category-link]')) link.addEventListener('click', event => {
    event.preventDefault(); select(link.dataset.categoryLink);
  });
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault(); search.focus(); search.select();
    }
  });
  apply();
  return { refresh: apply };
}

import assert from 'node:assert/strict';
import test from 'node:test';
import { findSettings, preserveSettingsDraft, initializeSettingsNavigation } from '../ui/settings-navigation.mjs';

const categories = [
  { id: 'recording', title: 'Recording', cards: [{ id: 'capture', text: 'Automatic full match capture quality resolution FPS' }] },
  { id: 'appearance', title: 'Appearance & startup', cards: [{ id: 'window', text: 'Window size minimize animation' }, { id: 'startup', text: 'Start with Windows system tray' }] },
  { id: 'data', title: 'Data & storage', cards: [{ id: 'backup', text: 'Automatic database backups' }, { id: 'restore', text: 'Restore a backup from café folder' }] },
];

test('settings search matches all words across category, label and help text without case or accent sensitivity', () => {
  assert.deepEqual(findSettings(categories, 'QUALITY recording')[0].cards, ['capture']);
  assert.deepEqual(findSettings(categories, 'startup tray')[1].cards, ['startup']);
  assert.deepEqual(findSettings(categories, 'restore CAFE')[2].cards, ['restore']);
  assert.deepEqual(findSettings(categories, 'backup resolution').flatMap(category => category.cards), []);
  assert.deepEqual(findSettings(categories, '').flatMap(category => category.cards), ['capture', 'window', 'startup', 'backup', 'restore']);
});

test('saving one card preserves unsaved values in hidden cards, including incomplete input', () => {
  const baseline = { clipsFolder: 'D:\\Clips', clipsMaxSizeMb: '2048', region: 'na1', backupEnabled: true, backupFolder: '' };
  const current = { ...baseline, clipsFolder: 'E:\\Clips', clipsMaxSizeMb: '', region: 'euw1', backupEnabled: false };
  assert.deepEqual(preserveSettingsDraft(current, baseline, ['region']),
    { clipsFolder: 'E:\\Clips', clipsMaxSizeMb: '', backupEnabled: false });
  assert.deepEqual(preserveSettingsDraft(current, baseline, ['clipsFolder', 'clipsMaxSizeMb']),
    { region: 'euw1', backupEnabled: false });
  assert.deepEqual(preserveSettingsDraft(baseline, baseline), {});
});

class Element extends EventTarget {
  constructor(properties = {}) {
    super(); Object.assign(this, { hidden: false, dataset: {}, textContent: '', value: '', attributes: {}, ...properties });
    const classes = new Set();
    this.classList = { toggle: (name, on) => on ? classes.add(name) : classes.delete(name), contains: name => classes.has(name) };
  }
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
  focus() { this.focused = true; }
  select() { this.selected = true; }
  click() { this.dispatchEvent(new Event('click', { cancelable: true })); }
}
function fixture() {
  const ids = new Map();
  for (const id of ['settings-search', 'settings-search-status', 'settings-search-empty', 'settings-search-clear', 'settings-search-reset']) ids.set(id, new Element());
  const cards = new Map();
  const sections = categories.map(category => {
    const heading = new Element({ textContent: category.title });
    const items = category.cards.map(card => {
      const element = new Element({ id: card.id, dataset: { search: card.text }, open: false });
      element.matches = selector => selector === 'details' && card.id === 'restore';
      element.querySelectorAll = () => [];
      cards.set(card.id, element); return element;
    });
    const section = new Element({ dataset: { settingsSection: category.id } });
    section.querySelector = () => heading;
    section.querySelectorAll = () => items;
    return section;
  });
  const buttons = categories.map(category => {
    const button = new Element({ dataset: { settingsCategory: category.id } });
    const badge = new Element(); button.querySelector = () => badge; return button;
  });
  const link = new Element({ dataset: { categoryLink: 'appearance' } });
  const document = new Element();
  document.getElementById = id => ids.get(id);
  document.querySelectorAll = selector => selector === '[data-settings-section]' ? sections
    : selector === '[data-settings-category]' ? buttons : selector === '[data-category-link]' ? [link] : [];
  const scope = { location: { hash: '' }, history: { replaceState: (_state, _unused, hash) => { scope.location.hash = hash; } } };
  initializeSettingsNavigation({ document, scope });
  const search = query => { ids.get('settings-search').value = query; ids.get('settings-search').dispatchEvent(new Event('input')); };
  return { ids, sections, cards, buttons, link, search, scope };
}

test('typing a search reveals matching sections and opens matching disclosure; clearing restores the selected category', () => {
  const f = fixture();
  assert.deepEqual(f.sections.map(section => section.hidden), [false, true, true]);
  f.search('restore backup');
  assert.deepEqual(f.sections.map(section => section.hidden), [true, true, false]);
  assert.equal(f.cards.get('restore').hidden, false); assert.equal(f.cards.get('restore').open, true);
  assert.equal(f.cards.get('backup').hidden, true);
  assert.equal(f.ids.get('settings-search-status').textContent, '1 setting group found');
  f.ids.get('settings-search-clear').click();
  assert.deepEqual(f.sections.map(section => section.hidden), [false, true, true]);
  assert.equal(f.cards.get('restore').open, false);
});

test('category navigation exits search and the recording startup link selects its actual section', () => {
  const f = fixture(); f.search('tray'); f.buttons[1].click();
  assert.equal(f.ids.get('settings-search').value, '');
  assert.deepEqual(f.sections.map(section => section.hidden), [true, false, true]);
  assert.equal(f.scope.location.hash, '#settings-appearance');
  f.buttons[0].click(); f.link.click();
  assert.equal(f.buttons[1].attributes['aria-current'], 'page');
  assert.equal(f.sections[1].hidden, false);
});

test('empty search results are actionable and user-opened disclosures remain open after clearing search', () => {
  const f = fixture(); f.cards.get('restore').open = true;
  f.search('nothing matches this phrase');
  assert.equal(f.ids.get('settings-search-empty').hidden, false);
  assert.ok(f.sections.every(section => section.hidden));
  f.search('restore'); f.ids.get('settings-search-reset').click();
  assert.equal(f.ids.get('settings-search-empty').hidden, true);
  assert.equal(f.cards.get('restore').open, true);
});

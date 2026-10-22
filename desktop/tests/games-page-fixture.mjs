// The Match library's real renderer and action handler (games.js) in a VM with a synthetic
// DOM, snapshot reader and invoke bridge. Shared by the row, library and skip tests.
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
export const source = (await read('games.js')).replace(/^import .*?;\r?\n/gm, '');
export const html = await read('games.html');
const skipHelpers = await import('../ui/games-skip.mjs');
export const plain = value => JSON.parse(JSON.stringify(value));
export const tick = () => new Promise(resolve => setImmediate(resolve));
export const NOW = () => new Date(2026, 9, 12, 15, 0);
export function fixture(reply = async () => ({ items: [] }), { invoke } = {}) {
  const ids = new Map(), listeners = new Map(), windowListeners = new Map(), requests = [], writes = [], announced = [];
  let focused = null;
  const scope = { location: { href: 'games.html', search: '' }, scrollX: 0, scrollY: 310,
    top: { dispatchEvent(event) { announced.push(event.type); return true; } },
    addEventListener(type, callback) { windowListeners.set(type, callback); },
    scrollTo({ left, top }) { this.scrollX = left; this.scrollY = top; } };
  function element(classes = []) {
    const names = new Set(classes), attributes = new Map();
    const node = { children: [], dataset: {}, style: {}, textContent: '', hidden: false, disabled: false, selectors: new Map(),
      get firstChild() { return this.children[0] || null; },
      set innerHTML(_) { throw new Error('Game data must never be interpreted as HTML'); },
      classList: {
        contains: name => names.has(name), add: (...values) => values.forEach(name => names.add(name)),
        remove: (...values) => values.forEach(name => names.delete(name)),
        toggle(name, force) { const active = force ?? !names.has(name); if (active) names.add(name); else names.delete(name); return active; },
      },
      appendChild(child) { this.children.push(child); child.parent = this; },
      querySelector(selector) { return this.selectors.get(selector)?.[0] || this.children.flatMap(child => child.querySelectorAll(selector))[0] || null; },
      querySelectorAll(selector) { return this.selectors.get(selector) || this.children.flatMap(child => child.querySelectorAll(selector)); },
      setAttribute(name, value) { attributes.set(name, String(value)); }, getAttribute: name => attributes.get(name) ?? null,
      closest(selector) {
        const needsRole = selector.includes('[role="button"]');
        if (selector.startsWith('[data-action]') && this.dataset.action && (!needsRole || this.getAttribute('role') === 'button')) return this;
        return this.parent?.closest(selector) || null;
      },
      click() { return emit('click', this); },
      focus() { focused = node; },
    };
    return node;
  }
  const $ = id => { if (!ids.has(id)) { const node = element(); node.id = id; ids.set(id, node); } return ids.get(id); };
  const document = { readyState: 'loading',
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
  };
  async function emit(type, target, extra = {}) {
    const event = { target, preventDefault() { this.defaultPrevented = true; }, ...extra };
    for (const handler of listeners.get(type) || []) await handler(event);
    return event;
  }
  const segments = ['history', 'queue', 'vod', 'removed', 'today'].map(view => {
    const button = element(); button.dataset = { action: 'view', view }; return button;
  });
  $('seg').selectors.set('button', segments);
  class CustomEvent { constructor(type) { this.type = type; } }
  const context = vm.createContext({ ...skipHelpers, document, window: scope, $, console: { error() {}, info() {} }, CustomEvent,
    show: (node, on) => { if (node) node.hidden = !on; }, clear: node => { node.children = []; },
    tpl(name) {
      const node = element();
      node.template = name;
      if (name === 'tpl-gamerow') {
        node.dataset.action = 'open_review'; node.setAttribute('role', 'button');
        for (const selector of ['.grow-wl', '.lib-champ', '.lib-name', '.lib-meta', '.lib-kda-n', '.lib-kda-r', '.lib-st', '.lib-go', '.lib-take']) {
          const child = element(); node.selectors.set(selector, [child]); node.appendChild(child);
        }
        const remove = element(['ui-iconbtn']); remove.tagName = 'BUTTON'; remove.dataset.action = 'hide_game';
        node.selectors.set('.lib-remove', [remove]); node.appendChild(remove);
        for (const [selector, action] of [['.lib-pick', 'pick_game'], ['.lib-skip', 'skip_game']]) {
          const button = element(); button.tagName = 'BUTTON'; button.dataset.action = action;
          node.selectors.set(selector, [button]); node.appendChild(button);
        }
      } else if (name === 'tpl-stat') {
        for (const selector of ['.v', '.k']) node.selectors.set(selector, [element()]);
      }
      return node;
    },
    readSnapshot: async (command, sample, args) => { requests.push({ command, sample, args: plain(args) }); return reply(args); },
    getInvoke: async () => invoke ? async (command, args) => { writes.push({ command, args: plain(args) }); return invoke(command, args); } : null,
    setTimeout: () => 0, clearTimeout() {},
  });
  vm.runInContext(`${source}\n globalThis.hooks = { buildRow, loadView, loadMore, dayGroups, viewFromSearch, gameStatuses,
    searchTerms, matchesSearch, get view() { return _view; }, get rows() { return _rows; }, set now(fn) { _now = fn; } };`, context);
  context.hooks.now = NOW;
  const list = () => $('games-list').children;
  return { $, hooks: context.hooks, scope, requests, writes, announced, segments, emit, get focused() { return focused; },
    rows: () => list().filter(el => 'gameId' in el.dataset),
    headings: () => list().filter(el => el.template === 'tpl-day').map(el => el.textContent),
    summary: () => $('strip').children.map(cell => [cell.querySelector('.v').textContent, cell.querySelector('.k').textContent]),
    status: row => row.querySelector('.lib-st').children,
    linked: gameId => windowListeners.get('revu:vod-linked')({ detail: { gameId } }),
    fire: (type, detail) => windowListeners.get(type)?.({ detail }) };
}

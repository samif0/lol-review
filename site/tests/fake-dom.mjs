// Minimal DOM, timer and browser stubs for running the account page scripts
// in node:vm. Not a test suite itself (tests/index.js only loads *.test.mjs).
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

export const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');

export const flush = async (rounds = 8) => {
  for (let i = 0; i < rounds; i += 1) await new Promise(resolve => setImmediate(resolve));
};

function parseAttributes(source) {
  const attrs = {};
  for (const match of source.matchAll(/([\w:-]+)(?:="([^"]*)")?/g)) attrs[match[1]] = match[2] ?? '';
  return attrs;
}

export function createDocument() {
  const document = {
    activeElement: null,
    visibilityState: 'visible',
    listeners: {},
    byId: new Map(),
    getElementById(id) { return this.byId.get(id) || null; },
    createElement(tag) { return makeElement(document, tag, {}); },
    querySelectorAll(selector) {
      const name = selector.match(/^\[([\w-]+)\]$/)?.[1];
      return name ? [...this.byId.values()].filter(el => el.attributes.has(name)) : [];
    },
    addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); },
    dispatch(type) { (this.listeners[type] || []).forEach(fn => fn({ type })); },
  };
  return document;
}

export function makeElement(document, tag, attrs) {
  const classes = new Set(String(attrs.class || '').split(/\s+/).filter(Boolean));
  const el = {
    tagName: tag.toUpperCase(),
    id: attrs.id || '',
    hidden: 'hidden' in attrs,
    disabled: 'disabled' in attrs,
    textContent: '',
    value: '',
    type: attrs.type || '',
    href: attrs.href || '',
    readOnly: false,
    spellcheck: true,
    children: [],
    parentNode: null,
    listeners: {},
    descendants: [],
    attributes: new Map(Object.entries(attrs)),
    get className() { return [...classes].join(' '); },
    set className(value) {
      classes.clear();
      String(value).split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
    },
    classList: {
      add: (...values) => values.forEach(v => classes.add(v)),
      remove: (...values) => values.forEach(v => classes.delete(v)),
      contains: value => classes.has(value),
    },
    get firstChild() { return this.children[0] || null; },
    appendChild(child) { child.parentNode = this; this.children.push(child); return child; },
    removeChild(child) {
      this.children = this.children.filter(c => c !== child);
      child.parentNode = null;
      return child;
    },
    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
    setAttribute(key, value) { this.attributes.set(key, String(value)); if (key === 'href') this.href = String(value); },
    getAttribute(key) {
      if (key === 'href') return this.href;
      return this.attributes.has(key) ? this.attributes.get(key) : null;
    },
    removeAttribute(key) { this.attributes.delete(key); },
    addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); },
    dispatch(type) {
      const event = { type, target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
      (this.listeners[type] || []).forEach(fn => fn(event));
      return event;
    },
    click() { return this.dispatch('click'); },
    focus() { document.activeElement = this; },
    select() { this.selected = true; },
    querySelectorAll() { return this.descendants; },
  };
  return el;
}

// Build stubs for every element with an id in the page's HTML. Forms learn
// the inputs and buttons between their open and close tags.
export function documentFromHtml(html) {
  const document = createDocument();
  const tags = [...html.matchAll(/<([a-z][\w-]*)\b([^>]*)>/g)];
  const placed = [];
  for (const match of tags) {
    const attrs = parseAttributes(match[2]);
    if (!('id' in attrs) && !('data-header-account' in attrs)) continue;
    const el = makeElement(document, match[1], attrs);
    const key = attrs.id || `header-account-${placed.length}`;
    document.byId.set(key, el);
    placed.push({ el, index: match.index });
  }
  for (const { el, index } of placed) {
    if (el.tagName !== 'FORM') continue;
    const end = html.indexOf('</form>', index);
    el.descendants = placed
      .filter(p => p.index > index && p.index < end && ['INPUT', 'BUTTON'].includes(p.el.tagName))
      .map(p => p.el);
  }
  return document;
}

export function memoryStorage(initial = {}) {
  const map = new Map(Object.entries(initial));
  const writes = [];
  return {
    map,
    writes,
    getItem: key => (map.has(key) ? map.get(key) : null),
    setItem: (key, value) => { writes.push([key, String(value)]); map.set(key, String(value)); },
    removeItem: key => { map.delete(key); },
  };
}

export function response(status, body, headers = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: name => headers[name] ?? null },
    json: async () => body,
  };
}

// Runs account-api.js and then `script` against a page's HTML. `routes`
// maps "METHOD /path" to a response or a function returning one.
export function runPage({ html, script, search = '', pathname = '/', hostname = 'revu.lol', routes = {}, storage = memoryStorage(), extra = {} }) {
  const document = documentFromHtml(html);
  const requests = [];
  const navigations = [];
  const replaced = [];
  const timers = [];
  const location = {
    hostname,
    pathname,
    search,
    replace: url => navigations.push(['replace', url]),
    assign: url => navigations.push(['assign', url]),
  };
  const context = {
    document,
    location,
    localStorage: storage,
    URLSearchParams,
    history: { replaceState: (state, title, url) => replaced.push(url) },
    setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
    clearTimeout: () => {},
    fetch: async (url, init) => {
      const path = url.replace(/^https?:\/\/[^/]+/, '');
      const body = init.body === undefined ? undefined : JSON.parse(init.body);
      requests.push({ method: init.method, path, body, init });
      const route = routes[`${init.method} ${path}`];
      if (route instanceof Error) throw route;
      const res = typeof route === 'function' ? route(body) : route;
      return res || response(404, { error: 'not_found' });
    },
    ...extra,
  };
  vm.runInNewContext(read('account-api.js'), context);
  vm.runInNewContext(script, context);
  return { document, context, requests, navigations, replaced, timers, storage, $: id => document.getElementById(id) };
}

// Depth-first list of an element and everything appended under it.
export function walk(el) {
  return [el, ...el.children.flatMap(walk)];
}

// Visible text of an element: hidden subtrees are skipped.
export function textOf(el) {
  const visible = node => (node.hidden ? '' : [node.textContent, ...node.children.map(visible)].join(' '));
  return visible(el).replace(/\s+/g, ' ').trim();
}

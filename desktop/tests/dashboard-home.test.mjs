import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Home's real renderer and action handler: the coaching stint exit and the
// load error path.
const source = (await readFile(new URL('../ui/app.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');
const html = await readFile(new URL('../ui/dashboard.html', import.meta.url), 'utf8');

function fixture(reply, { confirm = () => true } = {}) {
  const ids = new Map(), listeners = new Map(), requests = [], writes = [], questions = [];
  function element(tagName = 'DIV') {
    const names = new Set(), attributes = new Map();
    return {
      tagName, children: [], selectors: new Map(), dataset: {}, textContent: '', hidden: false,
      get lastChild() { return this.children.at(-1) || null; },
      set innerHTML(_) { throw new Error('Dashboard data must remain plain text'); },
      classList: {
        contains: name => names.has(name), add: (...values) => values.forEach(value => names.add(value)),
        toggle(name, force) { const active = force ?? !names.has(name); if (active) names.add(name); else names.delete(name); return active; },
      },
      appendChild(child) { this.children.push(child); child.parent = this; },
      removeChild(child) { this.children.splice(this.children.indexOf(child), 1); },
      querySelector(selector) { return this.selectors.get(selector) || null; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      closest(selector) {
        if (selector === 'button' && this.tagName === 'BUTTON') return this;
        if (selector === '[data-action]' && this.dataset.action) return this;
        return this.parent?.closest(selector) || null;
      },
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  $('statusline').appendChild($('status-text'));
  const end = element('BUTTON'); end.dataset.action = 'end_stint'; ids.set('nextstep-stint-end', end);
  $('nextstep-stint').appendChild($('nextstep-stint-text')); $('nextstep-stint').appendChild(end);
  const document = {
    readyState: 'loading',
    querySelector: selector => selector === '#statusline b' ? $('status-text') : null,
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
  };
  const scope = { location: { href: 'dashboard.html', search: '' }, addEventListener() {},
    confirm: message => { questions.push(message); return confirm(); } };
  const context = vm.createContext({ document, window: scope, $, URLSearchParams, encodeURIComponent,
    console: { error() {}, info() {} },
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    tpl() { const node = element(); for (const s of ['.k', '.v', '.s']) node.selectors.set(s, element()); return node; },
    readSnapshot: async () => { requests.push(1); return reply(); },
    getInvoke: async () => async (command, args) => { writes.push({ command, args: JSON.parse(JSON.stringify(args)) }); },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, loadDashboard };`, context);
  return { $, hooks: context.hooks, requests, writes, questions, end,
    click: async target => { for (const handler of listeners.get('click') || []) await handler({ target, preventDefault() {} }); } };
}

const stint = { name: 'Violet', blocksTotal: 12, blocksWithCoach: 5, blocksSolo: 7 };

test('an old coaching stint shows on Home with a way to end it, and ending it refreshes Home', async () => {
  let active = true;
  const f = fixture(() => ({ stint: active ? stint : null }));
  await f.hooks.loadDashboard();
  assert.equal(f.$('nextstep-stint').hidden, false);
  assert.equal(f.$('nextstep-stint-text').textContent, 'Coaching stint · Violet · 12 blocks · 5 with coach · 7 solo');
  assert.match(html, /<button class="nextstep-stint-end" id="nextstep-stint-end" type="button" data-action="end_stint">End stint<\/button>/);

  active = false;
  await f.click(f.end);
  assert.equal(f.questions.length, 1);
  assert.doesNotMatch(f.questions[0], /—/);
  assert.deepEqual(f.writes, [{ command: 'end_stint', args: { payload: {} } }]);
  assert.equal(f.requests.length, 2, 'Home reloads after the stint ends');
  assert.equal(f.$('nextstep-stint').hidden, true);
});

test('declining the confirmation keeps the stint running', async () => {
  const f = fixture(() => ({ stint }), { confirm: () => false });
  await f.hooks.loadDashboard();
  await f.click(f.end);
  assert.deepEqual(f.writes, []);
  assert.equal(f.requests.length, 1);
});

test('a failed Home load shows its error at once instead of retrying for ten seconds', async () => {
  const f = fixture(() => { throw new Error('Failed to fetch'); });
  const started = Date.now();
  await f.hooks.loadDashboard();
  assert.ok(Date.now() - started < 1000);
  assert.equal(f.requests.length, 1);
  assert.equal(f.$('errpanel').hidden, false);
  assert.equal(f.$('err-detail').textContent, 'Failed to fetch');
});

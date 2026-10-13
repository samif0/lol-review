// One leave barrier for pages that hold unsaved work. The persistent shell
// (shell-outer.js) awaits window.revuBeforeNavigate() before a sidebar click or
// an update restart replaces the page, and stays put when it resolves false.
//
// A page describes its work once:
//   autosave()  drains silent background writes first (optional; false = failed)
//   isDirty()   are there edits the player still has to save?
//   flush()     saves those edits (optional; resolves true once they are saved)
// A clean page leaves silently. Otherwise the player chooses to save and leave,
// discard the changes, or stay. A failed save keeps the choice open.

export const LEAVE_COPY = Object.freeze({
  title: 'Leave without saving?',
  unsaved: 'You have unsaved changes on this page.',
  failed: 'Your changes could not be saved. Stay to fix them, or discard them.',
  save: 'Save and leave',
  discard: 'Discard changes',
  stay: 'Stay on this page',
});

const attempt = async (fn) => {
  if (typeof fn !== 'function') return true;
  try { return await fn() !== false; }
  catch (err) { console.warn('[leave] saving before leaving failed:', err); return false; }
};

export function registerLeaveGuard({ isDirty = () => false, flush = null, autosave = null,
  ask = askToLeave, scope = globalThis.window } = {}) {
  let pending = null;
  async function decide() {
    const autosaved = await attempt(autosave);
    if (autosaved && !isDirty()) return true;
    let message = autosaved ? LEAVE_COPY.unsaved : LEAVE_COPY.failed;
    const canSave = !!(flush || autosave);
    for (;;) {
      const choice = await ask({ message, canSave });
      if (choice === 'discard') return true;
      if (choice !== 'save') return false;
      if (await attempt(autosave) && await attempt(flush) && !isDirty()) return true;
      message = LEAVE_COPY.failed;
    }
  }
  // A second rail click while the question is open joins it; the shell keeps
  // only the newest destination.
  function beforeNavigate() {
    pending ??= decide().finally(() => { pending = null; });
    return pending;
  }
  scope.revuBeforeNavigate = beforeNavigate;
  return {
    beforeNavigate,
    dispose() { if (scope.revuBeforeNavigate === beforeNavigate) delete scope.revuBeforeNavigate; },
  };
}

// Every field value inside root, in document order.
export function fieldValues(root) {
  return [...root.querySelectorAll('input, select, textarea')].map(field =>
    field.type === 'checkbox' || field.type === 'radio' ? field.checked : field.value);
}

// An always-visible form (manual entry) is dirty once any field leaves the
// value its markup started with.
export function changedFromDefaults(root) {
  return [...root.querySelectorAll('input, select, textarea')].some(field =>
    field.type === 'checkbox' || field.type === 'radio' ? field.checked !== field.defaultChecked
      : field.tagName === 'SELECT' ? [...field.options].some(option => option.selected !== option.defaultSelected)
        : field.value !== field.defaultValue);
}

// A form that opens and closes (rules, learning objectives, matchup notes) is
// dirty while it is open and differs from how it looked when it was filled.
// The page calls mark() once an opener has filled the fields; `extra` covers
// state kept outside the fields (chips, picked tokens).
export function trackForm(form, extra = () => null) {
  const snapshot = () => JSON.stringify([fieldValues(form), extra()]);
  let baseline = null;
  return {
    mark() { baseline = snapshot(); },
    isDirty: () => !form.hidden && baseline !== null && snapshot() !== baseline,
  };
}

// The question itself: a modal dialog in the page, so it sits next to the work.
// Escape means stay. Styles use the shared tokens and button classes.
export function askToLeave({ message, canSave, document: doc = globalThis.document }) {
  return new Promise((resolve) => {
    const dialog = doc.createElement('dialog');
    dialog.className = 'leave-guard';
    dialog.setAttribute('aria-labelledby', 'leave-guard-title');
    dialog.setAttribute('aria-describedby', 'leave-guard-message');
    Object.assign(dialog.style, { maxWidth: '440px', padding: '22px 24px', borderRadius: '14px',
      border: '1px solid var(--border, #3a3a3a)', background: 'var(--surface-raised, #262626)', color: 'var(--text, #ececec)' });
    const title = doc.createElement('h2');
    title.id = 'leave-guard-title';
    title.textContent = LEAVE_COPY.title;
    Object.assign(title.style, { margin: '0 0 8px', fontSize: '17px', fontWeight: '600' });
    const body = doc.createElement('p');
    body.id = 'leave-guard-message';
    body.textContent = message;
    Object.assign(body.style, { margin: '0 0 20px', fontSize: '14px', color: 'var(--muted, #a3a3a3)' });
    const actions = doc.createElement('div');
    Object.assign(actions.style, { display: 'flex', flexWrap: 'wrap', gap: '8px', justifyContent: 'flex-end' });
    let settled = false;
    const done = (choice) => {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      dialog.remove();
      resolve(choice);
    };
    const button = (label, choice, className) => {
      const el = doc.createElement('button');
      el.type = 'button';
      el.className = className;
      el.textContent = label;
      el.addEventListener('click', () => done(choice));
      return el;
    };
    if (canSave) actions.append(button(LEAVE_COPY.save, 'save', 'cta cta-sm'));
    actions.append(button(LEAVE_COPY.discard, 'discard', 'rule-act'), button(LEAVE_COPY.stay, 'stay', 'rule-act'));
    dialog.addEventListener('cancel', (event) => { event.preventDefault(); done('stay'); });
    dialog.append(title, body, actions);
    doc.body.append(dialog);
    dialog.showModal();
  });
}

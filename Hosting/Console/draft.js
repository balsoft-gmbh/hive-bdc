// The one draft of the settings that the Export and Connections pages edit, and the parts every editing screen shares:
// the Save bar lists the draft's changes and saves it with the version it was read at, so a save by someone else in
// between shows board W6 instead of being overwritten.
import { postBody } from './logic.js';
import * as X from './exportlogic.js';
import { el, put, save, trail } from './ui.js';
export const state = { draft: null, original: null, version: null, savedAt: '', keys: new Map(), ui: {}, targets: [] };

export function sync(config) {
  const fresh = postBody(config);
  const unchanged = !state.draft || X.draftChanges(state.original, state.draft).length === 0;
  if (unchanged && (state.version !== config.version || !state.draft)) {
    state.draft = fresh;
    state.original = structuredClone(fresh);
    state.version = config.version;
  }
}

export const change = (fn) => { state.draft = fn(state.draft); };
export const capital = (text) => text.charAt(0).toUpperCase() + text.slice(1);
export const go = (hash) => { if (window.location.hash === hash) window.dispatchEvent(new HashChangeEvent('hashchange')); else window.location.hash = hash; };


// ── shared parts ─────────────────────────────────────────────────────────────────────

export const crumbs = (...items) => trail(...items);

export function badge() {
  return el('span', { class: 'badge-outbound', text: 'OUTBOUND' });
}

export function quiet(text, onClick) {
  return el('button', { type: 'button', class: 'quiet edit-only', text, on: { click: onClick } });
}

export function confirmBox(text, onRemove, onKeep) {
  return el('div', { class: 'confirm', role: 'alert' }, el('span', { text }),
    el('span', { class: 'row-actions' },
      el('button', { type: 'button', class: 'button', text: 'Keep', on: { click: onKeep } }),
      el('button', { type: 'button', class: 'button button-danger', text: 'Remove', on: { click: onRemove } })));
}

export function toggle(label, on, onChange, disabled) {
  const box = el('input', { type: 'checkbox', class: 'switch', checked: on, disabled, 'aria-label': label });
  box.addEventListener('change', () => onChange(box.checked));
  return box;
}

export function segmented(name, options, value, onChange) {
  return el('span', { class: 'seg', role: 'radiogroup' }, options.map(([v, text]) => {
    const input = el('input', { type: 'radio', name, value: v, checked: v === value });
    input.addEventListener('change', () => { if (input.checked) onChange(v); });
    return el('label', { class: 'seg-option' }, input, el('span', { text }));
  }));
}

export function connectionSelect(kind, value, onChange, optional) {
  const choices = state.draft.connections.filter((c) => c.kind === kind);
  const select = el('select', { class: 'input' },
    optional || choices.length === 0 ? el('option', { value: '', text: choices.length ? '(none)' : 'No connection yet' }) : null,
    choices.map((c) => el('option', { value: c.id, selected: c.id === value, text: connectionLabel(c) })));
  if (!value && choices.length && !optional) queueMicrotask(() => onChange(choices[0].id));
  select.addEventListener('change', () => onChange(select.value));
  return select;
}

export function connectionLabel(c) {
  const s = c.settings || {};
  const host = (url) => { try { return new URL(url).host.split('.')[0]; } catch { return url; } };
  const where = s.AccountUrl ? host(s.AccountUrl) : s.Namespace ? s.Namespace.split('.')[0] : s.Url ? s.Url.replace(/^https:\/\//, '') : '';
  return where ? `${c.id} (${where})` : c.id;
}

/** The Save bar: the changes of the draft, Discard, and Save once nothing blocks it. */
export function saveBar(env) {
  const slot = el('div', { class: 'savebar edit-only', 'aria-live': 'polite' });
  const result = el('div', { class: 'slot' });
  const paint = () => {
    const lines = X.draftChanges(state.original, state.draft);
    if (lines.length === 0) {
      put(slot, state.savedAt ? el('span', { class: 'chip chip-ok', text: `Saved ${state.savedAt}` }) : null, result);
      return;
    }
    const problems = X.saveProblems(state.draft, env.ctx.config, state.targets);
    const saveButton = el('button', { type: 'button', class: 'button button-primary button-big', text: 'Save changes', disabled: problems.length > 0 });
    saveButton.addEventListener('click', async () => {
      saveButton.disabled = true;
      const done = () => {
        state.savedAt = new Date().toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
        state.draft = null;
        state.keys.clear();
        state.ui = {};
        go(window.location.hash);
      };
      if (await save(state.draft, result, done)) done();
      else saveButton.disabled = false;
    });
    put(slot,
      el('div', { class: 'savebar-text' },
        el('strong', { text: `${lines.length} ${lines.length === 1 ? 'change' : 'changes'} not saved` }),
        el('ul', {}, lines.slice(0, 5).map((l) => el('li', { text: l })), lines.length > 5 ? el('li', { text: `and ${lines.length - 5} more` }) : null),
        problems.length ? el('ul', { class: 'warn-text' }, problems.map((p) => el('li', { text: p }))) : null),
      el('span', { class: 'row-actions' },
        el('button', { type: 'button', class: 'button button-big', text: 'Discard', on: { click: () => { state.draft = null; state.keys.clear(); state.ui = {}; go(window.location.hash); } } }),
        saveButton),
      result);
  };
  paint();
  return { node: slot, paint };
}

export function savedChip() {
  return state.savedAt && X.draftChanges(state.original, state.draft).length === 0 ? el('span', { class: 'chip chip-ok', text: `Saved ${state.savedAt}` }) : null;
}

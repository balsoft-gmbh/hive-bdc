// DOM and network helpers of the web console. Every element is built with createElement and every value set with
// textContent or setAttribute, so nothing read from the settings or the history can become markup (failure case 52).
import { conflictView } from './logic.js';

export function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'on') {
      for (const [event, handler] of Object.entries(value)) node.addEventListener(event, handler);
    } else if (key === 'text') {
      node.textContent = String(value);
    } else if (value === true) {
      node.setAttribute(key, '');
    } else {
      node.setAttribute(key, String(value));
    }
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function put(node, ...children) {
  node.replaceChildren(...children.flat().filter((c) => c !== null && c !== undefined && c !== false));
}

export async function api(path, options) {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (response.status === 401) {
    // The App Service session ended: a reload goes through the sign-in again.
    window.location.reload();
    throw new Error('Signed out');
  }
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
  if (!response.ok) {
    const first = body && Array.isArray(body.errors) && body.errors.length ? body.errors.join(' ') : '';
    const error = new Error((body && (body.error || body.message)) || first || `The service answered ${response.status}.`);
    error.status = response.status;
    error.body = body;
    throw error;
  }
  return body;
}

export function notice(kind, text, ...extra) {
  return el('div', { class: `notice notice-${kind}`, role: kind === 'error' ? 'alert' : 'status' }, el('span', { text }), ...extra);
}

export function link(href, text, primary) {
  return el('a', { class: primary ? 'button button-primary button-big' : 'button button-big', href, text });
}

export function field(label, control, hint) {
  return el('label', { class: 'field' }, el('span', { class: 'field-label', text: label }), control, hint ? el('span', { class: 'field-hint', text: hint }) : null);
}

export function textInput(attrs) {
  return el('input', { type: 'text', class: 'input', autocomplete: 'off', spellcheck: 'false', ...attrs });
}

export function chip(ok, text) {
  return el('span', { class: ok ? 'chip chip-ok' : 'chip chip-warn' }, el('span', { class: ok ? 'dot dot-ok' : 'dot dot-warn', 'aria-hidden': 'true' }), text);
}

/**
 * Saves the settings with the version they were read at; a refusal shows board W6 (failure cases 54, 61, 70) and
 * "Save both" retries with the merge. Returns true when saved.
 */
export async function save(body, slot, afterSaveBoth) {
  put(slot);
  try {
    await api('/config', { method: 'POST', body: JSON.stringify(body) });
    return true;
  } catch (error) {
    if (error.status === 409 && error.body) {
      put(slot, conflictPanel(error.body, async () => {
        if (await save({ ...body, saveBoth: true }, slot)) (afterSaveBoth || (() => window.dispatchEvent(new HashChangeEvent('hashchange'))))();
      }));
    } else {
      put(slot, notice('error', error.message));
    }
    return false;
  }
}

function conflictPanel(refusal, saveBoth) {
  const view = conflictView(refusal, 'Europe/Berlin');
  const list = (title, lines) => el('div', { class: 'conflict-side' }, el('h3', { text: title }),
    lines.length ? el('ul', {}, lines.map((l) => el('li', { text: l }))) : el('p', { class: 'muted', text: 'Nothing.' }));
  return el('section', { class: 'card conflict', role: 'alert' },
    el('h2', { text: view.title }),
    el('p', { text: view.note }),
    el('div', { class: 'conflict-lists' }, list('Their changes', view.theirs), list('Your changes', view.yours)),
    el('div', { class: 'row-actions' },
      view.canSaveBoth ? el('button', { type: 'button', class: 'button button-primary', text: 'Save both', on: { click: saveBoth } }) : null,
      el('button', { type: 'button', class: 'button', text: 'Load the settings again', on: { click: () => window.location.reload() } })));
}

/** A Viewer sees every screen read-only (failure cases 58, 79): no input takes text, no button acts, edit-only parts go. */
export function lock(view) {
  for (const node of view.querySelectorAll('input, select, textarea, button')) node.disabled = true;
  for (const node of view.querySelectorAll('.edit-only')) node.hidden = true;
}

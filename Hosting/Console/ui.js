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

const SVG = 'http://www.w3.org/2000/svg';
const ICONS = {
  overview: [['rect', { x: 3.5, y: 3.5, width: 7, height: 7, rx: 1.6 }], ['rect', { x: 13.5, y: 3.5, width: 7, height: 7, rx: 1.6 }],
    ['rect', { x: 3.5, y: 13.5, width: 7, height: 7, rx: 1.6 }], ['rect', { x: 13.5, y: 13.5, width: 7, height: 7, rx: 1.6 }]],
  history: [['path', { d: 'M3 12a9 9 0 109-9 9.7 9.7 0 00-6.7 2.8L3 8' }], ['path', { d: 'M3 3v5h5M12 7.5V12l3 2' }]],
  export: [['path', { d: 'M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8z' }], ['path', { d: 'M14 3v5h5M12 18v-6M9.5 14.5L12 12l2.5 2.5' }]],
  connections: [['path', { d: 'M9.5 14.5l5-5' }], ['path', { d: 'M11 6.5l1.6-1.6a4 4 0 015.6 5.6L16.6 12M13 17.5l-1.6 1.6a4 4 0 01-5.6-5.6L7.4 12' }]],
  service: [['circle', { cx: 12, cy: 12, r: 3 }], ['path', { d: 'M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5.3 5.3l2.1 2.1M16.6 16.6l2.1 2.1M5.3 18.7l2.1-2.1M16.6 7.4l2.1-2.1' }]],
  instance: [['path', { d: 'M12 2.8l8 4.6v9.2l-8 4.6-8-4.6V7.4z' }], ['path', { d: 'M9 12.2l2 2 4-4.2' }]],
  signout: [['path', { d: 'M15 4h3a2 2 0 012 2v12a2 2 0 01-2 2h-3M10 17l5-5-5-5M15 12H4' }]],
  chevron: [['path', { d: 'M9 6l6 6-6 6' }]],
  check: [['path', { d: 'M5 12l4 4 10-10' }]],
  alert: [['circle', { cx: 12, cy: 12, r: 9 }], ['path', { d: 'M12 8v5M12 16.5v.5' }]],
  download: [['path', { d: 'M12 3v12M7 10l5 5 5-5M5 21h14' }]],
};

/** A line icon of the console; drawn with createElementNS and attributes only, so no markup is parsed. */
export function icon(name, cls) {
  const svg = document.createElementNS(SVG, 'svg');
  const attrs = { viewBox: '0 0 24 24', width: 18, height: 18, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.8,
    'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', focusable: 'false', class: cls || null };
  for (const [key, value] of Object.entries(attrs)) if (value !== null) svg.setAttribute(key, String(value));
  for (const [tag, shape] of ICONS[name] || []) {
    const node = document.createElementNS(SVG, tag);
    for (const [key, value] of Object.entries(shape)) node.setAttribute(key, String(value));
    svg.append(node);
  }
  return svg;
}

/** The breadcrumb in the top bar; the last item is the page itself. Returns null so a page can list it in put(). */
export function trail(...items) {
  const box = document.getElementById('crumbs');
  if (!box) return null;
  put(box, el('nav', { class: 'breadcrumb', 'aria-label': 'Breadcrumb' },
    items.flatMap((item, i) => [
      i > 0 ? icon('chevron', 'crumb-sep') : null,
      i === items.length - 1 ? el('strong', { 'aria-current': 'page', text: item.text })
        : item.href ? el('a', { href: item.href, text: item.text }) : el('span', { text: item.text }),
    ])));
  return null;
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
  for (const node of view.querySelectorAll('input, select, textarea, button')) if (!node.closest('.view-ok')) node.disabled = true;
  for (const node of view.querySelectorAll('.edit-only')) node.hidden = true;
}

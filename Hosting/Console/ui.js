// DOM and network helpers of the web console. Every element is built with createElement and every value set with
// textContent or setAttribute, so nothing read from the settings or the history can become markup (failure case 52).

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

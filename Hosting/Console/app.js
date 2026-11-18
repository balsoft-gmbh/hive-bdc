// The web console of a Hive BDC instance in Azure. Every element is built with createElement and every value set with
// textContent or setAttribute, so nothing read from the settings or the history can become markup (failure case 52).
import { attentionRows, initials, licenceLine, newestFirst, parseRoute, stepLabel, topRole } from './logic.js';

const state = { me: null, status: null };

function el(tag, attrs, ...children) {
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

function put(node, ...children) {
  node.replaceChildren(...children.flat().filter((c) => c !== null && c !== undefined && c !== false));
}

async function api(path, options) {
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
    const error = new Error((body && (body.error || body.message)) || `The service answered ${response.status}.`);
    error.status = response.status;
    error.body = body;
    throw error;
  }
  return body;
}

function notice(kind, text, ...extra) {
  return el('div', { class: `notice notice-${kind}`, role: kind === 'error' ? 'alert' : 'status' }, el('span', { text }), ...extra);
}

// ── shell ──────────────────────────────────────────────────────────────────────────

const pages = [
  { id: 'overview', label: 'Overview' },
  { id: 'history', label: 'Document history' },
];

function renderShell() {
  const nav = document.getElementById('nav-links');
  put(nav, ...pages.map((p) => el('a', { href: `#/${p.id}`, 'data-page': p.id, text: p.label })));

  const me = state.me;
  document.getElementById('instance').textContent = `${window.location.hostname.split('.')[0]} · version ${me.version}`;
  document.getElementById('user-initials').textContent = initials(me.name);
  document.getElementById('user-name').textContent = me.name;
  document.getElementById('user-role').textContent = topRole(me.roles) || 'No role';
}

function markCurrent(page) {
  for (const link of document.querySelectorAll('#nav-links a')) {
    if (link.getAttribute('data-page') === page) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
}

function renderServiceBox() {
  const box = document.getElementById('service-box');
  const line = licenceLine(state.status && state.status.license);
  put(box,
    el('span', { class: 'service-state' }, el('span', { class: state.status && state.status.running ? 'dot dot-ok' : 'dot dot-off', 'aria-hidden': 'true' }), state.status && state.status.running ? 'Running' : 'Stopped'),
    el('span', { class: line.ok ? 'service-licence' : 'service-licence service-licence-warn', text: line.text }),
  );
}

// ── pages ──────────────────────────────────────────────────────────────────────────

async function overviewPage(view) {
  const [metrics, attention] = await Promise.all([api('/metrics?recent=0'), api('/history/attention?limit=200')]);
  const rows = attentionRows(attention.events);
  const licence = licenceLine(state.status.license);

  const cards = el('div', { class: 'cards' },
    el('div', { class: 'card stat' }, el('span', { class: 'stat-label', text: 'Delivered today' }), el('span', { class: 'stat-value', text: metrics.today }), el('span', { class: 'stat-note', text: `${metrics.month} this month` })),
    el('a', { class: rows.length ? 'card stat stat-warn' : 'card stat', href: '#attention' }, el('span', { class: 'stat-label', text: 'Needs attention' }), el('span', { class: 'stat-value', text: rows.length }), el('span', { class: 'stat-note', text: rows.length ? 'see below' : 'nothing to do' })),
    el('div', { class: licence.ok ? 'card stat' : 'card stat stat-warn' }, el('span', { class: 'stat-label', text: 'Licence' }), el('span', { class: 'stat-value stat-value-small', text: licence.text })),
  );

  const list = el('section', { id: 'attention', class: 'card', 'aria-labelledby': 'attention-h' },
    el('h2', { id: 'attention-h', text: 'Needs attention' }),
    rows.length === 0
      ? el('p', { class: 'muted', text: 'Every document went through.' })
      : el('div', { class: 'rows' }, rows.map((row) => attentionRow(row))));

  put(view,
    el('div', { class: 'page-head' }, el('h1', { text: 'Today' }), el('p', { class: 'muted', text: `As of ${new Date().toLocaleString('de-DE', { dateStyle: 'medium', timeStyle: 'short' })}` })),
    licence.ok ? null : notice('warn', state.status.license && state.status.license.message ? state.status.license.message : licence.text),
    cards,
    list,
  );
}

function attentionRow(row) {
  const message = el('span', { class: row.waiting ? 'row-message' : 'row-message row-message-warn', text: row.target ? `${row.target}: ${row.message}` : row.message });
  const actions = el('span', { class: 'row-actions' }, el('a', { href: `#/history?search=${encodeURIComponent(row.documentId)}`, text: 'History' }));
  if (!row.waiting && state.me.canRetry) {
    const button = el('button', { type: 'button', class: 'button', text: 'Retry' });
    button.addEventListener('click', async () => {
      button.disabled = true;
      try {
        const answer = await api('/history/retry', { method: 'POST', body: JSON.stringify({ documentId: row.documentId }) });
        put(actions, el('span', { class: answer.ok ? 'ok-text' : 'warn-text', text: answer.message }));
      } catch (error) {
        put(actions, el('span', { class: 'warn-text', text: error.message }));
      }
    });
    actions.append(button);
  }
  return el('div', { class: 'row' },
    el('strong', { text: row.number }), el('span', { text: row.company }), message, actions);
}

async function historyPage(view, route) {
  const search = route.query.search || '';
  const input = el('input', { id: 'history-search', type: 'search', value: search, placeholder: 'Document number, company or file name' });
  const form = el('form', { class: 'search', role: 'search' },
    el('label', { for: 'history-search', class: 'sr-only', text: 'Search the document history' }), input,
    el('button', { type: 'submit', class: 'button button-primary', text: 'Search' }));
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    window.location.hash = `#/history?search=${encodeURIComponent(input.value.trim())}`;
  });

  const answer = await api(`/history?limit=300${search ? `&search=${encodeURIComponent(search)}` : ''}`);
  const table = el('table', { class: 'table' },
    el('thead', {}, el('tr', {}, ['When', 'Document', 'Company', 'Step', 'Where', 'What happened'].map((h) => el('th', { scope: 'col', text: h })))),
    el('tbody', {}, newestFirst(answer.events).map((e) => el('tr', { class: e.state === 'ok' ? '' : 'row-warn' },
      el('td', { class: 'nowrap', text: e.at }),
      el('td', { class: 'nowrap', text: e.documentNumber || e.documentId }),
      el('td', { text: e.dataAreaId || '' }),
      el('td', { text: stepLabel(e.step) }),
      el('td', { text: e.target || '' }),
      el('td', { text: e.message }),
    ))));

  put(view,
    el('div', { class: 'page-head' }, el('h1', { text: 'Document history' }), el('p', { class: 'muted', text: 'Every step of every document, newest first.' })),
    form,
    answer.events.length === 0 ? el('p', { class: 'muted', text: search ? `Nothing found for "${search}".` : 'No documents yet.' }) : el('div', { class: 'table-box' }, table),
  );
}

const renderers = { overview: overviewPage, history: historyPage };

async function show() {
  const route = parseRoute(window.location.hash);
  const render = renderers[route.page] || overviewPage;
  const page = renderers[route.page] ? route.page : 'overview';
  markCurrent(page);
  const view = document.getElementById('view');
  view.setAttribute('aria-busy', 'true');
  try {
    state.status = await api('/status');
    renderServiceBox();
    await render(view, route);
  } catch (error) {
    put(view, notice('error', error.message));
  } finally {
    view.removeAttribute('aria-busy');
    view.focus();
  }
}

async function start() {
  try {
    state.me = await api('/me');
  } catch (error) {
    put(document.getElementById('view'), notice('error', error.message));
    return;
  }
  renderShell();
  window.addEventListener('hashchange', show);
  await show();
}

start();
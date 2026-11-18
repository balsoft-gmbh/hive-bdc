// The web console of a Hive BDC instance in Azure. Every element is built with createElement and every value set with
// textContent or setAttribute (ui.js), so nothing read from the settings or the history can become markup (failure case 52).
import { attentionRows, initials, licenceLine, newestFirst, parseRoute, setupNeeded, stepLabel, topRole } from './logic.js';
import { setupPage } from './setup.js';
import { api, el, notice, put } from './ui.js';

const state = { me: null, status: null, config: null };

// ── shell ──────────────────────────────────────────────────────────────────────────

const pages = [
  { id: 'overview', label: 'Overview' },
  { id: 'history', label: 'Document history' },
];

/** While the set-up is not finished only the assistant opens; the other pages are shown greyed (board W1). */
function renderNav(setup) {
  const nav = document.getElementById('nav-links');
  if (setup) {
    put(nav,
      el('a', { href: '#/setup', 'data-page': 'setup', text: 'Set up' }),
      pages.map((p) => el('span', { class: 'nav-off', text: p.label })),
      el('p', { class: 'nav-note', text: 'The other pages open when Hive BDC is started at the end of the set-up.' }));
  } else {
    put(nav, ...pages.map((p) => el('a', { href: `#/${p.id}`, 'data-page': p.id, text: p.label })));
  }
}

function renderShell() {
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
  const running = Boolean(state.status && state.status.running);
  const paused = running && Boolean(state.config && state.config.paused);
  put(box,
    el('span', { class: 'service-state' }, el('span', { class: running && !paused ? 'dot dot-ok' : 'dot dot-off', 'aria-hidden': 'true' }),
      paused ? 'Paused until the set-up is finished' : running ? 'Running' : 'Stopped'),
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
  const view = document.getElementById('view');
  view.setAttribute('aria-busy', 'true');
  try {
    [state.status, state.config] = await Promise.all([api('/status'), api('/config')]);
    renderServiceBox();
    const setup = setupNeeded(state.config);
    renderNav(setup);
    if (setup) {
      markCurrent('setup');
      await setupPage(view, route, { me: state.me, status: state.status, config: state.config, started });
      return;
    }
    const page = renderers[route.page] ? route.page : 'overview';
    markCurrent(page);
    await renderers[page](view, route);
  } catch (error) {
    put(view, notice('error', error.message));
  } finally {
    view.removeAttribute('aria-busy');
    view.focus();
  }
}

/** Start was pressed: the normal pages open on the overview. */
function started() {
  if (window.location.hash === '#/overview') show();
  else window.location.hash = '#/overview';
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
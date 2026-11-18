// The web console of a Hive BDC instance in Azure. Every element is built with createElement and every value set with
// textContent or setAttribute (ui.js), so nothing read from the settings or the history can become markup (failure case 52).
import { attentionRows, canonicalPage, dayLine, greeting, initials, licenceLine, newestFirst, parseRoute, setupNeeded, stepLabel, topRole } from './logic.js';
import { connectionsPage } from './connections.js';
import { exportPage } from './export.js';
import { servicePage } from './service.js';
import { setupPage } from './setup.js';
import { api, el, icon, notice, put, trail } from './ui.js';
import { updateBadge } from './updatelogic.js';
import { updateNotice } from './updates.js';

const state = { me: null, status: null, config: null, updates: null };

// ── shell ──────────────────────────────────────────────────────────────────────────

const groups = [
  { label: 'WORK', pages: [{ id: 'overview', label: 'Overview' }, { id: 'history', label: 'Document history' }] },
  { label: 'SET UP', pages: [{ id: 'outbound', label: 'Outbound', sub: 'from your ERP', icon: 'export' }, { id: 'connections', label: 'Connections' }, { id: 'service', label: 'Service' }] },
];
const pages = groups.flatMap((g) => g.pages);
const instanceName = () => window.location.hostname.split('.')[0];

/** While the set-up is not finished only the assistant opens; the other pages are shown greyed (board W1). */
function renderNav(setup) {
  const nav = document.getElementById('nav-links');
  const count = updateBadge(state.updates);
  const link = (p) => el('a', { class: 'nav-link', href: `#/${p.id}`, 'data-page': p.id }, icon(p.icon || p.id, 'nav-icon'),
    el('span', { class: 'nav-text' }, el('span', { text: p.label }), p.sub ? el('span', { class: 'nav-sub', text: p.sub }) : null),
    p.id === 'service' && count ? el('span', { class: 'nav-count', 'aria-label': 'Update available', text: count }) : null);
  if (setup) {
    put(nav,
      el('div', { class: 'nav-label', text: 'SET UP' }),
      el('a', { class: 'nav-link', href: '#/setup', 'data-page': 'setup' }, icon('instance', 'nav-icon'), el('span', { text: 'Set up' })),
      pages.map((p) => el('span', { class: 'nav-off' }, icon(p.icon || p.id, 'nav-icon'), el('span', { text: p.label }))),
      el('p', { class: 'nav-note', text: 'The other pages open when Hive BDC is started at the end of the set-up.' }));
  } else {
    put(nav, groups.flatMap((g) => [el('div', { class: 'nav-label', text: g.label }), ...g.pages.map(link)]));
  }
}

function renderShell() {
  const me = state.me;
  document.getElementById('instance').textContent = `${instanceName()} · ${me.version}`;
  document.getElementById('user-initials').textContent = initials(me.name);
  document.getElementById('user-name').textContent = me.name;
  document.getElementById('user-role').textContent = topRole(me.roles) || 'No role';
  put(document.getElementById('sign-out'), icon('signout'));
}

/** The licence holder and the instance name above the menu, as the workspace card of the CRM and ERP. */
function renderInstanceCard() {
  const holder = state.status && state.status.license && state.status.license.customer;
  put(document.getElementById('instance-card'),
    el('span', { class: 'instance-mark' }, icon('instance')),
    el('span', { class: 'instance-text' }, el('strong', { text: holder || 'Hive BDC' }), el('span', { text: instanceName() })));
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
      paused ? (state.config.setupOpen ? 'Paused until the set-up is finished' : 'Paused') : running ? 'Running' : 'Stopped'),
    el('span', { class: line.ok ? 'service-licence' : 'service-licence service-licence-warn', text: line.text }),
  );
}

// ── pages ──────────────────────────────────────────────────────────────────────────

async function overviewPage(view) {
  const [metrics, attention] = await Promise.all([api('/metrics?recent=0'), api('/history/attention?limit=200')]);
  const rows = attentionRows(attention.events);
  const licence = licenceLine(state.status.license);
  const now = new Date();
  const stat = (tag, attrs, label, mark, value, note, small) => el(tag, attrs,
    el('span', { class: 'stat-top' }, el('span', { class: 'stat-label', text: label }), el('span', { class: `stat-icon stat-icon-${mark[1]}` }, icon(mark[0]))),
    el('span', { class: small ? 'stat-value stat-value-small' : 'stat-value', text: value }),
    note ? el('span', { class: 'stat-note', text: note }) : null);

  const cards = el('div', { class: 'cards' },
    stat('div', { class: 'card stat' }, 'Delivered today', ['check', 'ok'], metrics.today, `${metrics.month} this month`),
    stat('a', { class: rows.length ? 'card stat stat-warn' : 'card stat', href: '#attention' }, 'Needs attention', ['alert', rows.length ? 'warn' : 'ok'], rows.length, rows.length ? 'see below' : 'nothing to do'),
    stat('div', { class: licence.ok ? 'card stat' : 'card stat stat-warn' }, 'Licence', ['instance', licence.ok ? 'gold' : 'warn'], licence.text, null, true),
  );

  const list = el('section', { id: 'attention', class: 'card', 'aria-labelledby': 'attention-h' },
    el('div', { class: 'section-head' }, el('h2', { id: 'attention-h', text: 'Needs attention' }), el('a', { class: 'small', href: '#/history', text: 'Document history' })),
    rows.length === 0
      ? el('p', { class: 'muted', text: 'Every document went through.' })
      : el('div', { class: 'rows' }, rows.map((row) => attentionRow(row))));

  put(view,
    el('div', { class: 'page-head-row' },
      el('div', { class: 'page-head' }, el('div', { class: 'eyebrow', text: dayLine(now) }), el('h1', { text: greeting(now.getHours(), state.me.name) })),
      el('span', { class: 'muted small', text: `As of ${now.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })}` })),
    updateNotice(state.updates, { me: state.me }),
    licence.ok ? null : notice('warn', state.status.license && state.status.license.message ? state.status.license.message : licence.text),
    cards,
    list,
  );
}

function attentionRow(row) {
  const message = el('span', { class: row.waiting ? 'row-message' : 'row-message row-message-warn' },
    row.target ? el('span', { class: row.waiting ? 'badge badge-amber' : 'badge badge-rose', text: row.target }) : null,
    el('span', { text: row.message }));
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
    el('div', { class: 'page-head' }, el('div', { class: 'eyebrow', text: 'WORK' }), el('h1', { text: 'Document history' }), el('p', { class: 'muted', text: 'Every step of every document, newest first.' })),
    form,
    answer.events.length === 0 ? el('p', { class: 'muted', text: search ? `Nothing found for "${search}".` : 'No documents yet.' }) : el('div', { class: 'table-box' }, table),
  );
}

const renderers = {
  overview: overviewPage,
  history: historyPage,
  outbound: (view, route) => exportPage(view, route, { me: state.me, status: state.status, config: state.config }),
  connections: (view, route) => connectionsPage(view, route, { me: state.me, status: state.status, config: state.config }),
  service: (view, route) => servicePage(view, route, { me: state.me, status: state.status, config: state.config, updates: state.updates }),
};

async function show() {
  const route = parseRoute(window.location.hash);
  const view = document.getElementById('view');
  view.setAttribute('aria-busy', 'true');
  try {
    [state.status, state.config, state.updates] = await Promise.all([api('/status'), api('/config'), api('/service/updates').catch(() => null)]);
    renderServiceBox();
    renderInstanceCard();
    const setup = setupNeeded(state.config);
    renderNav(setup);
    if (setup) {
      markCurrent('setup');
      trail({ text: 'Hive BDC' }, { text: 'Set up' });
      await setupPage(view, route, { me: state.me, status: state.status, config: state.config, started });
      return;
    }
    const page = renderers[canonicalPage(route.page)] ? canonicalPage(route.page) : 'overview';
    markCurrent(page);
    trail({ text: 'Hive BDC' }, { text: pages.find((p) => p.id === page).label });
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
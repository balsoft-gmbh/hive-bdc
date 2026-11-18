// The Updates tab of the Service page (boards P3 to P6) and the update notice of the overview (P1). While a switch runs
// the page asks every 5 seconds; while the instance restarts it says so and reconnects by itself.
import { germanDate } from './logic.js';
import { historyRows, progress, screenOf, stepRows, updateBadge, whenText } from './updatelogic.js';
import { api, el, icon, lock, notice, put, trail } from './ui.js';

let timer = null;
const onPage = () => window.location.hash.startsWith('#/service/updates');
const stopPolling = () => { if (timer) { clearTimeout(timer); timer = null; } };
const megabytes = (bytes) => `${Math.round(bytes / 1048576)} MB`;

export function serviceTabs(current, count) {
  const tab = (id, href, text) => el('a', { class: 'tab', href, 'aria-current': id === current ? 'page' : null },
    text, id === 'updates' && count ? el('span', { class: 'nav-count', text: count }) : null);
  return el('nav', { class: 'tabs', 'aria-label': 'Service sections' }, tab('status', '#/service', 'Status'), tab('updates', '#/service/updates', 'Updates'));
}

async function act(path, body) {
  return api(path, { method: 'POST', body: JSON.stringify(body || {}) });
}

export async function updatesPage(view, route, ctx) {
  stopPolling();
  trail({ text: 'Service', href: '#/service' }, { text: 'Updates' });
  let data;
  try {
    data = await api('/service/updates');
  } catch (error) {
    put(view, head(0), notice(error.status === 404 ? 'info' : 'error', error.message));
    return;
  }
  draw(view, ctx, data);
}

function head(count) {
  return [el('div', { class: 'page-head' }, el('div', { class: 'eyebrow', text: 'SERVICE' }), el('h1', { text: 'This instance' })),
    serviceTabs('updates', count)];
}

function draw(view, ctx, data, extra) {
  const zone = data.timeZone;
  const screen = screenOf(data);
  const message = el('div', { class: 'slot' }, extra || null);
  const again = async () => { draw(view, ctx, await api('/service/updates')); };
  const run = async (button, path, body) => {
    button.disabled = true;
    try {
      const answer = await act(path, body);
      draw(view, ctx, await api('/service/updates'), answer.message ? notice('info', answer.message) : null);
    } catch (error) {
      // A stop after the download is in the history (board P6); a refusal before it is only this message.
      const fresh = await api('/service/updates').catch(() => null);
      if (fresh && screenOf(fresh) === 'stopped' && screenOf(data) !== 'stopped') draw(view, ctx, fresh);
      else if (fresh) draw(view, ctx, fresh, notice('error', error.message));
      else { put(message, notice('error', error.message)); button.disabled = false; }
    }
  };
  const button = (text, primary, path, body) => {
    const b = el('button', { type: 'button', class: primary ? 'button button-primary edit-only' : 'button edit-only', text });
    b.addEventListener('click', () => run(b, path, body));
    return b;
  };

  let main;
  let banner = null;
  if (screen === 'running') main = runningCard(data, zone);
  else if (screen === 'stopped') {
    const last = data.history[0];
    const running = data.installed.version || data.version;
    banner = el('section', { class: 'banner banner-bad', role: 'alert' }, el('span', { class: 'banner-icon' }, icon('alert')),
      el('span', { class: 'banner-text' },
        el('strong', { text: last.state === 'returned' ? `${last.version} did not run properly. Hive BDC is back on ${running}.` : `The update to ${last.version} stopped. Nothing changed.` }),
        el('span', { text: `${running} runs with your settings, and documents flow as before.` })));
    main = stoppedCard(data, zone, last, button);
  } else if (screen === 'offer') main = offerCard(data, zone, button, ctx);
  else if (screen === 'done') {
    banner = el('section', { class: 'banner banner-ok', role: 'status' }, el('span', { class: 'banner-icon' }, icon('check')),
      el('span', { class: 'banner-text' }, el('strong', { text: `Hive BDC runs ${data.installed.version} since ${whenText(data.installed.since, zone).slice(11)}` }),
        el('span', { text: data.history[0].note })));
    main = checksCard(data, zone);
  } else main = idleCard(data, button);

  const aside = el('aside', { class: 'stack' },
    screen === 'running' ? whileCard(data) : null,
    screen === 'done' && data.previous ? returnCard(data, run) : null,
    screen !== 'running' && screen !== 'done' ? installedCard(data, zone, button, screen !== 'idle') : null,
    screen !== 'running' ? historyCard(data, zone) : null);

  put(view, head(updateBadge(data)), message, banner, el('div', { class: 'update-grid' }, main, aside));
  const menuBadge = document.querySelector('#nav-links a[data-page="service"] .nav-count');
  if (menuBadge && !updateBadge(data)) menuBadge.remove();
  if (!ctx.me.canChange) lock(view);
  if (screen === 'running') poll(view, ctx, data);
}

function poll(view, ctx, last) {
  stopPolling();
  timer = setTimeout(async () => {
    if (!onPage()) return;
    try {
      const data = await api('/service/updates');
      if (!onPage()) return;
      // Another version answers now: load the whole page again, so the menu and the top bar show it too.
      if (data.version !== ctx.me.version) { window.location.reload(); return; }
      if (screenOf(data) !== 'running' || JSON.stringify(data.steps) !== JSON.stringify(last.steps)) draw(view, ctx, data);
      else poll(view, ctx, data);
    } catch {
      const line = view.querySelector('.reconnect');
      if (line) line.hidden = false;
      poll(view, ctx, last);
    }
  }, 5000);
}

function offerCard(data, zone, button, ctx) {
  const offer = data.available;
  const steps = ['Download from Balsoft', 'Check Balsoft\'s signature', 'Back up the settings', `Switch to ${offer.version}`, 'Restart, 2 to 4 minutes'];
  const planned = data.schedule ? el('p', { class: 'muted small', text: `Planned for ${whenText(data.schedule.at, zone)} by ${data.schedule.by}.` }) : null;
  return el('section', { class: 'card stack', 'aria-labelledby': 'offer-h' },
    el('div', { class: 'section-head' },
      el('div', { class: 'stack-tight' }, el('span', { class: 'badge badge-amber', text: 'New version' }),
        el('h2', { id: 'offer-h', class: 'update-title', text: `Hive BDC ${offer.version}` }),
        el('span', { class: 'muted small', text: [offer.releaseDate ? `Released ${germanDate(offer.releaseDate)}` : '', offer.size ? megabytes(offer.size) : '', `you have ${data.version}`].filter(Boolean).join(' · ') })),
      el('span', { class: 'banner-icon' }, icon('download'))),
    (offer.notes || []).length ? el('ul', { class: 'notes' }, offer.notes.map((n) => el('li', { text: n }))) : null,
    el('div', { class: 'happens' }, el('span', { class: 'happens-title', text: 'WHAT HAPPENS' }),
      el('ol', { class: 'happens-steps' }, steps.map((s, i) => el('li', {}, el('span', { class: 'happens-no', text: i + 1 }), el('span', { text: s })))),
      el('p', { class: 'muted small', text: `Documents wait in their source while it restarts; nothing is lost. ${data.version} stays ready, so you can return to it with one click.` })),
    data.canUpdate ? null : notice('info', data.cannotUpdate),
    planned,
    data.canUpdate && ctx.me.canChange ? el('div', { class: 'row-actions update-actions' },
      data.schedule ? button('Cancel the plan', false, '/service/updates/cancel') : button('Tonight at 22:00', false, '/service/updates/schedule', { at: '22:00' }),
      button('Update now', true, '/service/updates/run')) : null);
}

function idleCard(data, button) {
  const check = data.lastCheck;
  return el('section', { class: 'card stack', 'aria-labelledby': 'idle-h' },
    el('h2', { id: 'idle-h', class: 'update-title', text: `Hive BDC ${data.version}` }),
    data.canUpdate ? el('p', { class: 'muted', text: !check ? 'Hive BDC looks for updates every day at 06:00.' : check.ok ? `${data.version} is the newest version.` : check.message })
      : notice('info', data.cannotUpdate),
    data.canUpdate ? el('div', { class: 'row-actions' }, button('Look now', false, '/service/updates/check')) : null);
}

function stepList(rows) {
  const mark = (state) => {
    if (state === 'done') return el('span', { class: 'mark mark-done' }, icon('check'));
    if (state === 'failed') return el('span', { class: 'mark mark-failed', text: '×' });
    return el('span', { class: state === 'running' ? 'mark mark-running' : 'mark mark-waiting' });
  };
  return el('ol', { class: 'steplist' }, rows.map((r) => el('li', { class: `steprow steprow-${r.state}` }, mark(r.state),
    el('span', { class: 'stack-tight' }, el('strong', { text: r.title }), r.detail ? el('span', { class: 'small muted', text: r.detail }) : null),
    el('span', { class: r.state === 'running' ? 'small step-now-text' : 'small muted', text: r.state === 'running' ? 'now' : r.time }))));
}

function runningCard(data, zone) {
  const rows = stepRows(data, zone);
  for (const row of rows) {
    if (row.name === 'switch' && row.state === 'running') row.detail = 'About 2 to 4 minutes; the console is away for about 2 of them';
    if (row.name === 'running' && row.state === 'waiting') row.detail = 'The console reconnects by itself and shows the result';
  }
  const percent = progress(rows);
  const p = data.pending;
  return el('section', { class: 'card stack', 'aria-labelledby': 'run-h', 'aria-live': 'polite' },
    el('div', { class: 'section-head' },
      el('div', { class: 'stack-tight' }, el('h2', { id: 'run-h', class: 'update-title', text: p.kind === 'return' ? `Returning to ${p.to}` : `Updating to ${p.to}` }),
        el('span', { class: 'muted small', text: `Started ${whenText(p.startedAt, zone).slice(11)} by ${p.by}` })),
      p.kind === 'return' ? null : el('span', { class: 'update-percent', text: `${percent} %` })),
    p.kind === 'return' ? null : el('progress', { class: 'update-bar', max: 100, value: percent, 'aria-label': 'Progress' }),
    stepList(rows),
    el('p', { class: 'reconnect notice notice-info', hidden: true, text: 'Hive BDC is restarting. This page reconnects by itself.' }));
}

function whileCard(data) {
  const tip = (name, tone, text) => el('div', { class: 'tip' }, el('span', { class: `tip-icon tip-${tone}` }, icon(name)), el('span', { text }));
  return el('section', { class: 'card stack', 'aria-labelledby': 'while-h' }, el('h2', { id: 'while-h', text: 'While it runs' }),
    tip('instance', 'ok', 'Exports wait in Blob Storage, Service Bus or at the web address. Nothing is lost; they are taken right after the restart.'),
    tip('history', 'neutral', 'You can close this page. The update finishes on its own, and the history shows the result.'),
    data.pending.kind === 'return'
      ? tip('download', 'gold', `${data.pending.from} is removed once ${data.pending.to} runs; you can update to it again from the feed.`)
      : tip('download', 'gold', `${data.pending.from} stays in place. If ${data.pending.to} does not suit you, return to it with one click afterwards.`));
}

function stoppedCard(data, zone, last, button) {
  const rows = (data.steps || []).length ? stepRows(data, zone) : [];
  return el('section', { class: 'card stack', 'aria-labelledby': 'what-h' },
    el('div', { class: 'stack-tight' }, el('h2', { id: 'what-h', text: 'What happened' }),
      el('span', { class: 'muted small', text: `${whenText(last.at, zone)} · ${last.by}` })),
    rows.length ? stepList(rows) : el('p', { text: last.note }),
    el('p', { class: 'note', text: 'Other stops look the same: a download that breaks off, too little space, or a new version that does not answer within 10 minutes after the restart. In the last case Hive BDC returns to the version before by itself and says so here.' }),
    el('div', { class: 'row-actions update-actions' },
      el('button', { type: 'button', class: 'button view-ok', text: 'Copy details for Balsoft support', on: { click: () => navigator.clipboard.writeText(
        [`Hive BDC ${data.version}, ${last.version}: ${last.note}`, ...rows.map((r) => `${r.title}: ${r.state}${r.detail ? ` (${r.detail})` : ''}`)].join('\n')).catch(() => {}) } }),
      data.available && data.canUpdate ? button('Try again', true, '/service/updates/run') : null));
}

function checksCard(data, zone) {
  return el('section', { class: 'card stack', 'aria-labelledby': 'chk-h' },
    el('div', { class: 'stack-tight' }, el('h2', { id: 'chk-h', text: 'Checked after the restart' }),
      el('span', { class: 'muted small', text: `${data.installed.version} checks these itself before it calls the update done.` })),
    stepList((data.checks || []).map((c) => ({ state: c.state === 'done' ? 'done' : 'failed', title: c.name, detail: c.detail, time: whenText(c.at, zone).slice(11) }))));
}

function returnCard(data, run) {
  const box = el('div', { class: 'slot' });
  const ask = el('button', { type: 'button', class: 'button edit-only', text: `Return to ${data.previous.version}` });
  ask.addEventListener('click', () => {
    const yes = el('button', { type: 'button', class: 'button button-primary', text: `Return to ${data.previous.version}` });
    yes.addEventListener('click', () => run(yes, '/service/updates/return'));
    put(box, el('div', { class: 'confirm', role: 'alert' }, el('span', { text: `Return to ${data.previous.version}? Hive BDC restarts and is away for about 2 minutes.` }),
      el('span', { class: 'row-actions' }, el('button', { type: 'button', class: 'button', text: 'Keep', on: { click: () => put(box) } }), yes)));
  });
  return el('section', { class: 'card stack', 'aria-labelledby': 'back-h' },
    el('div', { class: 'inline' }, el('span', { class: 'tip-icon tip-gold' }, icon('history')), el('h2', { id: 'back-h', class: 'flush', text: `Return to ${data.previous.version}` })),
    el('p', { text: 'If something does not work as before, go back. It takes 2 to 4 minutes; documents wait in their source meanwhile.' }),
    el('p', { class: 'muted small', text: 'Your settings stay as they are. The backup taken before the update stays on the instance.' }),
    el('div', { class: 'row-actions' }, ask), box);
}

function installedCard(data, zone, button, withProblem) {
  const fact = (label, value) => [el('dt', { text: label }), el('dd', { text: value })];
  return el('section', { class: 'card stack', 'aria-labelledby': 'inst-h' }, el('h2', { id: 'inst-h', text: 'Installed' }),
    el('dl', { class: 'facts' },
      fact('Version', data.installed.version || data.version),
      data.installed.since ? fact('Since', whenText(data.installed.since, zone)) : null,
      fact('Looks for updates', 'every day at 06:00'),
      fact('Last looked', data.lastCheck ? whenText(data.lastCheck.at, zone) : 'not yet')),
    withProblem && data.lastCheck && !data.lastCheck.ok ? el('p', { class: 'warn-text small', text: data.lastCheck.message }) : null,
    el('div', { class: 'row-actions' }, button('Look now', false, '/service/updates/check')));
}

function historyCard(data, zone) {
  const rows = historyRows(data, zone);
  return el('section', { class: 'card history', 'aria-labelledby': 'hist-h' }, el('h2', { id: 'hist-h', text: 'Update history' }),
    rows.length === 0 ? el('p', { class: 'muted small', text: 'No update yet.' })
      : rows.slice(0, 8).map((r) => el('div', { class: 'history-row' },
        el('span', { class: 'history-top' }, el('strong', { text: r.version }), el('span', { class: `badge badge-${r.tone}`, text: r.badge })),
        el('span', { class: 'muted small', text: r.line }))));
}

/** The notice on the overview (board P1); null when no update is offered. */
export function updateNotice(data, ctx) {
  if (!updateBadge(data)) return null;
  const offer = data.available;
  const slot = el('div', { class: 'slot' });
  const go = async (b, path, body) => {
    b.disabled = true;
    try { await act(path, body); window.location.hash = '#/service/updates'; }
    catch (error) { put(slot, notice('error', error.message)); b.disabled = false; }
  };
  const button = (text, primary, path, body) => {
    const b = el('button', { type: 'button', class: primary ? 'button button-primary' : 'button', text });
    b.addEventListener('click', () => go(b, path, body));
    return b;
  };
  const lead = (offer.notes || [])[0];
  return [el('section', { class: 'banner banner-gold', 'aria-labelledby': 'upd-h' },
    el('span', { class: 'banner-icon' }, icon('download')),
    el('span', { class: 'banner-text' }, el('strong', { id: 'upd-h', text: `Hive BDC ${offer.version} is ready` }),
      el('span', {}, [lead, `Your settings are kept, and ${data.version} stays ready to return to.`].filter(Boolean).join(' ') + ' ',
        el('a', { href: '#/service/updates', text: 'What\'s new' }))),
    data.canUpdate && ctx.me.canChange ? el('span', { class: 'row-actions' },
      data.schedule ? el('span', { class: 'small', text: `Planned for ${whenText(data.schedule.at, data.timeZone).slice(11)}` }) : button('Tonight at 22:00', false, '/service/updates/schedule', { at: '22:00' }),
      button('Update now', true, '/service/updates/run')) : null), slot];
}
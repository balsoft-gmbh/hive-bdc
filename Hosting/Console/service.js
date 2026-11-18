// The Service page of the web console (board W13): whether the instance takes documents (Pause, Start, a test run),
// its licence, what it is (version, region, time zone, identity) and its technical log.
import { featureWords, germanDate, licenceNote, licenceWorks, sourceEntries, typeWords } from './logic.js';
import { runCard } from './setup.js';
import { api, chip, el, lock, notice, put } from './ui.js';
import { updateBadge } from './updatelogic.js';
import { serviceTabs, updatesPage } from './updates.js';

const ui = { onlyWarnings: false };
const warning = /\b(WRN|ERR|FTL|WARN|ERROR|FATAL|Warning|Error)\b/;

export async function servicePage(view, route, ctx) {
  if (route.parts[0] === 'updates') return updatesPage(view, route, ctx);
  const [identity, logs] = await Promise.all([api('/setup/identity'), api('/logs?tail=200')]);
  const config = ctx.config;
  const licence = (ctx.status && ctx.status.license) || {};
  const again = () => window.dispatchEvent(new HashChangeEvent('hashchange'));
  const message = el('div', { class: 'slot' });

  const paused = Boolean(config.paused);
  const flip = el('button', { type: 'button', class: paused ? 'button button-primary' : 'button', text: paused ? 'Start' : 'Pause', on: { click: async () => {
    flip.disabled = true;
    try { await api(paused ? '/setup/start' : '/setup/pause', { method: 'POST', body: '{}' }); again(); }
    catch (error) { put(message, notice('error', error.message)); flip.disabled = false; }
  } } });

  const entries = sourceEntries(config).filter((e) => e.company.source.kind === 'blob');
  const runResult = el('div', { class: 'slot' });
  const choice = el('select', { class: 'input', 'aria-label': 'Company for the test run' },
    entries.map((e, i) => el('option', { value: String(i), text: `${typeWords(e.type)} · company ${e.company.dataAreaId}` })));
  const run = el('button', { type: 'button', class: 'button', text: 'Run the test', disabled: entries.length === 0, on: { click: async () => {
    const entry = entries[Number(choice.value)];
    run.disabled = true;
    put(runResult, notice('info', 'The test run is going. This can take a minute.'));
    try {
      const answer = await api('/setup/test-run', { method: 'POST', body: JSON.stringify({ documentType: entry.type.id, company: entry.company.dataAreaId }) });
      put(runResult, runCard(entry, answer));
    } catch (error) {
      put(runResult, notice('error', error.status === 409 ? 'A test run is already going.' : error.message));
    } finally { run.disabled = false; }
  } } });

  const licenceResult = el('div', { class: 'slot' });
  const file = el('input', { type: 'file', accept: '.lic', id: 'service-lic' });
  file.addEventListener('change', async () => {
    const chosen = file.files && file.files[0];
    if (!chosen) return;
    try {
      const answer = await api('/license', { method: 'POST', body: JSON.stringify({ content: await chosen.text(), fileName: chosen.name }) });
      put(licenceResult, notice('info', answer.message || 'The licence is stored.'));
      again();
    } catch (error) { put(licenceResult, notice('error', error.message)); file.value = ''; }
  });

  const lines = logs.lines || [];
  const logBox = el('pre', { class: 'log' });
  const paintLog = () => {
    const shown = ui.onlyWarnings ? lines.filter((l) => warning.test(l)) : lines;
    logBox.textContent = shown.length ? shown.join('\n') : (logs.note || logs.error || 'Nothing in the log yet.');
    logBox.scrollTop = logBox.scrollHeight;
  };
  const onlyWarnings = el('input', { type: 'checkbox', checked: ui.onlyWarnings });
  onlyWarnings.addEventListener('change', () => { ui.onlyWarnings = onlyWarnings.checked; paintLog(); });
  const download = el('button', { type: 'button', class: 'button', text: 'Download today\'s log', on: { click: async () => {
    const all = await api('/logs?tail=5000');
    const link = el('a', { href: URL.createObjectURL(new Blob([(all.lines || []).join('\n')], { type: 'text/plain' })), download: all.file || 'hive-bdc.log' });
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 10000);
  } } });

  const copyId = el('button', { type: 'button', class: 'button view-ok', text: 'Copy', on: { click: () => navigator.clipboard.writeText(identity.objectId || '').catch(() => {}) } });
  const fact = (label, value, extra) => el('div', { class: 'cell' }, el('span', { class: 'cell-label', text: label }), el('strong', { class: 'fact', text: value || 'unknown' }), extra || null);

  put(view,
    el('div', { class: 'page-head' }, el('div', { class: 'eyebrow', text: 'SERVICE' }), el('h1', { text: 'This instance' }),
      el('p', { class: 'muted', text: 'Whether it takes documents, its licence, its version and what it writes into its log.' })),
    serviceTabs('status', updateBadge(ctx.updates)),
    message,
    el('div', { class: 'grid-2 grid-top' },
      el('section', { class: 'card stack', 'aria-labelledby': 'run-h' },
        el('div', { class: 'section-head' }, el('h2', { id: 'run-h', text: 'Taking documents' }),
          chip(!paused, paused ? (config.setupOpen ? 'Paused until the set-up is finished' : 'Paused') : 'Running')),
        el('p', { class: 'muted small', text: `Checks every source every ${config.pollSeconds || 30} seconds. Pause stops taking new documents; nothing is lost, they wait in their source until you start again.` }),
        el('div', { class: 'row-actions edit-only' }, flip),
        el('div', { class: 'inline edit-only' }, el('span', { class: 'field-label', text: 'Test run of the first waiting export of' }), choice, run),
        entries.length ? null : el('p', { class: 'muted small', text: 'A test run takes its export from a Blob container; no company reads one yet.' }),
        runResult),
      el('section', { class: 'card stack', 'aria-labelledby': 'lic-h' },
        el('div', { class: 'section-head' }, el('h2', { id: 'lic-h', text: 'Licence' }),
          chip(licenceWorks(licence), licenceWorks(licence) ? `Valid until ${germanDate(licence.expires)}` : (licence.state === 'Missing' ? 'No licence yet' : `Licence ${String(licence.state || 'unknown').toLowerCase()}`))),
        el('p', { class: 'muted small', text: [licence.customer, featureWords(licence.features), identity.tenantId ? `checked against tenant ${identity.tenantId.slice(0, 8)}` : ''].filter(Boolean).join(' · ') }),
        licenceNote(licence) || (!licenceWorks(licence) && licence.message) ? notice('warn', licenceNote(licence) || licence.message) : null,
        el('div', { class: 'inline edit-only' }, el('label', { for: 'service-lic', text: 'Renewal or another file:' }), file),
        licenceResult)),
    el('section', { class: 'card stack', 'aria-labelledby': 'inst-h' },
      el('h2', { id: 'inst-h', text: 'Version and identity' }),
      el('div', { class: 'grid-4 grid-top' },
        fact('Version', ctx.me.version),
        fact('Region', identity.region),
        fact('Time zone', identity.timeZone ? `${identity.timeZone} (from the installation)` : ''),
        fact('Identity for role commands', identity.objectId, identity.objectId ? copyId : null))),
    el('section', { class: 'card stack', 'aria-labelledby': 'log-h' },
      el('div', { class: 'section-head' }, el('h2', { id: 'log-h', text: 'Technical log' }),
        el('span', { class: 'row-actions view-ok' }, el('label', { class: 'inline-check' }, onlyWarnings, 'Only warnings and errors'), download)),
      logBox,
      el('span', { class: 'muted small', text: 'The last 200 lines; secrets never appear in the log. Azure keeps the full log under the app\'s Log stream.' })));
  paintLog();
  if (!ctx.me.canChange) lock(view);
}

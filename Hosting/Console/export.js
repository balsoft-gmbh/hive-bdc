// The Export pages of the web console (boards W7 to W11): the document types, one type, one company, one destination.
// All screens edit one draft of the settings; the Save bar on each screen lists its changes and saves it with the
// version it was read at, so a save by someone else in between shows board W6 instead of being overwritten.
import { postBody, typeWords } from './logic.js';
import * as X from './exportlogic.js';
import { api, chip, el, field, lock, notice, put, save, textInput } from './ui.js';

const state = { draft: null, original: null, version: null, savedAt: '', keys: new Map(), ui: {}, targets: [] };

function sync(config) {
  const fresh = postBody(config);
  const unchanged = !state.draft || X.draftChanges(state.original, state.draft).length === 0;
  if (unchanged && (state.version !== config.version || !state.draft)) {
    state.draft = fresh;
    state.original = structuredClone(fresh);
    state.version = config.version;
  }
}

const change = (fn) => { state.draft = fn(state.draft); };
const typeOf = (id) => state.draft.documentTypes.find((t) => t.id.toLowerCase() === String(id).toLowerCase());
const entryOf = (typeId, company) => {
  const t = typeOf(typeId);
  return t ? t.companies.find((c) => c.dataAreaId.toLowerCase() === String(company).toLowerCase()) : null;
};
const capital = (text) => text.charAt(0).toUpperCase() + text.slice(1);
const go = (hash) => { if (window.location.hash === hash) window.dispatchEvent(new HashChangeEvent('hashchange')); else window.location.hash = hash; };
const path = (...parts) => `#/export/${parts.map((p) => encodeURIComponent(p)).join('/')}`;

export async function exportPage(view, route, ctx) {
  sync(ctx.config);
  const [typeId, company, kind] = route.parts;
  const env = { view, ctx, again: () => exportPage(view, route, ctx) };
  if (kind) await destinationScreen(env, typeId, company, kind);
  else if (company) await companyScreen(env, typeId, company);
  else if (typeId) await typeScreen(env, typeId);
  else await listScreen(env);
  if (!ctx.me.canChange) lock(view);
}

// ── shared parts ─────────────────────────────────────────────────────────────────────

function crumbs(...items) {
  return el('nav', { class: 'crumbs', 'aria-label': 'Breadcrumb' },
    items.flatMap((item, i) => [i > 0 ? ' › ' : null, item.href ? el('a', { href: item.href, text: item.text }) : el('span', { text: item.text })]));
}

function badge() {
  return el('span', { class: 'badge-export', text: 'EXPORT' });
}

function quiet(text, onClick) {
  return el('button', { type: 'button', class: 'quiet edit-only', text, on: { click: onClick } });
}

function confirmBox(text, onRemove, onKeep) {
  return el('div', { class: 'confirm', role: 'alert' }, el('span', { text }),
    el('span', { class: 'row-actions' },
      el('button', { type: 'button', class: 'button', text: 'Keep', on: { click: onKeep } }),
      el('button', { type: 'button', class: 'button button-danger', text: 'Remove', on: { click: onRemove } })));
}

function toggle(label, on, onChange, disabled) {
  const box = el('input', { type: 'checkbox', class: 'switch', checked: on, disabled, 'aria-label': label });
  box.addEventListener('change', () => onChange(box.checked));
  return box;
}

function segmented(name, options, value, onChange) {
  return el('span', { class: 'seg', role: 'radiogroup' }, options.map(([v, text]) => {
    const input = el('input', { type: 'radio', name, value: v, checked: v === value });
    input.addEventListener('change', () => { if (input.checked) onChange(v); });
    return el('label', { class: 'seg-option' }, input, el('span', { text }));
  }));
}

function connectionSelect(kind, value, onChange, optional) {
  const choices = state.draft.connections.filter((c) => c.kind === kind);
  const select = el('select', { class: 'input' },
    optional || choices.length === 0 ? el('option', { value: '', text: choices.length ? '(none)' : 'No connection yet' }) : null,
    choices.map((c) => el('option', { value: c.id, selected: c.id === value, text: connectionLabel(c) })));
  if (!value && choices.length && !optional) queueMicrotask(() => onChange(choices[0].id));
  select.addEventListener('change', () => onChange(select.value));
  return select;
}

function connectionLabel(c) {
  const s = c.settings || {};
  const host = (url) => { try { return new URL(url).host.split('.')[0]; } catch { return url; } };
  const where = s.AccountUrl ? host(s.AccountUrl) : s.Namespace ? s.Namespace.split('.')[0] : s.Url ? s.Url.replace(/^https:\/\//, '') : '';
  return where ? `${c.id} (${where})` : c.id;
}

/** The Save bar: the changes of the draft, Discard, and Save once nothing blocks it. */
function saveBar(env) {
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

function savedChip() {
  return state.savedAt && X.draftChanges(state.original, state.draft).length === 0 ? el('span', { class: 'chip chip-ok', text: `Saved ${state.savedAt}` }) : null;
}

// ── W7: the document types ──────────────────────────────────────────────────────────

async function listScreen(env) {
  const [targets, attention] = await Promise.all([api('/targets'), api('/history/attention?limit=200')]);
  const descriptors = targets.targets || [];
  state.targets = descriptors;
  const { rows, folded } = X.typeRows(state.draft, attention.events, descriptors);
  const ui = state.ui;
  const bar = saveBar(env);
  const rerender = () => env.again();

  const card = (row) => {
    const sub = [row.displayName, row.handler === 'EInvoice' ? 'e-invoice' : 'PDF', row.party === 'Vendor' ? 'to vendors' : 'to customers'].filter(Boolean).join(' · ');
    const remove = ui.confirmType === row.id
      ? confirmBox(`Remove ${row.name.toLowerCase()}? An export F&O still sends for it then goes to error/ instead of being added again.`, () => {
        try { change((d) => X.removeType(d, row.id)); ui.confirmType = null; rerender(); } catch (error) { ui.confirmType = null; ui.error = error.message; rerender(); }
      }, () => { ui.confirmType = null; rerender(); })
      : null;
    const waiting = row.waiting.map((w) => el('div', { class: 'waiting' },
      el('span', { text: `${w.count} ${w.count === 1 ? 'export' : 'exports'} of company ${w.company} ${w.count === 1 ? 'is' : 'are'} waiting. Add the company and choose where ${typeWords({ id: row.id, displayName: row.displayName })} go, then Hive BDC takes ${w.count === 1 ? 'it' : 'them'}.` }),
      el('button', { type: 'button', class: 'button edit-only', text: `Add company ${w.company}`, on: { click: () => { change((d) => X.addCompany(d, row.id, w.company)); go(path(row.id, w.company)); } } })));
    return el('article', { class: row.waiting.length ? 'card type-card type-card-warn' : 'card type-card' },
      el('div', { class: 'type-row' },
        el('span', { class: 'type-name' }, el('a', { href: path(row.id), text: `${row.name} ›` }), el('span', { class: 'muted small', text: sub })),
        el('span', { class: 'cell' }, el('span', { class: 'cell-label', text: 'Companies' }), row.companies.length ? row.companies.join(', ') : 'none'),
        el('span', { class: 'cell' }, el('span', { class: 'cell-label', text: 'Goes to' }), row.goesTo),
        el('span', { class: 'row-actions' }, chip(row.enabled, row.enabled ? 'On' : 'Off'), quiet('Remove', () => { ui.confirmType = row.id; rerender(); }))),
      waiting, remove);
  };

  const addForm = ui.addingType ? newTypeForm(rerender) : null;
  put(env.view,
    el('div', { class: 'page-head-row' },
      el('div', { class: 'stack-tight' }, el('span', { class: 'badge-export', text: 'EXPORT · F&O TO YOUR SYSTEMS' }),
        el('h1', { text: 'Document types' }), el('p', { class: 'muted', text: 'What Hive BDC takes from F&O and where each type goes. Open one to change it.' })),
      ui.addingType ? null : el('button', { type: 'button', class: 'button button-primary button-big edit-only', text: '+ New document type', on: { click: () => { ui.addingType = true; rerender(); } } })),
    ui.error ? notice('error', ui.error) : null,
    addForm,
    el('section', { class: 'stack', 'aria-label': 'Document types' }, rows.map(card)),
    folded.length ? el('p', { class: 'muted small' },
      `${capital(folded.map((r) => r.name.toLowerCase()).join(', '))} ${folded.length === 1 ? 'is' : 'are'} off and ${folded.length === 1 ? 'has' : 'have'} no company. `,
      ui.showFolded ? null : el('button', { type: 'button', class: 'quiet', text: 'Show them', on: { click: () => { ui.showFolded = true; rerender(); } } })) : null,
    ui.showFolded ? el('section', { class: 'stack' }, folded.map(card)) : null,
    bar.node);
  ui.error = null;
}

function newTypeForm(rerender) {
  const id = textInput({ placeholder: 'DeliveryNote' });
  const name = textInput({ placeholder: 'Lieferschein' });
  const handler = el('select', { class: 'input' }, el('option', { value: 'Pdf', text: 'PDF as it is' }), el('option', { value: 'EInvoice', text: 'E-invoice' }));
  const party = el('select', { class: 'input' }, el('option', { value: 'Customer', text: 'Customers' }), el('option', { value: 'Vendor', text: 'Vendors' }));
  const message = el('div', { class: 'slot' });
  return el('section', { class: 'card stack' },
    el('h2', { text: 'New document type' }),
    el('div', { class: 'grid-4' }, field('Id in the export (documentType)', id), field('Name in the ERP', name), field('Made into', handler), field('Sent to', party)),
    message,
    el('div', { class: 'row-actions' },
      el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { state.ui.addingType = false; rerender(); } } }),
      el('button', { type: 'button', class: 'button button-primary', text: 'Add', on: { click: () => {
        try { change((d) => X.addType(d, id.value, name.value.trim(), handler.value, party.value)); state.ui.addingType = false; go(path(id.value.trim())); }
        catch (error) { put(message, notice('error', error.message)); }
      } } })));
}

// ── W8: one document type ───────────────────────────────────────────────────────────

async function typeScreen(env, typeId) {
  const type = typeOf(typeId);
  if (!type) { put(env.view, crumbs({ href: '#/export', text: 'Export' }), notice('error', `There is no document type ${typeId}.`)); return; }
  const targets = await api('/targets');
  const descriptors = targets.targets || [];
  state.targets = descriptors;
  const ui = state.ui;
  const bar = saveBar(env);
  const rerender = () => env.again();
  const name = capital(typeWords(type));
  const set = (changes) => { change((d) => X.setType(d, type.id, changes)); bar.paint(); };

  const nameInput = textInput({ value: type.displayName || '' });
  nameInput.addEventListener('change', () => set({ displayName: nameInput.value.trim() }));

  const companyRow = (c) => {
    if (!c.source && !c.input) {
      return el('div', { class: 'co-row co-row-new' },
        el('strong', { text: c.dataAreaId }),
        el('span', { class: 'warn-text', text: 'Added just now. Choose where its documents come from and where they go.' }),
        el('a', { class: 'button button-primary', href: path(type.id, c.dataAreaId), text: 'Set it up' }));
    }
    return el('div', { class: 'co-wrap' },
      el('div', { class: 'co-row' },
        el('a', { class: 'co-name', href: path(type.id, c.dataAreaId), text: `${c.dataAreaId} ›` }),
        el('span', { class: 'cell' }, el('span', { class: 'cell-label', text: 'Comes from' }), X.sourceWords(c.source)),
        el('span', { class: 'cell' }, el('span', { class: 'cell-label', text: 'Goes to' }), X.destinationWords(c, state.draft, descriptors)),
        el('span', { class: c.mail ? 'ok-text' : 'muted', text: c.mail ? 'Mail on' : 'Mail off' }),
        quiet('Remove', () => { ui.confirmCompany = c.dataAreaId; rerender(); })),
      ui.confirmCompany === c.dataAreaId ? confirmBox(`Remove company ${c.dataAreaId} from ${name.toLowerCase()}? Documents still waiting in its source stay there.`,
        () => { change((d) => X.removeCompany(d, type.id, c.dataAreaId)); ui.confirmCompany = null; rerender(); },
        () => { ui.confirmCompany = null; rerender(); }) : null);
  };

  const addCompanyForm = () => {
    const input = textInput({ placeholder: 'dataAreaId, for example 2000' });
    const message = el('div', { class: 'slot' });
    return el('div', { class: 'stack-tight' }, el('div', { class: 'inline' }, input,
      el('button', { type: 'button', class: 'button button-primary', text: 'Add', on: { click: () => {
        try { change((d) => X.addCompany(d, type.id, input.value)); ui.addingCompany = false; rerender(); }
        catch (error) { put(message, notice('error', error.message)); }
      } } }),
      el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { ui.addingCompany = false; rerender(); } } })), message);
  };

  put(env.view,
    crumbs({ href: '#/export', text: 'Export' }, { text: name }),
    el('div', { class: 'page-head-row' }, el('span', { class: 'inline' }, el('h1', { text: name }), badge()), savedChip()),
    el('section', { class: 'card stack', 'aria-labelledby': 'type-h' },
      el('h2', { id: 'type-h', text: 'This document type' }),
      el('div', { class: 'grid-4' },
        field('Name in the ERP', nameInput),
        el('div', { class: 'field' }, el('span', { class: 'field-label', text: 'Made into' }),
          segmented('handler', [['EInvoice', 'E-invoice'], ['Pdf', 'PDF as it is']], type.handler, (v) => set({ handler: v }))),
        el('div', { class: 'field' }, el('span', { class: 'field-label', text: 'Sent to' }),
          segmented('party', [['Customer', 'Customers'], ['Vendor', 'Vendors']], type.party, (v) => set({ party: v }))),
        el('label', { class: 'inline-check switch-field' }, 'On', toggle('On', type.enabled !== false, (on) => set({ enabled: on })))),
      el('p', { class: 'muted small', text: type.handler === 'EInvoice'
        ? 'Hive BDC makes XRechnung or ZUGFeRD, as each export says. Off: exports of this type wait in their source.'
        : 'The PDF goes out as F&O made it. Off: exports of this type wait in their source.' })),
    el('section', { class: 'card stack', 'aria-labelledby': 'co-h' },
      el('div', { class: 'section-head' }, el('h2', { id: 'co-h', text: 'Companies' }),
        ui.addingCompany ? null : el('button', { type: 'button', class: 'button edit-only', text: '+ Add company', on: { click: () => { ui.addingCompany = true; rerender(); } } })),
      ui.addingCompany ? addCompanyForm() : null,
      type.companies.length ? el('div', { class: 'rows' }, type.companies.map(companyRow)) : el('p', { class: 'muted', text: 'No company yet.' })),
    bar.node);
}

// ── W9 and W10: one company ─────────────────────────────────────────────────────────

async function companyScreen(env, typeId, company) {
  const type = typeOf(typeId);
  const entry = entryOf(typeId, company);
  if (!type || !entry) { put(env.view, crumbs({ href: '#/export', text: 'Export' }), notice('error', `There is no company ${company} for ${typeId}.`)); return; }
  const [targets, identity] = await Promise.all([api('/targets'), api('/setup/identity')]);
  const descriptors = targets.targets || [];
  state.targets = descriptors;
  const ui = state.ui;
  const bar = saveBar(env);
  const rerender = () => env.again();
  const name = capital(typeWords(type));
  const key = `${type.id}|${entry.dataAreaId}`;
  const kind = (ui.sourceKind && ui.sourceKind[key]) || (entry.source && entry.source.kind) || 'blob';
  const settings = { ...((entry.source && entry.source.kind === kind && entry.source.settings) || {}) };
  let connection = (entry.source && entry.source.kind === kind && entry.source.connection) || '';
  const apply = () => {
    change((d) => X.setSource(d, type.id, entry.dataAreaId, { kind, connection, settings }, identity.inputRoot));
    bar.paint();
  };

  const tile = (value, title, text) => {
    const input = el('input', { type: 'radio', name: 'source', value, checked: value === kind });
    input.addEventListener('change', () => { ui.sourceKind = { ...(ui.sourceKind || {}), [key]: value }; rerender(); });
    return el('label', { class: 'tile' }, input, el('span', { class: 'tile-text' }, el('span', { class: 'tile-title', text: title }), el('span', { class: 'tile-note', text })));
  };

  const checkLine = el('div', { class: 'stack-tight', 'aria-live': 'polite' });
  const showCheck = (answer) => {
    put(checkLine, chip(Boolean(answer.ok), answer.message),
      !answer.ok && answer.command ? el('div', { class: 'code' }, el('code', { class: 'code-text', text: answer.command }),
        el('button', { type: 'button', class: 'button button-dark', text: 'Copy', on: { click: () => navigator.clipboard.writeText(answer.command).catch(() => {}) } })) : null);
  };
  const runCheck = async (url, body) => {
    put(checkLine, el('span', { class: 'muted', text: 'Checking' }));
    try { showCheck(await api(url, { method: 'POST', body: JSON.stringify(body) })); }
    catch (error) { showCheck({ ok: false, message: error.message }); }
  };
  const input = (setting, attrs) => {
    const box = textInput({ value: settings[setting] || '', ...attrs });
    box.addEventListener('change', () => { settings[setting] = box.value.trim(); apply(); });
    return box;
  };
  const noConnection = (what) => el('p', { class: 'warn-text', text: `There is no ${what} connection yet. Add one under Connections, then come back.` });

  let form;
  if (kind === 'blob') {
    const hasAny = state.draft.connections.some((c) => c.kind === 'azure-storage');
    form = el('div', { class: 'stack' },
      hasAny ? null : noConnection('Azure Storage'),
      el('div', { class: 'grid-3' },
        field('Connection', connectionSelect('azure-storage', connection, (v) => { connection = v; apply(); })),
        field('Container', input('Container', { placeholder: 'bdc-export' })),
        field('Folder in the container (optional)', input('Prefix', { placeholder: 'empty: the whole container' }))),
      el('div', { class: 'inline edit-only' }, el('button', { type: 'button', class: 'button', text: 'Check', on: { click: () => runCheck('/setup/check-source', { connection, container: settings.Container || '', prefix: settings.Prefix || '' }) } }), checkLine),
      el('p', { class: 'note', text: 'F&O writes each export and its PDF into this container. Hive BDC moves both to processed/ or error/ when it is done.' }));
  } else if (kind === 'servicebus') {
    const topicMode = (ui.topic && ui.topic[key]) ?? Boolean(settings.Topic);
    const hasAny = state.draft.connections.some((c) => c.kind === 'servicebus');
    form = el('div', { class: 'stack' },
      hasAny ? null : noConnection('Service Bus'),
      el('div', { class: 'grid-3' },
        field('Connection', connectionSelect('servicebus', connection, (v) => { connection = v; apply(); })),
        el('div', { class: 'field' }, el('span', { class: 'field-label', text: 'Reads from' }),
          segmented('sbk', [['queue', 'A queue'], ['topic', 'A topic']], topicMode ? 'topic' : 'queue', (v) => {
            ui.topic = { ...(ui.topic || {}), [key]: v === 'topic' };
            if (v === 'topic') delete settings.Queue; else { delete settings.Topic; delete settings.Subscription; }
            apply();
            rerender();
          })),
        topicMode
          ? el('div', { class: 'grid-2' }, field('Topic', input('Topic')), field('Subscription', input('Subscription')))
          : field('Queue', input('Queue', { placeholder: 'bdc-export' }))),
      el('div', { class: 'grid-2' },
        field('PDFs are read from', connectionSelect('azure-storage', settings.PdfConnection || '', (v) => { settings.PdfConnection = v; apply(); }, true)),
        field('Container', input('PdfContainer', { placeholder: 'bdc-pdf' }))),
      el('div', { class: 'inline edit-only' }, el('button', { type: 'button', class: 'button', text: 'Check', on: { click: () => runCheck('/setup/check-queue', { connection, queue: settings.Queue || '', topic: settings.Topic || '', subscription: settings.Subscription || '' }) } }), checkLine),
      el('p', { class: 'note', text: 'F&O sends each export as a message and names its PDF in the container. A message that cannot be handled goes to the dead-letter part with the reason.' }));
  } else {
    const id = (entry.source && entry.source.kind === 'https' && entry.source.connection) || X.httpsConnectionId(type.id, entry.dataAreaId);
    const address = `${window.location.origin}/inbound/${encodeURIComponent(type.id)}/${encodeURIComponent(entry.dataAreaId)}`;
    const shown = state.keys.get(id);
    const stored = (env.ctx.config.connections || []).some((c) => c.id === id && c.hasSecret);
    const makeKey = () => {
      const bytes = crypto.getRandomValues(new Uint8Array(24));
      const keyText = btoa(String.fromCharCode(...bytes)).replace(/\+/g, 'A').replace(/\//g, 'B').replace(/=+$/, '');
      state.keys.set(id, keyText);
      change((d) => X.setHttpsKey(d, type.id, entry.dataAreaId, keyText, identity.inputRoot, id));
      rerender();
    };
    if (!connection) connection = id;
    form = el('div', { class: 'stack' },
      el('div', { class: 'field' }, el('span', { class: 'field-label', text: 'Address the sender posts to' }),
        el('div', { class: 'code code-light' }, el('code', { class: 'code-text', text: address }),
          el('button', { type: 'button', class: 'button', text: 'Copy', on: { click: () => navigator.clipboard.writeText(address).catch(() => {}) } }))),
      shown
        ? el('div', { class: 'keybox' },
          el('strong', { text: `New key for ${entry.dataAreaId}, shown only now` }),
          el('div', { class: 'code' }, el('code', { class: 'code-text', text: `x-api-key: ${shown}` }),
            el('button', { type: 'button', class: 'button button-dark', text: 'Copy', on: { click: () => navigator.clipboard.writeText(shown).catch(() => {}) } })),
          el('span', { text: 'Put it into the sender now. It goes to this instance\'s Key Vault when you save and is never shown again. A lost key is replaced, not looked up.' }))
        : el('p', { class: 'muted', text: stored ? 'A key is stored for this address. It is never shown again.' : 'No key yet. Create one; the sender sends it in the header x-api-key.' }),
      el('div', { class: 'inline edit-only' },
        el('button', { type: 'button', class: 'button', text: shown || stored ? 'Replace the key' : 'Create a key', on: { click: makeKey } }),
        stored && !shown ? el('span', { class: 'muted small', text: 'The old key stops working when you save.' }) : null),
      el('div', { class: 'grid-2 small muted' },
        el('div', { class: 'stack-tight' }, el('strong', { text: 'What the sender posts' }), el('span', { text: 'JSON with the ERP export as "document" and the PDF as Base64 in "pdf". A repeat of the same document number is answered "already received".' })),
        el('div', { class: 'stack-tight' }, el('strong', { text: 'Answers' }), el('span', { text: '202 taken · 200 already received · 401 wrong key · 413 over 50 MB. Nothing is stored on a refusal.' }))));
  }

  const rows = X.destinationRows(entry, descriptors);
  const destinationRow = (row) => el('div', { class: 'co-wrap' },
    el('div', { class: 'dest-row' },
      el('span', { class: 'stack-tight' },
        row.known ? el('a', { class: 'dest-name', href: path(type.id, entry.dataAreaId, row.kind), text: `${row.name} ›` }) : el('strong', { text: row.name }),
        el('span', { class: 'muted small', text: row.summary || 'Not set up yet' })),
      row.known ? toggle(`${row.name} on`, row.enabled, (on) => { change((d) => X.setDestinationEnabled(d, type.id, entry.dataAreaId, row.kind, on)); bar.paint(); }) : el('span'),
      quiet('Remove', () => { ui.confirmDest = `${key}|${row.kind}`; rerender(); })),
    ui.confirmDest === `${key}|${row.kind}` ? confirmBox(`Remove ${row.name}? Documents already delivered stay there.`,
      () => { change((d) => X.removeDestination(d, type.id, entry.dataAreaId, row.kind)); ui.confirmDest = null; rerender(); },
      () => { ui.confirmDest = null; rerender(); }) : null);

  const mailReady = Boolean(state.draft.mail && state.draft.mail.enabled && state.draft.mail.from);
  const present = new Set(rows.map((r) => r.kind));
  const picker = ui.picking === key ? el('div', { class: 'tiles tiles-auto' }, descriptors.filter((d) => !present.has(d.kind)).map((d) =>
    el('button', { type: 'button', class: 'tile tile-button', on: { click: () => {
      const conn = state.draft.connections.find((c) => c.kind === d.connectionKind);
      change((dr) => X.addDestination(dr, type.id, entry.dataAreaId, d.kind, conn ? conn.id : ''));
      ui.picking = null;
      go(path(type.id, entry.dataAreaId, d.kind));
    } } }, el('span', { class: 'tile-title', text: d.name }), el('span', { class: 'tile-note', text: d.description || '' })))) : null;

  const goes = X.destinationWords(entry, state.draft, descriptors);
  put(env.view,
    crumbs({ href: '#/export', text: 'Export' }, { href: path(type.id), text: name }, { text: `Company ${entry.dataAreaId}` }),
    el('div', { class: 'page-head-row' },
      el('h1', { text: `${name} of company ${entry.dataAreaId}` }),
      el('span', { class: 'dir', 'aria-label': 'Direction' }, badge(),
        el('span', { class: 'dir-box', text: kind === 'https' ? 'Web address' : 'F&O' }), el('span', { class: 'dir-arrow', 'aria-hidden': 'true', text: '→' }),
        el('span', { class: 'dir-box dir-box-dark', text: type.handler === 'EInvoice' ? 'E-invoice' : 'PDF' }), el('span', { class: 'dir-arrow', 'aria-hidden': 'true', text: '→' }),
        el('span', { class: 'dir-box', text: goes }))),
    el('section', { class: 'card stack', 'aria-labelledby': 'from-h' },
      el('h2', { id: 'from-h', text: 'Where documents come from' }),
      el('fieldset', { class: 'tiles tiles-3' }, el('legend', { class: 'sr-only', text: 'Source' }),
        tile('blob', 'Azure Blob Storage', 'F&O writes into a container'),
        tile('servicebus', 'Azure Service Bus', 'F&O sends a message'),
        tile('https', 'Web address (HTTPS)', 'Power Automate, Logic App, ERP')),
      form),
    el('section', { class: 'card stack', 'aria-labelledby': 'to-h' },
      el('div', { class: 'section-head' }, el('h2', { id: 'to-h', text: 'Where they go' }),
        el('button', { type: 'button', class: 'button edit-only', text: ui.picking === key ? 'Close' : '+ Add a destination', on: { click: () => { ui.picking = ui.picking === key ? null : key; rerender(); } } })),
      picker,
      el('div', { class: 'rows' }, rows.map(destinationRow),
        el('div', { class: 'dest-row' },
          el('span', { class: 'stack-tight' }, el('strong', { text: 'Send each document by mail' }),
            el('span', { class: 'muted small', text: mailReady ? `To the address in the export, from ${state.draft.mail.from}` : 'Set up the mailbox under Connections first.' })),
          toggle('Mail on', entry.mail === true, (on) => { change((d) => X.setMail(d, type.id, entry.dataAreaId, on)); bar.paint(); }, !mailReady && entry.mail !== true),
          el('span')))),
    bar.node);
  if (!entry.source || entry.source.kind !== kind) apply();
}

// ── W11: one destination ────────────────────────────────────────────────────────────

async function destinationScreen(env, typeId, company, kind) {
  const type = typeOf(typeId);
  const entry = entryOf(typeId, company);
  if (!type || !entry) { put(env.view, crumbs({ href: '#/export', text: 'Export' }), notice('error', `There is no company ${company} for ${typeId}.`)); return; }
  const [targets, sample] = await Promise.all([api('/targets'), api(`/export/sample?type=${encodeURIComponent(type.id)}&company=${encodeURIComponent(entry.dataAreaId)}`)]);
  state.targets = targets.targets || [];
  const d = state.targets.find((x) => x.kind === kind);
  const name = capital(typeWords(type));
  const back = path(type.id, entry.dataAreaId);
  const trail = crumbs({ href: '#/export', text: 'Export' }, { href: path(type.id), text: name }, { href: back, text: `Company ${entry.dataAreaId}` }, { text: d ? d.name : kind });
  if (!d) { put(env.view, trail, notice('warn', 'This destination was set up with a newer version of Hive BDC and is kept as it is.')); return; }

  const ui = state.ui;
  const bar = saveBar(env);
  const form = X.destinationForm(entry, kind);
  const rowsKey = `${type.id}|${entry.dataAreaId}|${kind}`;
  if (!ui.rows || ui.rowsKey !== rowsKey) { ui.rows = Object.entries(form.fields).map(([f, t]) => ({ field: f, template: t })); ui.rowsKey = rowsKey; }
  const fields = sample.fields || {};
  const apply = () => {
    form.fields = Object.fromEntries(ui.rows.filter((r) => r.field && r.template).map((r) => [r.field, r.template]));
    change((dr) => X.applyDestination(dr, type.id, entry.dataAreaId, form));
    bar.paint();
  };
  const request = (lookup) => ({ kind, lookup, connection: form.connection, settings: form.settings });
  const connectionField = d.connectionKind && d.connectionKind !== 'sharepoint'
    ? field('Connection', connectionSelect(d.connectionKind, form.connection, (v) => {
      form.connection = v;
      apply();
      for (const s of d.settings.filter((x) => x.lookup)) loadChoices(s);
      if (d.fieldsLookup) loadFields();
    }))
    : null;

  const folderLine = el('span', { class: 'muted small' });
  const paintFolder = () => {
    if (kind !== 'sharepoint') return;
    const number = fields.documentNumber || fields.DocumentNumber || 'document';
    const place = [form.settings.Library, X.folderPreview(form.settings.Folder, fields)].filter(Boolean).join('/');
    put(folderLine, Object.keys(fields).length ? `With the last document: ${place}/${number}.pdf` : sample.message);
  };

  const settingControls = d.settings.map((s) => {
    const hint = el('span', { class: 'field-hint' });
    let control;
    if (s.lookup) {
      control = el('select', { class: 'input' }, el('option', { value: form.settings[s.key] || '', text: form.settings[`${s.key}Name`] || form.settings[s.key] || (s.required === false ? '(none)' : 'Choose') }));
      control.addEventListener('change', () => {
        form.settings[s.key] = control.value;
        const label = control.selectedOptions[0] ? control.selectedOptions[0].textContent : '';
        if (label && label !== control.value) form.settings[`${s.key}Name`] = label;
        apply();
        paintFolder();
        for (const other of d.settings.filter((x) => x.dependsOn === s.key)) loadChoices(other);
        if (d.fieldsLookup) loadFields();
      });
    } else {
      control = textInput({ value: form.settings[s.key] || '' });
      control.addEventListener('input', () => { form.settings[s.key] = control.value.trim(); paintFolder(); });
      control.addEventListener('change', () => {
        form.settings[s.key] = control.value.trim();
        apply();
        for (const other of d.settings.filter((x) => x.dependsOn === s.key)) loadChoices(other);
      });
      if (s.hint) hint.textContent = s.hint;
    }
    s.control = control;
    s.hintSlot = hint;
    return el('div', { class: 'stack-tight' }, field(s.label, control), hint);
  });

  async function loadChoices(s) {
    if (!s.lookup) return;
    if (s.dependsOn && !String(form.settings[s.dependsOn] || '').trim()) { s.hintSlot.textContent = 'Fill in the field before first.'; return; }
    s.hintSlot.textContent = 'Loading';
    try {
      const answer = await api('/targets/choices', { method: 'POST', body: JSON.stringify(request(s.lookup)) });
      if (!answer.ok) throw new Error(answer.message);
      const current = form.settings[s.key] || '';
      put(s.control, el('option', { value: '', text: s.required === false ? '(none)' : 'Choose' }),
        answer.choices.map((c) => el('option', { value: c.value, selected: c.value === current, text: c.detail ? `${c.label} (${c.detail})` : c.label })));
      s.hintSlot.textContent = '';
    } catch (error) {
      s.hintSlot.textContent = error.message;
      s.hintSlot.className = 'field-hint warn-text';
    }
  }

  let fieldChoices = [];
  const fieldsSlot = el('div', { class: 'rows' });
  const testLine = el('div', { 'aria-live': 'polite' });
  const choices = X.templateChoices(fields);

  function mapRow(row, i) {
    const known = fieldChoices.some((c) => c.value === row.field);
    const fieldSelect = el('select', { class: known || !row.field || fieldChoices.length === 0 ? 'input' : 'input input-bad' },
      el('option', { value: '', text: 'Choose' }),
      row.field && !known ? el('option', { value: row.field, selected: true, text: row.field }) : null,
      fieldChoices.map((c) => el('option', { value: c.value, selected: c.value === row.field, text: c.label })));
    fieldSelect.addEventListener('change', () => { row.field = fieldSelect.value; apply(); paintRows(); });
    const custom = row.template && !choices.some((c) => c.value === row.template);
    const templateSelect = el('select', { class: 'input' },
      el('option', { value: '', text: 'Choose' }),
      choices.map((c) => el('option', { value: c.value, selected: c.value === row.template, text: c.label })),
      el('option', { value: '__custom', selected: custom, text: 'Own text with {fields}' }));
    const own = textInput({ value: row.template || '', placeholder: '{documentNumber} {issueDate:yyyy}', hidden: !custom });
    templateSelect.addEventListener('change', () => {
      if (templateSelect.value === '__custom') { own.hidden = false; own.focus(); return; }
      row.template = templateSelect.value;
      own.hidden = true;
      apply();
      paintRows();
    });
    own.addEventListener('change', () => { row.template = own.value.trim(); apply(); paintRows(); });
    const value = row.field && !known && fieldChoices.length
      ? el('span', { class: 'warn-text small', text: `${d.kind === 'sharepoint' ? 'The library' : d.name} has no field "${row.field}" yet` })
      : el('span', { text: Object.keys(fields).length ? X.templatePreview(row.template, fields) || '(empty)' : '' });
    return el('div', { class: 'map-row' }, fieldSelect, el('span', { class: 'muted', 'aria-hidden': 'true', text: '←' }),
      el('span', { class: 'stack-tight' }, templateSelect, own), value,
      quiet('Remove', () => { ui.rows.splice(i, 1); apply(); paintRows(); }));
  }

  function paintRows() {
    put(fieldsSlot,
      el('div', { class: 'map-row map-head' }, el('span', { text: d.kind === 'sharepoint' ? 'Column' : 'Field' }), el('span'), el('span', { text: 'Filled from the document' }), el('span', { text: 'Last document' }), el('span')),
      ui.rows.map(mapRow));
    if (!env.ctx.me.canChange) lock(fieldsSlot);
  }

  async function loadFields() {
    if (!d.fieldsLookup) return null;
    try {
      const answer = await api('/targets/choices', { method: 'POST', body: JSON.stringify(request(d.fieldsLookup)) });
      if (!answer.ok) throw new Error(answer.message);
      fieldChoices = answer.choices;
      paintRows();
      return null;
    } catch (error) {
      fieldChoices = [];
      paintRows();
      return error.message;
    }
  }

  const test = async () => {
    put(testLine, el('span', { class: 'muted', text: 'Signing in' }));
    const failed = await loadFields();
    if (failed) { put(testLine, chip(false, failed)); return; }
    const missing = ui.rows.filter((r) => r.field && !fieldChoices.some((c) => c.value === r.field)).map((r) => r.field);
    put(testLine, chip(missing.length === 0, missing.length === 0
      ? `Signed in. ${fieldChoices.length} fields found, every mapped one exists.`
      : `Signed in, but ${missing.map((m) => `"${m}"`).join(', ')} ${missing.length === 1 ? 'does' : 'do'} not exist there yet.`));
  };

  put(env.view,
    trail,
    el('div', { class: 'page-head-row' }, el('h1', { text: d.name }),
      el('span', { class: 'inline' }, chip(Boolean(d.connected), d.connected ? 'Connected' : 'Not connected yet: set it up under Connections'),
        el('label', { class: 'inline-check' }, 'On', toggle('On', form.enabled, (on) => { form.enabled = on; apply(); })))),
    d.settings.length || connectionField ? el('section', { class: 'card stack', 'aria-labelledby': 'where-h' },
      el('h2', { id: 'where-h', text: kind === 'sharepoint' ? 'Where the file goes' : 'Settings' }),
      el('div', { class: 'grid-3' }, connectionField, settingControls), folderLine) : null,
    d.fieldsLookup ? el('section', { class: 'card stack', 'aria-labelledby': 'col-h' },
      el('div', { class: 'section-head' },
        el('div', { class: 'stack-tight' }, el('h2', { id: 'col-h', text: kind === 'sharepoint' ? 'Columns of the library' : `Fields of ${d.name}` }),
          el('span', { class: 'muted small', text: Object.keys(fields).length ? 'Pick a field and what fills it. The right side shows the value from the last document.' : sample.message })),
        el('button', { type: 'button', class: 'button edit-only', text: '+ Add a column', on: { click: () => { ui.rows.push({ field: '', template: '' }); paintRows(); } } })),
      fieldsSlot,
      el('div', { class: 'inline edit-only' }, el('button', { type: 'button', class: 'button', text: 'Test', on: { click: test } }),
        el('span', { class: 'muted small', text: 'Signs in, reads the fields and checks that every mapped one exists. Nothing is sent.' })),
      testLine) : null,
    el('div', { class: 'inline edit-only' }, quiet('Remove this destination', () => { ui.confirmDest = `${rowsKey}`; env.again(); })),
    ui.confirmDest === rowsKey ? confirmBox(`Remove ${d.name} for company ${entry.dataAreaId}? Documents already delivered stay there.`,
      () => { change((dr) => X.removeDestination(dr, type.id, entry.dataAreaId, kind)); ui.confirmDest = null; ui.rows = null; go(back); },
      () => { ui.confirmDest = null; env.again(); }) : null,
    bar.node);
  paintFolder();
  paintRows();
  for (const s of d.settings.filter((x) => x.lookup)) loadChoices(s);
  loadFields();
}

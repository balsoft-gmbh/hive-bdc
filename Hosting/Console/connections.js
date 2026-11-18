// The Connections page of the web console (board W12): every sign-in Hive BDC uses, the SharePoint app and the document
// mailbox, each with its state, who uses it, Test, Edit and Remove. Changes go into the one draft of draft.js; passwords,
// keys and secrets are sent only with Save, into the instance's Key Vault, and never read back.
import * as X from './exportlogic.js';
import { change, confirmBox, quiet, saveBar, state, sync, toggle } from './draft.js';
import { api, chip, el, field, lock, notice, put, textInput } from './ui.js';

export async function connectionsPage(view, route, ctx) {
  sync(ctx.config);
  const env = { view, ctx, again: () => connectionsPage(view, route, ctx) };
  const [kinds, identity, targets] = await Promise.all([api('/connections/kinds'), api('/setup/identity'), api('/targets')]);
  state.targets = targets.targets || [];
  const ui = state.ui;
  ui.tests = ui.tests || {};
  const bar = saveBar(env);
  const rerender = () => env.again();
  const now = () => new Date().toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });

  const testChip = (id) => {
    const t = ui.tests[id];
    if (!t) return el('span', { class: 'chip', text: 'Not tested yet' });
    return chip(t.ok, t.ok ? `Works · tested ${t.at}` : 'Sign-in refused');
  };
  const testLine = (id) => {
    const t = ui.tests[id];
    return t ? el('span', { class: t.ok ? 'muted small' : 'warn-text small', text: `${t.at}: ${t.message}` }) : null;
  };
  const record = (id, answer) => { ui.tests[id] = { ok: Boolean(answer.ok), message: answer.message || '', at: now() }; rerender(); };
  const used = (uses) => el('span', { class: 'muted small', title: uses.join(', '), text: X.usesWords(uses) });

  const secretInput = (stored) => el('input', { type: 'password', class: 'input', autocomplete: 'off', placeholder: stored ? '(stored, leave empty to keep)' : '' });

  function connectionCard(c) {
    const kind = kinds.kinds.find((k) => k.kind === c.kind);
    const stored = (ctx.config.connections || []).some((x) => x.id === c.id && x.hasSecret);
    const uses = X.connectionUses(state.draft, c.id);
    const editing = ui.editing === c.id;
    // A storage connection is tested on a container it is used with: an instance often has a role on that container only,
    // so listing the whole account would be refused although the connection works.
    const containers = c.kind === 'azure-storage' ? X.connectionContainers(state.draft, c.id) : [];
    const unchanged = JSON.stringify(c) === JSON.stringify((state.original.connections || []).find((x) => x.id === c.id));
    const test = async () => {
      try {
        if (containers.length && unchanged) {
          const { container, prefix } = containers[0];
          const answer = await api('/setup/check-source', { method: 'POST', body: JSON.stringify({ connection: c.id, container, prefix }) });
          record(c.id, { ok: answer.ok, message: `Container ${container}: ${answer.message}` });
        } else {
          record(c.id, await api('/connections/test', { method: 'POST', body: JSON.stringify({ id: c.id, kind: c.kind, settings: c.settings, secret: c.secret || '' }) }));
        }
      } catch (error) { record(c.id, { ok: false, message: error.message }); }
    };

    let form = null;
    if (editing && c.kind === 'https') {
      const shown = state.keys.get(c.id);
      form = el('div', { class: 'stack-tight' },
        shown ? el('div', { class: 'keybox' }, el('strong', { text: 'New key, shown only now' }),
          el('div', { class: 'code' }, el('code', { class: 'code-text', text: shown }),
            el('button', { type: 'button', class: 'button button-dark', text: 'Copy', on: { click: () => navigator.clipboard.writeText(shown).catch(() => {}) } })),
          el('span', { text: 'Put it into the sender now. The old key stops working when you save.' })) : null,
        el('div', { class: 'row-actions' },
          el('button', { type: 'button', class: 'button', text: 'Make a new key', on: { click: () => {
            const bytes = crypto.getRandomValues(new Uint8Array(24));
            const key = btoa(String.fromCharCode(...bytes)).replace(/\+/g, 'A').replace(/\//g, 'B').replace(/=+$/, '');
            state.keys.set(c.id, key);
            change((d) => X.updateConnection(d, c.id, {}, key));
            rerender();
          } } }),
          el('button', { type: 'button', class: 'button', text: 'Close', on: { click: () => { ui.editing = null; rerender(); } } })));
    } else if (editing && kind) {
      const inputs = {};
      const secret = secretInput(stored || Boolean(c.secret));
      form = el('div', { class: 'stack-tight connect' },
        kind.settings.map((s) => { inputs[s.key] = textInput({ value: (c.settings || {})[s.key] || '' }); return field(s.label, inputs[s.key], s.hint); }),
        field(kind.secretLabel || 'Secret', secret, 'Kept in this instance\'s Key Vault, never shown again.'),
        el('div', { class: 'row-actions' },
          el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { ui.editing = null; rerender(); } } }),
          el('button', { type: 'button', class: 'button button-primary', text: 'Done', on: { click: () => {
            const settings = Object.fromEntries(Object.entries(inputs).map(([k, v]) => [k, v.value.trim()]));
            change((d) => X.updateConnection(d, c.id, settings, secret.value));
            ui.editing = null;
            delete ui.tests[c.id];
            rerender();
          } } })));
    }

    const removing = ui.removing === c.id;
    return el('article', { class: ui.tests[c.id] && !ui.tests[c.id].ok ? 'card conn-card conn-card-bad' : 'card conn-card' },
      el('div', { class: 'section-head' }, el('strong', { class: 'conn-title', text: c.kind === 'https' ? `Key ${c.id}` : c.id }),
        c.kind === 'https' ? chip(stored || Boolean(c.secret), stored || c.secret ? 'Key stored' : 'No key yet') : testChip(c.id)),
      el('span', { class: 'muted small', text: c.kind === 'https' ? `Web address · ${X.connectionWords(c)}` : X.connectionWords(c) }),
      used(uses),
      testLine(c.id),
      form,
      removing ? (uses.length
        ? notice('warn', `Still used by ${uses.join(', ')}. Change those first.`)
        : confirmBox(`Remove the connection ${c.id}?`, () => { change((d) => X.removeConnection(d, c.id)); ui.removing = null; rerender(); }, () => { ui.removing = null; rerender(); }))
        : null,
      el('div', { class: 'row-actions conn-actions' },
        c.kind === 'https' ? null : el('button', { type: 'button', class: 'button edit-only', text: 'Test', on: { click: test } }),
        editing ? null : el('button', { type: 'button', class: 'button edit-only', text: c.kind === 'https' ? 'Replace key' : 'Edit', on: { click: () => { ui.editing = c.id; rerender(); } } }),
        quiet('Remove', () => { ui.removing = removing ? null : c.id; rerender(); })));
  }

  function sharePointCard() {
    const app = state.draft.sharePointAuth || {};
    const stored = Boolean(ctx.config.sharePointAuth && ctx.config.sharePointAuth.secretSet);
    const set = Boolean(app.clientId);
    const uses = X.sharePointUses(state.draft);
    const editing = ui.editing === '#sharepoint';
    const firstLibrary = state.draft.documentTypes.flatMap((t) => t.companies).map((c) => c.sharePoint).find((sp) => sp && sp.enabled && sp.siteUrl && sp.library);
    const changed = JSON.stringify(app) !== JSON.stringify(state.original.sharePointAuth || {});
    const test = async () => {
      try {
        record('#sharepoint', await api('/config/test-sharepoint', { method: 'POST', body: JSON.stringify({ sharePoint: { siteUrl: firstLibrary.siteUrl, library: firstLibrary.library, folder: '' } }) }));
      } catch (error) { record('#sharepoint', { ok: false, message: error.message }); }
    };
    let form = null;
    if (editing) {
      const tenant = textInput({ value: app.tenantId || identity.tenantId || '' });
      const client = textInput({ value: app.clientId || '', placeholder: '00000000-0000-0000-0000-000000000000' });
      const secret = secretInput(stored);
      form = el('div', { class: 'stack-tight connect' },
        el('div', { class: 'grid-3' }, field('Tenant ID', tenant), field('Client ID', client), field('Client secret', secret)),
        el('div', { class: 'row-actions' },
          el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { ui.editing = null; rerender(); } } }),
          el('button', { type: 'button', class: 'button button-primary', text: 'Done', on: { click: () => {
            change((d) => X.setSharePointApp(d, { tenantId: tenant.value, clientId: client.value, secret: secret.value }));
            ui.editing = null;
            delete ui.tests['#sharepoint'];
            rerender();
          } } })));
    }
    return el('article', { class: set ? 'card conn-card' : 'card conn-card conn-card-warn' },
      el('div', { class: 'section-head' }, el('strong', { class: 'conn-title', text: 'SharePoint app' }), set ? testChip('#sharepoint') : chip(false, 'Not set up')),
      el('span', { class: 'muted small', text: set ? `Client ${app.clientId} in tenant ${app.tenantId}. Secret in Key Vault.` : 'The app your IT creates with the Balsoft script. Needed for SharePoint destinations and sources.' }),
      used(uses),
      testLine('#sharepoint'),
      form,
      el('div', { class: 'row-actions conn-actions' },
        set ? el('button', { type: 'button', class: 'button edit-only', text: 'Test', disabled: !firstLibrary || changed, title: !firstLibrary ? 'Add a SharePoint destination first' : changed ? 'Save first' : null, on: { click: test } }) : null,
        editing ? null : el('button', { type: 'button', class: set ? 'button edit-only' : 'button button-primary edit-only', text: set ? 'Edit' : 'Set up the SharePoint app', on: { click: () => { ui.editing = '#sharepoint'; rerender(); } } })));
  }

  function mailCard() {
    const mail = state.draft.mail || {};
    const stored = Boolean(ctx.config.mail && ctx.config.mail.secretSet);
    const wanting = state.draft.documentTypes.flatMap((t) => t.companies.filter((c) => c.mail === true).map((c) => `${t.id} › ${c.dataAreaId}`));
    const editing = ui.editing === '#mail';
    const test = async () => {
      try { record('#mail', await api('/config/test-mail', { method: 'POST', body: JSON.stringify({ mail: { tenantId: mail.tenantId, clientId: mail.clientId, from: mail.from, redirectTo: mail.redirectTo } }) })); }
      catch (error) { record('#mail', { ok: false, message: error.message }); }
    };
    let form = null;
    if (editing) {
      const on = el('input', { type: 'checkbox', class: 'switch', checked: mail.enabled || !mail.from, 'aria-label': 'Mail on' });
      const tenant = textInput({ value: mail.tenantId || '', placeholder: (state.draft.sharePointAuth && state.draft.sharePointAuth.tenantId) || identity.tenantId || '' });
      const client = textInput({ value: mail.clientId || '' });
      const secret = secretInput(stored);
      const from = textInput({ value: mail.from || '', placeholder: 'rechnung@yourcompany.example' });
      const redirect = textInput({ value: (mail.redirectTo || []).join(', '), placeholder: 'test@yourcompany.example' });
      const sent = el('input', { type: 'checkbox', checked: mail.saveToSentItems !== false });
      const problems = el('div', { class: 'slot' });
      form = el('div', { class: 'stack-tight connect' },
        el('label', { class: 'inline-check' }, on, 'Send documents by mail'),
        el('div', { class: 'grid-3' }, field('Tenant ID (empty: the SharePoint tenant)', tenant), field('Client ID of the mail app', client), field('Client secret', secret)),
        el('div', { class: 'grid-2' }, field('Sent from', from), field('Documents of a test ERP go to', redirect, 'Comma between addresses. A non-production ERP never mails a customer.')),
        el('label', { class: 'inline-check' }, sent, 'Keep a copy in Sent Items'),
        problems,
        el('div', { class: 'row-actions' },
          el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { ui.editing = null; rerender(); } } }),
          el('button', { type: 'button', class: 'button button-primary', text: 'Done', on: { click: () => {
            const next = { enabled: on.checked, tenantId: tenant.value, clientId: client.value, secret: secret.value, from: from.value, redirectTo: redirect.value.split(/[,;\s]+/), saveToSentItems: sent.checked };
            const found = X.mailboxProblems({ ...next, redirectTo: next.redirectTo.filter(Boolean) }, state.draft.sharePointAuth && state.draft.sharePointAuth.tenantId);
            if (found.length) { put(problems, el('ul', { class: 'problems' }, found.map((p) => el('li', { text: p })))); return; }
            change((d) => X.setMailbox(d, next));
            ui.editing = null;
            delete ui.tests['#mail'];
            rerender();
          } } })));
    }
    const set = Boolean(mail.enabled && mail.from);
    return el('article', { class: set ? 'card conn-card' : 'card conn-card conn-card-warn' },
      el('div', { class: 'section-head' }, el('strong', { class: 'conn-title', text: 'Document mail' }), set ? testChip('#mail') : chip(false, 'Not set up')),
      el('span', { class: 'muted small', text: set ? `From ${mail.from}. Documents of a test ERP go to ${(mail.redirectTo || []).join(', ')}.` : 'Sends each document from a company mailbox. Your IT makes the mail app with the Balsoft script.' }),
      el('span', { class: wanting.length && !set ? 'warn-text small' : 'muted small', text: wanting.length ? `${wanting.length} ${wanting.length === 1 ? 'company wants' : 'companies want'} to mail` : 'No company mails yet' }),
      testLine('#mail'),
      form,
      el('div', { class: 'row-actions conn-actions' },
        set ? el('button', { type: 'button', class: 'button edit-only', text: 'Test', on: { click: test } }) : null,
        editing ? null : el('button', { type: 'button', class: set ? 'button edit-only' : 'button button-primary edit-only', text: set ? 'Edit' : 'Set up mail', on: { click: () => { ui.editing = '#mail'; rerender(); } } })));
  }

  function newConnectionForm(kind) {
    const name = textInput({ value: suggestId(kind.kind) });
    const inputs = {};
    const secret = secretInput(false);
    const result = el('span', { 'aria-live': 'polite' });
    const values = () => ({ id: name.value.trim(), kind: kind.kind, settings: Object.fromEntries(Object.entries(inputs).map(([k, v]) => [k, v.value.trim()])), secret: secret.value });
    return el('section', { class: 'card stack' },
      el('h2', { text: `New ${kind.name} connection` }),
      el('p', { class: 'muted small', text: kind.description }),
      el('div', { class: 'grid-3' }, field('Name', name), kind.settings.map((s) => { inputs[s.key] = textInput(); return field(s.label, inputs[s.key], s.hint); }),
        field(kind.secretLabel || 'Secret', secret, kind.kind === 'azure-storage' || kind.kind === 'servicebus' ? 'Leave it empty to sign in as this instance; your IT gives it a role.' : 'Kept in this instance\'s Key Vault, never shown again.')),
      el('div', { class: 'inline' },
        el('button', { type: 'button', class: 'button', text: 'Test the connection', on: { click: async () => {
          try { const a = await api('/connections/test', { method: 'POST', body: JSON.stringify(values()) }); put(result, chip(a.ok, a.message)); }
          catch (error) { put(result, chip(false, error.message)); }
        } } }), result),
      el('div', { class: 'row-actions' },
        el('button', { type: 'button', class: 'button', text: 'Cancel', on: { click: () => { ui.adding = null; rerender(); } } }),
        el('button', { type: 'button', class: 'button button-primary', text: 'Add', on: { click: () => {
          try { change((d) => X.addConnection(d, values())); ui.adding = null; rerender(); }
          catch (error) { put(result, chip(false, error.message)); }
        } } })));
  }

  function suggestId(kind) {
    const base = { 'azure-storage': 'storage', servicebus: 'servicebus', docuware: 'docuware', https: 'web' }[kind] || kind;
    let id = base;
    for (let n = 2; state.draft.connections.some((c) => c.id.toLowerCase() === id.toLowerCase()); n++) id = `${base}-${n}`;
    return id;
  }

  const adding = ui.adding ? kinds.kinds.find((k) => k.kind === ui.adding) : null;
  put(view,
    el('div', { class: 'page-head' }, el('h1', { text: 'Connections' }),
      el('p', { class: 'muted', text: 'The systems Hive BDC signs in to. Each is set up once and used by any document type. Passwords, keys and secrets go to this instance\'s Key Vault and are never shown again.' })),
    el('section', { class: 'conn-grid', 'aria-label': 'Connections' }, state.draft.connections.map(connectionCard), sharePointCard(), mailCard()),
    adding ? newConnectionForm(adding) : null,
    el('section', { class: 'stack edit-only', 'aria-label': 'Add a connection' },
      el('h2', { text: 'Add a connection' }),
      el('div', { class: 'tiles tiles-auto' }, kinds.kinds.map((k) => el('button', { type: 'button', class: 'tile tile-button', on: { click: () => { ui.adding = k.kind; rerender(); } } },
        el('span', { class: 'tile-title', text: k.name }), el('span', { class: 'tile-note', text: k.description || '' })))),
      el('p', { class: 'muted small', text: `Azure Storage and Service Bus can sign in as this instance (identity ${identity.objectId || 'unknown'}): your IT gives it a role, the check on the Export page shows the command. A connection still in use cannot be removed.` })),
    bar.node);
  if (!ctx.me.canChange) lock(view);
}

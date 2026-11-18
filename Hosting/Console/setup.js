// The set-up assistant of an Azure instance (boards W1 to W4): licence, documents from F&O, where they go, test run.
// Shown instead of the other pages while the settings say "paused" or nothing takes documents from a source yet.
import {
  andList, cleanPrefix, licenceNote, destinationPicks, destinationProblems, featureWords, germanDate, licenceWorks, postBody, roleCommand,
  runLine, setupProgress, setupType, sourceEntries, typeWords, uniqueId, withBlobSource, withDestinations,
} from './logic.js';
import { api, chip, el, field, link, notice, put, save, textInput } from './ui.js';

const timeZone = 'Europe/Berlin';
const stepNames = ['Licence', 'Documents from F&O', 'Where they go', 'Test run'];

export async function setupPage(view, route, ctx) {
  if (!ctx.me.canChange) {
    put(view,
      el('div', { class: 'page-head' }, el('h1', { text: 'Hive BDC is being set up' })),
      notice('info', 'An administrator is setting Hive BDC up. The other pages open once it is started.'));
    return;
  }
  const progress = setupProgress(ctx.status, ctx.config);
  const asked = Number(route.parts[0]);
  const step = asked >= 1 && asked <= progress.open ? asked : progress.open;
  const steps = { 1: licenceStep, 2: sourceStep, 3: destinationStep, 4: testRunStep };
  await steps[step](view, ctx, progress, route);
}

// ── shared parts ─────────────────────────────────────────────────────────────────────

function stepBar(step, progress) {
  return el('ol', { class: 'steps', 'aria-label': 'Set-up steps' }, stepNames.map((name, i) => {
    const n = i + 1;
    const now = n === step;
    const done = progress.done[i] && !now;
    return el('li', { class: now ? 'step step-now' : done ? 'step step-done' : 'step', 'aria-current': now ? 'step' : null },
      el('span', { class: 'step-no', text: done ? `STEP ${n} · DONE` : `STEP ${n}` }),
      el('span', { class: 'step-name', text: name }));
  }));
}

function actions(...children) {
  return el('div', { class: 'actions' }, ...children);
}

function goTo(step) {
  const hash = `#/setup/${step}`;
  if (window.location.hash === hash) window.dispatchEvent(new HashChangeEvent('hashchange'));
  else window.location.hash = hash;
}

// ── step 1: licence ──────────────────────────────────────────────────────────────────

async function licenceStep(view, ctx, progress) {
  const licence = ctx.status.license || {};
  const works = licenceWorks(licence);
  const result = el('div', { class: 'slot' });

  let box;
  if (works) {
    box = el('div', { class: 'licence licence-ok' },
      el('span', { class: 'licence-mark', 'aria-hidden': 'true', text: '✓' }),
      el('span', { class: 'licence-text' },
        el('strong', { text: licence.customer ? `Licensed to ${licence.customer}` : 'Licensed' }),
        el('span', { text: [licence.expires ? `Valid until ${germanDate(licence.expires)}` : '', featureWords(licence.features)].filter(Boolean).join(' · ') }),
        licenceNote(licence) ? el('span', { class: 'muted', text: licenceNote(licence) }) : null));
  } else if (String(licence.state) === 'Missing' || !licence.state) {
    box = el('div', { class: 'licence' }, el('span', { class: 'licence-text' },
      el('strong', { text: 'No licence yet' }), el('span', { class: 'muted', text: 'Choose the .lic file below.' })));
  } else {
    box = el('div', { class: 'licence licence-warn' }, el('span', { class: 'licence-text' },
      el('strong', { text: `The licence is ${String(licence.state).toLowerCase()}` }), el('span', { text: licence.message || '' })));
  }

  const file = el('input', { id: 'licfile', type: 'file', accept: '.lic' });
  file.addEventListener('change', async () => {
    const chosen = file.files && file.files[0];
    if (!chosen) return;
    put(result, notice('info', 'Checking the licence'));
    try {
      const answer = await api('/license', { method: 'POST', body: JSON.stringify({ content: await chosen.text(), fileName: chosen.name }) });
      put(result, notice('info', answer.message || 'The licence is stored.'));
      goTo(1);
    } catch (error) {
      put(result, notice('error', error.message));
      file.value = '';
    }
  });

  put(view,
    el('div', { class: 'page-head' }, el('h1', { text: 'Welcome to Hive BDC' }),
      el('p', { class: 'muted', text: 'Four steps, then your first document is on its way. Everything can be changed later.' })),
    stepBar(1, progress),
    el('div', { class: 'split' },
      el('section', { class: 'card stack', 'aria-labelledby': 'lic-h' },
        el('h2', { id: 'lic-h', text: 'Your licence' }),
        el('p', { class: 'muted', text: 'Upload the licence file Balsoft sent you (.lic). Hive BDC checks that it was issued for this Azure tenant and keeps it with the instance\'s settings.' }),
        box,
        el('div', { class: 'inline' }, el('label', { for: 'licfile', text: works ? 'Another file:' : 'Licence file:' }), file),
        result,
        el('div', { class: 'help' },
          el('strong', { text: 'If the file is refused' }),
          el('span', { text: '"Issued for another tenant": the licence belongs to a different Microsoft 365 tenant; ask Balsoft for one for this tenant. "Expired": Balsoft sends the renewal each year; upload it here.' }))),
      el('aside', { class: 'card stack', 'aria-labelledby': 'roles-h' },
        el('h2', { id: 'roles-h', text: 'Who can do what' }),
        el('p', {}, el('strong', { text: 'Viewer' }), ' sees status, documents and logs.'),
        el('p', {}, el('strong', { text: 'Operator' }), ' also retries documents that need attention.'),
        el('p', {}, el('strong', { text: 'Administrator' }), ' also changes settings, connections and the licence.'),
        el('p', { class: 'muted small', text: 'Your IT gives colleagues a role in Microsoft Entra: Enterprise applications, Hive BDC sign-in, Users and groups.' }))),
    actions(el('span'), works ? link('#/setup/2', 'Next: documents from F&O', true)
      : el('button', { type: 'button', class: 'button button-primary button-big', disabled: true, text: 'Next: documents from F&O' })),
  );
}

// ── step 2: documents from F&O ───────────────────────────────────────────────────────

async function sourceStep(view, ctx, progress) {
  const identity = await api('/setup/identity');
  const existing = sourceEntries(ctx.config).find((e) => e.company.source.kind === 'blob');
  const existingConnection = existing && (ctx.config.connections || []).find((c) => c.id === existing.company.source.connection);

  const account = textInput({ id: 'src-account', type: 'url', placeholder: 'https://youraccount.blob.core.windows.net',
    value: (existingConnection && existingConnection.settings && existingConnection.settings.AccountUrl) || '' });
  const container = textInput({ id: 'src-container', placeholder: 'bdc-export',
    value: (existing && existing.company.source.settings && existing.company.source.settings.Container) || '' });
  const prefix = textInput({ id: 'src-prefix', placeholder: 'empty: the whole container',
    value: (existing && existing.company.source.settings && existing.company.source.settings.Prefix) || '' });
  const byIdentity = el('input', { type: 'radio', name: 'access', value: 'identity', checked: true });
  const byKey = el('input', { type: 'radio', name: 'access', value: 'key' });
  const key = el('input', { type: 'password', class: 'input', autocomplete: 'off', placeholder: 'DefaultEndpointsProtocol=https;AccountName=...' });
  const command = el('code', { class: 'code-text' });
  const copied = el('span', { class: 'muted small', 'aria-live': 'polite' });
  const checkLine = el('span', { class: 'check-line', 'aria-live': 'polite' });
  const planBox = el('div', { class: 'slot' });
  const saveSlot = el('div', { class: 'slot' });
  const next = el('button', { type: 'button', class: 'button button-primary button-big', disabled: true, text: 'Save and continue' });
  let check = null;
  let dirty = !progress.done[1];

  const usingKey = () => byKey.checked;
  const showCommand = () => { command.textContent = (check && check.command) || roleCommand(identity.objectId, account.value) || 'Enter the storage account address above.'; };
  const commandBox = el('div', { class: 'code' }, command,
    el('button', { type: 'button', class: 'button button-dark', text: 'Copy', on: { click: async () => {
      try { await navigator.clipboard.writeText(command.textContent); copied.textContent = 'Copied.'; } catch { copied.textContent = 'Select the command and copy it.'; }
    } } }));
  const keyBox = el('div', { class: 'indent' }, field('Connection string', key, 'From the storage account, Access keys. It is kept in this instance\'s Key Vault and never shown again.'));
  const identityBox = el('div', { class: 'indent stack' }, commandBox, copied);

  const refresh = () => {
    identityBox.hidden = usingKey();
    keyBox.hidden = !usingKey();
    showCommand();
    const plan = check && check.ok ? setupType(check.byType, ctx.config.documentTypes) : null;
    if (!dirty) {
      put(planBox);
      next.disabled = false;
      next.textContent = 'Next: where they go';
      return;
    }
    next.textContent = 'Save and continue';
    next.disabled = !(plan && plan.type);
    if (!check || !check.ok) { put(planBox); return; }
    if (!plan.type) {
      put(planBox, notice('warn', 'No export of a document type Hive BDC knows is waiting. Send one document from F&O into this container, then check again.'));
      return;
    }
    const type = ctx.config.documentTypes.find((t) => t.id === plan.type);
    const later = plan.later.map((l) => {
      const known = ctx.config.documentTypes.find((t) => t.id.toLowerCase() === String(l.documentType).toLowerCase());
      return l.documentType === 'unknown' ? `${l.count} without a document type` : `${l.count} ${typeWords(known || { id: l.documentType }, l.count)}`;
    });
    put(planBox, el('p', { class: 'note' },
      `Hive BDC sets up ${typeWords(type)} for ${plan.companies.length === 1 ? 'company' : 'companies'} ${andList(plan.companies)}.`,
      later.length ? ` Also waiting: ${later.join(', ')}. They stay in the container until you add them under Export.` : ''));
  };

  const checkButton = el('button', { type: 'button', class: 'button', text: 'Check access', on: { click: async () => {
    checkButton.disabled = true;
    put(checkLine, el('span', { class: 'muted', text: 'Checking' }));
    try {
      check = await api('/setup/check-source', { method: 'POST', body: JSON.stringify({
        accountUrl: account.value.trim(), container: container.value.trim(), prefix: cleanPrefix(prefix.value), connectionString: usingKey() ? key.value.trim() : null,
      }) });
      put(checkLine, chip(check.ok, check.message));
    } catch (error) {
      check = null;
      put(checkLine, chip(false, error.message));
    } finally {
      checkButton.disabled = false;
      refresh();
    }
  } } });

  for (const control of [account, container, prefix, key, byIdentity, byKey]) {
    control.addEventListener('input', () => { dirty = true; check = null; put(checkLine); refresh(); });
    control.addEventListener('change', () => refresh());
  }

  next.addEventListener('click', async () => {
    if (!dirty) { goTo(3); return; }
    next.disabled = true;
    const planned = withBlobSource(postBody(ctx.config),
      { accountUrl: account.value.trim(), container: container.value.trim(), prefix: prefix.value, connectionString: usingKey() ? key.value.trim() : '' },
      check.byType, identity.inputRoot);
    if (await save(planned.body, saveSlot)) goTo(3);
    else next.disabled = false;
  });

  const tiles = el('fieldset', { class: 'tiles tiles-3' },
    el('legend', { class: 'sr-only', text: 'Where documents come from' }),
    sourceTile('blob', 'Azure Blob Storage', 'Standard for F&O. F&O writes each export and its PDF into a container.', true),
    sourceTile('servicebus', 'Azure Service Bus', 'F&O sends each export as a message to a queue or topic.', false),
    sourceTile('https', 'Web address (HTTPS)', 'Power Automate, a Logic App or another ERP posts to Hive BDC.', false));
  const blobForm = el('section', { class: 'card stack', 'aria-labelledby': 'blob-h' },
    el('h2', { id: 'blob-h', text: 'The container F&O writes to' }),
    el('div', { class: 'grid-3' }, field('Storage account address', account), field('Container', container), field('Folder in the container (optional)', prefix)),
    el('fieldset', { class: 'plain stack' },
      el('legend', { class: 'field-label', text: 'How Hive BDC gets in' }),
      el('label', { class: 'radio' }, byIdentity, el('span', {}, el('strong', { text: 'Give Hive BDC access' }), ' (recommended, no key to store or renew). Your IT runs this once in Azure Cloud Shell:')),
      identityBox,
      el('label', { class: 'radio' }, byKey, el('span', {}, el('strong', { text: 'Use a connection string' }), '. It is kept in this instance\'s Key Vault and never shown again.')),
      keyBox),
    el('div', { class: 'inline' }, checkButton, checkLine),
    planBox,
    el('p', { class: 'note', text: 'F&O keeps writing to this container as it does today. Hive BDC takes each export with the PDF named in it and moves both to processed/ or error/ when it is done.' }));
  const otherNote = el('p', { class: 'note', hidden: true, text: 'The assistant checks and tests a Blob container. For Service Bus or a web address, add the companies on the Export page.' });
  tiles.addEventListener('change', () => {
    const blob = tiles.querySelector('input:checked').value === 'blob';
    blobForm.hidden = !blob;
    otherNote.hidden = blob;
    next.hidden = !blob;
  });

  put(view,
    stepBar(2, progress),
    el('div', { class: 'page-head' }, el('h1', { text: 'How does F&O hand over its documents?' }),
      el('p', { class: 'muted', text: 'Pick what your F&O already uses. The Balsoft model in F&O writes each export as JSON with its PDF.' })),
    tiles, blobForm, otherNote, saveSlot,
    actions(link('#/setup/1', 'Back'), next));
  refresh();
}

function sourceTile(value, title, text, checked) {
  return el('label', { class: 'tile' },
    el('input', { type: 'radio', name: 'source', value, checked }),
    el('span', { class: 'tile-text' }, el('span', { class: 'tile-title', text: title }), el('span', { class: 'tile-note', text })));
}

// ── step 3: where they go ────────────────────────────────────────────────────────────

async function destinationStep(view, ctx, progress, route) {
  const [targets, kinds, identity] = await Promise.all([api('/targets'), api('/connections/kinds'), api('/setup/identity')]);
  const config = ctx.config;
  const entries = sourceEntries(config);
  const typeIds = [...new Set(entries.map((e) => e.type.id))];
  const typeId = typeIds.find((t) => t === route.query.type) || typeIds[0];
  const type = config.documentTypes.find((t) => t.id === typeId);
  const companies = entries.filter((e) => e.type.id === typeId).map((e) => e.company.dataAreaId);
  const mailReady = Boolean(config.mail && config.mail.enabled && config.mail.from);

  const descriptors = [...targets.targets.map((d) => ({ ...d, settings: d.settings || [] })),
    { kind: 'mail', name: 'Mail', description: mailReady ? `To the customer, from ${config.mail.from}` : 'Needs a mailbox under Connections first.', connectionKind: '', connected: mailReady, settings: [] }];
  const offered = descriptors.map((d) => d.kind);
  const read = destinationPicks(type, offered);
  const picks = new Map(read.map((p) => [p.kind, p]));
  const drafts = {};
  const ready = {};
  for (const d of descriptors) ready[d.kind] = Boolean(d.connected);

  const connectionFor = (d) => {
    const pick = picks.get(d.kind);
    if (pick && pick.connection) return pick.connection;
    const found = (config.connections || []).find((c) => c.kind === d.connectionKind);
    return found ? found.id : uniqueId(config.connections, d.connectionKind || d.kind);
  };

  const sections = el('div', { class: 'stack' });
  const problemsBox = el('div', { class: 'slot' });
  const saveSlot = el('div', { class: 'slot' });
  const next = el('button', { type: 'button', class: 'button button-primary button-big', text: 'Save and continue' });

  const currentPicks = () => [...picks.values()];
  const check = () => {
    const problems = destinationProblems(currentPicks(), companies, descriptors, ready);
    put(problemsBox, problems.length ? el('ul', { class: 'problems' }, problems.map((p) => el('li', { text: p }))) : null);
    next.disabled = problems.length > 0;
  };

  const tiles = el('fieldset', { class: 'tiles tiles-auto' }, el('legend', { class: 'sr-only', text: 'Destinations' }),
    descriptors.map((d) => {
      const box = el('input', { type: 'checkbox', checked: picks.has(d.kind), disabled: d.kind === 'mail' && !mailReady });
      box.addEventListener('change', () => {
        if (box.checked) picks.set(d.kind, { kind: d.kind, companies: [...companies], settings: {}, connection: d.connectionKind ? connectionFor(d) : '' });
        else picks.delete(d.kind);
        renderSections();
      });
      return el('label', { class: box.disabled ? 'tile tile-off' : 'tile' },
        el('span', { class: 'tile-head' }, el('span', { class: 'tile-title', text: d.name }), box),
        el('span', { class: 'tile-note', text: d.description }));
    }));

  function renderSections() {
    put(sections, descriptors.filter((d) => picks.has(d.kind)).map((d) => destinationSection(d, picks.get(d.kind), {
      companies, config, identity, kinds, drafts, ready, onChange: check,
    })));
    check();
  }

  next.addEventListener('click', async () => {
    next.disabled = true;
    let body = withDestinations(postBody(config), typeId, currentPicks(), offered);
    for (const [kind, draft] of Object.entries(drafts)) {
      if (!picks.has(kind) || !ready[kind]) continue;
      if (kind === 'sharepoint') {
        body.sharePointAuth = { tenantId: draft.tenantId, clientId: draft.clientId, clientSecret: draft.secret };
      } else {
        body.connections = body.connections.filter((c) => c.id !== draft.id);
        body.connections.push({ id: draft.id, kind: draft.kind, settings: { ...draft.settings }, secret: draft.secret });
      }
    }
    if (await save(body, saveSlot)) goTo(4);
    else check();
  });

  const intro = [`The exports are ${typeWords(type)} for ${companies.length === 1 ? 'company' : 'companies'} ${andList(companies)}.`];
  if (type.handler === 'EInvoice') intro.push('Hive BDC makes the e-invoice each export asks for (XRechnung or ZUGFeRD).');
  intro.push('Choose one place or several; other document types follow later under Export.');

  put(view,
    stepBar(3, progress),
    typeIds.length > 1 ? el('nav', { class: 'pills', 'aria-label': 'Document types' }, typeIds.map((id) => {
      const t = config.documentTypes.find((x) => x.id === id);
      return el('a', { class: id === typeId ? 'pill pill-on' : 'pill', href: `#/setup/3?type=${encodeURIComponent(id)}`, text: typeWords(t) });
    })) : null,
    el('div', { class: 'page-head' }, el('h1', { text: `Where should ${typeWords(type)} go?` }), el('p', { class: 'muted', text: intro.join(' ') })),
    tiles, sections, problemsBox, saveSlot,
    actions(link('#/setup/2', 'Back'), next));
  renderSections();
}

function destinationSection(d, pick, env) {
  const { companies, config, identity, kinds, drafts, ready, onChange } = env;
  const status = el('span');
  const setStatus = () => put(status, chip(ready[d.kind], ready[d.kind] ? 'Connected' : 'Not connected yet'));
  const choiceSlots = {};
  const controls = {};

  const draftFor = () => {
    if (d.kind === 'sharepoint') return drafts.sharepoint ? { sharePoint: { tenantId: drafts.sharepoint.tenantId, clientId: drafts.sharepoint.clientId, secret: drafts.sharepoint.secret } } : {};
    if (d.kind === 'docuware' && drafts.docuware) {
      const x = drafts.docuware;
      return { docuWare: { id: x.id, url: x.settings.Url || '', userName: x.settings.UserName || '', secret: x.secret } };
    }
    return {};
  };

  async function loadChoices(setting) {
    const select = controls[setting.key];
    const slot = choiceSlots[setting.key];
    if (setting.dependsOn && !String(pick.settings[setting.dependsOn] || '').trim()) {
      put(slot, el('span', { class: 'field-hint', text: 'Fill in the field above first.' }));
      return;
    }
    if (!ready[d.kind] && !(d.kind === 'sharepoint' && drafts.sharepoint)) {
      put(slot, el('span', { class: 'field-hint', text: 'Connect it first.' }));
      return;
    }
    put(slot, el('span', { class: 'field-hint', text: 'Loading' }));
    try {
      const answer = await api('/targets/choices', { method: 'POST', body: JSON.stringify({
        kind: d.kind, lookup: setting.lookup, connection: pick.connection, settings: pick.settings, ...draftFor(),
      }) });
      if (!answer.ok) throw new Error(answer.message);
      const current = pick.settings[setting.key] || '';
      put(select, el('option', { value: '', text: setting.required === false ? '(none)' : 'Choose' }),
        answer.choices.map((c) => el('option', { value: c.value, selected: c.value === current, text: c.detail ? `${c.label} (${c.detail})` : c.label })));
      put(slot);
      if (d.kind === 'sharepoint' && !ready.sharepoint) {
        ready.sharepoint = true;
        setStatus();
      }
    } catch (error) {
      put(slot, el('span', { class: 'field-hint warn-text', text: error.message }));
      if (d.kind === 'sharepoint' && drafts.sharepoint) ready.sharepoint = false;
      setStatus();
    }
    onChange();
  }

  const settingRows = d.settings.map((setting) => {
    let control;
    if (setting.lookup) {
      control = el('select', { class: 'input' }, el('option', { value: pick.settings[setting.key] || '', text: pick.settings[setting.key] || 'Choose' }));
      control.addEventListener('change', () => {
        pick.settings[setting.key] = control.value;
        pick.mixed = false;
        for (const other of d.settings.filter((s) => s.dependsOn === setting.key)) loadChoices(other);
        onChange();
      });
    } else {
      control = textInput({ value: pick.settings[setting.key] || '', placeholder: setting.hint || '' });
      control.addEventListener('input', () => { pick.settings[setting.key] = control.value.trim(); pick.mixed = false; onChange(); });
      control.addEventListener('change', () => { for (const other of d.settings.filter((s) => s.dependsOn === setting.key)) loadChoices(other); });
    }
    controls[setting.key] = control;
    choiceSlots[setting.key] = el('span');
    return el('div', { class: 'stack-tight' }, field(setting.label, control, setting.lookup ? null : setting.hint), choiceSlots[setting.key]);
  });

  const connect = d.kind === 'mail' || ready[d.kind] ? null : connectForm(d, pick, { config, identity, kinds, drafts, ready, after: () => {
    setStatus();
    for (const s of d.settings.filter((x) => x.lookup)) loadChoices(s);
    onChange();
  } });

  const companyBoxes = el('div', { class: 'inline' }, el('span', { class: 'field-label', text: 'For companies' }), companies.map((company) => {
    const box = el('input', { type: 'checkbox', checked: pick.companies.includes(company) });
    box.addEventListener('change', () => {
      pick.companies = box.checked ? [...pick.companies, company] : pick.companies.filter((c) => c !== company);
      onChange();
    });
    return el('label', { class: 'inline-check' }, box, company);
  }));

  setStatus();
  for (const s of d.settings.filter((x) => x.lookup)) queueMicrotask(() => loadChoices(s));

  const notes = {
    sharepoint: 'The file goes into the library with its name. Columns of the library are filled from the document once you map them under Export.',
    mail: 'Each document goes to the customer\'s address from the export.',
  };
  return el('section', { class: 'card stack', 'aria-label': d.name },
    el('div', { class: 'section-head' }, el('h2', { text: d.name }), d.kind === 'mail' ? null : status),
    connect,
    pick.mixed ? el('p', { class: 'note', text: 'The companies have different values here and keep them. A value you change applies to every ticked company.' }) : null,
    d.settings.length ? el('div', { class: 'grid-3' }, settingRows) : null,
    companyBoxes,
    notes[d.kind] ? el('p', { class: 'note', text: notes[d.kind] }) : null);
}

function connectForm(d, pick, env) {
  const { config, identity, kinds, drafts, ready, after } = env;
  const result = el('span', { 'aria-live': 'polite' });
  if (d.kind === 'sharepoint') {
    const auth = config.sharePointAuth || {};
    const tenant = textInput({ value: auth.tenantId || identity.tenantId || '' });
    const client = textInput({ value: auth.clientId || '', placeholder: '00000000-0000-0000-0000-000000000000' });
    const secret = el('input', { type: 'password', class: 'input', autocomplete: 'off', placeholder: auth.secretSet ? '(stored, leave empty to keep)' : '' });
    const use = el('button', { type: 'button', class: 'button', text: 'Use this app', on: { click: () => {
      drafts.sharepoint = { tenantId: tenant.value.trim(), clientId: client.value.trim(), secret: secret.value };
      ready.sharepoint = false;
      put(result, el('span', { class: 'field-hint', text: 'Enter the site address below; the libraries load when the app can sign in.' }));
      after();
    } } });
    return el('div', { class: 'connect stack' },
      el('p', { class: 'muted', text: 'The SharePoint app your IT created with the Balsoft script. Its secret is kept in this instance\'s Key Vault.' }),
      el('div', { class: 'grid-3' }, field('Tenant ID', tenant), field('Client ID', client), field('Client secret', secret)),
      el('div', { class: 'inline' }, use, result));
  }

  const kind = kinds.kinds.find((k) => k.kind === d.connectionKind);
  if (!kind) return el('p', { class: 'warn-text', text: `This version cannot connect ${d.name} here yet.` });
  const stored = (config.connections || []).find((c) => c.id === pick.connection);
  const values = {};
  const inputs = kind.settings.map((s) => {
    const input = textInput({ value: (stored && stored.settings && stored.settings[s.key]) || '', placeholder: s.hint || '' });
    values[s.key] = input;
    return field(s.label, input, s.hint);
  });
  const secret = el('input', { type: 'password', class: 'input', autocomplete: 'off', placeholder: stored && stored.hasSecret ? '(stored, leave empty to keep)' : '' });
  const test = el('button', { type: 'button', class: 'button', text: 'Test the connection', on: { click: async () => {
    test.disabled = true;
    const draft = { id: pick.connection, kind: kind.kind, settings: Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v.value.trim()])), secret: secret.value };
    try {
      const answer = await api('/connections/test', { method: 'POST', body: JSON.stringify(draft) });
      put(result, chip(answer.ok, answer.message));
      ready[d.kind] = Boolean(answer.ok);
      if (answer.ok) drafts[d.kind] = draft;
      else delete drafts[d.kind];
    } catch (error) {
      put(result, chip(false, error.message));
      ready[d.kind] = false;
    } finally {
      test.disabled = false;
      after();
    }
  } } });
  return el('div', { class: 'connect stack' },
    el('p', { class: 'muted', text: kind.description }),
    el('div', { class: 'grid-3' }, inputs, field(kind.secretLabel || 'Secret', secret, 'Kept in this instance\'s Key Vault, never shown again.')),
    el('div', { class: 'inline' }, test, result));
}

// ── step 4: test run ─────────────────────────────────────────────────────────────────

async function testRunStep(view, ctx, progress) {
  const entries = sourceEntries(ctx.config);
  const choice = el('select', { class: 'input', id: 'run-of' }, entries.map((e, i) => el('option', { value: String(i), text: `${typeWords(e.type)} · company ${e.company.dataAreaId}` })));
  const result = el('div', { class: 'slot' });
  const head = el('div', { class: 'page-head' }, el('h1', { text: 'Test run' }),
    el('p', { class: 'muted', text: 'Hive BDC takes the first waiting export and runs it through every step, for real. The others wait until you start.' }));
  const startSlot = el('div', { class: 'slot' });

  const run = el('button', { type: 'button', class: 'button button-primary', text: 'Run the test', on: { click: async () => {
    const entry = entries[Number(choice.value)];
    run.disabled = true;
    put(result, notice('info', 'The test run is going. This can take a minute.'));
    try {
      const answer = await api('/setup/test-run', { method: 'POST', body: JSON.stringify({ documentType: entry.type.id, company: entry.company.dataAreaId }) });
      put(result, runCard(entry, answer));
      if (answer.ok) put(head, el('h1', { text: 'One document went all the way through' }),
        el('p', { class: 'muted', text: 'Run the next export to try another, or start Hive BDC. The others wait until you start.' }));
      run.textContent = 'Run the next export';
    } catch (error) {
      put(result, notice('error', error.status === 409 ? 'A test run is already going.' : error.message));
    } finally {
      run.disabled = false;
    }
  } } });

  const start = el('button', { type: 'button', class: 'button button-primary button-big', text: 'Start Hive BDC', on: { click: async () => {
    start.disabled = true;
    try {
      await api('/setup/start', { method: 'POST', body: '{}' });
      ctx.started();
    } catch (error) {
      put(startSlot, notice('error', error.message));
      start.disabled = false;
    }
  } } });

  put(view,
    stepBar(4, progress),
    head,
    el('section', { class: 'card stack' },
      el('div', { class: 'inline' }, el('label', { for: 'run-of', class: 'field-label', text: 'Test with the first waiting export of' }), choice, run)),
    result,
    el('p', { class: 'note', text: 'When a step fails, the list stops there and names the reason, for example a SharePoint column that does not exist. Nothing is moved; fix it on the step before and run again.' }),
    startSlot,
    actions(link('#/setup/3', 'Back'), start));
}

function runCard(entry, answer) {
  const lines = (answer.events || []).map((e) => runLine(e, timeZone));
  const title = `${answer.documentId ? `${answer.documentId} · ` : ''}${typeWords(entry.type)} · company ${entry.company.dataAreaId}`;
  return el('section', { class: 'card stack', 'aria-label': 'Steps of the test run' },
    el('div', { class: 'section-head' }, el('h2', { text: title }), chip(Boolean(answer.ok), answer.ok ? 'Done' : 'Stopped')),
    el('p', { class: answer.ok ? 'ok-text' : 'warn-text', text: answer.message }),
    lines.length ? el('ol', { class: 'timeline' }, lines.map((l) => el('li', { class: l.ok ? 'tl tl-ok' : l.waiting ? 'tl tl-wait' : 'tl tl-bad' },
      el('span', { class: 'tl-time', text: l.time }),
      el('span', { class: 'tl-mark', 'aria-hidden': 'true', text: l.ok ? '✓' : l.waiting ? '○' : '!' }),
      el('span', { class: 'tl-text', text: l.text })))) : null);
}

// Pure functions of the Export pages (boards W7 to W11): no DOM, no network, tested by tests/console/export.test.mjs.
// Every change works on a copy of the POST /config body (postBody in logic.js), so one draft can span several screens.
import { cleanPrefix, typeLabel } from './logic.js';

const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
const slug = (part) => String(part || '').replace(/[^A-Za-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');

/** What identifies a source, so companies reading the same container, queue or subscription share one staging folder. */
export function sourceKey(source) {
  const s = (source && source.settings) || {};
  switch (source && source.kind) {
    case 'blob': return ['blob', source.connection, s.Container, cleanPrefix(s.Prefix)].join('|').toLowerCase();
    case 'servicebus': return ['servicebus', source.connection, s.Queue || '', s.Topic || '', s.Subscription || ''].join('|').toLowerCase();
    default: return '';
  }
}

/**
 * The staging folder of a source (failure case 69): one per container and folder, queue or subscription; a web address
 * keeps its own per type and company. Only letters, digits and single hyphens reach the path.
 */
export function stagingInput(source, typeId, company, inputRoot) {
  const s = (source && source.settings) || {};
  let parts;
  switch (source && source.kind) {
    case 'blob': parts = [source.connection, s.Container, cleanPrefix(s.Prefix)]; break;
    case 'servicebus': parts = ['sb', source.connection, s.Queue || [s.Topic, s.Subscription].filter(Boolean).join('-')]; break;
    default: parts = ['https', typeId, company];
  }
  return `${String(inputRoot || '').replace(/\/+$/, '')}/${parts.map(slug).filter(Boolean).join('-')}`;
}

/** A source in the words of a row: "Blob container bdc-export/in", "Service Bus queue bdc-export", "Web address (HTTPS)". */
export function sourceWords(source) {
  const s = (source && source.settings) || {};
  switch (source && source.kind) {
    case 'blob': return s.Container ? `Blob container ${s.Container}${s.Prefix ? `/${cleanPrefix(s.Prefix).replace(/\/$/, '')}` : ''}` : 'Blob container, not chosen yet';
    case 'servicebus':
      if (s.Queue) return `Service Bus queue ${s.Queue}`;
      return s.Topic && s.Subscription ? `Service Bus topic ${s.Topic}/${s.Subscription}` : 'Service Bus, queue not chosen yet';
    case 'https': return 'Web address (HTTPS)';
    default: return 'No source yet';
  }
}

const kindNames = { sharepoint: 'SharePoint', docuware: 'DocuWare', blob: 'Blob container', https: 'Web address', servicebus: 'Service Bus', mail: 'mail' };

/** A destination kind in a list: the name the service reports, shortened for the built-ins. */
export function kindName(kind, descriptors) {
  if (kindNames[kind]) return kindNames[kind];
  const d = (descriptors || []).find((x) => x.kind === kind);
  return d ? d.name : kind;
}

/** The destinations of one company entry, as rows (failure case 74: a kind this version does not offer is kept as it is). */
export function destinationRows(entry, descriptors) {
  const rows = [];
  const sp = entry.sharePoint || {};
  if (sp.enabled || sp.siteUrl || sp.library) {
    rows.push({
      kind: 'sharepoint', name: 'SharePoint', enabled: Boolean(sp.enabled), known: true,
      summary: [sp.library, sp.folder].filter(Boolean).join(' › ') + (Object.keys(sp.fields || {}).length ? ` · ${Object.keys(sp.fields).length} columns` : ''),
    });
  }
  for (const t of entry.targets || []) {
    const d = (descriptors || []).find((x) => x.kind === t.kind);
    const fields = Object.keys(t.fields || {}).length;
    const settings = t.settings || {};
    const main = Object.entries(settings).filter(([k, v]) => v && !/Name$/.test(k)).map(([k, v]) => settings[`${k}Name`] || v);
    rows.push({
      kind: t.kind, name: d ? d.name : t.kind, enabled: t.enabled !== false, known: Boolean(d),
      summary: d ? [main.join(' › '), fields ? `${fields} fields` : ''].filter(Boolean).join(' · ') : 'Set up with a newer version of Hive BDC, kept as it is.',
    });
  }
  return rows;
}

/** Where a company's documents go, in words: "SharePoint, DocuWare" (mail counted only with a mailbox set up). */
export function destinationWords(entry, config, descriptors) {
  const names = [];
  if (entry.sharePoint && entry.sharePoint.enabled) names.push(kindName('sharepoint'));
  for (const t of entry.targets || []) if (t.enabled !== false) names.push(kindName(t.kind, descriptors));
  if (entry.mail === true && config && config.mail && config.mail.enabled) names.push('mail');
  return names.length ? names.join(', ') : 'nowhere yet';
}

/**
 * The rows of the Export list (W7): each type with its companies, where they go and, from the waiting documents, the
 * companies that have no entry yet. Types that are off and have no company are folded away.
 */
export function typeRows(config, attention, descriptors) {
  const rows = [];
  const folded = [];
  for (const t of (config && config.documentTypes) || []) {
    const companies = (t.companies || []).map((c) => c.dataAreaId);
    const waiting = new Map();
    for (const e of attention || []) {
      if (!same(e.documentType, t.id) || String(e.state || e.step).toLowerCase() !== 'waiting') continue;
      const company = e.dataAreaId || '';
      if (!company || companies.some((c) => same(c, company))) continue;
      waiting.set(company, (waiting.get(company) || 0) + 1);
    }
    const goes = [...new Set((t.companies || []).flatMap((c) => destinationWords(c, config, descriptors).split(', ')))].filter((w) => w !== 'nowhere yet');
    const row = {
      id: t.id, name: typeLabel(t), displayName: t.displayName || '', handler: t.handler, party: t.party,
      enabled: t.enabled !== false, companies, goesTo: goes.length ? goes.join(', ') : 'nowhere yet',
      waiting: [...waiting.entries()].map(([company, count]) => ({ company, count })),
    };
    if (!row.enabled && companies.length === 0 && row.waiting.length === 0) folded.push(row);
    else rows.push(row);
  }
  return { rows, folded };
}

const capital = (text) => text.charAt(0).toUpperCase() + text.slice(1);

const findType = (body, typeId) => body.documentTypes.find((t) => same(t.id, typeId));
const findEntry = (body, typeId, company) => {
  const type = findType(body, typeId);
  return type ? type.companies.find((c) => same(c.dataAreaId, company)) : null;
};

/** A new document type: letters, digits, "-" and "_" as its id (the service refuses others), no company yet. */
export function addType(body, id, displayName, handler, party) {
  const next = structuredClone(body);
  const clean = String(id || '').trim();
  if (!/^[A-Za-z0-9_-]+$/.test(clean)) throw new Error('The id takes letters, digits, "-" and "_" only.');
  if (findType(next, clean)) throw new Error(`There is a document type ${clean} already.`);
  next.documentTypes.push({ id: clean, displayName: displayName || clean, party: party || 'Customer', handler: handler || 'Pdf', enabled: true, mail: false, companies: [] });
  return next;
}

/** Removes a type (failure case 71: the service keeps at least one). */
export function removeType(body, typeId) {
  const next = structuredClone(body);
  if (next.documentTypes.length <= 1) throw new Error('Keep at least one document type. Switch it off instead.');
  next.documentTypes = next.documentTypes.filter((t) => !same(t.id, typeId));
  return next;
}

export function setType(body, typeId, changes) {
  const next = structuredClone(body);
  Object.assign(findType(next, typeId), changes);
  return next;
}

/** A company entry without a source yet; it is saved once a source is chosen. */
export function addCompany(body, typeId, company) {
  const next = structuredClone(body);
  const clean = String(company || '').trim();
  if (!clean) throw new Error('Enter the company (dataAreaId).');
  const type = findType(next, typeId);
  if (type.companies.some((c) => same(c.dataAreaId, clean))) throw new Error(`Company ${clean} is there already.`);
  type.companies.push({
    dataAreaId: clean, input: '', inputFormat: 'Json', output: '', enabled: true, mail: false,
    sharePoint: { enabled: false, siteUrl: '', library: '', folder: '', fields: {} }, targets: [],
  });
  return next;
}

export function removeCompany(body, typeId, company) {
  const next = structuredClone(body);
  const type = findType(next, typeId);
  type.companies = type.companies.filter((c) => !same(c.dataAreaId, company));
  return next;
}

/**
 * Where a company's documents come from. An entry that reads the same container, queue or subscription as another one
 * takes that entry's staging folder, so nothing is staged twice (failure case 69).
 */
export function setSource(body, typeId, company, source, inputRoot) {
  const next = structuredClone(body);
  const entry = findEntry(next, typeId, company);
  const settings = Object.fromEntries(Object.entries(source.settings || {}).filter(([, v]) => String(v || '').trim() !== '').map(([k, v]) => [k, String(v).trim()]));
  if (settings.Prefix) settings.Prefix = cleanPrefix(settings.Prefix);
  const clean = { kind: source.kind, connection: source.connection || '', settings };
  const key = sourceKey(clean);
  const twin = key ? next.documentTypes.flatMap((t) => t.companies).find((c) => c !== entry && c.source && sourceKey(c.source) === key && c.input) : null;
  entry.source = clean;
  entry.input = twin ? twin.input : stagingInput(clean, typeId, company, inputRoot);
  return next;
}

/** The connection a web address source checks the key against: one per type and company. */
export function httpsConnectionId(typeId, company) {
  return `in-${slug(typeId)}-${slug(company)}`.toLowerCase();
}

/** A web address source with a new key (failure case 72): the key goes out only with Save, as the connection's secret. */
export function setHttpsKey(body, typeId, company, key, inputRoot, connectionId) {
  const id = connectionId || httpsConnectionId(typeId, company);
  let next = setSource(body, typeId, company, { kind: 'https', connection: id, settings: {} }, inputRoot);
  next.connections = next.connections.filter((c) => c.id !== id);
  next.connections.push({ id, kind: 'https', settings: {}, secret: key });
  return next;
}

/** The company's Active switch, as in the Windows app: off, its documents of this type go to error with the reason. */
export function setCompanyActive(body, typeId, company, on) {
  const next = structuredClone(body);
  findEntry(next, typeId, company).enabled = Boolean(on);
  return next;
}

export function setMail(body, typeId, company, on) {
  const next = structuredClone(body);
  findEntry(next, typeId, company).mail = Boolean(on);
  return next;
}

export function setDestinationEnabled(body, typeId, company, kind, on) {
  const next = structuredClone(body);
  const entry = findEntry(next, typeId, company);
  if (kind === 'sharepoint') entry.sharePoint.enabled = Boolean(on);
  else for (const t of entry.targets) if (t.kind === kind) t.enabled = Boolean(on);
  return next;
}

export function removeDestination(body, typeId, company, kind) {
  const next = structuredClone(body);
  const entry = findEntry(next, typeId, company);
  if (kind === 'sharepoint') entry.sharePoint = { enabled: false, siteUrl: '', library: '', folder: '', fields: {} };
  else entry.targets = entry.targets.filter((t) => t.kind !== kind);
  return next;
}

export function addDestination(body, typeId, company, kind, connection) {
  const next = structuredClone(body);
  const entry = findEntry(next, typeId, company);
  if (kind === 'sharepoint') entry.sharePoint.enabled = true;
  else if (!entry.targets.some((t) => t.kind === kind)) entry.targets.push({ kind, connection: connection || '', enabled: true, settings: {}, fields: {} });
  return next;
}

/** One destination as the W11 form edits it: its settings, its fields (column or index field to template), on or off. */
export function destinationForm(entry, kind) {
  if (kind === 'sharepoint') {
    const sp = entry.sharePoint || {};
    return { kind, connection: '', enabled: Boolean(sp.enabled), settings: { SiteUrl: sp.siteUrl || '', Library: sp.library || '', Folder: sp.folder || '' }, fields: { ...(sp.fields || {}) } };
  }
  const t = (entry.targets || []).find((x) => x.kind === kind) || { connection: '', enabled: true, settings: {}, fields: {} };
  return { kind, connection: t.connection || '', enabled: t.enabled !== false, settings: { ...(t.settings || {}) }, fields: { ...(t.fields || {}) } };
}

/** Writes the W11 form back; settings the form does not show are kept, empty fields are dropped. */
export function applyDestination(body, typeId, company, form) {
  const next = structuredClone(body);
  const entry = findEntry(next, typeId, company);
  const fields = Object.fromEntries(Object.entries(form.fields || {}).filter(([k, v]) => k && String(v || '').trim()).map(([k, v]) => [k, String(v).trim()]));
  if (form.kind === 'sharepoint') {
    entry.sharePoint = {
      ...entry.sharePoint, enabled: form.enabled, siteUrl: form.settings.SiteUrl || '', library: form.settings.Library || '',
      folder: form.settings.Folder || '', fields,
    };
    return next;
  }
  const index = entry.targets.findIndex((t) => t.kind === form.kind);
  const old = index >= 0 ? entry.targets[index] : { kind: form.kind, settings: {} };
  const target = { ...old, kind: form.kind, connection: form.connection || old.connection || '', enabled: form.enabled, settings: { ...(old.settings || {}), ...form.settings }, fields };
  if (index >= 0) entry.targets[index] = target;
  else entry.targets.push(target);
  return next;
}

const placeholder = /\{([A-Za-z0-9_]+)(?::(date|[yMd][yMd.-]*))?\}/g;

function asDate(raw) {
  const text = String(raw || '').trim();
  let m = /^(\d{4})-(\d{2})-(\d{2})/.exec(text) || /^(\d{4})(\d{2})(\d{2})$/.exec(text);
  if (m) return { y: m[1], M: m[2], d: m[3] };
  m = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(text);
  if (m) return { y: m[3], M: m[2].padStart(2, '0'), d: m[1].padStart(2, '0') };
  return null;
}

function formatDate(date, format) {
  if (format === 'date') return `${date.y}-${date.M}-${date.d}`;
  return format.replace(/yyyy|yy|MM|M|dd|d/g, (t) => ({ yyyy: date.y, yy: date.y.slice(2), MM: date.M, M: String(Number(date.M)), dd: date.d, d: String(Number(date.d)) }[t]));
}

/** What a template gives for a sample document, as the service resolves it (SharePointFieldTemplate.Resolve). */
export function templatePreview(template, fields) {
  const lower = Object.fromEntries(Object.entries(fields || {}).map(([k, v]) => [k.toLowerCase(), v]));
  return String(template || '').replace(placeholder, (_, name, format) => {
    const raw = lower[name.toLowerCase()];
    if (raw === undefined) return '';
    if (!format || raw === '') return raw;
    const date = asDate(raw);
    return date ? formatDate(date, format) : raw;
  }).trim();
}

/** The folder a template gives, as the service builds it (SharePointFieldTemplate.ResolvePath, failure case 75). */
export function folderPreview(template, fields) {
  if (!String(template || '').trim()) return '';
  const lower = Object.fromEntries(Object.entries(fields || {}).map(([k, v]) => [k.toLowerCase(), v]));
  const filled = String(template).replace(placeholder, (_, name, format) => {
    const raw = lower[name.toLowerCase()];
    if (raw === undefined) return '';
    const date = format && raw !== '' ? asDate(raw) : null;
    return String(date ? formatDate(date, format) : raw).replace(/[/\\]/g, '-');
  });
  return filled.replace(/\\/g, '/').split('/')
    .map((part) => part.replace(/["*:<>?|#%]/g, '').replace(/\.{2,}/g, '.').replace(/^[ .]+|[ .]+$/g, ''))
    .filter(Boolean).join('/');
}

/** The template choices for a field mapping, from the sample's fields: each field, and "as date" for those holding a date. */
export function templateChoices(fields) {
  const choices = [];
  for (const [name, value] of Object.entries(fields || {})) {
    choices.push({ value: `{${name}}`, label: words(name) });
    if (asDate(value)) choices.push({ value: `{${name}:date}`, label: `${words(name)} (as date)` });
  }
  return choices;
}

/** "documentNumber" as "Document number". */
export function words(name) {
  const text = String(name || '').replace(/_/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return capital(text);
}

/** Every destination of the listed types, once each, for the ERP to Hive BDC strip: "SharePoint, DocuWare". */
export function allDestinations(rows) {
  const names = [];
  for (const row of rows || []) {
    if (!row.goesTo || row.goesTo === 'nowhere yet') continue;
    for (const name of row.goesTo.split(', ')) if (!names.includes(name)) names.push(name);
  }
  return names.length ? names.join(', ') : 'no destination yet';
}

/** The draft's changes against what was read, one plain line each, for the Save bar. */
export function draftChanges(original, draft) {
  const lines = [];
  const o = original || { documentTypes: [], connections: [] };
  const d = draft || { documentTypes: [], connections: [] };
  for (const t of d.documentTypes) {
    const before = findType(o, t.id);
    const name = typeLabel(before || t);
    if (!before) { lines.push(`${name} added`); continue; }
    if ((before.displayName || '') !== (t.displayName || '')) lines.push(`${name}: name is now ${t.displayName}`);
    if (before.handler !== t.handler) lines.push(`${name}: Hive BDC makes ${t.handler === 'EInvoice' ? 'e-invoice' : 'PDF as it is'}`);
    if (before.party !== t.party) lines.push(`${name}: sent to ${t.party === 'Vendor' ? 'vendors' : 'customers'}`);
    if ((before.enabled !== false) !== (t.enabled !== false)) lines.push(`${name}: ${t.enabled !== false ? 'active' : 'off'}`);
    for (const c of t.companies) {
      const was = before.companies.find((x) => same(x.dataAreaId, c.dataAreaId));
      if (!was) { lines.push(`${name}: company ${c.dataAreaId} added`); continue; }
      const where = `${name} › ${c.dataAreaId}`;
      if ((was.enabled !== false) !== (c.enabled !== false)) lines.push(`${where}: ${c.enabled !== false ? 'active' : 'off'}`);
      if (JSON.stringify(was.source || null) !== JSON.stringify(c.source || null)) lines.push(`${where}: comes from ${sourceWords(c.source)}`);
      if (Boolean(was.mail) !== Boolean(c.mail)) lines.push(`${where}: mail ${c.mail ? 'on' : 'off'}`);
      const spWas = was.sharePoint || {}, sp = c.sharePoint || {};
      const spOn = (x) => Boolean(x.enabled || x.siteUrl || x.library);
      if (spOn(spWas) && !spOn(sp)) lines.push(`${where}: SharePoint removed`);
      else if (!spOn(spWas) && spOn(sp)) lines.push(`${where}: SharePoint added`);
      else if (Boolean(spWas.enabled) !== Boolean(sp.enabled)) lines.push(`${where}: SharePoint ${sp.enabled ? 'on' : 'off'}`);
      else if (JSON.stringify(spWas) !== JSON.stringify(sp)) lines.push(`${where}: SharePoint changed`);
      for (const t2 of c.targets || []) {
        const old = (was.targets || []).find((x) => x.kind === t2.kind);
        const label = kindName(t2.kind);
        if (!old) lines.push(`${where}: ${label} added`);
        else if ((old.enabled !== false) !== (t2.enabled !== false)) lines.push(`${where}: ${label} ${t2.enabled !== false ? 'on' : 'off'}`);
        else if (JSON.stringify(old) !== JSON.stringify(t2)) lines.push(`${where}: ${label} changed`);
      }
      for (const old of was.targets || []) if (!(c.targets || []).some((x) => x.kind === old.kind)) lines.push(`${where}: ${kindName(old.kind)} removed`);
    }
    for (const c of before.companies) if (!t.companies.some((x) => same(x.dataAreaId, c.dataAreaId))) lines.push(`${name}: company ${c.dataAreaId} removed`);
  }
  for (const t of o.documentTypes) if (!findType(d, t.id)) lines.push(`${typeLabel(t)} removed`);
  lines.push(...settingsChanges(o, d));
  for (const c of d.connections) if (c.secret) lines.push(c.kind === 'https' ? `New key for ${c.id}` : `New secret for ${c.id}`);
  return lines;
}

/**
 * What keeps a draft from saving, in words: a company without a source, a Blob source without connection or container,
 * a Service Bus source without connection or queue (or topic and subscription), a web address without a key.
 */
export function saveProblems(draft, config, descriptors) {
  const problems = [];
  const keyStored = (id) => ((config && config.connections) || []).some((c) => c.id === id && c.hasSecret);
  const mailOn = Boolean(draft && draft.mail && draft.mail.enabled);
  for (const t of (draft && draft.documentTypes) || []) {
    for (const c of t.companies || []) {
      const where = `${typeLabel(t)} › ${c.dataAreaId}`;
      const s = c.source;
      const v = (s && s.settings) || {};
      if (!s || !s.kind) { if (!c.input) problems.push(`${where}: choose where its documents come from.`); continue; }
      if (s.kind === 'blob' && (!s.connection || !v.Container)) problems.push(`${where}: choose the storage connection and the container.`);
      if (s.kind === 'servicebus' && (!s.connection || !(v.Queue || (v.Topic && v.Subscription)))) problems.push(`${where}: choose the Service Bus connection and the queue, or the topic and its subscription.`);
      if (s.kind === 'https' && !keyStored(s.connection) && !draft.connections.some((x) => x.id === s.connection && x.secret)) problems.push(`${where}: create a key for the web address.`);
      // In Azure a company that delivers nowhere holds every document of the instance (the settings check), so Save waits.
      const sp = c.sharePoint || {};
      const delivers = sp.enabled || (c.targets || []).some((x) => x.enabled !== false) || (c.mail === true && mailOn);
      if (t.enabled !== false && c.enabled !== false && !delivers) problems.push(`${where}: choose where its documents go.`);
      if (sp.enabled && (!sp.siteUrl || !sp.library)) problems.push(`${where} › SharePoint library: the site address and the library are needed.`);
      for (const target of (c.targets || []).filter((x) => x.enabled !== false)) {
        const d = (descriptors || []).find((x) => x.kind === target.kind);
        for (const setting of (d && d.settings) || []) {
          if (setting.required !== false && !String((target.settings || {})[setting.key] || '').trim()) problems.push(`${where} › ${d.name}: ${setting.label} is missing.`);
        }
        const ts = target.settings || {};
        if (target.kind === 'servicebus' && !String(ts.Queue || '').trim() && !String(ts.Topic || '').trim()) problems.push(`${where} › ${(d && d.name) || 'Azure Service Bus'}: give a queue or a topic.`);
      }
    }
  }
  problems.push(...mailboxProblems(draft && draft.mail, draft && draft.sharePointAuth && draft.sharePointAuth.tenantId).map((p) => `Mailbox: ${p}`));
  return problems;
}

/** Entries a draft would save without a source: the service needs one per company (input folder), so Save waits. */
export function companiesWithoutSource(draft) {
  const missing = [];
  for (const t of (draft && draft.documentTypes) || []) {
    for (const c of t.companies || []) if ((!c.source || !c.source.kind) && !c.input) missing.push({ type: t.id, company: c.dataAreaId });
  }
  return missing;
}

// ── Connections (board W12) ──────────────────────────────────────────────────────────

/** Who uses a connection, in words: sources, PDF containers and destinations (failure case 77). */
export function connectionUses(draft, id) {
  const uses = [];
  for (const t of (draft && draft.documentTypes) || []) {
    for (const c of t.companies || []) {
      const where = `${typeLabel(t)} › ${c.dataAreaId}`;
      if (c.source && c.source.connection === id) uses.push(`${where} (source)`);
      if (c.source && c.source.settings && c.source.settings.PdfConnection === id) uses.push(`${where} (PDFs)`);
      for (const x of c.targets || []) if (x.connection === id) uses.push(`${where} (${kindName(x.kind)})`);
    }
  }
  return uses;
}

/** The companies whose documents go to SharePoint: the users of the SharePoint app. */
export function sharePointUses(draft) {
  const uses = [];
  for (const t of (draft && draft.documentTypes) || []) {
    for (const c of t.companies || []) if (c.sharePoint && c.sharePoint.enabled) uses.push(`${typeLabel(t)} › ${c.dataAreaId}`);
  }
  return uses;
}

/** A connection in a sentence: where it points and how it signs in. */
export function connectionWords(c) {
  const s = (c && c.settings) || {};
  const stored = Boolean(c && (c.hasSecret || c.secret));
  const host = (url) => { try { return new URL(url).host; } catch { return url; } };
  switch (c && c.kind) {
    case 'azure-storage':
      return stored ? `Azure Storage${s.AccountUrl ? ` ${host(s.AccountUrl).split('.')[0]}` : ''}. Connection string in Key Vault.`
        : `Azure Storage ${host(s.AccountUrl || '').split('.')[0]}. Signs in as this instance, no key stored.`;
    case 'servicebus':
      return stored ? 'Azure Service Bus. Connection string in Key Vault.' : `Azure Service Bus ${s.Namespace || ''}. Signs in as this instance.`;
    case 'docuware':
      return `DocuWare ${host(s.Url || '')} as ${s.UserName || 'a user'}. Password in Key Vault.`;
    case 'https':
      return s.Url ? `Web address ${host(s.Url)}. Key in Key Vault.` : 'Key a sender sends to /inbound. Kept in Key Vault, never shown again.';
    default:
      return c ? c.kind : '';
  }
}

export function addConnection(draft, connection) {
  const next = structuredClone(draft);
  const id = String(connection.id || '').trim();
  if (!/^[A-Za-z0-9_.-]+$/.test(id)) throw new Error('The name takes letters, digits, ".", "-" and "_" only.');
  if (next.connections.some((c) => c.id.toLowerCase() === id.toLowerCase())) throw new Error(`There is a connection ${id} already.`);
  const added = { id, kind: connection.kind, settings: { ...(connection.settings || {}) } };
  if (connection.secret) added.secret = connection.secret;
  next.connections.push(added);
  return next;
}

/** New settings for a connection; a blank secret keeps the stored one. */
export function updateConnection(draft, id, settings, secret) {
  const next = structuredClone(draft);
  const c = next.connections.find((x) => x.id === id);
  c.settings = { ...c.settings, ...settings };
  if (secret) c.secret = secret;
  return next;
}

export function removeConnection(draft, id) {
  const uses = connectionUses(draft, id);
  if (uses.length) throw new Error(`Still used by ${uses.join(', ')}.`);
  const next = structuredClone(draft);
  next.connections = next.connections.filter((c) => c.id !== id);
  return next;
}

/** Whether a company has this destination in the draft, by the rule of destinationRows (design part 20). */
export function hasDestination(entry, kind) {
  if (!entry) return false;
  if (kind === 'sharepoint') {
    const sp = entry.sharePoint || {};
    return Boolean(sp.enabled || sp.siteUrl || sp.library);
  }
  return (entry.targets || []).some((t) => t.kind === kind);
}

/** A mailbox counts as set up once it has a sender, also while it is switched off (its app and secret are kept). */
export function mailboxConfigured(mail) {
  return Boolean(mail && String(mail.from || '').trim());
}

/** The line under Document mail, in the words of the SharePoint app line: a typed secret is never named as stored. */
export function mailboxLine(mail, stored) {
  const m = mail || {};
  if (!m.from) return 'Sends each document from a company mailbox. Your IT makes the mail app with the Balsoft script.';
  const where = `${m.enabled ? '' : 'Switched off. '}From ${m.from}. Documents of a test ERP go to ${(m.redirectTo || []).join(', ')}.`;
  if (m.clientSecret) return `${where} The new secret goes to Key Vault when you save.`;
  return `${where} ${stored ? 'Secret in Key Vault.' : 'No secret yet.'}`;
}

/** The test results that still hold after a save: those of connections, the mailbox and the SharePoint app it did not change. */
export function keptTests(tests, original, draft) {
  const kept = { ...(tests || {}) };
  const o = original || {}, d = draft || {};
  const differs = (a, b) => JSON.stringify(a || null) !== JSON.stringify(b || null);
  for (const id of Object.keys(kept)) {
    if (id === '#mail') { if (differs(o.mail, d.mail)) delete kept[id]; continue; }
    if (id === '#sharepoint') { if (differs(o.sharePointAuth, d.sharePointAuth)) delete kept[id]; continue; }
    const before = (o.connections || []).find((c) => c.id === id);
    const after = (d.connections || []).find((c) => c.id === id);
    if (!before || !after || after.secret || differs(before, after)) delete kept[id];
  }
  return kept;
}

/** The line under the SharePoint app: Key Vault is named only for a saved secret, never for one typed but not saved. */
export function sharePointAppLine(app, stored) {
  if (!app || !app.clientId) return 'The app your IT creates with the Balsoft script. Needed for SharePoint destinations and sources.';
  const who = `Client ${app.clientId} in tenant ${app.tenantId}.`;
  if (app.clientSecret) return `${who} The new secret goes to Key Vault when you save.`;
  return stored ? `${who} Secret in Key Vault.` : `${who} No secret yet.`;
}

/** Why the SharePoint app test cannot run yet, or null when it can: it needs a library to try and the saved settings. */
export function sharePointTestBlock(firstLibrary, changed) {
  if (!firstLibrary) return 'Add a SharePoint destination first.';
  return changed ? 'Save first.' : null;
}

/** The SharePoint app: tenant, client and, when typed, a new secret (a blank one keeps the stored secret). */
export function setSharePointApp(draft, app) {
  const next = structuredClone(draft);
  next.sharePointAuth = { tenantId: String(app.tenantId || '').trim(), clientId: String(app.clientId || '').trim() };
  if (app.secret) next.sharePointAuth.clientSecret = app.secret;
  return next;
}

/** The document mailbox; the redirect list is where documents of a non-production ERP go instead of the customer. */
export function setMailbox(draft, mail) {
  const next = structuredClone(draft);
  next.mail = {
    enabled: Boolean(mail.enabled), tenantId: String(mail.tenantId || '').trim(), clientId: String(mail.clientId || '').trim(),
    from: String(mail.from || '').trim(), redirectTo: (mail.redirectTo || []).map((a) => String(a).trim()).filter(Boolean),
    saveToSentItems: mail.saveToSentItems !== false,
  };
  if (mail.secret) next.mail.clientSecret = mail.secret;
  return next;
}

/** What keeps the mailbox from being switched on, in words (the service checks the same on save). */
export function mailboxProblems(mail, sharePointTenant) {
  const problems = [];
  if (!mail || !mail.enabled) return problems;
  const address = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
  if (!(mail.tenantId || sharePointTenant)) problems.push('Enter the tenant ID.');
  if (!mail.clientId) problems.push('Enter the client ID of the mail app.');
  if (!mail.from || !address.test(mail.from)) problems.push('Enter the mailbox documents are sent from.');
  if (!(mail.redirectTo || []).length) problems.push('Enter at least one address for documents of a test ERP.');
  for (const a of mail.redirectTo || []) if (!address.test(a)) problems.push(`"${a}" is not an e-mail address.`);
  return problems;
}

/** The changes of the draft outside the document types: connections, the SharePoint app and the mailbox. */
export function settingsChanges(original, draft) {
  const lines = [];
  const o = original || {}, d = draft || {};
  for (const c of d.connections || []) {
    const was = (o.connections || []).find((x) => x.id === c.id);
    if (!was) lines.push(`Connection ${c.id} added`);
    else if (JSON.stringify(was.settings || {}) !== JSON.stringify(c.settings || {})) lines.push(`Connection ${c.id} changed`);
  }
  for (const c of o.connections || []) if (!(d.connections || []).some((x) => x.id === c.id)) lines.push(`Connection ${c.id} removed`);
  const a = o.sharePointAuth || {}, b = d.sharePointAuth || {};
  if (a.tenantId !== b.tenantId || a.clientId !== b.clientId || b.clientSecret) lines.push('SharePoint app changed');
  const m = o.mail || {}, n = d.mail || {};
  if (Boolean(m.enabled) !== Boolean(n.enabled)) lines.push(`Mail ${n.enabled ? 'on' : 'off'}`);
  else if (JSON.stringify({ ...m, clientSecret: undefined }) !== JSON.stringify({ ...n, clientSecret: undefined })) lines.push('Mailbox changed');
  if (n.clientSecret) lines.push('New secret for document mail');
  return lines;
}

/** The containers a storage connection is used with: Blob sources, PDF containers and Blob destinations. */
export function connectionContainers(draft, id) {
  const found = [];
  const add = (container, prefix) => {
    if (container && !found.some((f) => f.container === container && f.prefix === (prefix || ''))) found.push({ container, prefix: prefix || '' });
  };
  for (const t of (draft && draft.documentTypes) || []) {
    for (const c of t.companies || []) {
      const s = c.source || {};
      const v = s.settings || {};
      if (s.kind === 'blob' && s.connection === id) add(v.Container, v.Prefix);
      if (v.PdfConnection === id) add(v.PdfContainer, '');
      for (const x of c.targets || []) if (x.kind === 'blob' && x.connection === id) add((x.settings || {}).Container, '');
    }
  }
  return found;
}

/** Who uses something, short enough for a card: "Used by a, b and 2 more". */
export function usesWords(uses) {
  if (!uses.length) return 'Not used yet';
  if (uses.length <= 2) return `Used by ${uses.join(' and ')}`;
  return `Used by ${uses.slice(0, 2).join(', ')} and ${uses.length - 2} more`;
}

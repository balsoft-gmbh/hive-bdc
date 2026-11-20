// Pure functions of the Inbound pages (boards I1 to I6 of the 2026-11-20 inbound design): no DOM, no network, so Node's
// test runner checks them (tests/inbound.test.ts). Flows travel as GET /config gives them and the Windows app writes
// them (HiveBDC:Flows, PascalCase keys); keys this console does not know are kept.
/* eslint-disable @typescript-eslint/no-explicit-any */

const same = (a: any, b: any) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
const text = (v: any) => String(v ?? '').trim();

/** A flow as the console edits it: the computed DisplayName and TypeLabel of the answer are dropped. */
export function flowBody(f: any) {
  const { DisplayName: _d, TypeLabel: _t, ...rest } = f || {};
  const source = f?.Source || {};
  const produce = f?.Produce || {};
  return {
    ...structuredClone(rest),
    Id: text(f?.Id),
    Name: text(f?.Name),
    Enabled: f?.Enabled !== false,
    DocumentType: text(f?.DocumentType),
    DataAreaId: text(f?.DataAreaId),
    Source: { ...structuredClone(source), Kind: text(source.Kind), Connection: text(source.Connection), Settings: { ...(source.Settings || {}) } },
    Produce: {
      ...structuredClone(produce),
      Kind: text(produce.Kind) || 'csv',
      Settings: { ...(produce.Settings || {}) },
      Columns: (produce.Columns || []).map((c: any) => ({ Name: String(c?.Name ?? ''), Value: String(c?.Value ?? '') })),
    },
    Targets: (f?.Targets || []).map((t: any) => ({
      ...structuredClone(t),
      Kind: text(t?.Kind),
      Connection: text(t?.Connection),
      Enabled: t?.Enabled !== false,
      Settings: { ...(t?.Settings || {}) },
    })),
  };
}

/** The document type a flow stands under: its DocumentType, else its name (an entry written before 1.5.0). */
export function typeOf(f: any) {
  return text(f.DocumentType) || text(f.Name) || text(f.Id);
}

/** The inbound document types in the order of their first flow, each with its flows (one per company). */
export function inboundTypes(flows: any) {
  const types: { name: string; flows: any[] }[] = [];
  for (const f of flows || []) {
    const name = typeOf(f);
    const found = types.find((t) => same(t.name, name));
    if (found) found.flows.push(f);
    else types.push({ name, flows: [f] });
  }
  return types;
}

const listNames: Record<string, string> = { folder: 'Folder', blob: 'Blob container', servicebus: 'Service Bus' };

/** A source kind by the name the service reports ("SharePoint"), else the kind itself. */
const knownSources: Record<string, string> = { docuware: 'DocuWare', sharepoint: 'SharePoint' };

export function sourceName(kind: any, sources?: any) {
  const d = (sources || []).find((s: any) => same(s.kind, kind));
  return d ? d.name : knownSources[text(kind).toLowerCase()] || text(kind) || 'Nowhere yet';
}

/** The one switched-on destination of a flow (inbound is 1:1). */
export function destinationOf(f: any) {
  return (f.Targets || []).find((t: any) => t.Enabled !== false) || null;
}

/** Where a flow's CSV goes, in the words of a list cell: "Blob container erp-inbound/pending/". */
export function destinationWords(f: any) {
  const t = destinationOf(f);
  if (!t || !t.Kind) return 'Nowhere yet';
  const s = t.Settings || {};
  if (t.Kind === 'blob') return `Blob container ${[s.Container, s.Prefix].filter(Boolean).join('/').replace(/\/+/g, '/')}`.trim();
  if (t.Kind === 'servicebus') return s.Queue ? `Service Bus queue ${s.Queue}` : s.Topic ? `Service Bus topic ${s.Topic}` : 'Service Bus';
  if (t.Kind === 'folder') return s.Folder ? `Folder ${s.Folder}` : 'Folder';
  return t.Kind;
}

/** Which documents a flow picks up, in words: "Status is Ready for ERP". */
export function readyWords(f: any) {
  const s = (f.Source && f.Source.Settings) || {};
  const field = s.ReadyColumnName || s.ReadyColumn || s.ReadyFieldName || s.ReadyField;
  const value = s.ReadyValueName || s.ReadyValue;
  return field && value ? `${field} is ${value}` : 'Not set yet';
}

/** Where a flow's documents come from, in words: "SharePoint, library Vendor invoices". */
export function sourceWords(f: any, sources?: any) {
  const s = (f.Source && f.Source.Settings) || {};
  const name = sourceName(f.Source && f.Source.Kind, sources);
  if (s.LibraryName || s.Library) return `${name}, library ${s.LibraryName || s.Library}`;
  if (s.FileCabinetName || s.FileCabinet) return `${name}, cabinet ${s.FileCabinetName || s.FileCabinet}`;
  return name;
}

export type InboundRow = { name: string; companies: string[]; comesFrom: string; goesTo: string; enabled: boolean };

/** The rows of the Inbound list (board I1). */
export function inboundRows(flows: any, sources?: any): InboundRow[] {
  const unique = (xs: string[]) => xs.filter((x, i) => x && xs.indexOf(x) === i);
  return inboundTypes(flows).map((t) => ({
    name: t.name,
    companies: unique(t.flows.map((f: any) => f.DataAreaId || '?')),
    comesFrom: unique(t.flows.map((f: any) => sourceName(f.Source && f.Source.Kind, sources))).join(', ') || 'Nowhere yet',
    goesTo: unique(t.flows.map((f: any) => (destinationOf(f) ? listNames[destinationOf(f).Kind] || destinationOf(f).Kind : ''))).join(', ') || 'Nowhere yet',
    enabled: t.flows.some((f: any) => f.Enabled !== false),
  }));
}

/** A new flow id from the type's name, as the Windows app makes it: "vendor-invoice", "vendor-invoice-2". */
export function uniqueFlowId(flows: any, name: any) {
  let slug = String(name || '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
  if (!slug) slug = 'inbound';
  let id = slug;
  for (let n = 2; (flows || []).some((f: any) => same(f.Id, id)); n++) id = `${slug}-${n}`;
  return id;
}

const flowName = (type: string, company: string) => (company ? `${type} (${company})` : type);

const findFlow = (body: any, id: any) => {
  const f = (body.flows || []).find((x: any) => same(x.Id, id));
  if (!f) throw new Error(`There is no inbound flow ${id}.`);
  return f;
};

/**
 * Adds a company to an inbound document type (or starts the type): one flow reading from the chosen document system,
 * delivering to the first destination this run mode offers, CSV with ; and German numbers like the Windows app.
 */
export function addFlow(body: any, type: any, company: any, source: any, destinations?: any) {
  const name = text(type), code = text(company).toUpperCase();
  if (!name) throw new Error('Give the document type a name.');
  if (!code) throw new Error('Give the company in the ERP.');
  body.flows = body.flows || [];
  if (body.flows.some((f: any) => same(typeOf(f), name) && same(f.DataAreaId, code)))
    throw new Error(`Company ${code} already receives ${name}.`);
  const first = (destinations || [])[0];
  const id = uniqueFlowId(body.flows, name);
  body.flows.push({
    Id: id,
    Name: flowName(name, code),
    Enabled: true,
    DocumentType: name,
    DataAreaId: code,
    Source: { Kind: text(source && source.kind), Connection: same(source && source.kind, 'docuware') ? 'docuware' : '', Settings: {} },
    Produce: { Kind: 'csv', Settings: { Delimiter: ';', Culture: 'de-DE', RowsFrom: '', FileName: (source && source.defaultFileName) || '{ID}.csv' }, Columns: [] },
    Targets: first ? [{ Kind: first.kind, Connection: '', Enabled: true, Settings: { SaveFile: 'false' } }] : [],
  });
  return id;
}

export function removeFlow(body: any, id: any) {
  body.flows = (body.flows || []).filter((f: any) => !same(f.Id, id));
  return body;
}

export function removeInboundType(body: any, type: any) {
  body.flows = (body.flows || []).filter((f: any) => !same(typeOf(f), type));
  return body;
}

/** Renames a type: every flow of it gets the new DocumentType and name. */
export function renameInboundType(body: any, type: any, next: any) {
  const name = text(next);
  if (!name) throw new Error('Give the document type a name.');
  if (!same(name, type) && inboundTypes(body.flows).some((t) => same(t.name, name))) throw new Error(`There is already a document type ${name}.`);
  for (const f of body.flows || []) if (same(typeOf(f), type)) { f.DocumentType = name; f.Name = flowName(name, f.DataAreaId); }
  return body;
}

export function setTypeActive(body: any, type: any, on: any) {
  for (const f of body.flows || []) if (same(typeOf(f), type)) f.Enabled = Boolean(on);
  return body;
}

export function setFlowActive(body: any, id: any, on: any) {
  findFlow(body, id).Enabled = Boolean(on);
  return body;
}

export function setCompany(body: any, id: any, company: any) {
  const f = findFlow(body, id);
  f.DataAreaId = text(company).toUpperCase();
  f.Name = flowName(typeOf(f), f.DataAreaId);
  return body;
}

/** A different document system: its settings start empty, as in the Windows app. */
export function changeSource(body: any, id: any, source: any) {
  const f = findFlow(body, id);
  f.Source = { Kind: text(source.kind), Connection: same(source.kind, 'docuware') ? 'docuware' : '', Settings: {} };
  if (source.defaultFileName) f.Produce.Settings.FileName = source.defaultFileName;
  return body;
}

/** One setting of the source; a dropdown's label is kept next to it ({Key}Name), as the Windows app does. */
export function setSourceSetting(body: any, id: any, key: string, value: any, label?: any) {
  const s = findFlow(body, id).Source.Settings;
  s[key] = String(value ?? '');
  if (label && label !== value) s[`${key}Name`] = String(label);
  else delete s[`${key}Name`];
  return body;
}

/** Where the CSV goes: one destination; switching the kind keeps "save the document file". */
export function setDestinationKind(body: any, id: any, kind: any) {
  const f = findFlow(body, id);
  const before = destinationOf(f);
  if (before && before.Kind === kind) return body;
  const saveFile = (before && before.Settings && before.Settings.SaveFile) || 'false';
  f.Targets = [{ Kind: text(kind), Connection: '', Enabled: true, Settings: { SaveFile: saveFile } }];
  return body;
}

export function setDestination(body: any, id: any, changes: { connection?: any; settings?: Record<string, any> }) {
  const f = findFlow(body, id);
  let t = destinationOf(f);
  if (!t) throw new Error('Choose where the CSV goes first.');
  if (changes.connection !== undefined) t.Connection = text(changes.connection);
  for (const [k, v] of Object.entries(changes.settings || {})) t.Settings[k] = typeof v === 'boolean' ? (v ? 'true' : 'false') : String(v ?? '');
  return body;
}

export function setProduce(body: any, id: any, key: string, value: any) {
  findFlow(body, id).Produce.Settings[key] = String(value ?? '');
  return body;
}

export function setColumns(body: any, id: any, columns: { Name: string; Value: string }[]) {
  findFlow(body, id).Produce.Columns = columns.map((c) => ({ Name: String(c.Name ?? ''), Value: String(c.Value ?? '') }));
  return body;
}

/** One column per field of the document system, and per column of the table the rows come from (Windows FillFromFields). */
export function columnsFromFields(columns: any[], fields: any[], rowsFrom: any) {
  const out = columns.map((c) => ({ ...c }));
  const known = new Set(out.map((c) => String(c.Name).toLowerCase()));
  const add = (name: string, value: string) => {
    if (name && !known.has(name.toLowerCase())) { known.add(name.toLowerCase()); out.push({ Name: name, Value: value }); }
  };
  for (const f of fields || []) if (f.detail !== 'table' && !String(f.value || '').includes('.')) add(String(f.value || ''), `{${f.value}}`);
  const table = text(rowsFrom);
  if (table) for (const f of fields || []) if (String(f.value || '').toLowerCase().startsWith(`${table.toLowerCase()}.`)) add(String(f.value).slice(table.length + 1), `{${f.value}}`);
  return out;
}

/** The column names of the first line of a CSV the ERP reads (Windows ColumnsFromCsv): separator ;, tab or comma. */
export function columnsFromCsv(content: any, columns: any[], fields: any[]) {
  const header = String(content || '').replace(/^﻿/, '').split(/\r?\n/)[0] || '';
  const delimiter = header.includes(';') ? ';' : header.includes('\t') ? '\t' : ',';
  const out = columns.map((c) => ({ ...c }));
  const known = new Set(out.map((c) => String(c.Name).toLowerCase()));
  let added = 0;
  for (const raw of header.split(delimiter)) {
    const name = raw.trim().replace(/^"+|"+$/g, '').replace(/^﻿/, '');
    if (!name || known.has(name.toLowerCase())) continue;
    known.add(name.toLowerCase());
    const field = (fields || []).find((f: any) => same(f.value, name));
    out.push({ Name: name, Value: field ? `{${field.value}}` : '' });
    added++;
  }
  return { delimiter, columns: out, added };
}

const blobPrefix = (v: any) => {
  const p = text(v).replace(/\\/g, '/').replace(/^\/+/, '');
  return !p || p.endsWith('/') ? p : `${p}/`;
};

/** The service's own rule (BlobInboundTarget.SettingsProblems): the files must lie outside the folder the ERP reads. */
export function filePrefixProblem(target: any) {
  const s = (target && target.Settings) || {};
  if (target?.Kind !== 'blob' || String(s.SaveFile).toLowerCase() !== 'true') return '';
  const prefix = blobPrefix(s.Prefix);
  const files = blobPrefix(s.FilePrefix) || 'files/';
  if (!files.startsWith(prefix)) return '';
  return prefix
    ? `the folder for the files '${files}' lies inside the folder the ERP reads '${prefix}', so the ERP would take each file for a CSV. Choose a folder outside it.`
    : `the ERP reads the whole container, so it would take each file in '${files}' for a CSV. Set the folder in the container the ERP reads.`;
}

/** What keeps the inbound flows of a draft from saving, in words (the service checks the same when it runs them). */
export function flowProblems(body: any, destinations?: any) {
  const problems: string[] = [];
  for (const f of (body && body.flows) || []) {
    if (f.Enabled === false) continue;
    const where = `${typeOf(f)} › ${f.DataAreaId || 'new company'}`;
    if (!f.DataAreaId) problems.push(`${where}: give the company in the ERP.`);
    const t = destinationOf(f);
    if (!t || !t.Kind) { problems.push(`${where}: choose where your ERP reads the CSV.`); continue; }
    const d = (destinations || []).find((x: any) => same(x.kind, t.Kind));
    if (!d) {
      if ((destinations || []).length) problems.push(`${where}: it goes to ${t.Kind}, which this instance does not offer. Choose another place under Goes to.`);
      continue;
    }
    const s = t.Settings || {};
    if (d.connectionKind && !t.Connection) problems.push(`${where} › ${d.name}: choose the connection.`);
    for (const setting of d.settings || []) {
      if (setting.required === false) continue;
      if (setting.dependsOn && String(s[setting.dependsOn] || '').toLowerCase() !== 'true' && !String(s[setting.dependsOn] || '').trim()) continue;
      if (setting.dependsOn === 'SaveFile' && String(s.SaveFile).toLowerCase() !== 'true') continue;
      if (!text(s[setting.key])) problems.push(`${where} › ${d.name}: ${setting.label} is missing.`);
    }
    if (t.Kind === 'servicebus' && !text(s.Queue) && !text(s.Topic)) problems.push(`${where} › ${d.name}: give a queue or a topic.`);
    const overlap = filePrefixProblem(t);
    if (overlap) problems.push(`${where} › ${d.name}: ${overlap}`);
    if (!(f.Produce && f.Produce.Columns || []).some((c: any) => text(c.Name))) problems.push(`${where}: add at least one CSV column.`);
  }
  return problems;
}

/** The changes of the inbound flows for the Save bar, in the words of the pages. */
export function flowChanges(original: any, draft: any) {
  const lines: string[] = [];
  const o: any[] = original || [], d: any[] = draft || [];
  const label = (f: any) => `${typeOf(f)} › ${f.DataAreaId || 'new company'}`;
  for (const f of d) {
    const was = o.find((x) => same(x.Id, f.Id));
    if (!was) { lines.push(`Inbound ${typeOf(f)}: company ${f.DataAreaId || 'new'} added`); continue; }
    const where = label(f);
    if (!same(typeOf(was), typeOf(f))) lines.push(`Inbound ${typeOf(was)} is now ${typeOf(f)}`);
    if ((was.Enabled !== false) !== (f.Enabled !== false)) lines.push(`${where}: ${f.Enabled !== false ? 'active' : 'off'}`);
    if (!same(was.DataAreaId, f.DataAreaId)) lines.push(`${where}: company is now ${f.DataAreaId}`);
    if (JSON.stringify(was.Source) !== JSON.stringify(f.Source)) lines.push(`${where}: comes from ${sourceWords(f)}${readyWords(f) !== 'Not set yet' ? `, when ${readyWords(f)}` : ''}`);
    if (JSON.stringify(was.Targets) !== JSON.stringify(f.Targets)) lines.push(`${where}: goes to ${destinationWords(f)}`);
    if (JSON.stringify(was.Produce && was.Produce.Settings) !== JSON.stringify(f.Produce && f.Produce.Settings)) lines.push(`${where}: CSV settings changed`);
    if (JSON.stringify(was.Produce && was.Produce.Columns) !== JSON.stringify(f.Produce && f.Produce.Columns)) lines.push(`${where}: CSV columns changed`);
  }
  for (const f of o) if (!d.some((x) => same(x.Id, f.Id))) lines.push(`Inbound ${typeOf(f)}: ${f.DataAreaId ? `company ${f.DataAreaId}` : 'its flow'} removed`);
  return lines;
}

/** Inbound flows that use a connection, in words, so removing it asks first (failure case 15). */
export function flowConnectionUses(flows: any, id: any) {
  const uses: string[] = [];
  for (const f of flows || []) {
    const t = destinationOf(f);
    if (same(f.Source && f.Source.Connection, id)) uses.push(`Inbound ${typeOf(f)} › ${f.DataAreaId}: comes from it`);
    if (t && same(t.Connection, id)) uses.push(`Inbound ${typeOf(f)} › ${f.DataAreaId}: goes to it`);
    if (t && same(t.Settings && t.Settings.FileConnection, id)) uses.push(`Inbound ${typeOf(f)} › ${f.DataAreaId}: keeps its files there`);
  }
  return uses;
}

/** The request body of POST /sources/choices, /sources/sample and /sources/preview for a flow. */
export function sourceRequest(f: any, lookup = '') {
  return {
    kind: f.Source.Kind,
    lookup,
    connection: f.Source.Connection,
    settings: { ...f.Source.Settings },
    produce: f.Produce,
    dataAreaId: f.DataAreaId,
    target: destinationOf(f),
  };
}

export const NUMBER_FORMATS: [string, string][] = [
  ['de-DE', 'German: 1234,5 and 31.12.2026'],
  ['en-US', 'English (US): 1234.5 and 12/31/2026'],
  ['invariant', 'ISO: 1234.5 and 2026-12-31'],
];

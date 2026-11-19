// Pure functions of the web console: no DOM, no network, so Node's test runner checks them (tests/logic.test.ts).
/* eslint-disable @typescript-eslint/no-explicit-any */
import { addCompany, addConnection, httpsConnectionId, setCompanyActive, setHttpsKey, setSource, setType, suggestId, updateConnection } from './exportlogic';

const roleOrder = ['Viewer', 'Operator', 'Administrator'];

/** The highest role of the signed-in user, or '' when none is known. */
export function topRole(roles: any) {
  let best = -1;
  for (const role of roles || []) {
    const index = roleOrder.findIndex((r: any) => r.toLowerCase() === String(role).toLowerCase());
    if (index > best) best = index;
  }
  return best < 0 ? '' : roleOrder[best];
}

/** Initials for the user chip: "anna.weber@contoso.com" gives "AW", "Ertugrul Balveren" gives "EB". */
export function initials(name: any) {
  const text = String(name || '').split('@')[0];
  const parts = text.split(/[\s._-]+/).filter((p: any) => /[a-z]/i.test(p));
  if (parts.length === 0) return '?';
  const first = parts[0][0];
  const last = parts.length > 1 ? parts[parts.length - 1][0] : '';
  return (first + last).toUpperCase();
}

/** "Good afternoon, Anna": the first name from a display name or from "anna.berger@...", nothing for "x@...". */
export function greeting(hour: any, name: any) {
  const part = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
  const first = String(name || '').split('@')[0].split(/[\s._-]+/).find((p: any) => /^[\p{L}]{2,}$/u.test(p)) || '';
  return first ? `${part}, ${first.charAt(0).toUpperCase()}${first.slice(1)}` : part;
}

/** "WEDNESDAY 18.11.2026" above the overview's greeting. */
export function dayLine(date: any) {
  const two = (n: number) => String(n).padStart(2, '0');
  const weekday = ['SUNDAY', 'MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY'][date.getDay()];
  return `${weekday} ${two(date.getDate())}.${two(date.getMonth() + 1)}.${date.getFullYear()}`;
}

const stepWords: Record<string, string> = {
  received: 'Received',
  read: 'Read',
  produced: 'Made',
  delivered: 'Delivered',
  waiting: 'Waiting',
  failed: 'Failed',
  done: 'Done',
};

/** The history step in plain words. */
export function stepLabel(step: any) {
  return stepWords[String(step || '').toLowerCase()] || String(step || '');
}

/**
 * The documents that need attention, one row each: the newest event per document wins, and a document whose newest
 * event is "done" is left out.
 */
export function attentionRows(events: any) {
  const newest = new Map<string, any>();
  for (const e of events || []) {
    if (!e || !e.documentId) continue;
    const seen = newest.get(e.documentId);
    if (!seen || Number(e.id) > Number(seen.id)) newest.set(e.documentId, e);
  }
  return [...newest.values()]
    .filter((e: any) => String(e.step).toLowerCase() !== 'done')
    .sort((a: any, b: any) => Number(b.id) - Number(a.id))
    .map((e: any) => ({
      documentId: e.documentId,
      number: e.documentNumber || e.documentId,
      type: e.documentType || '',
      company: e.dataAreaId || '',
      target: e.target || '',
      waiting: String(e.state).toLowerCase() === 'waiting' || String(e.step).toLowerCase() === 'waiting',
      message: e.message || '',
      at: e.at || '',
    }));
}

/** History events with the newest first, whatever order the store returned. */
export function newestFirst(events: any) {
  return [...(events || [])].sort((a: any, b: any) => Number(b.id) - Number(a.id));
}

/** The licence line for the side panel and the warning banner. */
export function licenceLine(licence: any) {
  if (!licence) return { ok: false, text: 'Licence unknown' };
  const state = String(licence.state || '');
  if (state === 'Valid' || state === 'Warning' || state === 'Grace') {
    return { ok: state === 'Valid', text: licence.expires ? `Licence valid until ${germanDate(licence.expires)}` : 'Licence valid' };
  }
  if (state === 'Missing') return { ok: false, text: 'No licence yet' };
  return { ok: false, text: licence.message || `Licence ${state.toLowerCase()}` };
}

/** "2027-09-30" as "30.09.2027"; anything else unchanged. */
export function germanDate(iso: any) {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(iso || ''));
  return m ? `${m[3]}.${m[2]}.${m[1]}` : String(iso || '');
}

/** One change of the refusal in words: "SharePoint › Folder: {yyyy} is now {yyyy}/{MM}". */
export function changeLine(change: any) {
  if (change.before === '(none)') return `${change.path}: added`;
  if (change.after === '(none)') return `${change.path}: removed`;
  return `${change.path}: ${change.before} is now ${change.after}`;
}

/**
 * The panel of board W6 from the 409 answer of POST /config: who saved and when, both change lists, and whether
 * "Save both" may be offered (only when the start is known and no setting was changed by both).
 */
export function conflictView(refusal: any, timeZone?: string) {
  const r = refusal || {};
  const who = r.savedBy || 'Someone else';
  const at = r.savedAt ? new Date(r.savedAt) : null;
  const time = at && !Number.isNaN(at.getTime())
    ? at.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit', timeZone })
    : '';
  const overlaps = r.overlaps || [];
  const canSaveBoth = Boolean(r.baseKnown) && overlaps.length === 0;
  let note = 'Nothing was lost. Your change and theirs do not touch the same setting.';
  if (!r.baseKnown) note = 'Load the settings again, then make your change once more.';
  else if (overlaps.length > 0) note = 'You both changed the same setting. Take theirs or keep editing.';
  return {
    title: time ? `Not saved: ${who} saved these settings at ${time}, after you opened them` : `Not saved: ${who} saved these settings after you opened them`,
    note,
    theirs: (r.theirChanges || []).map(changeLine),
    yours: (r.yourChanges || []).map(changeLine),
    overlaps,
    canSaveBoth,
  };
}

// ── set-up assistant (boards W1 to W4) ─────────────────────────────────────────────

const same = (a: any, b: any) => String(a || '').toLowerCase() === String(b || '').toLowerCase();

/** Whether a licence state lets documents through. */
export function licenceWorks(licence: any) {
  return Boolean(licence) && ['Valid', 'Warning', 'Grace'].includes(String(licence.state));
}

/** Outbound company entries of switched-on types that take documents from a source, as { type, company }. */
export function sourceEntries(config: any) {
  const entries: { type: any; company: any }[] = [];
  for (const type of (config && config.documentTypes) || []) {
    if (type.enabled === false) continue;
    for (const company of type.companies || []) {
      if (company.source && company.source.kind) entries.push({ type, company });
    }
  }
  return entries;
}

/**
 * Whether the console opens on the assistant: its set-up is not finished, or nothing takes documents from a source yet.
 * A pause from the Service page keeps the other pages (failure case 78).
 */
export function setupNeeded(config: any) {
  if (!config) return false;
  return config.setupOpen === true || sourceEntries(config).length === 0;
}

/** Whether a company entry delivers anywhere, as the Azure settings check counts it. */
export function hasDestination(company: any, config: any) {
  if (company.sharePoint && company.sharePoint.enabled) return true;
  if ((company.targets || []).some((t: any) => t.enabled !== false)) return true;
  return company.mail === true && Boolean(config && config.mail && config.mail.enabled);
}

/** Which of the four steps are done, and the step to open. */
export function setupProgress(status: any, config: any) {
  const licence = licenceWorks(status && status.license);
  const entries = sourceEntries(config);
  const source = entries.length > 0;
  const destinations = source && entries.every((e: any) => hasDestination(e.company, config));
  const open = !licence ? 1 : !source ? 2 : !destinations ? 3 : 4;
  return { done: [licence, source, destinations, false], open };
}

const copy = (o: any) => ({ ...(o || {}) });

/**
 * GET /config as the body of POST /config: every setting goes back as it was read, secrets left out (a blank secret
 * keeps the stored one), with the version the editor started from (failure case 61).
 */
export function postBody(config: any) {
  const c = config || {};
  const auth = c.sharePointAuth || {};
  const mail = c.mail || {};
  return {
    baseVersion: c.version || '',
    paused: Boolean(c.paused),
    setupOpen: Boolean(c.setupOpen),
    pollSeconds: c.pollSeconds,
    logDir: c.logDir || '',
    license: { file: (c.license && c.license.file) || '' },
    sharePointAuth: { tenantId: auth.tenantId || '', clientId: auth.clientId || '' },
    mail: {
      enabled: Boolean(mail.enabled),
      tenantId: mail.tenantId || '',
      clientId: mail.clientId || '',
      from: mail.from || '',
      redirectTo: [...(mail.redirectTo || [])],
      saveToSentItems: mail.saveToSentItems !== false,
    },
    connections: (c.connections || []).map((x: any) => ({ id: x.id, kind: x.kind, settings: copy(x.settings) })),
    documentTypes: (c.documentTypes || []).map((t: any) => ({
      id: t.id,
      displayName: t.displayName || '',
      party: t.party,
      handler: t.handler,
      enabled: t.enabled !== false,
      mail: t.mail !== false,
      companies: (t.companies || []).map((r: any) => {
        const entry: any = {
          dataAreaId: r.dataAreaId,
          input: r.input || '',
          inputFormat: r.inputFormat || 'Json',
          output: r.output || '',
          enabled: r.enabled !== false,
          mail: Boolean(r.mail),
          sharePoint: {
            enabled: Boolean(r.sharePoint && r.sharePoint.enabled),
            siteUrl: (r.sharePoint && r.sharePoint.siteUrl) || '',
            library: (r.sharePoint && r.sharePoint.library) || '',
            folder: (r.sharePoint && r.sharePoint.folder) || '',
            fields: copy(r.sharePoint && r.sharePoint.fields),
          },
          targets: (r.targets || []).map((x: any) => ({
            kind: x.kind, connection: x.connection || '', enabled: x.enabled !== false, settings: copy(x.settings), fields: copy(x.fields),
          })),
        };
        if (r.source && r.source.kind) entry.source = { kind: r.source.kind, connection: r.source.connection || '', settings: copy(r.source.settings) };
        return entry;
      }),
    })),
  };
}

/** "https://Account.blob.core.windows.net/" as "https://account.blob.core.windows.net". */
export function cleanAccount(url: any) {
  return String(url || '').trim().replace(/\/+$/, '').toLowerCase();
}

/** A folder in a container as the Blob source reads it: "invoices" and "/invoices/" become "invoices/", empty stays empty. */
export function cleanPrefix(prefix: any) {
  const p = String(prefix || '').trim().replace(/^\/+/, '').replace(/\/+$/, '');
  return p ? `${p}/` : '';
}

/** The first free id: "fo-storage", then "fo-storage-2", ... */
export function uniqueId(items: any, base: any) {
  const taken = new Set((items || []).map((x: any) => String(x.id || '').toLowerCase()));
  if (!taken.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}-${n}`.toLowerCase())) return `${base}-${n}`;
}

/**
 * The type the assistant sets up from the check's counts: the known type with the most waiting exports (the order of
 * the settings breaks a tie) and its companies; every other waiting type is named as "later".
 */
export function setupType(byType: any, documentTypes: any) {
  const known = (documentTypes || []).map((t: any) => t.id);
  const totals = new Map<string, { id: string; count: number; companies: string[] }>();
  for (const row of byType || []) {
    const id = known.find((k: any) => same(k, row.documentType));
    if (!id || same(row.company, 'unknown')) continue;
    const total = totals.get(id) || { id, count: 0, companies: [] as string[] };
    total.count += Number(row.count) || 0;
    if (!total.companies.some((c: any) => same(c, row.company))) total.companies.push(row.company);
    totals.set(id, total);
  }
  const ranked = [...totals.values()].sort((a, b) => b.count - a.count || known.indexOf(a.id) - known.indexOf(b.id));
  const chosen = ranked[0] || null;
  const later: { documentType: string; count: number }[] = [];
  for (const row of byType || []) {
    if (chosen && same(row.documentType, chosen.id)) continue;
    const seen = later.find((l: any) => same(l.documentType, row.documentType));
    if (seen) seen.count += Number(row.count) || 0;
    else later.push({ documentType: row.documentType, count: Number(row.count) || 0 });
  }
  return { type: chosen ? chosen.id : null, companies: chosen ? chosen.companies : [], later };
}

/**
 * The settings after step 2: the storage connection (reused when one has the same account address), one company entry
 * per waiting company of the chosen type, all staging into one folder per container (the stager's notes keep an export
 * from being staged twice), mail off until step 3 says otherwise, and paused until Start.
 */
export function withBlobSource(body: any, source: any, byType: any, inputRoot: any) {
  const next = structuredClone(body);
  const account = cleanAccount(source.accountUrl);
  let connection = next.connections.find((c: any) => c.kind === 'azure-storage' && cleanAccount(c.settings && c.settings.AccountUrl) === account);
  if (!connection) {
    connection = { id: uniqueId(next.connections, 'fo-storage'), kind: 'azure-storage', settings: { AccountUrl: account } };
    next.connections.push(connection);
  }
  if (source.connectionString) connection.secret = source.connectionString;
  const plan = setupType(byType, next.documentTypes);
  next.paused = true;
  next.setupOpen = true;
  if (!plan.type) return { body: next, ...plan };
  const type = next.documentTypes.find((t: any) => same(t.id, plan.type));
  type.enabled = true;
  const prefix = cleanPrefix(source.prefix);
  // Only letters, digits and single hyphens reach the path, whatever a connection id or folder holds.
  const folder = [connection.id, source.container, prefix]
    .map((part: any) => String(part || '').replace(/[^A-Za-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, ''))
    .filter(Boolean).join('-');
  const input = `${String(inputRoot).replace(/\/+$/, '')}/${folder}`;
  for (const company of plan.companies) {
    let entry = type.companies.find((r: any) => same(r.dataAreaId, company));
    if (!entry) {
      entry = {
        dataAreaId: company, input, inputFormat: 'Json', output: '', enabled: true, mail: false,
        sharePoint: { enabled: false, siteUrl: '', library: '', folder: '', fields: {} }, targets: [],
      };
      type.companies.push(entry);
    }
    entry.input = input;
    entry.enabled = true;
    entry.source = { kind: 'blob', connection: connection.id, settings: prefix ? { Container: source.container, Prefix: prefix } : { Container: source.container } };
  }
  return { body: next, ...plan };
}

/** "1000, demf;DEMF 2000" as ["1000", "demf", "2000"]: company IDs typed by hand, each once. */
export function companyList(text: any) {
  const list: string[] = [];
  for (const part of String(text || '').split(/[\s,;]+/)) if (part && !list.some((c) => same(c, part))) list.push(part);
  return list;
}

/** The company entry of the type switched on; a missing one is added the way the Outbound page adds it. */
function activeCompany(body: any, typeId: any, company: any) {
  const type = body.documentTypes.find((t: any) => same(t.id, typeId));
  const entry = type && type.companies.find((c: any) => same(c.dataAreaId, company));
  return entry ? setCompanyActive(body, typeId, entry.dataAreaId, true) : addCompany(body, typeId, company);
}

/**
 * The settings after step 2 for a Service Bus source, made with the Outbound and Connections pages' own helpers
 * (exportlogic.ts), so the body is theirs for the same choice (design version 3, S1). The bus connection is one with the
 * same namespace, else the one the set-up saved before (choice.connection), else a new one; a connection whose secret is
 * stored (choice.withSecret) serves the connection string only, so a managed identity gets its own. The storage
 * account of the PDFs is reused by its address as in withBlobSource. Paused until Start.
 */
export function withServiceBusSource(body: any, choice: any, inputRoot: any) {
  let next = structuredClone(body);
  const keyed = (id: any) => (choice.withSecret || []).includes(id);
  const key = choice.access === 'key';
  const namespace = key ? '' : String(choice.namespace || '').trim();
  const secret = key ? String(choice.connectionString || '').trim() : '';
  const buses = next.connections.filter((c: any) => c.kind === 'servicebus');
  const twin = key || !namespace ? null : buses.find((c: any) => !keyed(c.id) && same(c.settings && c.settings.Namespace, namespace));
  const kept = buses.find((c: any) => c.id === choice.connection && (key || !keyed(c.id)));
  let id: string;
  if (twin) id = twin.id;
  else if (kept) {
    id = kept.id;
    next = updateConnection(next, id, key ? {} : { Namespace: namespace }, secret);
  } else {
    id = suggestId(next.connections, 'servicebus');
    next = addConnection(next, { id, kind: 'servicebus', settings: { Namespace: namespace }, secret });
  }

  const account = cleanAccount(choice.pdfAccount);
  let pdf = '';
  if (account) {
    const found = next.connections.find((c: any) => c.kind === 'azure-storage' && cleanAccount(c.settings && c.settings.AccountUrl) === account);
    pdf = found ? found.id : uniqueId(next.connections, 'fo-storage');
    if (!found) next = addConnection(next, { id: pdf, kind: 'azure-storage', settings: { AccountUrl: account } });
  }
  const entity = choice.mode === 'topic' ? { Topic: choice.topic, Subscription: choice.subscription } : { Queue: choice.queue };
  const settings = { ...entity, PdfConnection: pdf, PdfContainer: pdf ? choice.pdfContainer : '' };

  next = setType(next, choice.type, { enabled: true });
  for (const company of choice.companies) {
    next = activeCompany(next, choice.type, company);
    next = setSource(next, choice.type, company, { kind: 'servicebus', connection: id, settings }, inputRoot);
  }
  next.paused = true;
  next.setupOpen = true;
  return { body: next, connection: id };
}

/**
 * The settings after step 2 for a web address: per company the Outbound page's setHttpsKey with its new key, on the
 * connection the company has already, else in-{type}-{company}; the type switched on, paused until Start.
 */
export function withHttpsSource(body: any, typeId: any, keys: { company: string; key: string }[], inputRoot: any) {
  let next = setType(body, typeId, { enabled: true });
  for (const { company, key } of keys) {
    next = activeCompany(next, typeId, company);
    const type = next.documentTypes.find((t: any) => same(t.id, typeId));
    const entry = type.companies.find((c: any) => same(c.dataAreaId, company));
    const id = (entry.source && entry.source.kind === 'https' && entry.source.connection) || httpsConnectionId(type.id, entry.dataAreaId);
    next = setHttpsKey(next, type.id, entry.dataAreaId, key, inputRoot, id);
  }
  next.paused = true;
  next.setupOpen = true;
  return next;
}

/**
 * The destinations step 3 shows for one type, read from the settings: per destination kind the companies that have it
 * and its settings (SharePoint from the company's SharePoint block, mail from the company switch).
 */
export function destinationPicks(type: any, kinds: any) {
  const entries = (type.companies || []).filter((r: any) => r.source && r.source.kind);
  const picks: any[] = [];
  for (const kind of kinds) {
    const pick: any = { kind, companies: [], settings: {}, connection: '' };
    const seen = new Set<string>();
    for (const entry of entries) {
      if (kind === 'sharepoint') {
        if (!entry.sharePoint || !entry.sharePoint.enabled) continue;
        pick.settings = { SiteUrl: entry.sharePoint.siteUrl || '', Library: entry.sharePoint.library || '', Folder: entry.sharePoint.folder || '' };
      } else if (kind === 'mail') {
        if (entry.mail !== true) continue;
      } else {
        const target = (entry.targets || []).find((t: any) => t.kind === kind && t.enabled !== false);
        if (!target) continue;
        pick.settings = copy(target.settings);
        pick.connection = target.connection || '';
      }
      seen.add(JSON.stringify([pick.settings, pick.connection]));
      pick.companies.push(entry.dataAreaId);
    }
    // Companies set up differently keep their own values until the step's values are edited.
    if (seen.size > 1) pick.mixed = true;
    if (pick.companies.length > 0) picks.push(pick);
  }
  return picks;
}

/**
 * The settings after step 3: each company entry of the type that has a source gets exactly the chosen destinations of
 * the kinds the step offers; a target of a kind this version does not offer here is kept as it is.
 */
export function withDestinations(body: any, typeId: any, picks: any, offered: any) {
  const next = structuredClone(body);
  const type = next.documentTypes.find((t: any) => same(t.id, typeId));
  if (!type) return next;
  for (const entry of type.companies.filter((r: any) => r.source && r.source.kind)) {
    const mine = picks.filter((p: any) => p.companies.some((c: any) => same(c, entry.dataAreaId)));
    const sharePoint = mine.find((p: any) => p.kind === 'sharepoint');
    const ownSharePoint = sharePoint && sharePoint.mixed && entry.sharePoint && entry.sharePoint.enabled;
    entry.sharePoint = !sharePoint ? { ...entry.sharePoint, enabled: false }
      : ownSharePoint ? { ...entry.sharePoint }
        : { ...entry.sharePoint, enabled: true, siteUrl: sharePoint.settings.SiteUrl || '', library: sharePoint.settings.Library || '', folder: sharePoint.settings.Folder || '' };
    entry.mail = mine.some((p: any) => p.kind === 'mail');
    const kept = (entry.targets || []).filter((t: any) => !offered.includes(t.kind));
    const chosen = mine.filter((p: any) => p.kind !== 'sharepoint' && p.kind !== 'mail').map((p: any) => {
      const old = (entry.targets || []).find((t: any) => t.kind === p.kind);
      if (p.mixed && old) return { ...old, enabled: true, settings: copy(old.settings), fields: copy(old.fields) };
      return { kind: p.kind, connection: p.connection || '', enabled: true, settings: copy(p.settings), fields: copy(old && old.fields) };
    });
    entry.targets = [...kept, ...chosen];
  }
  return next;
}

/**
 * What still keeps step 3 from saving, in words: every company needs a destination, every chosen destination a working
 * connection and its required settings (failure case 63).
 */
export function destinationProblems(picks: any, companies: any, descriptors: any, ready: any) {
  const problems: string[] = [];
  if (picks.length === 0) problems.push('Choose at least one place.');
  for (const company of companies) {
    if (picks.length > 0 && !picks.some((p: any) => p.companies.some((c: any) => same(c, company)))) {
      problems.push(`Company ${company} has no place yet.`);
    }
  }
  for (const pick of picks) {
    const d = descriptors.find((x: any) => x.kind === pick.kind);
    const name = d ? d.name : pick.kind;
    if (pick.companies.length === 0) problems.push(`${name}: tick at least one company.`);
    if (!ready[pick.kind]) problems.push(`${name}: connect it first.`);
    for (const s of (d && d.settings) || []) {
      if (s.required !== false && !String(pick.settings[s.key] || '').trim()) problems.push(`${name}: ${s.label} is missing.`);
    }
  }
  return problems;
}

/**
 * One step of a test run for the W4 list: time of day, plain words, and whether it went well. The history's stamp
 * ("2026-11-18 13:12:21") is the instance's own clock and is shown as it is, like the document history shows it.
 */
export function runLine(event: any, timeZone: any) {
  const raw = String((event && event.at) || '');
  const plain = /^\d{4}-\d{2}-\d{2}[ T](\d{2}:\d{2}:\d{2})$/.exec(raw);
  const at = plain ? null : raw ? new Date(raw) : null;
  const time = plain ? plain[1]
    : at && !Number.isNaN(at.getTime()) ? at.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit', second: '2-digit', timeZone })
      : '';
  const state = String((event && event.state) || '').toLowerCase();
  return { time, text: (event && event.message) || stepLabel(event && event.step), ok: state === 'ok', waiting: state === 'waiting' };
}

const typeNames: Record<string, string> = {
  salesinvoice: 'sales invoices',
  freetextinvoice: 'free text invoices',
  salesconfirmation: 'sales confirmations',
  salesquotation: 'sales quotations',
  collectionletter: 'collection letters',
  purchaseorder: 'purchase orders',
  paymentadvice: 'payment advices',
};

const typeName: Record<string, string> = {
  salesinvoice: 'sales invoice',
  freetextinvoice: 'free text invoice',
  salesconfirmation: 'sales confirmation',
  salesquotation: 'sales quotation',
  collectionletter: 'collection letter',
  purchaseorder: 'purchase order',
  paymentadvice: 'payment advice',
};

/** The name of a document type in titles, breadcrumbs and paths, as the Windows app shows it: its name, else its ID. */
export function typeLabel(type: any) {
  const name = String((type && type.displayName) || '').trim();
  return name || String((type && type.id) || '').trim();
}

/** A document type in the words of a sentence: "sales invoices" for the built-ins (one: "sales invoice"), else its own name. */
export function typeWords(type: any, count?: number) {
  const id = String((type && type.id) || '').toLowerCase();
  const names = count === 1 ? typeName : typeNames;
  return names[id] || (type && type.displayName) || String((type && type.id) || '');
}

/** "1000", "1000 and DEMF", "1000, 2000 and DEMF". */
export function andList(items: any) {
  const list = (items || []).map(String);
  return list.length <= 1 ? list.join('') : `${list.slice(0, -1).join(', ')} and ${list[list.length - 1]}`;
}

/** The licence line under "Licensed to": its own message only when it warns (Warning, Grace). */
export function licenceNote(licence: any) {
  return licence && ['Warning', 'Grace'].includes(String(licence.state)) ? licence.message || '' : '';
}

const featureNames: Record<string, string> = { einvoice: 'e-invoice', documents: 'documents', sharepoint: 'document systems' };

/** The licence features as words: "e-invoice, documents, document systems". */
export function featureWords(features: any) {
  return (features || []).map((f: any) => featureNames[String(f).toLowerCase()] || String(f)).join(', ');
}

/**
 * The command that gives the instance's identity access to the storage account, before the first check (the check
 * answers with the same command from the service).
 */
export function roleCommand(objectId: any, accountUrl: any) {
  const match = /^https:\/\/([a-z0-9]+)\.blob\./i.exec(String(accountUrl || '').trim());
  if (!objectId || !match) return '';
  return `az role assignment create --assignee-object-id ${objectId} --assignee-principal-type ServicePrincipal `
    + `--role "Storage Blob Data Contributor" --scope "$(az storage account show -n ${match[1].toLowerCase()} --query id -o tsv)"`;
}
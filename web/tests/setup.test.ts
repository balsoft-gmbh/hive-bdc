// npm test: the set-up assistant's logic, ported from tests/console/setup.test.mjs, and the assistant's own helpers.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  andList, cleanAccount, cleanPrefix, destinationPicks, destinationProblems, featureWords, hasDestination, licenceNote, postBody,
  roleCommand, runLine, setupNeeded, setupProgress, setupType, typeWords, uniqueId, withBlobSource, withDestinations,
} from '../src/lib/logic';
import {
  ENTRA_PATH, ROLES, SETUP_LINE, SETUP_TITLE, STEP_NAMES, entryName, licenceRequest, shownStep, stepHref, stepLabel, typesWord, upperFirst,
} from '../src/app/setup/steps';

const fresh = (): any => ({
  version: 'v1',
  pollSeconds: 30,
  logDir: '/home/LogFiles/hivebdc',
  paused: false,
  license: { file: '' },
  sharePointAuth: { tenantId: '', clientId: '', secretSet: false },
  mail: { enabled: false, tenantId: '', clientId: '', secretSet: false, from: '', redirectTo: [], saveToSentItems: true },
  connections: [],
  documentTypes: [
    { id: 'SalesInvoice', displayName: 'Rechnung', party: 'Customer', handler: 'EInvoice', enabled: true, mail: true, discovered: false, companies: [] },
    { id: 'SalesConfirmation', displayName: 'Auftragsbestätigung', party: 'Customer', handler: 'Pdf', enabled: true, mail: true, discovered: false, companies: [] },
  ],
});

const waiting = [
  { documentType: 'SalesInvoice', company: '1000', count: 2 },
  { documentType: 'SalesConfirmation', company: '1000', count: 1 },
  { documentType: 'SalesInvoice', company: 'DEMF', count: 1 },
  { documentType: 'unknown', company: 'DEMF', count: 1 },
];

const source = { accountUrl: 'https://StBalsoftFo.blob.core.windows.net/', container: 'bdc-export' };

test('a fresh instance and an unfinished set-up open the assistant; a running or merely paused one does not', () => {
  const config = fresh();
  assert.equal(setupNeeded(config), true);
  const { body } = withBlobSource(postBody(config), source, waiting, '/home/data/hivebdc/in');
  assert.equal(body.setupOpen, true);
  assert.equal(body.paused, true);
  assert.equal(setupNeeded(body), true);
  assert.equal(setupNeeded({ ...body, setupOpen: false, paused: true }), false);
  assert.equal(setupNeeded({ ...body, setupOpen: false, paused: false }), false);
  assert.equal(setupNeeded(null), false);
});

test('a switched-off type with a source does not count as set up', () => {
  const { body } = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in');
  body.paused = false;
  body.setupOpen = false;
  body.documentTypes[0].enabled = false;
  assert.equal(setupNeeded(body), true);
});

test('every setting goes back as it was read, without secrets and with the version', () => {
  const config = fresh();
  config.sharePointAuth = { tenantId: 't', clientId: 'c', secretSet: true };
  config.mail = { enabled: true, tenantId: '', clientId: 'm', secretSet: true, from: 'invoice@contoso.example', redirectTo: ['test@contoso.example'], saveToSentItems: false };
  config.connections = [{ id: 'dw', kind: 'docuware', settings: { Url: 'https://x.docuware.cloud', UserName: 'ax' }, hasSecret: true }];
  config.documentTypes[0].companies = [{
    dataAreaId: '1000', input: '/home/in', inputFormat: 'Json', output: '', enabled: true, mail: false,
    sharePoint: { enabled: true, siteUrl: 'https://contoso.sharepoint.com/sites/F', library: 'Invoices', folder: '{InvoiceDate:yyyy}', fields: { Title: '{DocumentNumber}' } },
    targets: [{ kind: 'docuware', connection: 'dw', enabled: true, settings: { FileCabinet: 'abc' }, fields: { NR: '{DocumentNumber}' } }],
    source: { kind: 'blob', connection: 'fo', settings: { Container: 'c' } },
  }];
  const body: any = postBody(config);
  assert.equal(body.baseVersion, 'v1');
  assert.equal(body.logDir, '/home/LogFiles/hivebdc');
  assert.deepEqual(body.sharePointAuth, { tenantId: 't', clientId: 'c' });
  assert.equal(body.mail.saveToSentItems, false);
  assert.deepEqual(body.mail.redirectTo, ['test@contoso.example']);
  assert.equal('clientSecret' in body.mail, false);
  assert.deepEqual(body.connections, [{ id: 'dw', kind: 'docuware', settings: { Url: 'https://x.docuware.cloud', UserName: 'ax' } }]);
  const entry = body.documentTypes[0].companies[0];
  assert.deepEqual(entry.sharePoint.fields, { Title: '{DocumentNumber}' });
  assert.deepEqual(entry.targets[0].fields, { NR: '{DocumentNumber}' });
  assert.deepEqual(entry.source, { kind: 'blob', connection: 'fo', settings: { Container: 'c' } });
  assert.equal('source' in body.documentTypes[1], false);
  assert.equal(JSON.stringify(body).includes('secretSet'), false);
  assert.equal(JSON.stringify(body).includes('hasSecret'), false);
});

test('the type with the most waiting exports is set up, the rest named as later', () => {
  const plan = setupType(waiting, fresh().documentTypes);
  assert.equal(plan.type, 'SalesInvoice');
  assert.deepEqual(plan.companies, ['1000', 'DEMF']);
  assert.deepEqual(plan.later, [{ documentType: 'SalesConfirmation', count: 1 }, { documentType: 'unknown', count: 1 }]);
  assert.equal(setupType([], fresh().documentTypes).type, null);
  assert.equal(setupType([{ documentType: 'Unheard', company: '1000', count: 5 }], fresh().documentTypes).type, null);
});

test('step 2 adds one connection, one entry per company with a shared staging folder, mail off, paused', () => {
  const result = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in/');
  const { body } = result;
  assert.equal(result.type, 'SalesInvoice');
  assert.deepEqual(body.connections, [{ id: 'fo-storage', kind: 'azure-storage', settings: { AccountUrl: 'https://stbalsoftfo.blob.core.windows.net' } }]);
  const companies = body.documentTypes[0].companies;
  assert.deepEqual(companies.map((c: any) => c.dataAreaId), ['1000', 'DEMF']);
  for (const c of companies) {
    assert.equal(c.input, '/home/data/hivebdc/in/fo-storage-bdc-export');
    assert.equal(c.mail, false);
    assert.deepEqual(c.source, { kind: 'blob', connection: 'fo-storage', settings: { Container: 'bdc-export' } });
  }
  assert.deepEqual(body.documentTypes[1].companies, []);
  assert.equal(body.paused, true);
});

test('step 2 again reuses the connection of the same account and keeps an entry\'s destinations', () => {
  const first = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in').body;
  first.documentTypes[0].companies[0].targets = [{ kind: 'blob', connection: 'fo-storage', enabled: true, settings: { Container: 'out' }, fields: {} }];
  const second = withBlobSource(first, { ...source, accountUrl: 'https://stbalsoftfo.blob.core.windows.net', connectionString: 'DefaultEndpointsProtocol=https;AccountName=x' }, waiting, '/home/data/hivebdc/in').body;
  assert.equal(second.connections.length, 1);
  assert.equal(second.connections[0].secret, 'DefaultEndpointsProtocol=https;AccountName=x');
  assert.equal(second.documentTypes[0].companies.length, 2);
  assert.equal(second.documentTypes[0].companies[0].targets.length, 1);
});

test('a folder in the container goes into the source and its own staging folder', () => {
  assert.equal(cleanPrefix(' /F&O exports/ '), 'F&O exports/');
  assert.equal(cleanPrefix(''), '');
  const { body } = withBlobSource(postBody(fresh()), { ...source, prefix: 'hivebdc-setup' }, waiting, '/home/data/hivebdc/in');
  const entry = body.documentTypes[0].companies[0];
  assert.deepEqual(entry.source.settings, { Container: 'bdc-export', Prefix: 'hivebdc-setup/' });
  assert.equal(entry.input, '/home/data/hivebdc/in/fo-storage-bdc-export-hivebdc-setup');
  const odd: any = postBody(fresh());
  odd.connections = [{ id: '../../etc', kind: 'azure-storage', settings: { AccountUrl: 'https://stbalsoftfo.blob.core.windows.net' } }];
  const moved = withBlobSource(odd, { ...source, prefix: '../x' }, waiting, '/home/data/hivebdc/in').body;
  assert.equal(moved.documentTypes[0].companies[0].input, '/home/data/hivebdc/in/etc-bdc-export-x');
});

test('ids and account addresses are compared the way the service stores them', () => {
  assert.equal(uniqueId([{ id: 'fo-storage' }, { id: 'FO-STORAGE-2' }], 'fo-storage'), 'fo-storage-3');
  assert.equal(uniqueId([], 'docuware'), 'docuware');
  assert.equal(cleanAccount(' https://A.blob.core.windows.net// '), 'https://a.blob.core.windows.net');
});

test('chosen destinations land on each company and read back the same', () => {
  let body = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in').body;
  const offered = ['sharepoint', 'docuware', 'blob', 'mail'];
  const picks = [
    { kind: 'sharepoint', companies: ['1000', 'DEMF'], settings: { SiteUrl: 'https://contoso.sharepoint.com/sites/F', Library: 'Invoices', Folder: '{InvoiceDate:yyyy}' }, connection: '' },
    { kind: 'docuware', companies: ['1000'], settings: { FileCabinet: 'abc', MatchField: 'NR' }, connection: 'docuware' },
    { kind: 'mail', companies: ['DEMF'], settings: {}, connection: '' },
  ];
  body.documentTypes[0].companies[0].targets = [{ kind: 'future-dms', connection: 'x', enabled: true, settings: {}, fields: { A: 'B' } }];
  body = withDestinations(body, 'SalesInvoice', picks, offered);
  const [c1000, demf] = body.documentTypes[0].companies;
  assert.equal(c1000.sharePoint.enabled, true);
  assert.equal(c1000.sharePoint.library, 'Invoices');
  assert.equal(c1000.mail, false);
  assert.equal(demf.mail, true);
  assert.deepEqual(c1000.targets.map((t: any) => t.kind), ['future-dms', 'docuware']);
  assert.deepEqual(demf.targets, []);
  assert.deepEqual(destinationPicks(body.documentTypes[0], offered), picks);

  const fewer = withDestinations(body, 'SalesInvoice', [picks[0]], offered);
  assert.deepEqual(fewer.documentTypes[0].companies[0].targets.map((t: any) => t.kind), ['future-dms']);
  assert.equal(fewer.documentTypes[0].companies[1].mail, false);
});

test('companies set up differently keep their own values until the step is edited', () => {
  const body = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in').body;
  const [a, b] = body.documentTypes[0].companies;
  a.targets = [{ kind: 'blob', connection: 'own', enabled: true, settings: { Container: 'out-a' }, fields: {} }];
  b.targets = [{ kind: 'blob', connection: 'own', enabled: true, settings: { Container: 'out-b' }, fields: {} }];
  const picks = destinationPicks(body.documentTypes[0], ['blob']);
  assert.equal(picks[0].mixed, true);
  const kept = withDestinations(body, 'SalesInvoice', picks, ['blob']).documentTypes[0].companies;
  assert.deepEqual(kept.map((c: any) => c.targets[0].settings.Container), ['out-a', 'out-b']);
  const edited = withDestinations(body, 'SalesInvoice', [{ ...picks[0], mixed: false, settings: { Container: 'out-all' } }], ['blob']).documentTypes[0].companies;
  assert.deepEqual(edited.map((c: any) => c.targets[0].settings.Container), ['out-all', 'out-all']);
});

test('progress follows licence, source and destinations', () => {
  const status = { license: { state: 'Valid' } };
  let body = withBlobSource(postBody(fresh()), source, waiting, '/home/data/hivebdc/in').body;
  assert.equal(setupProgress({ license: { state: 'Missing' } }, fresh()).open, 1);
  assert.equal(setupProgress(status, fresh()).open, 2);
  assert.equal(setupProgress(status, body).open, 3);
  body = withDestinations(body, 'SalesInvoice', [{ kind: 'blob', companies: ['1000', 'DEMF'], settings: { Container: 'out' }, connection: 'fo-storage' }], ['blob']);
  assert.deepEqual(setupProgress(status, body), { done: [true, true, true, false], open: 4 });
});

test('mail counts as a destination only with a mailbox set up', () => {
  assert.equal(hasDestination({ mail: true, targets: [] }, { mail: { enabled: false } }), false);
  assert.equal(hasDestination({ mail: true, targets: [] }, { mail: { enabled: true } }), true);
  assert.equal(hasDestination({ mail: false, targets: [{ kind: 'blob', enabled: false }] }, {}), false);
});

test('step 3 names what keeps it from saving', () => {
  const descriptors = [
    { kind: 'sharepoint', name: 'SharePoint library', settings: [{ key: 'SiteUrl', label: 'Site address' }, { key: 'Library', label: 'Library' }, { key: 'Folder', label: 'Folder (optional)', required: false }] },
  ];
  assert.deepEqual(destinationProblems([], ['1000'], descriptors, {}), ['Choose at least one place.']);
  const pick = { kind: 'sharepoint', companies: ['1000'], settings: { SiteUrl: 'https://x.sharepoint.com/sites/F' }, connection: '' };
  assert.deepEqual(destinationProblems([pick], ['1000', 'DEMF'], descriptors, {}), [
    'Company DEMF has no place yet.',
    'SharePoint library: connect it first.',
    'SharePoint library: Library is missing.',
  ]);
  const done = { ...pick, companies: ['1000', 'DEMF'], settings: { ...pick.settings, Library: 'Invoices' } };
  assert.deepEqual(destinationProblems([done], ['1000', 'DEMF'], descriptors, { sharepoint: true }), []);
});

test('a test-run step reads as time, words and outcome', () => {
  const line = runLine({ at: '2026-11-18T09:14:02Z', step: 'delivered', state: 'ok', message: 'Delivered to SharePoint' }, 'Europe/Berlin');
  assert.deepEqual(line, { time: '10:14:02', text: 'Delivered to SharePoint', ok: true, waiting: false });
  assert.equal(runLine({ at: '2026-11-18 13:12:21', state: 'ok' }, 'America/New_York').time, '13:12:21');
  assert.equal(runLine({ step: 'failed', state: 'error' }, 'Europe/Berlin').text, 'Failed');
});

test('types, features and the role command read as the boards show them', () => {
  assert.equal(typeWords({ id: 'SalesInvoice', displayName: 'Rechnung' }, undefined), 'sales invoices');
  assert.equal(typeWords({ id: 'Lieferschein', displayName: 'Lieferschein' }, undefined), 'Lieferschein');
  assert.equal(featureWords(['einvoice', 'documents', 'sharepoint']), 'e-invoice, documents, document systems');
  assert.equal(
    roleCommand('4c1f9a20-5d7e-4b8e-9a61-2f0c7d3e8b15', 'https://StBalsoftFo.blob.core.windows.net'),
    'az role assignment create --assignee-object-id 4c1f9a20-5d7e-4b8e-9a61-2f0c7d3e8b15 --assignee-principal-type ServicePrincipal '
      + '--role "Storage Blob Data Contributor" --scope "$(az storage account show -n stbalsoftfo --query id -o tsv)"');
  assert.equal(roleCommand('', 'https://a.blob.core.windows.net'), '');
  assert.equal(roleCommand('x', 'not an address'), '');
});

test('counts, lists and licence notes read as plain sentences', () => {
  assert.equal(typeWords({ id: 'SalesQuotation' }, 1), 'sales quotation');
  assert.equal(typeWords({ id: 'SalesQuotation' }, 2), 'sales quotations');
  assert.equal(andList(['1000']), '1000');
  assert.equal(andList(['1000', 'DEMF']), '1000 and DEMF');
  assert.equal(andList(['1000', '2000', '4000', '5000']), '1000, 2000, 4000 and 5000');
  assert.equal(licenceNote({ state: 'Valid', message: 'Licensed to X, valid until 2027-09-30' }), '');
  assert.equal(licenceNote({ state: 'Warning', message: 'Expires in 20 days' }), 'Expires in 20 days');
});

test('the assistant opens the step asked for while it is reached, else the step reached (failure case 9)', () => {
  assert.equal(shownStep(null, 3), 3);
  assert.equal(shownStep('', 2), 2);
  assert.equal(shownStep('1', 3), 1);
  assert.equal(shownStep('3', 3), 3);
  assert.equal(shownStep('4', 2), 2);
  assert.equal(shownStep('0', 4), 4);
  assert.equal(shownStep('2.5', 4), 4);
  assert.equal(shownStep('two', 4), 4);
  assert.equal(stepHref(2), '/setup/?step=2');
  assert.equal(stepHref(3, 'Sales Invoice&x'), '/setup/?step=3&type=Sales%20Invoice%26x');
});

test('the licence request mail names the tenant and the instance address, encoded', () => {
  const link = licenceRequest('00000000-0000-0000-0000-000000000002', 'https://hivebdc-contoso.example.net');
  const url = new URL(link);
  assert.equal(url.protocol, 'mailto:');
  assert.equal(url.pathname, 'support@balsoft.de');
  assert.match(link, /\?subject=Hive%20BDC%20licence&body=/);
  const body = decodeURIComponent(link.split('&body=')[1]);
  assert.match(body, /Tenant ID: 00000000-0000-0000-0000-000000000002\r\n/);
  assert.match(body, /Instance address: https:\/\/hivebdc-contoso\.example\.net$/);
  assert.equal(link.includes(' '), false);
  assert.equal(link.split('&body=')[1].includes('&'), false);
});

test('the step names and the Entra path read as the Windows app and the design say', () => {
  assert.deepEqual(STEP_NAMES, ['Licence', 'Where F&O hands over', 'Where documents go', 'Test run']);
  assert.equal(ENTRA_PATH, 'Microsoft Entra admin center > Enterprise applications > Hive BDC sign-in > Users and groups');
  assert.equal(typesWord({ id: 'SalesInvoice', displayName: 'Sales invoice' }), 'sales invoices');
});

test('the title has one line of at most 60 characters, and the roles one line each (design version 2, change 5)', () => {
  assert.equal(SETUP_TITLE, 'Set up Hive BDC');
  assert.ok(SETUP_LINE.length <= 60, `${SETUP_LINE.length} characters`);
  assert.doesNotMatch(SETUP_LINE, /welcome|your|just|simply/i);
  assert.deepEqual(ROLES.map((r) => r.name), ['Viewer', 'Operator', 'Administrator']);
  for (const role of ROLES) assert.ok(role.text.length <= 60 && !role.text.includes('\n'), role.name);
});

test('a step in the step bar is named with its number, and done once it is done', () => {
  assert.equal(stepLabel(0, true), 'Step 1: Licence, done');
  assert.equal(stepLabel(2, false), 'Step 3: Where documents go');
});

test('a test-run entry reads in the Windows wording: type, then Company and its id', () => {
  assert.equal(entryName({ id: 'SalesInvoice', displayName: 'Rechnung' }, 'DEMF'), 'Sales invoices · Company DEMF');
  assert.equal(entryName({ id: 'Lieferschein', displayName: 'lieferschein' }, '1000'), 'Lieferschein · Company 1000');
  assert.equal(upperFirst(''), '');
});

test('no set-up text uses a dash as a pause', async () => {
  const { readdirSync, readFileSync } = await import('node:fs');
  const { join } = await import('node:path');
  const dir = join(import.meta.dirname, '..', 'src', 'components');
  const files = readdirSync(dir).filter((f) => f.startsWith('setup-')).map((f) => join(dir, f));
  files.push(join(import.meta.dirname, '..', 'src', 'app', 'setup', 'steps.ts'));
  const found: string[] = [];
  for (const file of files) {
    readFileSync(file, 'utf8').split('\n').forEach((line, i) => {
      if (/[\u2013\u2014]| - /.test(line) && !/^\s*(\/\/|\*|\/\*)/.test(line)) found.push(`${file}:${i + 1}`);
    });
  }
  assert.deepEqual(found, []);
});
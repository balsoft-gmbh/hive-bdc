// npm test (tsx --test tests/*.test.ts). Port of tests/console/export.test.mjs, plus the helpers moved out of draft.js,
// connections.js and export.js.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { postBody, withBlobSource } from '../src/lib/logic';
import * as X from '../src/lib/exportlogic';
import {
  addCompany, addDestination, addType, applyDestination, companiesWithoutSource, destinationForm, destinationRows, draftChanges,
  folderPreview, httpsConnectionId, removeCompany, removeDestination, removeType, setCompanyActive, setDestinationEnabled, setHttpsKey, setMail,
  setSource, setType, sourceKey, sourceWords, stagingInput, templateChoices, templatePreview, typeRows, words,
} from '../src/lib/exportlogic';

const root = '/home/data/hivebdc/in';

const config = (): any => ({
  version: 'v7', pollSeconds: 30, logDir: '', paused: false, license: { file: '' },
  sharePointAuth: { tenantId: 't', clientId: 'c', secretSet: true },
  mail: { enabled: true, tenantId: '', clientId: 'm', secretSet: true, from: 'rechnung@contoso.example', redirectTo: ['test@contoso.example'], saveToSentItems: true },
  connections: [
    { id: 'fo', kind: 'azure-storage', settings: { AccountUrl: 'https://stcontosofo.blob.core.windows.net' }, hasSecret: false },
    { id: 'sb', kind: 'servicebus', settings: { Namespace: 'sb-contoso-fo.servicebus.windows.net' }, hasSecret: false },
    { id: 'docuware', kind: 'docuware', settings: { Url: 'https://contoso.docuware.cloud', UserName: 'ax' }, hasSecret: true },
  ],
  documentTypes: [
    {
      id: 'SalesInvoice', displayName: 'Rechnung', party: 'Customer', handler: 'EInvoice', enabled: true, mail: true, companies: [
        {
          dataAreaId: '1000', input: `${root}/fo-bdc-export`, inputFormat: 'Json', output: '', enabled: true, mail: false,
          sharePoint: { enabled: true, siteUrl: 'https://contoso.sharepoint.com/sites/F', library: 'Sales Invoices', folder: '{IssueDate:yyyy}', fields: { Title: '{DocumentNumber}' } },
          targets: [
            { kind: 'docuware', connection: 'docuware', enabled: true, settings: { FileCabinet: 'abc', FileCabinetName: 'Outgoing invoices' }, fields: { NR: '{DocumentNumber}' } },
            { kind: 'future-dms', connection: 'x', enabled: true, settings: { A: '1' }, fields: {} },
          ],
          source: { kind: 'blob', connection: 'fo', settings: { Container: 'bdc-export' } },
        },
      ],
    },
    { id: 'SalesQuotation', displayName: 'Angebot', party: 'Customer', handler: 'Pdf', enabled: true, mail: true, companies: [] },
    { id: 'CollectionLetter', displayName: 'Mahnung', party: 'Customer', handler: 'Pdf', enabled: false, mail: true, companies: [] },
  ],
});

const descriptors = [
  { kind: 'sharepoint', name: 'SharePoint library', settings: [] },
  { kind: 'docuware', name: 'DocuWare file cabinet', settings: [] },
  { kind: 'blob', name: 'Azure Blob container', settings: [] },
];

test('companies on the same container, queue or subscription share a staging folder; a web address has its own', () => {
  const blob = { kind: 'blob', connection: 'fo', settings: { Container: 'bdc-export', Prefix: 'in' } };
  assert.equal(stagingInput(blob, 'SalesInvoice', '1000', `${root}/`), `${root}/fo-bdc-export-in`);
  assert.equal(stagingInput({ kind: 'servicebus', connection: 'sb', settings: { Queue: 'bdc-export' } }, 'X', '1', root), `${root}/sb-sb-bdc-export`);
  assert.equal(stagingInput({ kind: 'servicebus', connection: 'sb', settings: { Topic: 'bdc', Subscription: 'hive' } }, 'X', '1', root), `${root}/sb-sb-bdc-hive`);
  assert.equal(stagingInput({ kind: 'https', connection: 'in-x', settings: {} }, 'SalesInvoice', 'DEMF', root), `${root}/https-SalesInvoice-DEMF`);
  assert.equal(stagingInput({ kind: 'blob', connection: '../..', settings: { Container: 'c' } }, 'X', '1', root), `${root}/c`);
  assert.equal(sourceKey(blob), sourceKey({ kind: 'blob', connection: 'FO', settings: { Container: 'bdc-export', Prefix: '/in/' } }));
  assert.equal(sourceKey({ kind: 'https', connection: 'a', settings: {} }), '');
  // The set-up assistant's folder for a Blob source is the same one.
  const assistant = withBlobSource(postBody(config()), { accountUrl: 'https://stcontosofo.blob.core.windows.net', container: 'bdc-export', prefix: 'in' },
    [{ documentType: 'SalesQuotation', company: '4000', count: 1 }], root).body;
  assert.equal(assistant.documentTypes[1].companies[0].input, stagingInput(blob, 'SalesQuotation', '4000', root));
});

test('a company that reads a source another company already reads takes its folder', () => {
  let body = addCompany(postBody(config()), 'SalesQuotation', '1000');
  body = setSource(body, 'SalesQuotation', '1000', { kind: 'blob', connection: 'fo', settings: { Container: 'bdc-export', Prefix: '' } }, root);
  const entry = body.documentTypes[1].companies[0];
  assert.equal(entry.input, `${root}/fo-bdc-export`);
  assert.deepEqual(entry.source, { kind: 'blob', connection: 'fo', settings: { Container: 'bdc-export' } });
  body = setSource(body, 'SalesQuotation', '1000', { kind: 'servicebus', connection: 'sb', settings: { Queue: 'q1', Topic: '' } }, root);
  assert.equal(body.documentTypes[1].companies[0].input, `${root}/sb-sb-q1`);
  assert.deepEqual(body.documentTypes[1].companies[0].source.settings, { Queue: 'q1' });
});

test('sources read as plain words', () => {
  assert.equal(sourceWords({ kind: 'blob', settings: { Container: 'bdc-export', Prefix: 'in/' } }), 'Blob container bdc-export/in');
  assert.equal(sourceWords({ kind: 'servicebus', settings: { Queue: 'bdc-export' } }), 'Service Bus queue bdc-export');
  assert.equal(sourceWords({ kind: 'servicebus', settings: { Topic: 'bdc', Subscription: 'hive' } }), 'Service Bus topic bdc/hive');
  assert.equal(sourceWords({ kind: 'https' }), 'Web address (HTTPS)');
  assert.equal(sourceWords({ kind: 'servicebus', settings: { Topic: 'bdc' } }), 'Service Bus, queue not chosen yet');
  assert.equal(sourceWords({ kind: 'blob', settings: {} }), 'Blob container, not chosen yet');
  assert.equal(sourceWords(null), 'No source yet');
});

test('the Outbound list shows each type, folds away the unused ones and offers companies that wait', () => {
  const attention = [
    { documentId: 'SU-4', documentType: 'SalesQuotation', dataAreaId: '4000', step: 'waiting', state: 'waiting' },
    { documentId: 'RE-9', documentType: 'SalesInvoice', dataAreaId: '1000', step: 'waiting', state: 'waiting' },
    { documentId: 'RE-8', documentType: 'SalesInvoice', dataAreaId: '2000', step: 'failed', state: 'error' },
  ];
  const { rows, folded } = typeRows(config(), attention, descriptors);
  assert.deepEqual(rows.map((r) => r.id), ['SalesInvoice', 'SalesQuotation']);
  assert.deepEqual(folded.map((r) => r.id), ['CollectionLetter']);
  assert.equal(rows[0].name, 'Rechnung');
  assert.equal(rows[0].goesTo, 'SharePoint, DocuWare, future-dms');
  assert.deepEqual(rows[0].waiting, []);
  assert.deepEqual(rows[1].waiting, [{ company: '4000', count: 1 }]);
  assert.equal(rows[1].goesTo, 'nowhere yet');
});

test('types and companies are added, changed and removed on the draft only', () => {
  const start = postBody(config());
  let body = addType(start, 'DeliveryNote', 'Lieferschein', 'Pdf', 'Customer');
  assert.equal(body.documentTypes.at(-1).id, 'DeliveryNote');
  assert.equal(start.documentTypes.length, 3);
  assert.throws(() => addType(body, 'Delivery Note'), /letters, digits/);
  assert.throws(() => addType(body, 'deliverynote'), /already/);
  body = setType(body, 'SalesInvoice', { handler: 'Pdf', party: 'Vendor', enabled: false, displayName: 'Faktura' });
  body = addCompany(body, 'SalesInvoice', 'DEMF');
  assert.throws(() => addCompany(body, 'SalesInvoice', 'demf'), /there already/);
  assert.deepEqual(companiesWithoutSource(body), [{ type: 'SalesInvoice', company: 'DEMF' }]);
  body = removeType(body, 'CollectionLetter');
  assert.deepEqual(draftChanges(start, body), [
    'Rechnung: name is now Faktura',
    'Rechnung: Hive BDC makes PDF as it is',
    'Rechnung: sent to vendors',
    'Rechnung: off',
    'Rechnung: company DEMF added',
    'Lieferschein added',
    'Mahnung removed',
  ]);
  assert.deepEqual(draftChanges(start, setCompanyActive(start, 'SalesInvoice', '1000', false)), ['Rechnung › 1000: off']);
  assert.equal(setCompanyActive(start, 'SalesInvoice', '1000', false).documentTypes[0].companies[0].enabled, false);
  body = removeCompany(body, 'SalesInvoice', 'DEMF');
  assert.deepEqual(companiesWithoutSource(body), []);
  const one = { ...start, documentTypes: [start.documentTypes[0]] };
  assert.throws(() => removeType(one, 'SalesInvoice'), /at least one/);
});

test('destinations are listed, switched, removed and added; a kind this version does not offer is kept', () => {
  const entry = config().documentTypes[0].companies[0];
  const rows = destinationRows(entry, descriptors);
  assert.deepEqual(rows.map((r) => [r.kind, r.enabled, r.known]), [['sharepoint', true, true], ['docuware', true, true], ['future-dms', true, false]]);
  assert.equal(rows[0].summary, 'Sales Invoices › {IssueDate:yyyy} · 1 column');
  assert.equal(rows[1].summary, 'Outgoing invoices · 1 field');
  assert.match(rows[2].summary, /newer version/);
  assert.equal(rows[0].name, 'SharePoint');

  const start = postBody(config());
  let body = setDestinationEnabled(start, 'SalesInvoice', '1000', 'docuware', false);
  body = removeDestination(body, 'SalesInvoice', '1000', 'sharepoint');
  body = addDestination(body, 'SalesInvoice', '1000', 'blob', 'fo');
  body = setMail(body, 'SalesInvoice', '1000', true);
  const after = body.documentTypes[0].companies[0];
  assert.equal(after.sharePoint.enabled, false);
  assert.equal(after.sharePoint.siteUrl, '');
  assert.deepEqual(after.targets.map((t: any) => [t.kind, t.enabled]), [['docuware', false], ['future-dms', true], ['blob', true]]);
  assert.deepEqual(draftChanges(start, body), [
    'Rechnung › 1000: mail on',
    'Rechnung › 1000: SharePoint removed',
    'Rechnung › 1000: DocuWare off',
    'Rechnung › 1000: Blob container added',
  ]);
});

test('a web address source gets its own connection and a key that goes out only with Save', () => {
  const start = postBody(config());
  let body = addCompany(start, 'SalesInvoice', 'DEMF');
  body = setHttpsKey(body, 'SalesInvoice', 'DEMF', 'q7Rk2vN9xTb4Lm8sWc3Pz6YdHe5Ju1Af', root);
  const id = httpsConnectionId('SalesInvoice', 'DEMF');
  assert.equal(id, 'in-salesinvoice-demf');
  const entry = body.documentTypes[0].companies[1];
  assert.deepEqual(entry.source, { kind: 'https', connection: id, settings: {} });
  assert.equal(entry.input, `${root}/https-SalesInvoice-DEMF`);
  assert.deepEqual(body.connections.at(-1), { id, kind: 'https', settings: {}, secret: 'q7Rk2vN9xTb4Lm8sWc3Pz6YdHe5Ju1Af' });
  assert.ok(draftChanges(start, body).includes('New key for in-salesinvoice-demf'));
  body = setHttpsKey(body, 'SalesInvoice', 'DEMF', 'another', root);
  assert.equal(body.connections.filter((c: any) => c.id === id).length, 1);
  const older = setHttpsKey(body, 'SalesInvoice', 'DEMF', 'third', root, 'erp-in');
  assert.equal(older.documentTypes[0].companies[1].source.connection, 'erp-in');
  assert.deepEqual(older.connections.at(-1), { id: 'erp-in', kind: 'https', settings: {}, secret: 'third' });
  assert.equal(JSON.stringify(start).includes('q7Rk'), false);
});

test('the destination form reads and writes SharePoint and any other kind, keeping what it does not show', () => {
  const entry = config().documentTypes[0].companies[0];
  const sp = destinationForm(entry, 'sharepoint');
  assert.deepEqual(sp.settings, { SiteUrl: 'https://contoso.sharepoint.com/sites/F', Library: 'Sales Invoices', Folder: '{IssueDate:yyyy}' });
  sp.fields = { Title: '{DocumentNumber}', Customer: '{RecipientName}', Empty: '  ' };
  sp.settings.Folder = '{IssueDate:yyyy}/{IssueDate:MM}';
  let body = applyDestination(postBody(config()), 'SalesInvoice', '1000', sp);
  assert.deepEqual(body.documentTypes[0].companies[0].sharePoint.fields, { Title: '{DocumentNumber}', Customer: '{RecipientName}' });
  assert.equal(body.documentTypes[0].companies[0].sharePoint.folder, '{IssueDate:yyyy}/{IssueDate:MM}');

  const dw = destinationForm(entry, 'docuware');
  dw.settings = { FileCabinet: 'def', MatchField: 'NR' };
  body = applyDestination(body, 'SalesInvoice', '1000', dw);
  const target = body.documentTypes[0].companies[0].targets[0];
  assert.deepEqual(target.settings, { FileCabinet: 'def', FileCabinetName: 'Outgoing invoices', MatchField: 'NR' });
  assert.equal(target.connection, 'docuware');

  const added = applyDestination(body, 'SalesInvoice', '1000', { kind: 'blob', connection: 'fo', enabled: true, settings: { Container: 'out' }, fields: {} });
  assert.deepEqual(added.documentTypes[0].companies[0].targets.at(-1), { kind: 'blob', settings: { Container: 'out' }, connection: 'fo', enabled: true, fields: {} });
});

test('previews give what the service gives for columns and folders', () => {
  const fields = { documentNumber: 'RE-260042', issueDate: '2026-11-18', dataAreaId: '1000', recipientName: 'Muster/Handel ..GmbH', dueDate: '', evil: '../../Shared Documents', odd: 'a:b*c?', german: '05.03.2026' };
  assert.equal(templatePreview('{DocumentNumber}', fields), 'RE-260042');
  assert.equal(templatePreview('{IssueDate:date}', fields), '2026-11-18');
  assert.equal(templatePreview('{IssueDate:yyyy}', fields), '2026');
  assert.equal(templatePreview('{german:date}', fields), '2026-03-05');
  assert.equal(templatePreview('{missing}', fields), '');
  // The same cases as SharePointFolderTemplateTests.
  for (const [template, expected] of [
    ['{IssueDate:yyyy}/{IssueDate:MM}', '2026/11'], ['Invoices/{dataAreaId}/{IssueDate:yyyy}', 'Invoices/1000/2026'],
    ['{IssueDate:date}', '2026-11-18'], ['Fixed folder', 'Fixed folder'], ['', ''], ['{evil}', '-.-Shared Documents'],
    ['Customers/{recipientName}', 'Customers/Muster-Handel .GmbH'], ['A/{odd}', 'A/abc'], ['{dueDate:yyyy}/{documentNumber}', 'RE-260042'], ['{missing}/x', 'x'],
  ]) assert.equal(folderPreview(template, fields), expected, template);
});

test('Save waits for complete sources and names what is missing', () => {
  const { saveProblems } = X;
  const cfg = config();
  let body = postBody(cfg);
  assert.deepEqual(saveProblems(body, cfg), []);
  body = addCompany(body, 'SalesInvoice', 'DEMF');
  assert.deepEqual(saveProblems(body, cfg), ['Rechnung › DEMF: choose where its documents come from.']);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'blob', connection: 'fo', settings: { Container: '' } }, root);
  assert.match(saveProblems(body, cfg)[0], /storage connection and the container/);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'servicebus', connection: 'sb', settings: { Topic: 'bdc' } }, root);
  assert.match(saveProblems(body, cfg)[0], /topic and its subscription/);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'https', connection: httpsConnectionId('SalesInvoice', 'DEMF'), settings: {} }, root);
  assert.match(saveProblems(body, cfg)[0], /create a key/);
  body = setHttpsKey(body, 'SalesInvoice', 'DEMF', 'k', root);
  assert.deepEqual(saveProblems(body, cfg), ['Rechnung › DEMF: choose where its documents go.']);
  const withBlob = [...descriptors.filter((d) => d.kind !== 'blob'), { kind: 'blob', name: 'Azure Blob container', settings: [{ key: 'Container', label: 'Container' }, { key: 'Prefix', label: 'Folder', required: false }] }];
  body = addDestination(body, 'SalesInvoice', 'DEMF', 'blob', 'fo');
  assert.deepEqual(saveProblems(body, cfg, withBlob), ['Rechnung › DEMF › Azure Blob container: Container is missing.']);
  body = applyDestination(body, 'SalesInvoice', 'DEMF', { kind: 'blob', connection: 'fo', enabled: true, settings: { Container: 'out' }, fields: {} });
  assert.deepEqual(saveProblems(body, cfg, withBlob), []);
  const mailOnly = setMail(removeDestination(body, 'SalesInvoice', 'DEMF', 'blob'), 'SalesInvoice', 'DEMF', true);
  assert.deepEqual(saveProblems(mailOnly, cfg, withBlob), []);
  const halfSharePoint = addDestination(removeDestination(body, 'SalesInvoice', 'DEMF', 'blob'), 'SalesInvoice', 'DEMF', 'sharepoint');
  assert.deepEqual(saveProblems(halfSharePoint, cfg, withBlob), ['Rechnung › DEMF › SharePoint library: the site address and the library are needed.']);
  const stored = { ...cfg, connections: [...cfg.connections, { id: 'in-salesinvoice-demf', kind: 'https', settings: {}, hasSecret: true }] };
  let kept = setSource(addCompany(postBody(stored), 'SalesInvoice', 'DEMF'), 'SalesInvoice', 'DEMF', { kind: 'https', connection: 'in-salesinvoice-demf', settings: {} }, root);
  kept = setMail(kept, 'SalesInvoice', 'DEMF', true);
  assert.deepEqual(saveProblems(kept, stored), []);
});

test('template choices come from the sample, with "as date" for dates', () => {
  assert.deepEqual(templateChoices({ documentNumber: 'RE-1', issueDate: '2026-11-18', total: '10.5' }), [
    { value: '{documentNumber}', label: 'Document number' },
    { value: '{issueDate}', label: 'Issue date' },
    { value: '{issueDate:date}', label: 'Issue date (as date)' },
    { value: '{total}', label: 'Total' },
  ]);
  assert.equal(words('recipientVatId'), 'Recipient vat id');
});

test('connections name their users, refuse removal while used, and show up in the change list', () => {
  const cfg = config();
  const start = postBody(cfg);
  assert.deepEqual(X.connectionUses(start, 'fo'), ['Rechnung › 1000 (source)']);
  assert.deepEqual(X.connectionUses(start, 'docuware'), ['Rechnung › 1000 (DocuWare)']);
  assert.deepEqual(X.connectionUses(start, 'sb'), []);
  assert.deepEqual(X.sharePointUses(start), ['Rechnung › 1000']);
  assert.throws(() => X.removeConnection(start, 'fo'), /Still used by Rechnung › 1000 \(source\)/);

  let body = X.removeConnection(start, 'sb');
  body = X.addConnection(body, { id: 'sb2', kind: 'servicebus', settings: { Namespace: 'sb2.servicebus.windows.net' } });
  assert.throws(() => X.addConnection(body, { id: 'SB2', kind: 'servicebus' }), /already/);
  assert.throws(() => X.addConnection(body, { id: 'two words', kind: 'servicebus' }), /letters, digits/);
  body = X.updateConnection(body, 'docuware', { UserName: 'api' }, '');
  assert.equal(body.connections.find((c: any) => c.id === 'docuware').secret, undefined);
  body = X.setSharePointApp(body, { tenantId: 't', clientId: 'c2', secret: '' });
  assert.equal('clientSecret' in body.sharePointAuth, false);
  body = X.setMailbox(body, { ...cfg.mail, enabled: false });
  assert.deepEqual(X.draftChanges(start, body), [
    'Connection docuware changed', 'Connection sb2 added', 'Connection sb removed', 'SharePoint app changed', 'Mail off',
  ]);
});

test('connections and the mailbox read as plain words and the mailbox names what is missing', () => {
  assert.equal(X.connectionWords({ kind: 'azure-storage', settings: { AccountUrl: 'https://stbdctest8021.blob.core.windows.net' } }), 'Azure Storage stbdctest8021. Signs in as this instance, no key stored.');
  assert.equal(X.connectionWords({ kind: 'azure-storage', settings: {}, hasSecret: true }), 'Azure Storage. Connection string in Key Vault.');
  assert.equal(X.connectionWords({ kind: 'servicebus', settings: { Namespace: 'sb.servicebus.windows.net' } }), 'Azure Service Bus sb.servicebus.windows.net. Signs in as this instance.');
  assert.equal(X.connectionWords({ kind: 'docuware', settings: { Url: 'https://contoso.docuware.cloud', UserName: 'ax' } }), 'DocuWare contoso.docuware.cloud as ax. Password in Key Vault.');
  assert.match(X.connectionWords({ kind: 'https', settings: {} }), /never shown again/);
  assert.deepEqual(X.mailboxProblems({ enabled: false }, ''), []);
  assert.deepEqual(X.mailboxProblems({ enabled: true, tenantId: '', clientId: '', from: 'x', redirectTo: ['a@b.example', 'nope'] }, ''), [
    'Enter the tenant ID.', 'Enter the client ID of the mail app.', 'Enter the mailbox documents are sent from.', '"nope" is not an e-mail address.',
  ]);
  assert.deepEqual(X.mailboxProblems({ enabled: true, clientId: 'm', from: 'rechnung@contoso.example', redirectTo: ['test@contoso.example'] }, 'tenant'), []);
});

test('a storage connection names the containers it is used with, and users read short', () => {
  let body = postBody(config());
  body = X.addDestination(body, 'SalesInvoice', '1000', 'blob', 'fo');
  body = X.applyDestination(body, 'SalesInvoice', '1000', { kind: 'blob', connection: 'fo', enabled: true, settings: { Container: 'out' }, fields: {} });
  body = addCompany(body, 'SalesQuotation', '1000');
  body = setSource(body, 'SalesQuotation', '1000', { kind: 'servicebus', connection: 'sb', settings: { Queue: 'q', PdfConnection: 'fo', PdfContainer: 'pdf' } }, root);
  assert.deepEqual(X.connectionContainers(body, 'fo'), [{ container: 'bdc-export', prefix: '' }, { container: 'out', prefix: '' }, { container: 'pdf', prefix: '' }]);
  assert.deepEqual(X.connectionContainers(body, 'sb'), []);
});

test('a Service Bus destination needs a queue or a topic before Save', () => {
  const { saveProblems } = X;
  const cfg = config();
  let body = addCompany(postBody(cfg), 'SalesInvoice', 'DEMF');
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'servicebus', connection: 'sb', settings: { Queue: 'fo-out' } }, root);
  body = addDestination(body, 'SalesInvoice', 'DEMF', 'servicebus', 'sb');
  const target = (b: any) => b.documentTypes.find((t: any) => t.id === 'SalesInvoice').companies.find((c: any) => c.dataAreaId === 'DEMF').targets[0];
  const busNames = /give a queue or a topic/;
  assert.ok(saveProblems(body, cfg).some((p) => busNames.test(p)));
  target(body).settings = { Topic: 'back-to-fo' };
  assert.ok(!saveProblems(body, cfg).some((p) => busNames.test(p)));
  target(body).settings = { Queue: 'back-to-fo' };
  assert.deepEqual(saveProblems(body, cfg), []);
});

test('the SharePoint app line says Key Vault only for a saved secret and why Test is off', () => {
  const { sharePointAppLine, sharePointTestBlock } = X;
  assert.match(sharePointAppLine({}, false), /The app your IT creates/);
  const typed = { tenantId: 't', clientId: 'c', clientSecret: 'typed' };
  for (const stored of [false, true]) {
    assert.match(sharePointAppLine(typed, stored), /new secret goes to Key Vault when you save/);
    assert.doesNotMatch(sharePointAppLine(typed, stored), /Secret in Key Vault\./);
  }
  assert.equal(sharePointAppLine({ tenantId: 't', clientId: 'c' }, true), 'Client c in tenant t. Secret in Key Vault.');
  assert.match(sharePointAppLine({ tenantId: 't', clientId: 'c' }, false), /No secret yet/);
  assert.equal(sharePointTestBlock(null, false), 'Add a SharePoint destination first.');
  assert.equal(sharePointTestBlock({ siteUrl: 's', library: 'l' }, true), 'Save first.');
  assert.equal(sharePointTestBlock({ siteUrl: 's', library: 'l' }, false), null);
});

// Design part 20 of the connector design: the findings of the live checks after part 19.

test('a destination page opens only for a destination the draft has', () => {
  const { hasDestination } = X;
  const entry = config().documentTypes[0].companies[0];
  assert.equal(hasDestination(entry, 'sharepoint'), true);
  assert.equal(hasDestination(entry, 'docuware'), true);
  assert.equal(hasDestination(entry, 'blob'), false);
  const added = addDestination(postBody(config()), 'SalesInvoice', '1000', 'blob', 'fo').documentTypes[0].companies[0];
  assert.equal(hasDestination(added, 'blob'), true);
  const bare = { ...entry, sharePoint: { enabled: false, siteUrl: '', library: '', folder: '', fields: {} }, targets: [] };
  assert.equal(hasDestination(bare, 'sharepoint'), false);
});

test('the mail line says where a typed secret goes and names a stored one only after Save', () => {
  const { mailboxLine } = X;
  const mail = { enabled: true, from: 'info@contoso.example', redirectTo: ['test@contoso.example'], clientId: 'm' };
  assert.equal(mailboxLine(mail, false), 'From info@contoso.example. Documents of a test ERP go to test@contoso.example. No secret yet.');
  assert.equal(mailboxLine({ ...mail, clientSecret: 'typed' }, false), 'From info@contoso.example. Documents of a test ERP go to test@contoso.example. The new secret goes to Key Vault when you save.');
  assert.equal(mailboxLine({ ...mail, clientSecret: 'typed' }, true), 'From info@contoso.example. Documents of a test ERP go to test@contoso.example. The new secret goes to Key Vault when you save.');
  assert.equal(mailboxLine(mail, true), 'From info@contoso.example. Documents of a test ERP go to test@contoso.example. Secret in Key Vault.');
  assert.match(mailboxLine({ enabled: false }, false), /Sends each document from a company mailbox/);
  assert.equal(mailboxLine({ ...mail, enabled: false }, true), 'Switched off. From info@contoso.example. Documents of a test ERP go to test@contoso.example. Secret in Key Vault.');
  const start = postBody(config());
  const typed = { ...start, mail: { ...start.mail, enabled: false, clientSecret: 'typed' } };
  assert.deepEqual(draftChanges(start, typed), ['Mail off', 'New secret for document mail']);
});

test('test results stay through Save and Discard and go only with a saved change of their own connection', () => {
  const { keptTests } = X;
  const tests = { fo: { ok: true }, docuware: { ok: true }, '#mail': { ok: true }, '#sharepoint': { ok: false } };
  const start = postBody(config());
  assert.deepEqual(keptTests(tests, start, start), tests);
  let changed: any = structuredClone(start);
  changed.connections.find((c: any) => c.id === 'docuware').settings.UserName = 'other';
  changed.mail.from = 'other@contoso.example';
  assert.deepEqual(Object.keys(keptTests(tests, start, changed)).sort(), ['#sharepoint', 'fo']);
  changed = structuredClone(start);
  changed.connections.find((c: any) => c.id === 'fo').secret = 'new';
  changed.sharePointAuth = { ...changed.sharePointAuth, clientSecret: 'new' };
  assert.deepEqual(Object.keys(keptTests(tests, start, changed)).sort(), ['#mail', 'docuware']);
  changed = structuredClone(start);
  changed.connections = changed.connections.filter((c: any) => c.id !== 'fo');
  assert.equal(keptTests(tests, start, changed).fo, undefined);
});

// The helpers that left draft.js, connections.js and export.js for the React pages.

test('a new key is 24 random bytes in Base64 without "+", "/" or padding', () => {
  const fixed = (value: number) => (bytes: Uint8Array) => bytes.fill(value);
  assert.equal(X.newKey(fixed(0xfb)), 'ABv7'.repeat(8));
  assert.equal(X.newKey(fixed(0xff)), 'B'.repeat(32));
  const real = X.newKey();
  assert.equal(real.length, 32);
  assert.match(real, /^[A-Za-z0-9]+$/);
  assert.notEqual(real, X.newKey());
});

test('a connection reads in a dropdown with its account, namespace or address', () => {
  assert.equal(X.connectionLabel({ id: 'fo', settings: { AccountUrl: 'https://stcontosofo.blob.core.windows.net' } }), 'fo (stcontosofo)');
  assert.equal(X.connectionLabel({ id: 'sb', settings: { Namespace: 'sb-contoso.servicebus.windows.net' } }), 'sb (sb-contoso)');
  assert.equal(X.connectionLabel({ id: 'dw', settings: { Url: 'https://contoso.docuware.cloud' } }), 'dw (contoso.docuware.cloud)');
  assert.equal(X.connectionLabel({ id: 'plain', settings: {} }), 'plain');
  assert.equal(X.connectionLabel({ id: 'odd', settings: { AccountUrl: 'not a url' } }), 'odd (not a url)');
});

test('a new connection is named after its kind, numbered when the name is taken', () => {
  const taken = [{ id: 'storage' }, { id: 'Storage-2' }, { id: 'web' }];
  assert.equal(X.suggestId(taken, 'azure-storage'), 'storage-3');
  assert.equal(X.suggestId(taken, 'servicebus'), 'servicebus');
  assert.equal(X.suggestId(taken, 'https'), 'web-2');
  assert.equal(X.suggestId([], 'future'), 'future');
});

test('a test result reads as the state pill of the list, and the field test names what is missing', () => {
  assert.deepEqual(X.testState(undefined), { tone: 'neutral', text: 'Not tested' });
  assert.deepEqual(X.testState({ ok: true, at: '10:42' }), { tone: 'green', text: 'Works' });
  assert.deepEqual(X.testState({ ok: false, at: '10:42' }), { tone: 'rose', text: 'Refused' });
  const found = [{ value: 'Title' }, { value: 'Customer' }];
  assert.deepEqual(X.fieldTestLine(['Title', ''], found), { ok: true, text: 'Signed in. 2 fields found, every mapped one exists.' });
  assert.deepEqual(X.fieldTestLine(['Title', 'Amount'], found), { ok: false, text: 'Signed in, but "Amount" does not exist there yet.' });
  assert.deepEqual(X.fieldTestLine(['A', 'B'], found), { ok: false, text: 'Signed in, but "A", "B" do not exist there yet.' });
});

test('the Connections list names each kind and how it signs in, short enough for a cell', () => {
  const kinds = [{ kind: 'azure-storage', name: 'Azure Storage' }, { kind: 'docuware', name: 'DocuWare' }];
  assert.equal(X.connectionKindName('azure-storage', kinds), 'Azure Storage');
  assert.equal(X.connectionKindName('https', kinds), 'Web address key');
  assert.equal(X.connectionKindName('future', kinds), 'future');
  const storage = { kind: 'azure-storage', settings: { AccountUrl: 'https://stcontosofo.blob.core.windows.net' }, hasSecret: false };
  assert.equal(X.signInWords(storage), 'stcontosofo as this instance');
  assert.equal(X.signInWords({ ...storage, hasSecret: true }), 'stcontosofo with a connection string');
  assert.equal(X.signInWords({ kind: 'azure-storage', settings: {}, secret: 'typed' }), 'With a connection string');
  assert.equal(X.signInWords({ kind: 'servicebus', settings: { Namespace: 'sb-contoso.servicebus.windows.net' } }), 'sb-contoso as this instance');
  assert.equal(X.signInWords({ kind: 'docuware', settings: { Url: 'https://contoso.docuware.example/DocuWare', UserName: 'hivebdc' } }), 'contoso.docuware.example as hivebdc');
  assert.equal(X.signInWords({ kind: 'https', settings: {} }), 'The sender posts with the key');
  assert.equal(X.signInWords({ kind: 'future', settings: {} }), '');
});

test('the companies that mail are named with their type, for Used by of document mail', () => {
  let body = postBody(config());
  assert.deepEqual(X.mailCompanies(body), []);
  body = setMail(body, 'SalesInvoice', '1000', true);
  assert.deepEqual(X.mailCompanies(body), ['Rechnung › 1000']);
});

test('a connection kind has its name before the kinds list has answered', () => {
  assert.equal(X.connectionKindName('azure-storage', []), 'Azure Storage');
  assert.equal(X.connectionKindName('servicebus', undefined), 'Azure Service Bus');
  assert.equal(X.connectionKindName('docuware', [{ kind: 'docuware', name: 'DocuWare file system' }]), 'DocuWare file system');
  assert.equal(X.connectionKindName('future-dms', []), 'future-dms');
});

test('Compress PDF is a company switch that the save body carries and the change list names', () => {
  const start = postBody(config());
  assert.equal(start.documentTypes[0].companies[0].compressPdf, false);
  assert.equal('compressPdf' in start.documentTypes[0], false);   // a type the service sent without it stays without it

  const on = X.setCompressPdf(start, 'SalesInvoice', '1000', true);
  assert.equal(on.documentTypes[0].companies[0].compressPdf, true);
  assert.equal(start.documentTypes[0].companies[0].compressPdf, false);   // the draft is a copy
  assert.deepEqual(draftChanges(start, on), ['Rechnung › 1000: compress PDF on']);
  assert.deepEqual(draftChanges(on, X.setCompressPdf(on, 'SalesInvoice', '1000', false)), ['Rechnung › 1000: compress PDF off']);

  const sent = postBody({ ...config(), documentTypes: [{ ...config().documentTypes[0], compressPdf: true }] });
  assert.equal(sent.documentTypes[0].compressPdf, true);
  assert.equal(addCompany(start, 'SalesInvoice', '2000').documentTypes[0].companies.at(-1).compressPdf, false);
});

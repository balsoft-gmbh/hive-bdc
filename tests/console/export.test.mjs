// node --test tests/console/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { postBody, withBlobSource } from '../../Hosting/Console/logic.js';
import {
  addCompany, addDestination, addType, applyDestination, companiesWithoutSource, destinationForm, destinationRows, draftChanges,
  folderPreview, httpsConnectionId, removeCompany, removeDestination, removeType, setDestinationEnabled, setHttpsKey, setMail,
  setSource, setType, sourceKey, sourceWords, stagingInput, templateChoices, templatePreview, typeRows, words,
} from '../../Hosting/Console/exportlogic.js';

const root = '/home/data/hivebdc/in';

const config = () => ({
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

test('the Export list shows each type, folds away the unused ones and offers companies that wait', () => {
  const attention = [
    { documentId: 'SU-4', documentType: 'SalesQuotation', dataAreaId: '4000', step: 'waiting', state: 'waiting' },
    { documentId: 'RE-9', documentType: 'SalesInvoice', dataAreaId: '1000', step: 'waiting', state: 'waiting' },
    { documentId: 'RE-8', documentType: 'SalesInvoice', dataAreaId: '2000', step: 'failed', state: 'error' },
  ];
  const { rows, folded } = typeRows(config(), attention, descriptors);
  assert.deepEqual(rows.map((r) => r.id), ['SalesInvoice', 'SalesQuotation']);
  assert.deepEqual(folded.map((r) => r.id), ['CollectionLetter']);
  assert.equal(rows[0].name, 'Sales invoices');
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
    'Sales invoices: name in the ERP is now Faktura',
    'Sales invoices: made into PDF as it is',
    'Sales invoices: sent to vendors',
    'Sales invoices: off',
    'Sales invoices: company DEMF added',
    'Lieferschein added',
    'Collection letters removed',
  ]);
  body = removeCompany(body, 'SalesInvoice', 'DEMF');
  assert.deepEqual(companiesWithoutSource(body), []);
  const one = { ...start, documentTypes: [start.documentTypes[0]] };
  assert.throws(() => removeType(one, 'SalesInvoice'), /at least one/);
});

test('destinations are listed, switched, removed and added; a kind this version does not offer is kept', () => {
  const entry = config().documentTypes[0].companies[0];
  const rows = destinationRows(entry, descriptors);
  assert.deepEqual(rows.map((r) => [r.kind, r.enabled, r.known]), [['sharepoint', true, true], ['docuware', true, true], ['future-dms', true, false]]);
  assert.equal(rows[0].summary, 'Sales Invoices › {IssueDate:yyyy} · 1 columns');
  assert.equal(rows[1].summary, 'Outgoing invoices · 1 fields');
  assert.match(rows[2].summary, /newer version/);

  const start = postBody(config());
  let body = setDestinationEnabled(start, 'SalesInvoice', '1000', 'docuware', false);
  body = removeDestination(body, 'SalesInvoice', '1000', 'sharepoint');
  body = addDestination(body, 'SalesInvoice', '1000', 'blob', 'fo');
  body = setMail(body, 'SalesInvoice', '1000', true);
  const after = body.documentTypes[0].companies[0];
  assert.equal(after.sharePoint.enabled, false);
  assert.equal(after.sharePoint.siteUrl, '');
  assert.deepEqual(after.targets.map((t) => [t.kind, t.enabled]), [['docuware', false], ['future-dms', true], ['blob', true]]);
  assert.deepEqual(draftChanges(start, body), [
    'Sales invoices › 1000: mail on',
    'Sales invoices › 1000: SharePoint removed',
    'Sales invoices › 1000: DocuWare off',
    'Sales invoices › 1000: Blob container added',
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
  assert.equal(body.connections.filter((c) => c.id === id).length, 1);
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

test('Save waits for complete sources and names what is missing', async () => {
  const { saveProblems } = await import('../../Hosting/Console/exportlogic.js');
  const cfg = config();
  let body = postBody(cfg);
  assert.deepEqual(saveProblems(body, cfg), []);
  body = addCompany(body, 'SalesInvoice', 'DEMF');
  assert.deepEqual(saveProblems(body, cfg), ['Sales invoices › DEMF: choose where its documents come from.']);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'blob', connection: 'fo', settings: { Container: '' } }, root);
  assert.match(saveProblems(body, cfg)[0], /storage connection and the container/);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'servicebus', connection: 'sb', settings: { Topic: 'bdc' } }, root);
  assert.match(saveProblems(body, cfg)[0], /topic and its subscription/);
  body = setSource(body, 'SalesInvoice', 'DEMF', { kind: 'https', connection: httpsConnectionId('SalesInvoice', 'DEMF'), settings: {} }, root);
  assert.match(saveProblems(body, cfg)[0], /create a key/);
  body = setHttpsKey(body, 'SalesInvoice', 'DEMF', 'k', root);
  assert.deepEqual(saveProblems(body, cfg), ['Sales invoices › DEMF: choose where its documents go.']);
  const withBlob = [...descriptors.filter((d) => d.kind !== 'blob'), { kind: 'blob', name: 'Azure Blob container', settings: [{ key: 'Container', label: 'Container' }, { key: 'Prefix', label: 'Folder', required: false }] }];
  body = addDestination(body, 'SalesInvoice', 'DEMF', 'blob', 'fo');
  assert.deepEqual(saveProblems(body, cfg, withBlob), ['Sales invoices › DEMF › Azure Blob container: Container is missing.']);
  body = applyDestination(body, 'SalesInvoice', 'DEMF', { kind: 'blob', connection: 'fo', enabled: true, settings: { Container: 'out' }, fields: {} });
  assert.deepEqual(saveProblems(body, cfg, withBlob), []);
  const mailOnly = setMail(removeDestination(body, 'SalesInvoice', 'DEMF', 'blob'), 'SalesInvoice', 'DEMF', true);
  assert.deepEqual(saveProblems(mailOnly, cfg, withBlob), []);
  const halfSharePoint = addDestination(removeDestination(body, 'SalesInvoice', 'DEMF', 'blob'), 'SalesInvoice', 'DEMF', 'sharepoint');
  assert.deepEqual(saveProblems(halfSharePoint, cfg, withBlob), ['Sales invoices › DEMF › SharePoint library: the site address and the library are needed.']);
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

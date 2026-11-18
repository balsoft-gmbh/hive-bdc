// node --test tests/console/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attentionRows, canonicalPage, changeLine, conflictView, dayLine, germanDate, greeting, initials, licenceLine, newestFirst, parseRoute, stepLabel, topRole, typeLabel } from '../../Hosting/Console/logic.js';

test('the highest role wins and unknown roles are ignored', () => {
  assert.equal(topRole(['viewer', 'Administrator']), 'Administrator');
  assert.equal(topRole(['Operator', 'Viewer']), 'Operator');
  assert.equal(topRole(['Guest']), '');
  assert.equal(topRole(undefined), '');
});

test('initials come from a name or a mail address', () => {
  assert.equal(initials('Ertugrul Balveren'), 'EB');
  assert.equal(initials('anna.weber@contoso.example'), 'AW');
  assert.equal(initials('anna@contoso.example'), 'A');
  assert.equal(initials(''), '?');
});

test('steps read as plain words, unknown steps stay as they are', () => {
  assert.equal(stepLabel('produced'), 'Made');
  assert.equal(stepLabel('DELIVERED'), 'Delivered');
  assert.equal(stepLabel('custom'), 'custom');
});

test('one attention row per document, newest event first, finished documents left out', () => {
  const rows = attentionRows([
    { id: 1, documentId: 'A', step: 'failed', state: 'error', target: 'docuware', message: 'refused', documentNumber: 'RE-1', dataAreaId: '1000' },
    { id: 3, documentId: 'A', step: 'failed', state: 'error', target: 'docuware', message: 'refused again', documentNumber: 'RE-1', dataAreaId: '1000' },
    { id: 2, documentId: 'B', step: 'waiting', state: 'waiting', message: 'PDF not there yet', dataAreaId: 'DEMF' },
    { id: 4, documentId: 'C', step: 'failed', state: 'error', message: 'x' },
    { id: 5, documentId: 'C', step: 'done', state: 'ok', message: 'finished' },
    { id: 6, step: 'failed', message: 'no document id' },
  ]);
  assert.deepEqual(rows.map((r) => r.documentId), ['A', 'B']);
  assert.equal(rows[0].message, 'refused again');
  assert.equal(rows[0].number, 'RE-1');
  assert.equal(rows[0].waiting, false);
  assert.equal(rows[1].number, 'B');
  assert.equal(rows[1].waiting, true);
});

test('the licence line names the date or what is wrong', () => {
  assert.deepEqual(licenceLine({ state: 'Valid', expires: '2027-09-30' }), { ok: true, text: 'Licence valid until 30.09.2027' });
  assert.equal(licenceLine({ state: 'Warning', expires: '2026-12-01' }).ok, false);
  assert.deepEqual(licenceLine({ state: 'Missing' }), { ok: false, text: 'No licence yet' });
  assert.deepEqual(licenceLine({ state: 'Invalid', message: 'Issued for another tenant.' }), { ok: false, text: 'Issued for another tenant.' });
  assert.equal(licenceLine(null).ok, false);
  assert.equal(germanDate('not a date'), 'not a date');
});

test('routes keep their parts and decode the query', () => {
  assert.deepEqual(parseRoute(''), { page: 'overview', parts: [], query: {} });
  assert.deepEqual(parseRoute('#/history?search=RE%2D1+x'), { page: 'history', parts: [], query: { search: 'RE-1 x' } });
  assert.deepEqual(parseRoute('#/export/SalesInvoice/1000'), { page: 'export', parts: ['SalesInvoice', '1000'], query: {} });
});

test('history shows the newest event first and leaves the input alone', () => {
  const events = [{ id: 1 }, { id: 3 }, { id: 2 }];
  assert.deepEqual(newestFirst(events).map((e) => e.id), [3, 2, 1]);
  assert.deepEqual(events.map((e) => e.id), [1, 3, 2]);
  assert.deepEqual(newestFirst(undefined), []);
});

test('a change reads as a sentence, with added and removed named', () => {
  assert.equal(changeLine({ path: 'SalesInvoice › company 1000 › SharePoint › Folder', before: '{yyyy}', after: '{yyyy}/{MM}' }),
    'SalesInvoice › company 1000 › SharePoint › Folder: {yyyy} is now {yyyy}/{MM}');
  assert.equal(changeLine({ path: 'SalesInvoice › company 2000', before: '(none)', after: 'added' }), 'SalesInvoice › company 2000: added');
  assert.equal(changeLine({ path: 'connection dw', before: 'present', after: '(none)' }), 'connection dw: removed');
});

test('the refusal offers Save both only when nothing overlaps and the start is known', () => {
  const base = {
    baseKnown: true, savedBy: 'anna@contoso.example', savedAt: '2026-11-18T09:44:00Z', overlaps: [],
    theirChanges: [{ path: 'PollSeconds', before: '30', after: '45' }],
    yourChanges: [{ path: 'SalesInvoice › company 1000 › docuware dw Datapool › Enabled', before: 'false', after: 'true' }],
  };
  const view = conflictView(base, 'Europe/Berlin');
  assert.equal(view.title, 'Not saved: anna@contoso.example saved these settings at 10:44, after you opened them');
  assert.equal(view.canSaveBoth, true);
  assert.deepEqual(view.theirs, ['PollSeconds: 30 is now 45']);
  assert.equal(view.yours.length, 1);

  const overlap = conflictView({ ...base, overlaps: ['PollSeconds'] }, 'Europe/Berlin');
  assert.equal(overlap.canSaveBoth, false);
  assert.match(overlap.note, /same setting/);

  const unknown = conflictView({ baseKnown: false, savedBy: '', savedAt: '' });
  assert.equal(unknown.canSaveBoth, false);
  assert.equal(unknown.title, 'Not saved: Someone else saved these settings after you opened them');
  assert.match(unknown.note, /Load the settings again/);
});

test('the overview greets by first name only when the sign-in name gives one', () => {
  assert.equal(greeting(9, 'Ertugrul Balveren'), 'Good morning, Ertugrul');
  assert.equal(greeting(15, 'anna.berger@contoso.example'), 'Good afternoon, Anna');
  assert.equal(greeting(15, 'x@contoso.example'), 'Good afternoon');
  assert.equal(greeting(20, ''), 'Good evening');
  assert.equal(greeting(11, 'Anna'), 'Good morning, Anna');
  assert.equal(greeting(12, 'Anna'), 'Good afternoon, Anna');
  assert.equal(greeting(18, 'Anna'), 'Good evening, Anna');
});

test('the overview names the weekday and the German date', () => {
  assert.equal(dayLine(new Date(2026, 10, 18, 16, 24)), 'WEDNESDAY 18.11.2026');
  assert.equal(dayLine(new Date(2027, 0, 3)), 'SUNDAY 03.01.2027');
});

test('a document type is named as in the Windows app: its name, else its ID', () => {
  assert.equal(typeLabel({ id: 'SalesInvoice', displayName: 'Sales invoice' }), 'Sales invoice');
  assert.equal(typeLabel({ id: 'SalesInvoice', displayName: '  ' }), 'SalesInvoice');
  assert.equal(typeLabel({ id: 'SalesInvoice' }), 'SalesInvoice');
});

test('links to the former Export pages open the Outbound pages', () => {
  assert.equal(canonicalPage(parseRoute('#/export/SalesInvoice/DEMF').page), 'outbound');
  assert.equal(canonicalPage('outbound'), 'outbound');
  assert.equal(canonicalPage('history'), 'history');
});

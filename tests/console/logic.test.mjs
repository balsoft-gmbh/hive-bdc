// node --test tests/console/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { attentionRows, germanDate, initials, licenceLine, newestFirst, parseRoute, stepLabel, topRole } from '../../Hosting/Console/logic.js';

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
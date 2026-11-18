// Pure functions of the web console: no DOM, no network, so Node's test runner checks them
// (tests/console/logic.test.mjs).

const roleOrder = ['Viewer', 'Operator', 'Administrator'];

/** The highest role of the signed-in user, or '' when none is known. */
export function topRole(roles) {
  let best = -1;
  for (const role of roles || []) {
    const index = roleOrder.findIndex((r) => r.toLowerCase() === String(role).toLowerCase());
    if (index > best) best = index;
  }
  return best < 0 ? '' : roleOrder[best];
}

/** Initials for the user chip: "anna.weber@contoso.com" gives "AW", "Ertugrul Balveren" gives "EB". */
export function initials(name) {
  const text = String(name || '').split('@')[0];
  const parts = text.split(/[\s._-]+/).filter((p) => /[a-z]/i.test(p));
  if (parts.length === 0) return '?';
  const first = parts[0][0];
  const last = parts.length > 1 ? parts[parts.length - 1][0] : '';
  return (first + last).toUpperCase();
}

const stepWords = {
  received: 'Received',
  read: 'Read',
  produced: 'Made',
  delivered: 'Delivered',
  waiting: 'Waiting',
  failed: 'Failed',
  done: 'Done',
};

/** The history step in plain words. */
export function stepLabel(step) {
  return stepWords[String(step || '').toLowerCase()] || String(step || '');
}

/**
 * The documents that need attention, one row each: the newest event per document wins, and a document whose newest
 * event is "done" is left out.
 */
export function attentionRows(events) {
  const newest = new Map();
  for (const e of events || []) {
    if (!e || !e.documentId) continue;
    const seen = newest.get(e.documentId);
    if (!seen || Number(e.id) > Number(seen.id)) newest.set(e.documentId, e);
  }
  return [...newest.values()]
    .filter((e) => String(e.step).toLowerCase() !== 'done')
    .sort((a, b) => Number(b.id) - Number(a.id))
    .map((e) => ({
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
export function newestFirst(events) {
  return [...(events || [])].sort((a, b) => Number(b.id) - Number(a.id));
}

/** The licence line for the side panel and the warning banner. */
export function licenceLine(licence) {
  if (!licence) return { ok: false, text: 'Licence unknown' };
  const state = String(licence.state || '');
  if (state === 'Valid' || state === 'Warning' || state === 'Grace') {
    return { ok: state === 'Valid', text: licence.expires ? `Licence valid until ${germanDate(licence.expires)}` : 'Licence valid' };
  }
  if (state === 'Missing') return { ok: false, text: 'No licence yet' };
  return { ok: false, text: licence.message || `Licence ${state.toLowerCase()}` };
}

/** "2027-09-30" as "30.09.2027"; anything else unchanged. */
export function germanDate(iso) {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(iso || ''));
  return m ? `${m[3]}.${m[2]}.${m[1]}` : String(iso || '');
}

/** The route of the page: "#/history?search=RE-1" gives { page: 'history', query: { search: 'RE-1' } }. */
export function parseRoute(hash) {
  const text = String(hash || '').replace(/^#\/?/, '');
  const [path, queryText] = text.split('?');
  const query = {};
  for (const pair of (queryText || '').split('&')) {
    if (!pair) continue;
    const [key, value] = pair.split('=');
    query[decodeURIComponent(key)] = decodeURIComponent((value || '').replace(/\+/g, ' '));
  }
  const parts = (path || '').split('/').filter(Boolean).map((p) => decodeURIComponent(p));
  return { page: parts[0] || 'overview', parts: parts.slice(1), query };
}
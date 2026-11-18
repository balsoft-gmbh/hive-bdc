// What the Updates tab shows (boards P3 to P6), without the DOM so node can test it.
const STEPS = ['download', 'signature', 'backup', 'switch', 'running'];

function parts(iso, zone, withSeconds) {
  const date = new Date(iso);
  if (!iso || Number.isNaN(date.getTime())) return null;
  const options = { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
  if (withSeconds) options.second = '2-digit';
  if (zone) options.timeZone = zone;
  const list = new Intl.DateTimeFormat('de-DE', options).formatToParts(date);
  return Object.fromEntries(list.map((p) => [p.type, p.value]));
}

/** "18.11.2026 16:34" in the instance's time zone. */
export function whenText(iso, zone) {
  const p = parts(iso, zone, false);
  return p ? `${p.day}.${p.month}.${p.year} ${p.hour}:${p.minute}` : '';
}

/** "16:31:04" in the instance's time zone. */
export function timeText(iso, zone) {
  const p = parts(iso, zone, true);
  return p ? `${p.hour}:${p.minute}:${p.second}` : '';
}

/** Which board applies: running (P4), stopped (P6), offer (P3), done (P5) or idle. */
export function screenOf(view) {
  if (!view) return 'idle';
  if (view.pending) return 'running';
  const last = (view.history || [])[0];
  const installed = view.installed && view.installed.version;
  if (last && (last.state === 'stopped' || (last.state === 'returned' && last.version !== installed))) return 'stopped';
  if (view.available) return 'offer';
  if (last && last.version === installed && (view.checks || []).length) return 'done';
  return 'idle';
}

const TITLES = {
  download: { done: 'Downloaded from Balsoft', running: 'Downloading from Balsoft', failed: 'Download stopped', waiting: 'Download from Balsoft' },
  signature: { done: 'Signature checked', running: 'Checking the signature', failed: 'Signature does not match', waiting: 'Check Balsoft\'s signature' },
  backup: { done: 'Settings backed up', running: 'Backing up the settings', failed: 'Settings not backed up', waiting: 'Back up the settings' },
  switch: { done: 'Restarted', running: 'Restarting with {to}', failed: 'Not switched', waiting: 'Switch and restart' },
  running: { done: 'Running {to}', running: 'Running {to}', failed: '{to} did not start properly', waiting: 'Running {to}' },
};

/** The steps of board P4 (a return has only its restart); a step after a failed one was not started (P6). */
export function stepRows(view, zone) {
  const last = (view.history || [])[0] || {};
  const to = (view.pending && view.pending.to) || last.version || (view.available && view.available.version) || '';
  const byName = new Map((view.steps || []).map((s) => [s.name, s]));
  const returnedByItself = !view.pending && last.state === 'returned' && last.version !== (view.installed && view.installed.version);
  const names = view.pending && view.pending.kind === 'return' ? ['switch', 'running'] : STEPS;
  let stopped = false;
  return names.map((name) => {
    const step = byName.get(name);
    let state = step ? step.state : stopped ? 'skipped' : 'waiting';
    if (name === 'switch' && state === 'done' && view.pending) state = 'running';
    if (name === 'running' && returnedByItself) state = 'failed';
    if (step && step.state === 'failed') stopped = true;
    const title = TITLES[name][state === 'skipped' ? 'waiting' : state] || TITLES[name].waiting;
    return {
      name,
      state,
      title: title.replace('{to}', to),
      detail: state === 'skipped' ? 'not started' : name === 'running' && returnedByItself ? last.note : (step && step.detail) || '',
      time: step && state !== 'running' ? timeText(step.at, zone) : '',
    };
  });
}

/** Percent of the steps done, for the bar of board P4. */
export function progress(rows) {
  return rows.length ? Math.round((100 * rows.filter((r) => r.state === 'done').length) / rows.length) : 0;
}

/** Each line of the history with what that version is now (boards P3, P5, P6). */
export function historyRows(view, zone) {
  const installed = view.installed && view.installed.version;
  const previous = view.previous && view.previous.version;
  let running = false;
  let kept = false;
  return (view.history || []).map((h) => {
    let badge = 'Removed';
    let tone = 'neutral';
    if (h.state === 'stopped') { badge = 'Stopped'; tone = 'rose'; }
    else if (h.state === 'returned' && h.version !== installed) { badge = 'Returned'; tone = 'amber'; }
    else if (h.version === installed && !running) { badge = 'Running'; tone = 'green'; running = true; }
    else if (h.version === previous && !kept) { badge = 'Kept for return'; kept = true; }
    return { version: h.version, badge, tone, line: [whenText(h.at, zone), h.by, h.note].filter(Boolean).join(' · ') };
  });
}

/** 1 when an update is offered and none runs: the badge on Service and the notice on the overview. */
export function updateBadge(view) {
  return view && view.available && !view.pending ? 1 : 0;
}
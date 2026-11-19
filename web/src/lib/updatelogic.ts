// What the Updates tab shows (boards P3 to P6), without the DOM so node can test it.

/** GET /service/updates (Hosting/SelfUpdate.cs Describe); every part but the version can be missing. */
export type UpdateStep = { name: string; state: string; at?: string; detail?: string };
export type UpdateCheck = { name: string; state: string; at?: string; detail?: string };
export type UpdateHistoryEntry = { version: string; at?: string; by?: string; state: string; note?: string };
export type UpdateView = {
  version: string;
  runtime?: string;
  timeZone?: string;
  canUpdate?: boolean;
  cannotUpdate?: string;
  installed?: { version?: string; since?: string; by?: string } | null;
  previous?: { version: string; package?: string } | null;
  pending?: { kind?: string; from?: string; to?: string; by?: string; startedAt?: string } | null;
  schedule?: { at: string; by?: string } | null;
  lastCheck?: { at?: string; ok?: boolean; message?: string; available?: unknown } | null;
  available?: { version: string; size?: number; notes?: string[]; releaseDate?: string } | null;
  steps?: UpdateStep[];
  checks?: UpdateCheck[];
  history?: UpdateHistoryEntry[];
};

export type StepState = "done" | "running" | "failed" | "waiting" | "skipped";
export type StepRow = { name: string; state: StepState; title: string; detail: string; time: string };
export type HistoryTone = "neutral" | "green" | "rose" | "amber";
export type HistoryRow = { version: string; badge: string; tone: HistoryTone; line: string; when: string; by: string; note: string };

const STEPS = ["download", "signature", "backup", "switch", "running"];

function parts(iso: string | undefined, zone: string | undefined, withSeconds: boolean) {
  const date = new Date(iso ?? "");
  if (!iso || Number.isNaN(date.getTime())) return null;
  const options: Intl.DateTimeFormatOptions = { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", hourCycle: "h23" };
  if (withSeconds) options.second = "2-digit";
  if (zone) options.timeZone = zone;
  const list = new Intl.DateTimeFormat("de-DE", options).formatToParts(date);
  return Object.fromEntries(list.map((p) => [p.type, p.value])) as Record<string, string>;
}

/** "18.11.2026 16:34" in the instance's time zone. */
export function whenText(iso: string | undefined, zone: string | undefined) {
  const p = parts(iso, zone, false);
  return p ? `${p.day}.${p.month}.${p.year} ${p.hour}:${p.minute}` : "";
}

/** "16:31:04" in the instance's time zone. */
export function timeText(iso: string | undefined, zone: string | undefined) {
  const p = parts(iso, zone, true);
  return p ? `${p.hour}:${p.minute}:${p.second}` : "";
}

export type Screen = "running" | "stopped" | "offer" | "done" | "idle";

/** Which board applies: running (P4), stopped (P6), offer (P3), done (P5) or idle. */
export function screenOf(view: UpdateView | null | undefined): Screen {
  if (!view) return "idle";
  if (view.pending) return "running";
  const last = (view.history || [])[0];
  const installed = view.installed && view.installed.version;
  if (last && (last.state === "stopped" || (last.state === "returned" && last.version !== installed))) return "stopped";
  if (view.available) return "offer";
  if (last && last.version === installed && (view.checks || []).length) return "done";
  return "idle";
}

const TITLES: Record<string, Record<string, string>> = {
  download: { done: "Downloaded from Balsoft", running: "Downloading from Balsoft", failed: "Download stopped", waiting: "Download from Balsoft" },
  signature: { done: "Signature checked", running: "Checking the signature", failed: "Signature does not match", waiting: "Check Balsoft's signature" },
  backup: { done: "Settings backed up", running: "Backing up the settings", failed: "Settings not backed up", waiting: "Back up the settings" },
  switch: { done: "Restarted", running: "Restarting with {to}", failed: "Not switched", waiting: "Switch and restart" },
  running: { done: "Running {to}", running: "Running {to}", failed: "{to} did not start properly", waiting: "Running {to}" },
};

/** The steps of board P4 (a return has only its restart); a step after a failed one was not started (P6). */
export function stepRows(view: UpdateView, zone: string | undefined): StepRow[] {
  const last: Partial<UpdateHistoryEntry> = (view.history || [])[0] || {};
  const to = (view.pending && view.pending.to) || last.version || (view.available && view.available.version) || "";
  const byName = new Map((view.steps || []).map((s) => [s.name, s]));
  const returnedByItself = !view.pending && last.state === "returned" && last.version !== (view.installed && view.installed.version);
  const names = view.pending && view.pending.kind === "return" ? ["switch", "running"] : STEPS;
  let stopped = false;
  return names.map((name) => {
    const step = byName.get(name);
    let state = (step ? step.state : stopped ? "skipped" : "waiting") as StepState;
    if (name === "switch" && state === "done" && view.pending) state = "running";
    if (name === "running" && returnedByItself) state = "failed";
    if (step && step.state === "failed") stopped = true;
    const title = TITLES[name][state === "skipped" ? "waiting" : state] || TITLES[name].waiting;
    return {
      name,
      state,
      title: title.replace("{to}", to),
      detail: state === "skipped" ? "not started" : name === "running" && returnedByItself ? last.note || "" : (step && step.detail) || "",
      time: step && state !== "running" ? timeText(step.at, zone) : "",
    };
  });
}

/** Percent of the steps done, for the bar of board P4. */
export function progress(rows: { state: string }[]) {
  return rows.length ? Math.round((100 * rows.filter((r) => r.state === "done").length) / rows.length) : 0;
}

/**
 * Each line of the history with what that version is now (boards P3, P5, P6). `line` is the one-line form of the
 * former console; the list shows when, by and note in their own columns.
 */
export function historyRows(view: UpdateView, zone: string | undefined): HistoryRow[] {
  const installed = view.installed && view.installed.version;
  const previous = view.previous && view.previous.version;
  let running = false;
  let kept = false;
  return (view.history || []).map((h) => {
    let badge = "Removed";
    let tone: HistoryTone = "neutral";
    if (h.state === "stopped") {
      badge = "Stopped";
      tone = "rose";
    } else if (h.state === "returned" && h.version !== installed) {
      badge = "Returned";
      tone = "amber";
    } else if (h.version === installed && !running) {
      badge = "Running";
      tone = "green";
      running = true;
    } else if (h.version === previous && !kept) {
      badge = "Kept for return";
      kept = true;
    }
    const when = whenText(h.at, zone);
    return { version: h.version, badge, tone, line: [when, h.by, h.note].filter(Boolean).join(" · "), when, by: h.by || "", note: h.note || "" };
  });
}

/** 1 when an update is offered and none runs: the badge on Service and the notice on the overview. */
export function updateBadge(view: UpdateView | null | undefined) {
  return view && view.available && !view.pending ? 1 : 0;
}

/** "23 MB" from the size in bytes. */
export function megabytes(bytes: number) {
  return `${Math.round(bytes / 1048576)} MB`;
}

/** The text "Copy details for Balsoft support" puts on the clipboard (board P6). */
export function supportDetails(view: UpdateView, last: UpdateHistoryEntry, rows: StepRow[]) {
  return [`Hive BDC ${view.version}, ${last.version}: ${last.note}`, ...rows.map((r) => `${r.title}: ${r.state}${r.detail ? ` (${r.detail})` : ""}`)].join("\n");
}

export type StopText = { badge: string; tone: HistoryTone; line: string };

/** The stop of board P6 in one line, with the state pill: a stop before the switch, or a return by itself. */
export function stopText(view: UpdateView): StopText {
  const last = (view.history || [])[0];
  const running = (view.installed && view.installed.version) || view.version;
  if (last && last.state === "returned")
    return { badge: "Returned", tone: "amber", line: `${last.version} did not run properly. Hive BDC is back on ${running}, and documents flow as before.` };
  return { badge: "Stopped", tone: "rose", line: `The update stopped before anything changed. ${running} runs with your settings, and documents flow as before.` };
}
// Design 2026-11-19-update-page-after-restart.md: the page after the restart.

/**
 * While a switch runs, a 404 to /api/service/updates comes from a version before 1.9.0 (its API had no /api/ prefix),
 * so another version answers (U2). A gap while App Service restarts the app is 502 or 503, or no answer (0): wait (U3).
 */
export function answeredByOlderVersion(error: unknown) {
  return typeof error === "object" && error !== null && (error as { status?: unknown }).status === 404;
}

const SLOW_MINUTES = 6;
const RETURN_MINUTES = 10;

/** What the running page adds after 6 and after 10 minutes, so a hanging restart is never only the same line (U5). */
export function restartNotice(startedAt: string | undefined, now: number, to: string, from: string, isReturn = false) {
  const started = startedAt ? Date.parse(startedAt) : NaN;
  if (Number.isNaN(started)) return null;
  const minutes = (now - started) / 60_000;
  if (minutes >= RETURN_MINUTES && !isReturn)
    return `${to} did not answer within ${RETURN_MINUTES} minutes. Hive BDC then returns to ${from} by itself; reload the page to see where it stands.`;
  if (minutes >= SLOW_MINUTES) return `This takes longer than usual. Hive BDC may already run ${to}; reload the page to see it.`;
  return null;
}

const SERVICE_TAB_IDS = ["status", "licence", "updates", "log"];
const PAGES = ["history", "outbound", "connections", "service"];

/**
 * The page of this console for an address of the console before 1.9.0 (#/service/updates and the like), which an open
 * page of that version reloads into after an update (U6). Anything else stays where it is.
 */
export function legacyConsoleRoute(hash: string) {
  const parts = hash.startsWith("#/") ? hash.slice(2).split(/[/?]/).filter(Boolean) : [];
  const [page, tab] = parts;
  if (!page || !PAGES.includes(page)) return null;
  if (page === "service" && tab && SERVICE_TAB_IDS.includes(tab)) return `/service/?tab=${tab}`;
  return `/${page}/`;
}

/** Where a reload after a switch goes: the update page of whichever version answers (both consoles know this address). */
export const UPDATES_ADDRESS = "/#/service/updates";

// npm test (tsx --test tests/*.test.ts); port of tests/console/updates.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  answeredByOlderVersion,
  historyRows,
  legacyConsoleRoute,
  megabytes,
  progress,
  restartNotice,
  screenOf,
  stepRows,
  stopText,
  supportDetails,
  updateBadge,
  whenText,
  type UpdateView,
} from "../src/lib/updatelogic";

const base = (): UpdateView => ({
  version: "1.8.0",
  canUpdate: true,
  installed: { version: "1.8.0", since: "2026-11-13T08:40:00+00:00", by: "Ertugrul Balveren" },
  previous: null,
  pending: null,
  schedule: null,
  lastCheck: { at: "2026-11-18T15:20:00+00:00", ok: true, message: "Hive BDC 1.8.1 is ready." },
  available: { version: "1.8.1", size: 24326963, notes: ["DocuWare date fields take German dates."], releaseDate: "2026-11-17" },
  steps: [],
  checks: [],
  history: [],
});

test("a newer version is offered, nothing else is the plain page", () => {
  assert.equal(screenOf(base()), "offer");
  assert.equal(screenOf({ ...base(), available: null }), "idle");
});

test("a pending switch shows the running update, also after the restart until it is settled", () => {
  const view = { ...base(), pending: { kind: "update", from: "1.8.0", to: "1.8.1", by: "Anna", startedAt: "2026-11-18T15:31:00+00:00" } };
  assert.equal(screenOf(view), "running");
});

test("the last update shows its result until something newer happens", () => {
  const done = {
    ...base(),
    version: "1.8.1",
    available: null,
    installed: { version: "1.8.1", since: "2026-11-18T15:34:00+00:00", by: "Anna" },
    previous: { version: "1.8.0", package: "x.zip" },
    checks: [{ name: "Settings read", state: "done", at: "2026-11-18T15:34:12+00:00", detail: "2 document types, 3 companies" }],
    history: [{ version: "1.8.1", at: "2026-11-18T15:34:20+00:00", by: "Anna", state: "updated", note: "Updated from 1.8.0." }],
  };
  assert.equal(screenOf(done), "done");
  const stopped = {
    ...base(),
    steps: [
      { name: "download", state: "done" },
      { name: "signature", state: "failed", detail: "The file is not the one Balsoft signed: the signature does not match." },
    ],
    history: [
      { version: "1.8.1", at: "2026-11-18T15:31:05+00:00", by: "Anna", state: "stopped", note: "The file is not the one Balsoft signed: the signature does not match." },
    ],
  };
  assert.equal(screenOf(stopped), "stopped");
  const returned = {
    ...base(),
    history: [{ version: "1.8.1", at: "2026-11-18T15:45:00+00:00", by: "Anna", state: "returned", note: "Returned to 1.8.0 by itself: 1.8.1 did not start properly 3 times." }],
  };
  assert.equal(screenOf(returned), "stopped");
});

test("the steps of a running update read like board P4, the ones not reached yet stay grey", () => {
  const view = {
    ...base(),
    pending: { to: "1.8.1" },
    steps: [
      { name: "download", state: "done", at: "2026-11-18T15:31:04+00:00", detail: "hivebdc-1.8.1.zip, 23.2 MB" },
      { name: "signature", state: "done", at: "2026-11-18T15:31:05+00:00", detail: "Made and signed by Balsoft GmbH" },
      { name: "backup", state: "done", at: "2026-11-18T15:31:05+00:00", detail: "settings-1.8.0-20261118-1631.json" },
      { name: "switch", state: "done", at: "2026-11-18T15:31:06+00:00", detail: "Restarting with 1.8.1" },
    ],
  };
  const rows = stepRows(view, "Europe/Berlin");
  assert.deepEqual(
    rows.map((r) => r.title),
    ["Downloaded from Balsoft", "Signature checked", "Settings backed up", "Restarting with 1.8.1", "Running 1.8.1"],
  );
  assert.deepEqual(
    rows.map((r) => r.state),
    ["done", "done", "done", "running", "waiting"],
  );
  assert.equal(rows[0].time, "16:31:04");
  assert.equal(progress(rows), 60);
});

test("a stopped step names the reason and the steps after it were not started", () => {
  const view = {
    ...base(),
    steps: [
      { name: "download", state: "done", at: "2026-11-18T15:31:04+00:00", detail: "hivebdc-1.8.1.zip, 23.2 MB" },
      { name: "signature", state: "failed", at: "2026-11-18T15:31:05+00:00", detail: "The file is not the one Balsoft signed: the signature does not match." },
    ],
  };
  const rows = stepRows(view, "Europe/Berlin");
  assert.equal(rows[1].title, "Signature does not match");
  assert.equal(rows[1].state, "failed");
  assert.deepEqual(
    rows.slice(2).map((r) => r.state),
    ["skipped", "skipped", "skipped"],
  );
});

test("the history says which version runs, which one is kept to return to and which update stopped", () => {
  const view = {
    ...base(),
    installed: { version: "1.8.1" },
    previous: { version: "1.8.0", package: "a.zip" },
    history: [
      { version: "1.8.1", at: "2026-11-18T15:34:00+00:00", by: "Anna", state: "updated", note: "Updated from 1.8.0." },
      { version: "1.8.1", at: "2026-11-17T21:00:00+00:00", by: "Anna", state: "stopped", note: "The download stopped: The response ended prematurely." },
      { version: "1.8.0", at: "2026-11-13T08:40:00+00:00", by: "Ben", state: "updated", note: "Updated from 1.7.9." },
      { version: "1.7.9", at: "2026-11-04T21:00:00+00:00", by: "Ben", state: "updated", note: "Updated from 1.7.8." },
    ],
  };
  const rows = historyRows(view, "Europe/Berlin");
  assert.deepEqual(
    rows.map((r) => r.badge),
    ["Running", "Stopped", "Kept for return", "Removed"],
  );
  assert.deepEqual(
    rows.map((r) => r.tone),
    ["green", "rose", "neutral", "neutral"],
  );
  assert.equal(rows[0].line, "18.11.2026 16:34 · Anna · Updated from 1.8.0.");
});

test("the history list has when, by and note in their own columns, empty parts stay empty", () => {
  const view = {
    ...base(),
    history: [
      { version: "1.8.0", at: "2026-11-13T08:40:00+00:00", by: "Ben", state: "updated", note: "Updated from 1.7.9." },
      { version: "1.7.9", state: "installed" },
    ],
  };
  const rows = historyRows(view, "Europe/Berlin");
  assert.deepEqual(
    rows.map((r) => [r.when, r.by, r.note]),
    [
      ["13.11.2026 09:40", "Ben", "Updated from 1.7.9."],
      ["", "", ""],
    ],
  );
  assert.equal(rows[1].line, "");
});

test("the menu and the overview point at an update only when one is offered and none runs", () => {
  assert.equal(updateBadge(base()), 1);
  assert.equal(updateBadge({ ...base(), pending: { to: "1.8.1" } }), 0);
  assert.equal(updateBadge({ ...base(), available: null }), 0);
  assert.equal(updateBadge(null), 0);
});

test("times are shown in the instance time zone", () => {
  assert.equal(whenText("2026-11-18T15:34:00+00:00", "Europe/Berlin"), "18.11.2026 16:34");
  assert.equal(whenText("", "Europe/Berlin"), "");
});

test("a return shows only its restart, and a return by itself names why the new version did not run", () => {
  const back = {
    ...base(),
    pending: { kind: "return", from: "1.8.1", to: "1.8.0" },
    steps: [{ name: "switch", state: "done", at: "2026-11-18T15:47:00+00:00", detail: "Restarting with 1.8.0" }],
  };
  assert.deepEqual(
    stepRows(back, "Europe/Berlin").map((r) => [r.name, r.state]),
    [
      ["switch", "running"],
      ["running", "waiting"],
    ],
  );

  const itself = {
    ...base(),
    steps: ["download", "signature", "backup", "switch"].map((name) => ({ name, state: "done", at: "2026-11-18T15:52:28+00:00", detail: "" })),
    history: [{ version: "1.8.2", at: "2026-11-18T15:57:00+00:00", by: "Anna", state: "returned", note: "Returned to 1.8.0 by itself: 1.8.2 did not start properly 3 times." }],
  };
  const rows = stepRows(itself, "Europe/Berlin");
  assert.equal(rows[4].state, "failed");
  assert.equal(rows[4].title, "1.8.2 did not start properly");
  assert.match(rows[4].detail, /by itself/);
});

test("a return made by a person reads like any other version change, not like a failure", () => {
  const view = {
    ...base(),
    installed: { version: "1.8.0" },
    previous: null,
    available: { version: "1.8.1" },
    history: [
      { version: "1.8.0", at: "2026-11-18T15:49:00+00:00", by: "Ben", state: "back", note: "Returned from 1.8.1." },
      { version: "1.8.1", at: "2026-11-18T15:46:00+00:00", by: "Anna", state: "updated", note: "Updated from 1.8.0." },
    ],
  };
  assert.equal(screenOf(view), "offer");
  assert.deepEqual(
    historyRows(view, "Europe/Berlin").map((r) => r.badge),
    ["Running", "Removed"],
  );
});

test("the size reads in whole megabytes and the support details name every step", () => {
  assert.equal(megabytes(24326963), "23 MB");
  const view = {
    ...base(),
    steps: [
      { name: "download", state: "done", at: "2026-11-18T15:31:04+00:00", detail: "hivebdc-1.8.1.zip, 23.2 MB" },
      { name: "signature", state: "failed", at: "2026-11-18T15:31:05+00:00", detail: "The signature does not match." },
    ],
  };
  const last = { version: "1.8.1", state: "stopped", note: "The signature does not match." };
  assert.deepEqual(supportDetails(view, last, stepRows(view, "Europe/Berlin")).split("\n"), [
    "Hive BDC 1.8.0, 1.8.1: The signature does not match.",
    "Downloaded from Balsoft: done (hivebdc-1.8.1.zip, 23.2 MB)",
    "Signature does not match: failed (The signature does not match.)",
    "Back up the settings: skipped (not started)",
    "Switch and restart: skipped (not started)",
    "Running 1.8.1: skipped (not started)",
  ]);
});

test("a stop says in one line that nothing changed, a return by itself names the version that did not run", () => {
  const stopped = { ...base(), history: [{ version: "1.8.1", at: "2026-11-18T15:31:05+00:00", by: "Anna", state: "stopped", note: "The signature does not match." }] };
  assert.equal(screenOf(stopped), "stopped");
  assert.deepEqual(stopText(stopped), {
    badge: "Stopped",
    tone: "rose",
    line: "The update stopped before anything changed. 1.8.0 runs with your settings, and documents flow as before.",
  });
  const returned = { ...base(), history: [{ version: "1.8.1", at: "2026-11-18T15:57:00+00:00", by: "Anna", state: "returned", note: "Returned to 1.8.0 by itself." }] };
  assert.equal(screenOf(returned), "stopped");
  assert.deepEqual(stopText(returned), {
    badge: "Returned",
    tone: "amber",
    line: "1.8.1 did not run properly. Hive BDC is back on 1.8.0, and documents flow as before.",
  });
  assert.match(stopText({ ...returned, installed: null }).line, /back on 1\.8\.0,/);
});

// Design 2026-11-19-update-page-after-restart.md: the page after the restart (U2, U3, U5, U6).
test("a 404 during the restart is an older version answering; a gap while the app restarts is not", () => {
  assert.equal(answeredByOlderVersion({ status: 404 }), true);
  for (const status of [0, 502, 503, 500]) assert.equal(answeredByOlderVersion({ status }), false, String(status));
  assert.equal(answeredByOlderVersion(new Error("no status")), false);
});

test("the running page says nothing extra for 6 minutes, then that it takes longer, then what happens after 10", () => {
  const started = "2026-11-19T22:48:00+00:00";
  const at = (minutes: number) => Date.parse(started) + minutes * 60_000;
  assert.equal(restartNotice(started, at(5.9), "1.10.1", "1.8.1"), null);
  assert.equal(
    restartNotice(started, at(6), "1.10.1", "1.8.1"),
    "This takes longer than usual. Hive BDC may already run 1.10.1; reload the page to see it.",
  );
  assert.equal(
    restartNotice(started, at(10), "1.10.1", "1.8.1"),
    "1.10.1 did not answer within 10 minutes. Hive BDC then returns to 1.8.1 by itself; reload the page to see where it stands.",
  );
  assert.equal(restartNotice(undefined, at(30), "1.10.1", "1.8.1"), null);
  // A return has no further version to fall back to: it keeps the first line.
  assert.match(restartNotice(started, at(12), "1.8.1", "1.10.1", true)!, /^This takes longer than usual\. Hive BDC may already run 1\.8\.1;/);
});

test("an address of the former console opens the same place in this one", () => {
  assert.equal(legacyConsoleRoute("#/service/updates"), "/service/?tab=updates");
  assert.equal(legacyConsoleRoute("#/service/status"), "/service/?tab=status");
  assert.equal(legacyConsoleRoute("#/service"), "/service/");
  assert.equal(legacyConsoleRoute("#/history"), "/history/");
  assert.equal(legacyConsoleRoute("#/connections"), "/connections/");
  assert.equal(legacyConsoleRoute("#/service/elsewhere"), "/service/");
  assert.equal(legacyConsoleRoute("#/nowhere"), null);
  assert.equal(legacyConsoleRoute("#section"), null);
  assert.equal(legacyConsoleRoute(""), null);
});
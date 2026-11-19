import { defaults } from "./fixtures";

// Answers for the Service page tests in the shapes the Hive BDC web host gives (Hosting/LocalStatusServer*.cs,
// SelfUpdate Describe); values made up. The instance runs 1.9.0 and is offered 1.9.1.

type View = Record<string, any>;

const installed = (): View => defaults()["GET /service/updates"] as View;

export const viewer = { name: "Vera Viewer", roles: ["Viewer"], canRetry: false, canChange: false, version: "1.9.0" };
export const operator = { name: "Otto Operator", roles: ["Operator"], canRetry: true, canChange: false, version: "1.9.0" };

export const offer = (): View => ({
  ...installed(),
  lastCheck: { at: "2026-11-19T06:01:00+00:00", ok: true, available: "1.9.1", message: "Hive BDC 1.9.1 is ready." },
  available: {
    version: "1.9.1",
    size: 24326963,
    notes: ["DocuWare date fields take German dates.", "The test run names the SharePoint column it could not fill."],
    releaseDate: "2026-11-18",
  },
});

export const scheduled = (): View => ({ ...offer(), schedule: { at: "2026-11-19T21:00:00+00:00", by: "Anna Weber" } });

export const cannotUpdate = (): View => ({
  ...offer(),
  canUpdate: false,
  cannotUpdate: "This instance runs from a package that Balsoft does not update; ask the person who installed it.",
});

const pending = { kind: "update", from: "1.9.0", to: "1.9.1", by: "Anna Weber", startedAt: "2026-11-19T15:31:00+00:00" };

/** Download and signature done, the signature step of a fresh switch still going. */
export const runningEarly = (): View => ({
  ...offer(),
  pending,
  steps: [
    { name: "download", state: "done", at: "2026-11-19T15:31:04+00:00", detail: "hivebdc-1.9.1.zip, 23.2 MB" },
    { name: "signature", state: "running", at: "2026-11-19T15:31:05+00:00", detail: "" },
  ],
});

/** Everything before the restart done: the switch runs (board P4). */
export const running = (): View => ({
  ...offer(),
  pending,
  steps: [
    { name: "download", state: "done", at: "2026-11-19T15:31:04+00:00", detail: "hivebdc-1.9.1.zip, 23.2 MB" },
    { name: "signature", state: "done", at: "2026-11-19T15:31:05+00:00", detail: "Made and signed by Balsoft GmbH" },
    { name: "backup", state: "done", at: "2026-11-19T15:31:05+00:00", detail: "settings-1.9.0-20261119-1631.json" },
    { name: "switch", state: "done", at: "2026-11-19T15:31:06+00:00", detail: "Restarting with 1.9.1" },
  ],
});

export const returning = (): View => ({
  ...done(),
  previous: null,
  pending: { kind: "return", from: "1.9.1", to: "1.9.0", by: "Anna Weber", startedAt: "2026-11-19T16:02:00+00:00" },
  steps: [{ name: "switch", state: "done", at: "2026-11-19T16:02:01+00:00", detail: "Restarting with 1.9.0" }],
  checks: [],
});

const signatureNote = "The file is not the one Balsoft signed: the signature does not match.";

/** The signature did not match: the switch stopped before anything changed (board P6). */
export const stopped = (): View => ({
  ...offer(),
  steps: [
    { name: "download", state: "done", at: "2026-11-19T15:31:04+00:00", detail: "hivebdc-1.9.1.zip, 23.2 MB" },
    { name: "signature", state: "failed", at: "2026-11-19T15:31:05+00:00", detail: signatureNote },
  ],
  history: [
    { version: "1.9.1", at: "2026-11-19T15:31:05+00:00", by: "Anna Weber", state: "stopped", note: signatureNote },
    ...installed().history,
  ],
});

/** 1.9.1 did not start properly, Hive BDC went back to 1.9.0 by itself. */
export const returnedByItself = (): View => ({
  ...offer(),
  steps: ["download", "signature", "backup", "switch"].map((name) => ({ name, state: "done", at: "2026-11-19T15:52:28+00:00", detail: "" })),
  history: [
    { version: "1.9.1", at: "2026-11-19T15:57:00+00:00", by: "Anna Weber", state: "returned", note: "Returned to 1.9.0 by itself: 1.9.1 did not start properly 3 times." },
    ...installed().history,
  ],
});

/** 1.9.1 runs and checked itself; 1.9.0 is kept to return to (board P5). */
export const done = (): View => ({
  ...installed(),
  version: "1.9.1",
  installed: { version: "1.9.1", since: "2026-11-19T15:34:00+00:00", by: "Anna Weber" },
  previous: { version: "1.9.0", package: "hivebdc-1.9.0.zip" },
  lastCheck: { at: "2026-11-19T15:20:00+00:00", ok: true, available: null, message: "" },
  checks: [
    { name: "Settings read", state: "done", at: "2026-11-19T15:34:12+00:00", detail: "2 document types, 1 company" },
    { name: "Sources answer", state: "done", at: "2026-11-19T15:34:14+00:00", detail: "Blob container bdc-export of stcontosofo" },
    { name: "Licence valid", state: "done", at: "2026-11-19T15:34:15+00:00", detail: "Contoso GmbH until 30.09.2027" },
  ],
  history: [
    { version: "1.9.1", at: "2026-11-19T15:34:20+00:00", by: "Anna Weber", state: "updated", note: "Updated from 1.9.0." },
    ...installed().history,
  ],
});

export const notOnAzure = { __status: 404, error: "This instance is updated by Setup from the Windows app." };

export const logs = {
  file: "hivebdc-20261119.log",
  lines: [
    "2026-11-19 08:00:01.204 +01:00 [INF] Hive BDC 1.9.0 started in Azure run mode.",
    "2026-11-19 08:12:03.551 +01:00 [WRN] The container answered 403: the instance has no data role on it.",
    "2026-11-19 08:14:10.020 +01:00 [INF] SI-0042 delivered to hivebdc/out/.",
    "2026-11-19 08:15:44.901 +01:00 [ERR] SharePoint answered 404 for the library Documents of contoso.example.",
  ],
};

export const fullLog = { ...logs, lines: [...logs.lines, "2026-11-19 09:00:00.000 +01:00 [INF] Checked every source."] };

export const testRun = {
  ok: true,
  documentId: "SI-0043",
  message: "One document went all the way through.",
  events: [
    { at: "2026-11-19 09:01:02", step: "received", target: "", state: "ok", message: "Taken from the Blob container bdc-export." },
    { at: "2026-11-19 09:01:03", step: "einvoice", target: "", state: "ok", message: "E-invoice made." },
    { at: "2026-11-19 09:01:05", step: "done", target: "Blob container", state: "ok", message: "Delivered to hivebdc/out/." },
  ],
};

export const licenceStored = {
  ok: true,
  state: "Valid",
  message: "The licence for Contoso GmbH is stored. It is valid until 30.09.2028.",
  customer: "Contoso GmbH",
  expires: "2028-09-30",
};

export const licenceRefused = { __status: 400, ok: false, state: "Invalid", message: "This licence was issued for another tenant." };
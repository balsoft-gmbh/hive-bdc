// The answers of a set-up Azure instance, in the shapes the Hive BDC web host gives (read from a test instance on
// 2026-11-19, values made up). Tests change single answers through mock() in helpers.ts.

import { outboundAnswers } from "./fixtures-outbound";

const root = "/home/data/hivebdc/in";

export const config = () => ({
  version: "v7",
  pollSeconds: 30,
  logDir: "",
  paused: false,
  setupOpen: false,
  license: { file: "" },
  sharePointAuth: { tenantId: "", clientId: "", secretSet: false },
  mail: { enabled: false, tenantId: "", clientId: "", secretSet: false, from: "", redirectTo: [] as string[], saveToSentItems: true },
  connections: [
    { id: "fo-storage", kind: "azure-storage", settings: { AccountUrl: "https://stcontosofo.blob.core.windows.net" }, hasSecret: false },
    { id: "own-storage", kind: "azure-storage", settings: { AccountUrl: "https://sthivebdcexample.blob.core.windows.net" }, hasSecret: false },
  ],
  documentTypes: [
    {
      id: "SalesInvoice",
      displayName: "Sales invoice",
      party: "Customer",
      handler: "EInvoice",
      enabled: true,
      mail: true,
      companies: [
        {
          dataAreaId: "1000",
          input: `${root}/fo-storage-bdc-export`,
          inputFormat: "Json",
          output: "",
          enabled: true,
          mail: false,
          sharePoint: { enabled: false, siteUrl: "", library: "", folder: "", fields: {} },
          targets: [{ kind: "blob", connection: "own-storage", enabled: true, settings: { Container: "hivebdc", Prefix: "out/" }, fields: {} }],
          source: { kind: "blob", connection: "fo-storage", settings: { Container: "bdc-export" } },
        },
      ],
    },
    { id: "SalesQuotation", displayName: "Quotation", party: "Customer", handler: "Pdf", enabled: true, mail: true, companies: [] },
  ],
});

export const defaults = (): Record<string, unknown> => ({
  ...outboundAnswers(),
  "GET /me": { name: "Anna Weber", roles: ["Administrator"], canRetry: true, canChange: true, version: "1.9.0" },
  "GET /status": {
    running: true,
    version: "1.9.0",
    companyCount: 1,
    companies: [{ dataAreaId: "1000", sharePointEnabled: false }],
    folderCount: 1,
    pollSeconds: 30,
    sharePoint: false,
    documentTypeCount: 2,
    documentTypes: [],
    license: {
      state: "Valid",
      message: "",
      customer: "Contoso GmbH",
      expires: "2027-09-30",
      features: ["einvoice", "documents"],
      blocked: false,
    },
  },
  "GET /config": config(),
  "GET /service/updates": {
    version: "1.9.0",
    runtime: "DOTNETCORE|10.0",
    timeZone: "Europe/Berlin",
    canUpdate: true,
    cannotUpdate: "",
    installed: { version: "1.9.0", since: "2026-11-19T07:02:00+00:00", by: "deployment" },
    previous: null,
    pending: null,
    schedule: null,
    lastCheck: { at: "2026-11-19T06:01:00+00:00", ok: true, available: null, message: "" },
    available: null,
    steps: [],
    checks: [],
    history: [{ version: "1.9.0", at: "2026-11-19T07:02:00+00:00", by: "deployment", state: "installed", note: "Installed by a deployment." }],
  },
  "GET /metrics": { today: 4, month: 37, total: 412, monthByType: [{ documentType: "SalesInvoice", count: 37 }], recent: [] },
  "GET /history/attention": {
    events: [
      {
        id: 41,
        at: "2026-11-19 08:12:03",
        documentId: "SI-0041",
        documentNumber: "SI-0041",
        documentType: "SalesInvoice",
        dataAreaId: "1000",
        input: "",
        step: "failed",
        target: "Blob container",
        state: "error",
        message: "The container answered 403: the instance has no data role on it.",
      },
    ],
  },
  "GET /history": {
    search: "",
    events: [
      { id: 42, at: "2026-11-19 08:14:10", documentId: "SI-0042", documentNumber: "SI-0042", documentType: "SalesInvoice", dataAreaId: "1000", input: "", step: "done", target: "", state: "ok", message: "Delivered." },
      { id: 41, at: "2026-11-19 08:12:03", documentId: "SI-0041", documentNumber: "SI-0041", documentType: "SalesInvoice", dataAreaId: "1000", input: "", step: "failed", target: "Blob container", state: "error", message: "The container answered 403." },
    ],
  },
  "POST /history/retry": { ok: true, message: "Back in the queue." },
  "GET /setup/identity": {
    objectId: "00000000-0000-0000-0000-000000000001",
    tenantId: "00000000-0000-0000-0000-000000000002",
    inputRoot: root,
    timeZone: "Europe/Berlin",
    region: "germanywestcentral",
    message: "",
  },
});

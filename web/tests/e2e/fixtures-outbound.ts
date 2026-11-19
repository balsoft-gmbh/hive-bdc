import { config } from "./fixtures";

// The answers the Outbound and Connections pages read besides GET /config, in the shapes the Hive BDC web host gives
// (Hosting/LocalStatusServer.Destinations.cs and .Setup.cs; descriptors as Connectors/Destinations.cs and
// ServiceBus.cs describe them). Values are made up.

const root = "/home/data/hivebdc/in";

const setting = (key: string, label: string, extra: Record<string, unknown> = {}) => ({
  key,
  label,
  step: "which",
  required: true,
  lookup: null,
  dependsOn: null,
  hint: "",
  ...extra,
});

export const targets = () => ({
  targets: [
    {
      kind: "sharepoint",
      name: "SharePoint library",
      description: "Uploaded to a document library, with columns filled from the document.",
      connectionKind: "sharepoint",
      connected: true,
      fieldsLookup: "columns",
      settings: [
        setting("SiteUrl", "Site address", { hint: "https://yourcompany.sharepoint.com/sites/Name" }),
        setting("Library", "Library", { lookup: "libraries", dependsOn: "SiteUrl" }),
        setting("Folder", "Folder (optional)", { required: false }),
      ],
    },
    {
      kind: "docuware",
      name: "DocuWare file cabinet",
      description: "Stored with index values from the document. A repeat updates the same document.",
      connectionKind: "docuware",
      connected: false,
      fieldsLookup: "fields",
      settings: [
        setting("FileCabinet", "File cabinet", { lookup: "cabinets" }),
        setting("MatchField", "Never store twice: find an earlier version by", { required: false, lookup: "matchfields", dependsOn: "FileCabinet" }),
      ],
    },
    {
      kind: "blob",
      name: "Azure Blob container",
      description: "Uploaded to a container of an Azure storage account, for example for Dynamics 365 Finance and Operations.",
      connectionKind: "azure-storage",
      connected: true,
      fieldsLookup: null,
      settings: [
        setting("Container", "Container"),
        setting("Prefix", "Folder in the container (optional)", { required: false, hint: "For example outbound/" }),
      ],
    },
    {
      kind: "servicebus",
      name: "Azure Service Bus",
      description: "Sent as a JSON message to a queue or topic.",
      connectionKind: "servicebus",
      connected: true,
      fieldsLookup: null,
      settings: [setting("Queue", "Queue", { required: false, hint: "Give a queue or a topic." }), setting("Topic", "Topic", { required: false })],
    },
  ],
});

export const kinds = () => ({
  kinds: [
    {
      kind: "azure-storage",
      name: "Azure Storage",
      description: "Documents from and to Azure Blob containers, for example of Dynamics 365 Finance and Operations.",
      secretLabel: "Connection string",
      secretHint: "",
      settings: [
        {
          key: "AccountUrl",
          label: "Account address (only for a managed identity in Azure)",
          required: false,
          hint: "https://<account>.blob.core.windows.net.",
        },
      ],
    },
    {
      kind: "servicebus",
      name: "Azure Service Bus",
      description: "Documents from and to Service Bus queues and topics.",
      secretLabel: "Connection string",
      secretHint: "",
      settings: [
        { key: "Namespace", label: "Namespace (only for a managed identity in Azure)", required: false, hint: "<name>.servicebus.windows.net." },
      ],
    },
    {
      kind: "docuware",
      name: "DocuWare",
      description: "Store documents in DocuWare file cabinets, or import from them.",
      secretLabel: "Password",
      secretHint: "",
      settings: [
        { key: "Url", label: "Address of your DocuWare", required: true, hint: "" },
        { key: "UserName", label: "User name", required: true, hint: "" },
      ],
    },
  ],
});

/** A set-up instance with every kind of source and destination: 1000 reads Blob, 2000 a web address, 3000 Service Bus. */
export const fullConfig = () => {
  const base = config();
  return {
    ...base,
    sharePointAuth: { tenantId: "00000000-0000-0000-0000-000000000002", clientId: "11111111-2222-3333-4444-555555555555", secretSet: true },
    mail: {
      enabled: true,
      tenantId: "",
      clientId: "66666666-7777-8888-9999-000000000000",
      secretSet: true,
      from: "invoices@contoso.example",
      redirectTo: ["test@contoso.example"],
      saveToSentItems: true,
    },
    connections: [
      ...base.connections,
      { id: "sb-fo", kind: "servicebus", settings: { Namespace: "sb-contoso.servicebus.windows.net" }, hasSecret: false },
      { id: "docuware", kind: "docuware", settings: { Url: "https://contoso.docuware.example", UserName: "hivebdc" }, hasSecret: true },
      { id: "in-salesinvoice-2000", kind: "https", settings: {}, hasSecret: true },
    ],
    documentTypes: [
      {
        ...base.documentTypes[0],
        companies: [
          {
            ...base.documentTypes[0].companies[0],
            sharePoint: {
              enabled: true,
              siteUrl: "https://contoso.sharepoint.example/sites/Finance",
              library: "Invoices",
              folder: "{issueDate:yyyy}",
              fields: { Title: "{documentNumber}" },
            },
          },
          {
            dataAreaId: "2000",
            input: `${root}/https-SalesInvoice-2000`,
            inputFormat: "Json",
            output: "",
            enabled: true,
            mail: true,
            sharePoint: { enabled: false, siteUrl: "", library: "", folder: "", fields: {} },
            targets: [
              {
                kind: "docuware",
                connection: "docuware",
                enabled: true,
                settings: { FileCabinet: "fc-1", FileCabinetName: "Outgoing invoices" },
                fields: {},
              },
            ],
            source: { kind: "https", connection: "in-salesinvoice-2000", settings: {} },
          },
          {
            dataAreaId: "3000",
            input: `${root}/sb-sb-fo-bdc-export`,
            inputFormat: "Json",
            output: "",
            enabled: true,
            mail: false,
            sharePoint: { enabled: false, siteUrl: "", library: "", folder: "", fields: {} },
            targets: [{ kind: "blob", connection: "own-storage", enabled: true, settings: { Container: "hivebdc" }, fields: {} }],
            source: {
              kind: "servicebus",
              connection: "sb-fo",
              settings: { Queue: "bdc-export", PdfConnection: "fo-storage", PdfContainer: "bdc-pdf" },
            },
          },
        ],
      },
      base.documentTypes[1],
      { id: "CollectionLetter", displayName: "Collection letter", party: "Customer", handler: "Pdf", enabled: false, mail: true, companies: [] },
    ],
  };
};

/** The extra answers both pages need; the tests add them on top of the defaults of fixtures.ts. */
export const outboundAnswers = (): Record<string, unknown> => ({
  "GET /targets": targets(),
  "GET /connections/kinds": kinds(),
  "GET /export/sample": {
    file: "SI-0042_1000.json",
    output: "SI-0042.pdf",
    fields: { documentNumber: "SI-0042", issueDate: "2026-11-18", recipientName: "Fabrikam GmbH", total: "1190.00" },
    message: "",
  },
  "POST /targets/choices": {
    ok: true,
    message: "",
    choices: [
      { value: "Title", label: "Title", detail: "" },
      { value: "Customer", label: "Customer", detail: "text" },
    ],
  },
  "POST /setup/check-source": {
    ok: false,
    message: "The instance has no data role on the container bdc-export.",
    command: 'az role assignment create --role "Storage Blob Data Contributor" --assignee-object-id 00000000-0000-0000-0000-000000000001',
  },
  "POST /setup/check-queue": { ok: true, message: "Queue bdc-export: 3 messages waiting." },
  "POST /connections/test": { ok: true, message: "Signed in." },
  "POST /config/test-sharepoint": { ok: true, message: "Signed in. The library Invoices takes files." },
  "POST /config/test-mail": { ok: true, message: "Signed in. The mailbox invoices@contoso.example may send." },
  "POST /config": { ok: true, version: "v8" },
});

export const viewer = { name: "Vera Viewer", roles: ["Viewer"], canRetry: false, canChange: false, version: "1.9.0" };

/** The refusal of a save that someone else's save overtook (board W6), with nothing changed by both. */
export const conflict = {
  __status: 409,
  error: "The settings were saved by someone else in the meantime.",
  savedBy: "Bernd Berger",
  savedAt: "2026-11-19T09:30:00+00:00",
  baseKnown: true,
  overlaps: [],
  theirChanges: [{ path: "Quotation › Name", before: "Quotation", after: "Sales quotation" }],
  yourChanges: [{ path: "Sales invoice › Active", before: "on", after: "off" }],
};
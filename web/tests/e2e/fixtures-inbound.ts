// The answers of an Azure instance with inbound flows (design 2026-11-20): GET /sources as the web host gives it on
// Azure (no folder destination), and GET /config with flows in the settings' own PascalCase. Values made up.

import { config } from "./fixtures";

export const sources = () => ({
  sources: [
    {
      kind: "docuware",
      name: "DocuWare",
      description: "Documents in a file cabinet, ready when a field has a value.",
      connectionKind: "docuware",
      connected: false,
      recordNoun: "document",
      defaultFileName: "{DWDOCID}.csv",
      fieldsLookup: "fields",
      settings: [
        { key: "FileCabinet", label: "File cabinet", step: "which", required: true, lookup: "cabinets", dependsOn: null, hint: "" },
        { key: "ReadyField", label: "Ready when the field", step: "which", required: true, lookup: "readyfields", dependsOn: "FileCabinet", hint: "" },
        { key: "ReadyValue", label: "has the value", step: "which", required: true, lookup: null, dependsOn: null, hint: "For example Ready for ERP." },
        { key: "DoneValue", label: "Afterwards set the field to", step: "after", required: true, lookup: null, dependsOn: null, hint: "" },
      ],
    },
    {
      kind: "sharepoint",
      name: "SharePoint",
      description: "Files in a document library, ready when a column has a value.",
      connectionKind: "sharepoint",
      connected: true,
      recordNoun: "file",
      defaultFileName: "{ID}.csv",
      fieldsLookup: "fields",
      settings: [
        { key: "SiteUrl", label: "Site address", step: "which", required: true, lookup: null, dependsOn: null, hint: "https://yourcompany.sharepoint.com/sites/Name" },
        { key: "Library", label: "Library", step: "which", required: true, lookup: "libraries", dependsOn: "SiteUrl", hint: "" },
        { key: "ReadyColumn", label: "Ready when the column", step: "which", required: true, lookup: "columns", dependsOn: "Library", hint: "" },
        { key: "ReadyValue", label: "has the value", step: "which", required: true, lookup: "values", dependsOn: "ReadyColumn", hint: "" },
        { key: "DoneValue", label: "Afterwards set the column to", step: "after", required: true, lookup: "values", dependsOn: "ReadyColumn", hint: "" },
      ],
    },
  ],
  destinations: [
    {
      kind: "blob",
      name: "Blob Storage",
      description: "A CSV file in a blob container, for example the one a Dynamics 365 F&O receive flow reads.",
      connectionKind: "azure-storage",
      connected: true,
      settings: [
        { key: "Container", label: "Container", step: "which", required: true, lookup: null, dependsOn: null, hint: "" },
        { key: "Prefix", label: "Folder in the container", step: "which", required: false, lookup: null, dependsOn: null, hint: "The folder the ERP takes CSV files from." },
        { key: "FilePrefix", label: "Folder for the files", step: "after", required: false, lookup: null, dependsOn: "SaveFile", hint: "Outside the folder above." },
      ],
    },
    {
      kind: "servicebus",
      name: "Service Bus",
      description: "The CSV as a message to a queue or topic, for example one a Dynamics 365 F&O receive flow reads.",
      connectionKind: "servicebus",
      connected: true,
      settings: [
        { key: "Queue", label: "Queue", step: "which", required: false, lookup: null, dependsOn: null, hint: "Give a queue or a topic." },
        { key: "Topic", label: "Topic", step: "which", required: false, lookup: null, dependsOn: null, hint: "" },
        { key: "FileConnection", label: "Storage for the files", step: "after", required: true, lookup: "connections:azure-storage", dependsOn: "SaveFile", hint: "" },
        { key: "FileContainer", label: "Container for the files", step: "after", required: true, lookup: null, dependsOn: "SaveFile", hint: "" },
        { key: "FilePrefix", label: "Folder for the files", step: "after", required: false, lookup: null, dependsOn: "SaveFile", hint: "" },
      ],
    },
  ],
});

export const vendorInvoice = () => ({
  Id: "vendor-invoice",
  Name: "Vendor invoice (DEMF)",
  Enabled: true,
  DocumentType: "Vendor invoice",
  DataAreaId: "DEMF",
  Source: {
    Kind: "sharepoint",
    Connection: "",
    Settings: { SiteUrl: "https://contoso.sharepoint.com/sites/Finance", Library: "lib-1", LibraryName: "Vendor invoices", ReadyColumn: "Status", ReadyValue: "Ready for ERP", DoneValue: "Imported" },
  },
  Produce: { Kind: "csv", Settings: { Delimiter: ";", Culture: "de-DE", RowsFrom: "", FileName: "{ID}.csv" }, Columns: [{ Name: "DOCID", Value: "{ID}" }] },
  Targets: [{ Kind: "blob", Connection: "fo-storage", Enabled: true, Settings: { Container: "erp-inbound", Prefix: "pending/", SaveFile: "true", FilePrefix: "files/" }, Fields: {} }],
  DisplayName: "Vendor invoice (DEMF)",
  TypeLabel: "Vendor invoice",
});

export const inboundConfig = () => ({
  ...config(),
  connections: [
    ...config().connections,
    { id: "fo-bus", kind: "servicebus", settings: { Namespace: "sb-contoso-fo.servicebus.windows.net" }, hasSecret: false },
  ],
  flows: [vendorInvoice()],
});

export const inboundAnswers = (): Record<string, unknown> => ({
  "GET /sources": sources(),
  "GET /config": inboundConfig(),
  "POST /sources/choices": {
    ok: true,
    message: "",
    choices: [
      { value: "lib-1", label: "Vendor invoices", detail: "" },
      { value: "Status", label: "Status", detail: "" },
      { value: "Ready for ERP", label: "Ready for ERP", detail: "" },
      { value: "Imported", label: "Imported", detail: "" },
      { value: "ID", label: "Item id", detail: "number" },
      { value: "InvoiceNumber", label: "Invoice number", detail: "text" },
    ],
  },
  "POST /sources/preview": {
    ok: true,
    message: "SharePoint file 42. 2 column(s), 1 row(s).",
    fileName: "42.csv",
    lines: ["DOCID;InvoiceId", "42;RE-2026-1187"],
    values: [
      { name: "ID", value: "42" },
      { name: "InvoiceNumber", value: "RE-2026-1187" },
    ],
  },
});

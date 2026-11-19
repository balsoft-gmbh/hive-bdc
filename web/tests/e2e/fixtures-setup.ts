// The answers of an instance during its set-up, in the shapes the Hive BDC web host gives (LocalStatusServer.Setup.cs and
// .Destinations.cs, values made up). tests/e2e/setup.spec.ts mocks them per step.
import { config, defaults } from "./fixtures";

const root = "/home/data/hivebdc/in";

const status = () => defaults()["GET /status"] as Record<string, unknown>;

export const noLicence = () => ({
  ...status(),
  license: { state: "Missing", message: "No licence file yet.", customer: "", expires: "", features: [] as string[], blocked: true },
});

export const licensed = () => status();

const types = () => config().documentTypes.map((t) => ({ ...t, companies: [] as unknown[] }));

/** A fresh install: no connection, the built-in types without companies, paused until Start. */
export const freshConfig = () => ({ ...config(), version: "v1", paused: true, setupOpen: true, connections: [] as unknown[], documentTypes: types() });

const company = (dataAreaId: string, targets: unknown[] = []) => ({
  dataAreaId,
  input: `${root}/fo-storage-bdc-export`,
  inputFormat: "Json",
  output: "",
  enabled: true,
  mail: false,
  sharePoint: { enabled: false, siteUrl: "", library: "", folder: "", fields: {} },
  targets,
  source: { kind: "blob", connection: "fo-storage", settings: { Container: "bdc-export" } },
});

/** After step 2: the F&O container saved for sales invoices of 1000 and DEMF, nothing delivers yet. */
export const sourceConfig = () => {
  const c = freshConfig();
  c.version = "v2";
  c.connections = [{ id: "fo-storage", kind: "azure-storage", settings: { AccountUrl: "https://stcontosofo.blob.core.windows.net" }, hasSecret: false }];
  c.documentTypes[0].companies = [company("1000"), company("DEMF")];
  return c;
};

const blobTarget = { kind: "blob", connection: "fo-storage", enabled: true, settings: { Container: "hivebdc-out" }, fields: {} };

/** After step 3: both companies deliver to a container, the set-up is still open. */
export const destinationConfig = () => {
  const c = sourceConfig();
  c.version = "v3";
  c.documentTypes[0].companies = [company("1000", [blobTarget]), company("DEMF", [blobTarget])];
  return c;
};

/** After Start: running, the set-up closed. */
export const startedConfig = () => ({ ...destinationConfig(), version: "v4", paused: false, setupOpen: false });

const storage = { id: "fo-storage", kind: "azure-storage", settings: { AccountUrl: "https://stcontosofo.blob.core.windows.net" }, hasSecret: false };
const bus = { id: "servicebus", kind: "servicebus", settings: { Namespace: "sb-contoso.servicebus.windows.net" }, hasSecret: false };
const key = (company: string) => ({ id: `in-salesinvoice-${company.toLowerCase()}`, kind: "https", settings: {}, hasSecret: true });

const busCompany = (dataAreaId: string, targets: unknown[] = []) => ({
  ...company(dataAreaId, targets),
  input: `${root}/sb-servicebus-bdc-export`,
  source: { kind: "servicebus", connection: "servicebus", settings: { Queue: "bdc-export", PdfConnection: "fo-storage", PdfContainer: "bdc-pdf" } },
});

const httpsCompany = (dataAreaId: string, targets: unknown[] = []) => ({
  ...company(dataAreaId, targets),
  input: `${root}/https-SalesInvoice-${dataAreaId}`,
  source: { kind: "https", connection: `in-salesinvoice-${dataAreaId.toLowerCase()}`, settings: {} },
});

/** After step 2 with Service Bus: sales invoices of 1000 and DEMF from the queue bdc-export, the PDFs from fo-storage. */
export const busConfig = () => {
  const c = freshConfig();
  c.version = "v2";
  c.connections = [bus, storage];
  c.documentTypes[0].companies = [busCompany("1000"), busCompany("DEMF")];
  return c;
};

/** After step 2 with a web address: sales invoices of 1000 and DEMF, each posted with its own key. */
export const httpsConfig = () => {
  const c = freshConfig();
  c.version = "v2";
  c.connections = [key("1000"), key("DEMF")];
  c.documentTypes[0].companies = [httpsCompany("1000"), httpsCompany("DEMF")];
  return c;
};

/** After step 3 without Blob: 1000 from Service Bus, 2000 posted to its web address. */
export const busHttpsDestinationConfig = () => {
  const c = freshConfig();
  c.version = "v3";
  c.connections = [bus, storage, key("2000")];
  c.documentTypes[0].companies = [busCompany("1000", [blobTarget]), httpsCompany("2000", [blobTarget])];
  return c;
};

/** After step 3 with every kind: 1000 from Blob, DEMF from Service Bus, 2000 posted. */
export const mixedDestinationConfig = () => {
  const c = freshConfig();
  c.version = "v3";
  c.connections = [storage, bus, key("2000")];
  c.documentTypes[0].companies = [company("1000", [blobTarget]), busCompany("DEMF", [blobTarget]), httpsCompany("2000", [blobTarget])];
  return c;
};

export const busRoleCommand =
  'az role assignment create --assignee-object-id 00000000-0000-0000-0000-000000000001 --assignee-principal-type ServicePrincipal ' +
  '--role "Azure Service Bus Data Receiver" --scope "$(az resource list --name sb-contoso --resource-type Microsoft.ServiceBus/namespaces --query [0].id -o tsv)"';

export const queueRefused = () => ({
  ok: false,
  message: "This instance has no access to sb-contoso.servicebus.windows.net yet. Your IT runs the command below once; then wait a minute and check again.",
  waiting: 0,
  command: busRoleCommand,
});

export const queueOk = () => ({ ok: true, message: "Access works. 1 message is waiting in bdc-export.", waiting: 1, command: busRoleCommand });

export const roleCommand =
  'az role assignment create --assignee-object-id 00000000-0000-0000-0000-000000000001 --assignee-principal-type ServicePrincipal ' +
  '--role "Storage Blob Data Contributor" --scope "$(az storage account show -n stcontosofo --query id -o tsv)"';

export const checkOk = () => ({
  ok: true,
  message: "Access works. 4 exports are waiting: 3 for company 1000, 1 for DEMF.",
  waiting: 4,
  byCompany: [
    { company: "1000", count: 3 },
    { company: "DEMF", count: 1 },
  ],
  byType: [
    { documentType: "SalesInvoice", company: "1000", count: 2 },
    { documentType: "SalesInvoice", company: "DEMF", count: 1 },
    { documentType: "SalesQuotation", company: "1000", count: 1 },
  ],
  command: roleCommand,
});

export const checkRefused = () => ({
  ok: false,
  message: "This instance has no access to stcontosofo.blob.core.windows.net yet. Your IT runs the command below once; then wait a minute and check again.",
  waiting: 0,
  byCompany: [],
  byType: [],
  command: roleCommand,
});

export const targets = () => ({
  targets: [
    {
      kind: "sharepoint",
      name: "SharePoint library",
      description: "Uploaded to a document library, with columns filled from the document.",
      connectionKind: "sharepoint",
      connected: false,
      fieldsLookup: "columns",
      settings: [
        { key: "SiteUrl", label: "Site address", step: "which", required: true, lookup: null, dependsOn: null, hint: "https://yourcompany.sharepoint.com/sites/Name" },
        { key: "Library", label: "Library", step: "which", required: true, lookup: "libraries", dependsOn: "SiteUrl", hint: null },
        { key: "Folder", label: "Folder (optional)", step: "which", required: false, lookup: null, dependsOn: null, hint: null },
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
        { key: "Container", label: "Container", step: "which", required: true, lookup: null, dependsOn: null, hint: null },
        { key: "Prefix", label: "Folder in the container (optional)", step: "which", required: false, lookup: null, dependsOn: null, hint: "For example outbound/" },
        {
          key: "Status",
          label: "Write a status file",
          step: "which",
          required: false,
          lookup: "yesno",
          dependsOn: null,
          hint: "{document id}.status.json next to the file says whether the delivery worked.",
        },
      ],
    },
    {
      kind: "docuware",
      name: "DocuWare file cabinet",
      description: "Stored with index values from the document. A repeat updates the same document.",
      connectionKind: "docuware",
      connected: false,
      fieldsLookup: "fields",
      settings: [{ key: "FileCabinet", label: "File cabinet", step: "which", required: true, lookup: "cabinets", dependsOn: null, hint: null }],
    },
  ],
});

export const connectionKinds = () => ({
  kinds: [
    {
      kind: "docuware",
      name: "DocuWare",
      description: "Store documents in DocuWare file cabinets, or import from them.",
      secretLabel: "Password",
      secretHint: "Stored encrypted on this machine.",
      settings: [
        { key: "Url", label: "Address of your DocuWare", required: true, hint: "https://yourcompany.docuware.cloud" },
        { key: "UserName", label: "User name", required: true, hint: "A user allowed to sign in with a DocuWare password." },
      ],
    },
    {
      kind: "azure-storage",
      name: "Azure Storage",
      description: "Documents from and to Azure Blob containers, for example of Dynamics 365 Finance and Operations.",
      secretLabel: "Connection string",
      secretHint: "From the storage account, Access keys.",
      settings: [{ key: "AccountUrl", label: "Account address", required: false, hint: "https://<account>.blob.core.windows.net" }],
    },
  ],
});

export const yesNo = () => ({
  ok: true,
  message: "",
  choices: [
    { value: "true", label: "Yes", detail: null },
    { value: "false", label: "No", detail: null },
  ],
});

export const testRun = () => ({
  ok: true,
  documentId: "SI-100042",
  message: "The sales invoice went all the way through.",
  events: [
    { at: "2026-11-19T08:14:02Z", step: "received", target: "", state: "ok", message: "Received from bdc-export" },
    { at: "2026-11-19 09:14:03", step: "produced", target: "", state: "ok", message: "Made the XRechnung" },
    { at: "2026-11-19 09:14:05", step: "delivered", target: "Azure Blob container", state: "ok", message: "Delivered to hivebdc-out" },
  ],
});

export const conflict = () => ({
  __status: 409,
  error: "Someone else saved these settings after you opened them.",
  savedBy: "admin@contoso.example",
  savedAt: "2026-11-19T08:30:00Z",
  baseKnown: true,
  theirChanges: [{ path: "Mail › From", before: "(none)", after: "invoice@contoso.example" }],
  yourChanges: [{ path: "Sales invoice › Company 1000 › Comes from", before: "(none)", after: "bdc-export" }],
  overlaps: [],
});

export const viewer = { name: "Vera Viewer", roles: ["Viewer"], canRetry: false, canChange: false, version: "1.9.0" };

export const operator = { name: "Otto Operator", roles: ["Operator"], canRetry: true, canChange: false, version: "1.9.0" };
// npm test: the Inbound pages' logic (design 2026-11-20, failure cases 1, 8, 9, 10, 12, 13, 15).
import { test } from "node:test";
import assert from "node:assert/strict";
import { postBody } from "../src/lib/logic";
import { connectionUses, draftChanges } from "../src/lib/exportlogic";
import * as I from "../src/lib/inboundlogic";

const destinations = [
  {
    kind: "blob", name: "Blob Storage", connectionKind: "azure-storage", connected: true,
    settings: [
      { key: "Container", label: "Container", step: "which", required: true },
      { key: "Prefix", label: "Folder in the container", step: "which", required: false },
      { key: "FilePrefix", label: "Folder for the files", step: "after", required: false, dependsOn: "SaveFile" },
    ],
  },
  {
    kind: "servicebus", name: "Service Bus", connectionKind: "servicebus", connected: true,
    settings: [
      { key: "Queue", label: "Queue", step: "which", required: false },
      { key: "Topic", label: "Topic", step: "which", required: false },
      { key: "FileConnection", label: "Storage for the files", step: "after", required: true, dependsOn: "SaveFile", lookup: "connections:azure-storage" },
      { key: "FileContainer", label: "Container for the files", step: "after", required: true, dependsOn: "SaveFile" },
    ],
  },
];
const sharePoint = { kind: "sharepoint", name: "SharePoint", defaultFileName: "{ID}.csv", connected: true };

// GET /config as the service answers it: flows in the settings' own PascalCase, with the computed names.
const config = (): any => ({
  version: "v9",
  connections: [{ id: "erp-storage", kind: "azure-storage", settings: { AccountUrl: "https://erp.blob.core.windows.net" }, hasSecret: false }],
  documentTypes: [],
  flows: [
    {
      Id: "vendor-invoice", Name: "Vendor invoice (DEMF)", Enabled: true, DocumentType: "Vendor invoice", DataAreaId: "DEMF",
      Source: { Kind: "sharepoint", Connection: "", Settings: { SiteUrl: "https://contoso.sharepoint.com/sites/F", Library: "lib-1", LibraryName: "Vendor invoices", ReadyColumn: "Status", ReadyValue: "Ready for ERP", DoneValue: "Imported" } },
      Produce: { Kind: "csv", Settings: { Delimiter: ";", Culture: "de-DE", RowsFrom: "", FileName: "{ID}.csv" }, Columns: [{ Name: "DOCID", Value: "{ID}" }] },
      Targets: [{ Kind: "blob", Connection: "erp-storage", Enabled: true, Settings: { Container: "erp-inbound", Prefix: "pending/", SaveFile: "true", FilePrefix: "files/" }, Fields: {} }],
      DisplayName: "Vendor invoice (DEMF)", TypeLabel: "Vendor invoice", FutureKey: { keep: true },
    },
    {
      Id: "purchase-orders", Name: "Purchase orders from DocuWare", Enabled: false, DocumentType: "", DataAreaId: "",
      Source: { Kind: "docuware", Connection: "docuware", Settings: { FileCabinet: "c-1", FileCabinetName: "Orders" } },
      Produce: { Kind: "csv", Settings: {}, Columns: [] },
      Targets: [{ Kind: "folder", Enabled: true, Settings: { Folder: "C:\\ERP\\in", SaveFile: "false" } }],
      DisplayName: "Purchase orders from DocuWare", TypeLabel: "Purchase orders from DocuWare",
    },
  ],
});

test("postBody carries the flows without the computed names and keeps keys a newer version wrote", () => {
  const body = postBody(config());
  assert.equal(body.flows.length, 2);
  assert.equal(body.flows[0].DisplayName, undefined);
  assert.equal(body.flows[0].TypeLabel, undefined);
  assert.deepEqual(body.flows[0].FutureKey, { keep: true });
  assert.equal(body.flows[1].Targets[0].Settings.Folder, "C:\\ERP\\in");
  assert.deepEqual(draftChanges(postBody(config()), body), []);
});

test("a flow written before 1.5.0 stands under its own name; the list shows companies, sources and destinations", () => {
  const body = postBody(config());
  const rows = I.inboundRows(body.flows, [sharePoint, { kind: "docuware", name: "DocuWare" }]);
  assert.deepEqual(rows, [
    { name: "Vendor invoice", companies: ["DEMF"], comesFrom: "SharePoint", goesTo: "Blob container", enabled: true },
    { name: "Purchase orders from DocuWare", companies: ["?"], comesFrom: "DocuWare", goesTo: "Folder", enabled: false },
  ]);
  assert.equal(I.sourceWords(body.flows[0], [sharePoint]), "SharePoint, library Vendor invoices");
  assert.equal(I.readyWords(body.flows[0]), "Status is Ready for ERP");
  assert.equal(I.destinationWords(body.flows[0]), "Blob container erp-inbound/pending/");
});

test("a company is added as the Windows app adds it and goes to the first destination of the run mode", () => {
  const body = postBody(config());
  const id = I.addFlow(body, "Vendor invoice", "usmf", sharePoint, destinations);
  assert.equal(id, "vendor-invoice-2");
  const flow = body.flows.find((f: any) => f.Id === id);
  assert.equal(flow.Name, "Vendor invoice (USMF)");
  assert.equal(flow.DataAreaId, "USMF");
  assert.deepEqual(flow.Produce.Settings, { Delimiter: ";", Culture: "de-DE", RowsFrom: "", FileName: "{ID}.csv" });
  assert.deepEqual(flow.Targets, [{ Kind: "blob", Connection: "", Enabled: true, Settings: { SaveFile: "false" } }]);
  assert.throws(() => I.addFlow(body, "Vendor invoice", "DEMF", sharePoint, destinations), /already receives/);
  assert.match(draftChanges(postBody(config()), body).join("|"), /Inbound Vendor invoice: company USMF added/);
});

test("renaming a type renames every flow of it; a name taken by another type is refused", () => {
  const body = postBody(config());
  I.renameInboundType(body, "Vendor invoice", "Supplier invoice");
  assert.equal(body.flows[0].DocumentType, "Supplier invoice");
  assert.equal(body.flows[0].Name, "Supplier invoice (DEMF)");
  assert.throws(() => I.renameInboundType(body, "Supplier invoice", "purchase orders from docuware"), /already a document type/);
});

test("switching the destination keeps the file switch and starts the rest afresh", () => {
  const body = postBody(config());
  I.setDestinationKind(body, "vendor-invoice", "servicebus");
  assert.deepEqual(body.flows[0].Targets, [{ Kind: "servicebus", Connection: "", Enabled: true, Settings: { SaveFile: "true" } }]);
  I.setDestination(body, "vendor-invoice", { connection: "erp-bus", settings: { Queue: "bdc-inbound", SaveFile: false } });
  assert.deepEqual(body.flows[0].Targets[0].Settings, { SaveFile: "false", Queue: "bdc-inbound" });
});

test("save waits for a destination, its connection and settings, and a file folder outside the ERP folder (case 1)", () => {
  const body = postBody(config());
  body.flows[1].Enabled = true;
  const problems = I.flowProblems(body, destinations);
  assert.ok(problems.some((p) => /Purchase orders from DocuWare › new company: give the company in the ERP\./.test(p)));
  assert.ok(problems.some((p) => /goes to folder, which this instance does not offer/.test(p)));
  assert.equal(problems.filter((p) => p.startsWith("Vendor invoice")).length, 0);

  body.flows[0].Targets[0].Settings.FilePrefix = "pending/files";
  assert.match(I.flowProblems(body, destinations).join("|"), /Vendor invoice › DEMF › Blob Storage: the folder for the files 'pending\/files\/' lies inside the folder the ERP reads 'pending\/'/);
  body.flows[0].Targets[0].Settings.Prefix = "";
  body.flows[0].Targets[0].Settings.FilePrefix = "";
  assert.match(I.flowProblems(body, destinations).join("|"), /the ERP reads the whole container/);

  I.setDestinationKind(body, "vendor-invoice", "servicebus");
  const bus = I.flowProblems(body, destinations).join("|");
  assert.match(bus, /Service Bus: choose the connection\./);
  assert.match(bus, /Service Bus: Storage for the files is missing\./);
  assert.match(bus, /Service Bus: give a queue or a topic\./);
  body.flows[0].Produce.Columns = [];
  assert.match(I.flowProblems(body, destinations).join("|"), /add at least one CSV column/);
});

test("columns from a CSV the ERP reads: BOM, separator and quotes as the Windows button (case 13)", () => {
  const fields = [{ value: "InvoiceId", label: "Invoice" }];
  for (const [text, delimiter] of [
    ["\uFEFFDOCID;\"InvoiceId\";Amount\r\n1;2;3", ";"],
    ["DOCID,InvoiceId,Amount\n", ","],
    ["DOCID\tInvoiceId\tAmount", "\t"],
  ] as const) {
    const result = I.columnsFromCsv(text, [{ Name: "docid", Value: "{ID}" }], fields);
    assert.equal(result.delimiter, delimiter);
    assert.equal(result.added, 2);
    assert.deepEqual(result.columns, [
      { Name: "docid", Value: "{ID}" },
      { Name: "InvoiceId", Value: "{InvoiceId}" },
      { Name: "Amount", Value: "" },
    ]);
  }
});

test("one column per field, and per column of the chosen table", () => {
  const fields = [
    { value: "ID", label: "Id" },
    { value: "ITEMS", label: "Items", detail: "table" },
    { value: "ITEMS.QTY", label: "Items: Qty" },
  ];
  assert.deepEqual(I.columnsFromFields([], fields, "ITEMS"), [
    { Name: "ID", Value: "{ID}" },
    { Name: "QTY", Value: "{ITEMS.QTY}" },
  ]);
});

test("a connection a flow uses is named before it is removed (case 15)", () => {
  const body = postBody(config());
  assert.deepEqual(connectionUses(body, "erp-storage"), ["Inbound Vendor invoice › DEMF: goes to it"]);
  I.setDestinationKind(body, "vendor-invoice", "servicebus");
  I.setDestination(body, "vendor-invoice", { settings: { SaveFile: true, FileConnection: "erp-storage" } });
  assert.deepEqual(connectionUses(body, "erp-storage"), ["Inbound Vendor invoice › DEMF: keeps its files there"]);
});

test("the Save bar lists flow changes in the words of the pages", () => {
  const original = postBody(config());
  const body = postBody(config());
  I.setSourceSetting(body, "vendor-invoice", "ReadyValue", "rfe", "Ready");
  I.setFlowActive(body, "vendor-invoice", false);
  I.setColumns(body, "vendor-invoice", [{ Name: "DOCID", Value: "{ID}" }, { Name: "InvoiceId", Value: "{Invoice}" }]);
  I.removeFlow(body, "purchase-orders");
  assert.deepEqual(I.flowChanges(original.flows, body.flows), [
    "Vendor invoice › DEMF: off",
    "Vendor invoice › DEMF: comes from SharePoint, library Vendor invoices, when Status is Ready",
    "Vendor invoice › DEMF: CSV columns changed",
    "Inbound Purchase orders from DocuWare: its flow removed",
  ]);
});

test("ids are made from the name as the Windows app makes them", () => {
  assert.equal(I.uniqueFlowId([], "Bestellung (Einkauf)"), "bestellung-einkauf");
  assert.equal(I.uniqueFlowId([{ Id: "bestellung" }], "Bestellung"), "bestellung-2");
  assert.equal(I.uniqueFlowId([], "  "), "inbound");
});

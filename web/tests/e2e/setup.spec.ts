import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {
  busConfig,
  busHttpsDestinationConfig,
  busRoleCommand,
  checkOk,
  checkRefused,
  conflict,
  connectionKinds,
  destinationConfig,
  freshConfig,
  httpsConfig,
  licensed,
  mixedDestinationConfig,
  noLicence,
  operator,
  queueOk,
  queueRefused,
  roleCommand,
  sourceConfig,
  startedConfig,
  targets,
  testRun,
  viewer,
  yesNo,
} from "./fixtures-setup";
import { calls, mock, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";

// The set-up assistant (design 2026-11-19-web-console-nextjs.md, decision 7 and version 2; failure cases 1, 7, 9, 11, 13,
// V1, V2, V5).

const TITLE = "Set up Hive BDC";
const LINE = "Hive BDC stays paused until you start it in step 4.";
const ENTRA = "Microsoft Entra admin center > Enterprise applications > Hive BDC sign-in > Users and groups";

const setupApi = {
  "GET /targets": targets(),
  "GET /connections/kinds": connectionKinds(),
  "POST /targets/choices": yesNo(),
  "POST /config": { ok: true },
};

const steps = [
  { name: "step 1", status: noLicence(), config: freshConfig(), heading: "Licence" },
  { name: "step 2", status: licensed(), config: freshConfig(), heading: "Comes from" },
  { name: "step 3", status: licensed(), config: sourceConfig(), heading: "Sales invoices go to" },
  { name: "step 4", status: licensed(), config: destinationConfig(), heading: "Test run" },
];

/** Opens a step: the page title is the same on every step, the step shows by its own section heading. */
async function openStep(page: Page, heading: string, path = "/setup/") {
  await page.goto(path);
  await opened(page);
  await expect(page.getByRole("heading", { level: 1, name: TITLE, exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: heading, exact: true })).toBeVisible();
}

function stepBar(page: Page) {
  return page.getByRole("navigation", { name: "Set-up steps" });
}

function currentStep(page: Page) {
  return stepBar(page).locator("[aria-current='step']");
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, setupApi);
});

test("a fresh instance without a licence opens step 1 under the content security policy", async ({ page, request }) => {
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  const violations = await watchCsp(page);
  await page.goto("/");
  await expect(page).toHaveURL(/\/setup\/$/);
  await expect(page.getByRole("heading", { level: 1, name: TITLE, exact: true })).toBeVisible();
  const line = page.locator("main .page-heading p");
  await expect(line).toHaveText(LINE);
  expect(LINE.length).toBeLessThanOrEqual(60);
  await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
  await expect(currentStep(page)).toContainText("Licence");
  await expect(page.getByRole("heading", { name: "What you need" })).toHaveCount(0);
  await expect(page.getByText(/welcome/i)).toHaveCount(0);
  await expect(page.locator("main").getByText("No licence yet")).toBeVisible();
  const roles = page.getByRole("region", { name: "Roles" });
  await expect(roles.locator("dt")).toHaveText(["Viewer", "Operator", "Administrator"]);
  await expect(roles.locator("dd")).toHaveText([
    "Sees status, documents and logs.",
    "Also retries documents that need attention.",
    "Also changes settings, connections and the licence.",
  ]);
  await expect(roles).toContainText(ENTRA);
  await expect(roles.locator("svg")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Choose file" })).toBeVisible();
  const file = page.locator("input[type=file]");
  expect(await file.evaluate((el) => [getComputedStyle(el).position, getComputedStyle(el).clip])).toEqual(["absolute", "rect(0px, 0px, 0px, 0px)"]);
  await expect(page.getByRole("button", { name: "Continue" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Finish later" })).toHaveCount(0);
  await onePrimaryAtMost(page);
  expect(violations).toEqual([]);
});

test("tenant ID, instance ID and instance address are read-only facts with icon-only copy buttons", async ({ page, request, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Licence");
  const origin = new URL(page.url()).origin;
  const facts = page.locator(".setup-facts");
  await expect(facts.locator("dt")).toHaveText(["Tenant ID", "Instance ID", "Instance address"]);
  expect(await facts.locator("dt").first().evaluate((el) => getComputedStyle(el).textTransform)).toBe("uppercase");
  await expect(facts.locator("dd").first()).toContainText("00000000-0000-0000-0000-000000000002");
  await expect(facts.locator("dd").nth(1)).toContainText("00000000-0000-0000-0000-000000000001");
  await expect(facts.locator("dd").nth(2)).toContainText(origin);
  await expect(facts.locator("input")).toHaveCount(0);
  for (const [label, value] of [
    ["Copy the tenant ID", "00000000-0000-0000-0000-000000000002"],
    ["Copy the instance ID", "00000000-0000-0000-0000-000000000001"],
    ["Copy the instance address", origin],
  ]) {
    const button = page.getByRole("button", { name: label });
    await expect(button).toHaveAttribute("title", label);
    await expect(button).toHaveText("");
    await expect(button.locator("svg")).toHaveCount(1);
    await button.click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(value);
  }
});

test("the licence request mail names the tenant ID, the instance ID and the instance address", async ({ page, request }) => {
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Licence");
  const origin = new URL(page.url()).origin;
  const link = page.getByRole("link", { name: "Request your licence" });
  await expect(link).toHaveClass(/\bbutton\b/);
  await expect(link).toHaveClass(/\bsecondary\b/);
  const href = await link.getAttribute("href");
  expect(href).toMatch(/^mailto:support@balsoft\.de\?subject=Hive%20BDC%20licence&body=/);
  const body = decodeURIComponent(href!.split("&body=")[1]);
  expect(body).toContain("Tenant ID: 00000000-0000-0000-0000-000000000002");
  expect(body).toContain("Instance ID: 00000000-0000-0000-0000-000000000001");
  expect(body).toContain(`Instance address: ${origin}`);
  expect(href).not.toContain(" ");
});

test("a stored licence moves on to step 2", async ({ page, request }) => {
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Licence");
  await mock(request, {
    "GET /status": licensed(),
    "POST /license": { ok: true, state: "Valid", message: "Licensed to Contoso GmbH until 30.09.2027.", customer: "Contoso GmbH", expires: "2027-09-30" },
  });
  await page.locator("input[type=file]").setInputFiles({ name: "contoso.lic", mimeType: "text/plain", buffer: Buffer.from("LICENCE-CONTOSO-EXAMPLE") });
  await expect(page.getByRole("heading", { level: 2, name: "Comes from", exact: true })).toBeVisible();
  await expect(currentStep(page)).toContainText("Where F&O hands over");
  await expect(stepBar(page).getByRole("link", { name: "Step 1: Licence, done" })).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/license", body: { content: "LICENCE-CONTOSO-EXAMPLE", fileName: "contoso.lic" } }]);
});

test("a valid licence shows as facts and step 1 continues with a link", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": sourceConfig() });
  await openStep(page, "Licence", "/setup/?step=1");
  const licence = page.getByRole("region", { name: "Licence" });
  await expect(licence.locator(".badge")).toHaveText("Valid");
  await expect(licence.locator(".setup-facts dt")).toHaveText(["Licensed to", "Valid until", "Tenant ID", "Instance ID", "Instance address", "Features"]);
  await expect(licence.locator(".setup-facts dd").first()).toHaveText("Contoso GmbH");
  await expect(licence.locator(".setup-facts dd").nth(1)).toHaveText("30.09.2027");
  await expect(page.getByRole("link", { name: "Continue" })).toHaveAttribute("href", "/setup/?step=2");
  await onePrimaryAtMost(page);
});

test("the source check and the save send the bodies of the former console", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-source": checkRefused() });
  await openStep(page, "Comes from");
  await expect(page.locator(".setup-code")).toHaveText("Enter the storage account address above.");
  await expect(page.getByRole("button", { name: "Copy the command" })).toHaveCount(0);
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await expect(page.locator(".setup-code code")).toHaveText(roleCommand);
  await expect(page.getByRole("button", { name: "Copy the command" })).toHaveAttribute("title", "Copy the command");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await expect(page.getByRole("button", { name: "Check access" })).toHaveClass(/\bsecondary\b/);

  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.getByText("This instance has no access to stcontosofo.blob.core.windows.net yet.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Continue" })).toBeDisabled();

  await mock(request, { "POST /setup/check-source": checkOk() });
  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.getByText("Access works. 4 exports are waiting: 3 for company 1000, 1 for DEMF.")).toBeVisible();
  await expect(page.locator(".setup-plan")).toHaveText(
    "Hive BDC sets up sales invoices for companies 1000 and DEMF. Also waiting: 1 sales quotation. They stay in the container until you add them under Outbound.",
  );
  await onePrimaryAtMost(page);

  await mock(request, { "GET /config": sourceConfig() });
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
  await expect(page).toHaveURL(/\/setup\/\?step=3$/);

  const sent = (await calls(request)).filter((c) => c.path !== "/targets/choices");
  expect(sent.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /setup/check-source", "POST /setup/check-source", "POST /config"]);
  expect(sent[1].body).toEqual({ accountUrl: "https://stcontosofo.blob.core.windows.net", container: "bdc-export", prefix: "", connectionString: null });
  const body = sent[2].body;
  expect(body.baseVersion).toBe("v1");
  expect(body.paused).toBe(true);
  expect(body.setupOpen).toBe(true);
  expect(body.connections).toEqual([{ id: "fo-storage", kind: "azure-storage", settings: { AccountUrl: "https://stcontosofo.blob.core.windows.net" } }]);
  const companies = body.documentTypes[0].companies;
  expect(companies.map((c: { dataAreaId: string }) => c.dataAreaId)).toEqual(["1000", "DEMF"]);
  for (const c of companies) {
    expect(c.source).toEqual({ kind: "blob", connection: "fo-storage", settings: { Container: "bdc-export" } });
    expect(c.input).toBe("/home/data/hivebdc/in/fo-storage-bdc-export");
    expect(c.mail).toBe(false);
  }
  expect(body.documentTypes[1].companies).toEqual([]);
});

test("a connection string goes with the check and the save, and a folder in the container too", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-source": checkOk() });
  await openStep(page, "Comes from");
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await page.getByLabel("Folder in the container (optional)").fill("/hivebdc/");
  await page.getByRole("radio", { name: "Connection string" }).check();
  await expect(page.locator(".setup-code")).toHaveCount(0);
  await page.getByRole("textbox", { name: "Connection string" }).fill("DefaultEndpointsProtocol=https;AccountName=stcontosofo;AccountKey=ZXhhbXBsZQ==");
  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.locator(".setup-plan")).toBeVisible();
  await mock(request, { "GET /config": sourceConfig() });
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
  const [check, save] = (await calls(request)).filter((c) => c.path !== "/targets/choices");
  expect(check.body).toEqual({
    accountUrl: "https://stcontosofo.blob.core.windows.net",
    container: "bdc-export",
    prefix: "hivebdc/",
    connectionString: "DefaultEndpointsProtocol=https;AccountName=stcontosofo;AccountKey=ZXhhbXBsZQ==",
  });
  expect(save.body.connections[0].secret).toBe("DefaultEndpointsProtocol=https;AccountName=stcontosofo;AccountKey=ZXhhbXBsZQ==");
  expect(save.body.documentTypes[0].companies[0].source.settings).toEqual({ Container: "bdc-export", Prefix: "hivebdc/" });
});

const SB_KEY = "Endpoint=sb://sb-contoso.servicebus.windows.net/;SharedAccessKeyName=listen-test-only";

/** Picks Service Bus in step 2 and fills the form for sales invoices of 1000 and DEMF from the queue bdc-export. */
async function fillBus(page: Page) {
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await page.getByLabel("Namespace", { exact: true }).fill("sb-contoso.servicebus.windows.net");
  await page.getByLabel("Queue", { exact: true }).fill("bdc-export");
  await page.getByLabel("Companies", { exact: true }).fill("1000, DEMF");
  await page.getByLabel("Storage account of the PDFs (optional)", { exact: true }).fill("https://stcontosofo.blob.core.windows.net");
  await page.getByLabel("Container of the PDFs", { exact: true }).fill("bdc-pdf");
}

const paths = async (request: APIRequestContext) => (await calls(request)).map((c) => `${c.method} ${c.path}`);

test("Service Bus: Save and check stores the Outbound body first, then checks the saved connection (S1, S2)", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-queue": queueRefused() });
  await openStep(page, "Comes from");
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await expect(page.getByRole("heading", { level: 2, name: "Service Bus", exact: true })).toBeVisible();
  await expect(page.getByText("The assistant checks and tests a Blob container.", { exact: false })).toHaveCount(0);
  const saveAndCheck = page.getByRole("button", { name: "Save and check" });
  await expect(saveAndCheck).toHaveClass(/\bsecondary\b/);
  await expect(saveAndCheck).toBeDisabled();
  await expect(page.getByRole("button", { name: "Continue" })).toBeDisabled();
  await expect(page.getByLabel("Document type", { exact: true })).toHaveValue("SalesInvoice");
  await onePrimaryAtMost(page);

  await fillBus(page);
  await expect(saveAndCheck).toBeEnabled();
  await mock(request, { "GET /config": busConfig() });
  await saveAndCheck.click();
  await expect(page.getByText("This instance has no access to sb-contoso.servicebus.windows.net yet.", { exact: false })).toBeVisible();
  await expect(page.locator(".setup-command code")).toHaveText(busRoleCommand);
  const copy = page.getByRole("button", { name: "Copy the command" });
  await expect(copy).toHaveAttribute("title", "Copy the command");
  await expect(copy).toHaveText("");
  await expect(page.getByRole("heading", { level: 2, name: "Service Bus", exact: true })).toBeVisible();
  await expect(currentStep(page)).toContainText("Where F&O hands over");
  await expect(page.getByRole("link", { name: "Continue" })).toHaveAttribute("href", "/setup/?step=3");
  await onePrimaryAtMost(page);

  const sent = await calls(request);
  expect(sent.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /config", "POST /setup/check-queue"]);
  const body = sent[0].body;
  expect(body.baseVersion).toBe("v1");
  expect(body.paused).toBe(true);
  expect(body.setupOpen).toBe(true);
  expect(body.connections).toEqual([
    { id: "servicebus", kind: "servicebus", settings: { Namespace: "sb-contoso.servicebus.windows.net" } },
    { id: "fo-storage", kind: "azure-storage", settings: { AccountUrl: "https://stcontosofo.blob.core.windows.net" } },
  ]);
  const companies = body.documentTypes[0].companies;
  expect(companies.map((c: { dataAreaId: string }) => c.dataAreaId)).toEqual(["1000", "DEMF"]);
  for (const c of companies) {
    expect(c.source).toEqual({ kind: "servicebus", connection: "servicebus", settings: { Queue: "bdc-export", PdfConnection: "fo-storage", PdfContainer: "bdc-pdf" } });
    expect(c.input).toBe("/home/data/hivebdc/in/sb-servicebus-bdc-export");
    expect(c.mail).toBe(false);
  }
  expect(body.documentTypes[1].companies).toEqual([]);
  expect(sent[1].body).toEqual({ connection: "servicebus", queue: "bdc-export", topic: "", subscription: "" });

  await mock(request, { "POST /setup/check-queue": queueOk() });
  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.getByText("Access works. 1 message is waiting in bdc-export.")).toBeVisible();
  await expect(page.locator(".setup-command")).toHaveCount(0);
  expect(await paths(request)).toEqual(["POST /config", "POST /setup/check-queue", "POST /setup/check-queue"]);
  expect((await calls(request))[2].body).toEqual({ connection: "servicebus", queue: "bdc-export", topic: "", subscription: "" });

  await page.getByRole("link", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
});

test("Service Bus: a topic read with a connection string goes with the save and the check", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-queue": queueOk() });
  await openStep(page, "Comes from");
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await page.getByRole("radio", { name: "Connection string" }).check();
  await expect(page.getByLabel("Namespace", { exact: true })).toHaveCount(0);
  await page.getByRole("textbox", { name: "Connection string" }).fill(SB_KEY);
  await page.getByRole("radio", { name: "A topic" }).check();
  await expect(page.getByLabel("Queue", { exact: true })).toHaveCount(0);
  await page.getByLabel("Topic", { exact: true }).fill("bdc-export");
  await page.getByLabel("Subscription", { exact: true }).fill("hivebdc");
  await page.getByLabel("Document type", { exact: true }).selectOption("SalesQuotation");
  await page.getByLabel("Companies", { exact: true }).fill("2000");
  await mock(request, { "GET /config": busConfig() });
  await page.getByRole("button", { name: "Save and check" }).click();
  await expect(page.getByText("Access works.", { exact: false })).toBeVisible();
  const [save, check] = await calls(request);
  expect(save.body.connections).toEqual([{ id: "servicebus", kind: "servicebus", settings: { Namespace: "" }, secret: SB_KEY }]);
  expect(save.body.documentTypes[0].companies).toEqual([]);
  const [entry] = save.body.documentTypes[1].companies;
  expect(entry.dataAreaId).toBe("2000");
  expect(entry.source).toEqual({ kind: "servicebus", connection: "servicebus", settings: { Topic: "bdc-export", Subscription: "hivebdc" } });
  expect(entry.input).toBe("/home/data/hivebdc/in/sb-servicebus-bdc-export-hivebdc");
  expect(check).toEqual({ method: "POST", path: "/setup/check-queue", body: { connection: "servicebus", queue: "", topic: "bdc-export", subscription: "hivebdc" } });
});

test("Service Bus: no check without a saved connection, after a refused save or before Save both (S2)", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": sourceConfig(), "POST /setup/check-queue": queueOk() });
  await openStep(page, "Comes from", "/setup/?step=2");
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await expect(page.getByRole("button", { name: "Check access" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Save and check" })).toBeDisabled();
  await expect(page.getByRole("link", { name: "Continue" })).toBeVisible();

  const refusal = "Connection 'servicebus': give the Service Bus connection string, or in Azure the namespace (<name>.servicebus.windows.net) for the managed identity.";
  await mock(request, { "POST /config": { __status: 400, error: refusal } });
  await fillBus(page);
  await expect(page.getByRole("button", { name: "Continue" })).toBeDisabled();
  await page.getByRole("button", { name: "Save and check" }).click();
  await expect(page.locator("main").getByRole("alert")).toHaveText(refusal);
  expect(await paths(request)).toEqual(["POST /config"]);

  await mock(request, { "POST /config": conflict() });
  await page.getByRole("button", { name: "Save and check" }).click();
  const panel = page.locator(".conflict");
  await expect(panel.getByRole("heading", { level: 2 })).toContainText("admin@contoso.example saved these settings");
  await onePrimaryAtMost(page);
  expect(await paths(request)).toEqual(["POST /config", "POST /config"]);

  await mock(request, { "POST /config": { ok: true }, "GET /config": busConfig() });
  await panel.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByText("Access works. 1 message is waiting in bdc-export.")).toBeVisible();
  const sent = await calls(request);
  expect(sent.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /config", "POST /config", "POST /config", "POST /setup/check-queue"]);
  expect(sent[2].body.saveBoth).toBe(true);
  expect(sent[2].body.baseVersion).toBe("v2");
  expect(sent[3].body.connection).toBe("servicebus");
});

test("a web address shows each new key once with its address, and nothing of it stays in the browser (S3)", async ({ page, request, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig() });
  await openStep(page, "Comes from");
  await page.getByRole("radio", { name: /Web address/ }).check();
  await expect(page.getByRole("heading", { level: 2, name: "Web address", exact: true })).toBeVisible();
  const save = page.getByRole("button", { name: "Save and create keys" });
  await expect(save).toHaveClass(/\bsecondary\b/);
  await expect(save).toBeDisabled();
  await page.getByLabel("Companies", { exact: true }).fill("1000, DEMF");
  const origin = new URL(page.url()).origin;
  const rows = page.getByRole("list", { name: "Addresses and keys" }).getByRole("listitem");
  await expect(rows).toHaveCount(2);
  await expect(rows.first()).toContainText(`${origin}/inbound/SalesInvoice/1000`);
  await expect(rows.first()).toContainText("Save makes its key.");
  await onePrimaryAtMost(page);

  await mock(request, { "GET /config": httpsConfig() });
  await save.click();
  await expect(page.getByText("Copy each key into its sender now. It is not shown again.")).toBeVisible();
  const sent = await calls(request);
  expect(sent.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /config"]);
  const body = sent[0].body;
  expect(body.connections.map((c: { id: string; kind: string }) => `${c.kind} ${c.id}`)).toEqual(["https in-salesinvoice-1000", "https in-salesinvoice-demf"]);
  const keys: string[] = body.connections.map((c: { secret: string }) => c.secret);
  for (const key of keys) expect(key).toMatch(/^[A-Za-z0-9]{32}$/);
  expect(keys[0]).not.toBe(keys[1]);
  const [c1000, demf] = body.documentTypes[0].companies;
  expect(c1000.source).toEqual({ kind: "https", connection: "in-salesinvoice-1000", settings: {} });
  expect(demf.input).toBe("/home/data/hivebdc/in/https-SalesInvoice-DEMF");
  expect(body.paused).toBe(true);

  await expect(rows.nth(0).locator("code")).toHaveText([`${origin}/inbound/SalesInvoice/1000`, `x-api-key: ${keys[0]}`]);
  await expect(rows.nth(1).locator("code")).toHaveText([`${origin}/inbound/SalesInvoice/DEMF`, `x-api-key: ${keys[1]}`]);
  for (const [label, value] of [
    ["Copy the key of company 1000", keys[0]],
    ["Copy the address of company DEMF", `${origin}/inbound/SalesInvoice/DEMF`],
  ]) {
    const button = page.getByRole("button", { name: label });
    await expect(button).toHaveAttribute("title", label);
    await expect(button).toHaveText("");
    await button.click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(value);
  }
  await expect(page).toHaveURL(/\/setup\/\?step=2$/);
  await expect(currentStep(page)).toContainText("Where F&O hands over");
  await expect(page.getByRole("link", { name: "Continue" })).toHaveAttribute("href", "/setup/?step=3");
  await onePrimaryAtMost(page);

  const kept = () => page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  for (const key of keys) expect(await kept()).not.toContain(key);
  await page.reload();
  await expect(page.getByRole("heading", { level: 2, name: "Web address", exact: true })).toBeVisible();
  await expect(rows).toHaveCount(2);
  await expect(rows.first()).toContainText("Key stored in Key Vault. A save replaces it.");
  await expect(page.getByText("It is not shown again.", { exact: false })).toHaveCount(0);
  const html = await page.content();
  for (const key of keys) {
    expect(html).not.toContain(key);
    expect(await kept()).not.toContain(key);
  }
  expect(await paths(request)).toEqual(["POST /config"]);
});

test("the destinations step names what keeps it from saving, then saves the chosen place", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": sourceConfig() });
  await openStep(page, "Sales invoices go to");
  await expect(page.getByText("Companies 1000 and DEMF. Choose one place or more.")).toBeVisible();
  const save = page.getByRole("button", { name: "Continue" });
  const problems = page.locator(".setup-problems li");
  await expect(problems).toHaveText(["Choose at least one place."]);
  await expect(save).toBeDisabled();

  await page.getByRole("checkbox", { name: "SharePoint library" }).check();
  await expect(problems).toHaveText([
    "SharePoint library: connect it first.",
    "SharePoint library: Site address is missing.",
    "SharePoint library: Library is missing.",
  ]);
  const sharePoint = page.getByRole("region", { name: "SharePoint library" });
  await expect(sharePoint.getByText("Not connected yet")).toBeVisible();
  await expect(sharePoint.getByText("Fill in the field above first.")).toBeVisible();
  await expect(sharePoint.getByLabel("Tenant ID")).toHaveValue("00000000-0000-0000-0000-000000000002");
  await expect(sharePoint.getByRole("button", { name: "Use this app" })).toHaveClass(/\bsecondary\b/);
  await onePrimaryAtMost(page);
  await page.getByRole("checkbox", { name: "SharePoint library" }).uncheck();

  await page.getByRole("checkbox", { name: "Azure Blob container" }).check();
  const blob = page.getByRole("region", { name: "Azure Blob container" });
  await expect(blob.getByText("Connected", { exact: true })).toBeVisible();
  await expect(problems).toHaveText(["Azure Blob container: Container is missing."]);
  await blob.getByRole("checkbox", { name: "DEMF" }).uncheck();
  await expect(problems).toHaveText(["Company DEMF has no place yet.", "Azure Blob container: Container is missing."]);
  await blob.getByRole("checkbox", { name: "DEMF" }).check();
  await blob.getByLabel("Container", { exact: true }).fill("hivebdc-out");
  await expect(problems).toHaveCount(0);
  await expect(blob.getByRole("combobox", { name: "Write a status file" })).toBeEnabled();
  await expect(save).toBeEnabled();

  await mock(request, { "GET /config": destinationConfig() });
  await save.click();
  await expect(page.getByRole("heading", { level: 2, name: "Test run" })).toBeVisible();

  const sent = await calls(request);
  expect(sent[0]).toEqual({
    method: "POST",
    path: "/targets/choices",
    body: { kind: "blob", lookup: "yesno", connection: "fo-storage", settings: {} },
  });
  const posted = sent.filter((c) => c.path === "/config");
  expect(posted).toHaveLength(1);
  expect(posted[0].body.baseVersion).toBe("v2");
  for (const c of posted[0].body.documentTypes[0].companies) {
    expect(c.targets).toEqual([{ kind: "blob", connection: "fo-storage", enabled: true, settings: { Container: "hivebdc-out" }, fields: {} }]);
    expect(c.sharePoint.enabled).toBe(false);
    expect(c.mail).toBe(false);
  }
});

test("a test run lists its steps, and Start opens the overview", async ({ page, request }) => {
  await mock(request, {
    "GET /status": licensed(),
    "GET /config": destinationConfig(),
    "POST /setup/test-run": testRun(),
    "POST /setup/start": { ok: true, paused: false },
  });
  await openStep(page, "Test run");
  await expect(page.getByRole("combobox", { name: "Document type and company" })).toHaveText(/Sales invoices · Company 1000/);
  await expect(page.getByRole("button", { name: "Run the test" })).toHaveClass(/\bsecondary\b/);
  await expect(page.getByRole("button", { name: "Start Hive BDC" })).toHaveClass(/\bprimary\b/);
  await onePrimaryAtMost(page);
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("heading", { name: "SI-100042 · Sales invoices · Company 1000" })).toBeVisible();
  await expect(page.getByText("The sales invoice went all the way through.")).toBeVisible();
  const lines = page.getByRole("list", { name: "Steps of the test run" }).getByRole("listitem");
  await expect(lines).toHaveText(["09:14:02Received from bdc-export", "09:14:03Made the XRechnung", "09:14:05Delivered to hivebdc-out"]);
  await expect(page.getByRole("button", { name: "Run the next export" })).toBeVisible();
  await expect(page.getByText("Nothing was moved.", { exact: false })).toHaveCount(0);
  await onePrimaryAtMost(page);

  await mock(request, { "GET /config": startedConfig() });
  await page.getByRole("button", { name: "Start Hive BDC" }).click();
  await expect(page).toHaveURL(/127\.0\.0\.1:\d+\/$/);
  await expect(page.getByRole("heading", { name: "Needs attention" })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Main navigation" })).toBeVisible();
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/setup/test-run", body: { documentType: "SalesInvoice", company: "1000" } },
    { method: "POST", path: "/setup/start", body: {} },
  ]);
});

test("step 4 for Service Bus and a web address names what to send where and offers Start, no test run (S4)", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": busHttpsDestinationConfig(), "POST /setup/start": { ok: true, paused: false } });
  await openStep(page, "First document");
  const origin = new URL(page.url()).origin;
  await expect(page.getByRole("heading", { level: 2, name: "Test run" })).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: "Document type and company" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Run the/ })).toHaveCount(0);
  const send = page.getByRole("region", { name: "First document" });
  await expect(send).toContainText("Start Hive BDC, then send one document. It shows in the document history.");
  await expect(send.locator("dt")).toHaveText(["Sales invoices · Company 1000", "Sales invoices · Company 2000"]);
  await expect(send.locator("dd").first()).toHaveText("A message to queue bdc-export on sb-contoso.servicebus.windows.net.");
  await expect(send.locator("dd").nth(1).locator("code")).toHaveText(`${origin}/inbound/SalesInvoice/2000`);
  await expect(send.locator("dd").nth(1)).toContainText("With the key in the x-api-key header.");
  await expect(send.getByRole("button", { name: "Copy the address for Sales invoices · Company 2000" })).toHaveText("");
  await expect(page.getByRole("button", { name: "Start Hive BDC" })).toHaveClass(/\bprimary\b/);
  await onePrimaryAtMost(page);

  await mock(request, { "GET /config": { ...busHttpsDestinationConfig(), version: "v4", paused: false, setupOpen: false } });
  await page.getByRole("button", { name: "Start Hive BDC" }).click();
  await expect(page).toHaveURL(/127\.0\.0\.1:\d+\/$/);
  await expect(page.getByRole("heading", { name: "Needs attention" })).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/setup/start", body: {} }]);
});

test("step 4 with a Blob source too runs the test for Blob only and names the others (S4)", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": mixedDestinationConfig(), "POST /setup/test-run": testRun() });
  await openStep(page, "Test run");
  await page.getByRole("combobox", { name: "Document type and company" }).click();
  await expect(page.getByRole("option")).toHaveText(["Sales invoices · Company 1000"]);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("region", { name: "First document" }).locator("dt")).toHaveText(["Sales invoices · Company DEMF", "Sales invoices · Company 2000"]);
  await onePrimaryAtMost(page);
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("list", { name: "Steps of the test run" })).toBeVisible();
  await onePrimaryAtMost(page);
  expect(await calls(request)).toEqual([{ method: "POST", path: "/setup/test-run", body: { documentType: "SalesInvoice", company: "1000" } }]);
});

for (const [who, me] of [
  ["a viewer", viewer],
  ["an operator", operator],
] as const) {
  test(`${who} sees the Service Bus and web address forms of step 2 read-only, and nothing is sent (S5)`, async ({ page, request }) => {
    await mock(request, { "GET /me": me });
    for (const [config, heading] of [
      [busConfig(), "Service Bus"],
      [httpsConfig(), "Web address"],
    ] as const) {
      await mock(request, { "GET /status": licensed(), "GET /config": config });
      await openStep(page, heading, "/setup/?step=2");
      await expect(page.getByText("An administrator is setting Hive BDC up.", { exact: false })).toBeVisible();
      await expect(page.locator("main").getByRole("button"), heading).toHaveCount(0);
      for (const control of await page.locator("main input, main select").all()) await expect(control).toBeDisabled();
      await expect(page.getByRole("link", { name: "Continue" })).toHaveAttribute("href", "/setup/?step=3");
      await expect(page.getByLabel("Companies", { exact: true })).toHaveValue("1000, DEMF");
      if (heading === "Service Bus") {
        await expect(page.getByLabel("Namespace", { exact: true })).toHaveValue("sb-contoso.servicebus.windows.net");
        await expect(page.getByLabel("Queue", { exact: true })).toHaveValue("bdc-export");
        await expect(page.getByLabel("Container of the PDFs", { exact: true })).toHaveValue("bdc-pdf");
      }
    }
    await expect(page.getByRole("list", { name: "Addresses and keys" }).getByRole("listitem").first()).toContainText("Key stored in Key Vault.");
    expect(await calls(request)).toEqual([]);
  });
}

test("a stopped test run says nothing was moved", async ({ page, request }) => {
  const stopped = { ...testRun(), ok: false, message: "SharePoint refused the column Kunde.", events: testRun().events.slice(0, 2) };
  await mock(request, { "GET /status": licensed(), "GET /config": destinationConfig(), "POST /setup/test-run": stopped });
  await openStep(page, "Test run");
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByText("SharePoint refused the column Kunde.")).toBeVisible();
  await expect(page.getByText("Nothing was moved. Fix the cause in an earlier step and run the test again.")).toBeVisible();
});

test("a test run already going says so", async ({ page, request }) => {
  await mock(request, {
    "GET /status": licensed(),
    "GET /config": destinationConfig(),
    "POST /setup/test-run": { __status: 409, ok: false, message: "A test run is already going." },
  });
  await openStep(page, "Test run");
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.locator("main").getByRole("alert")).toHaveText("A test run is already going.");
});

test("a reload in step 3 resumes at step 3; an address ahead of the progress opens the step reached", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": sourceConfig() });
  await openStep(page, "Sales invoices go to");
  await page.reload();
  await opened(page);
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
  await expect(currentStep(page)).toContainText("Where documents go");

  await openStep(page, "Sales invoices go to", "/setup/?step=3");
  await page.reload();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();

  await openStep(page, "Sales invoices go to", "/setup/?step=4");
  await expect(stepBar(page).getByRole("link", { name: /Test run/ })).toHaveCount(0);
  await expect(stepBar(page).locator(".setup-tab.later")).toContainText("Test run");

  await openStep(page, "Licence", "/setup/?step=1");
  await expect(stepBar(page).getByRole("link")).toHaveCount(2);
  await expect(stepBar(page).getByRole("link", { name: "Step 2: Where F&O hands over, done" })).toBeVisible();
  await expect(stepBar(page).locator(".setup-tab.done svg")).toHaveCount(1);
  await stepBar(page).getByRole("link", { name: /Where documents go/ }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
});

test("a viewer follows every step without a button to act, and nothing is sent", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer });
  for (const step of [...steps, { name: "step 3 set", status: licensed(), config: destinationConfig(), heading: "Sales invoices go to", path: "/setup/?step=3" }]) {
    await mock(request, { "GET /status": step.status, "GET /config": step.config });
    await openStep(page, step.heading, "path" in step ? step.path : "/setup/");
    await expect(page.getByText("An administrator is setting Hive BDC up.", { exact: false })).toBeVisible();
    await expect(page.locator("main").getByRole("button"), step.name).toHaveCount(0);
    await expect(page.locator("main input[type=file]")).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Request your licence" })).toHaveCount(0);
    await expect(page.getByText("Choose one place or more.", { exact: false })).toHaveCount(0);
    for (const input of await page.locator("main input").all()) await expect(input).toBeDisabled();
  }
  await expect(page.getByRole("region", { name: "Azure Blob container" }).getByLabel("Container", { exact: true })).toHaveValue("hivebdc-out");
  expect(await calls(request)).toEqual([]);
});

test("a save refused because someone else saved shows who did, and Save both sends both", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-source": checkOk(), "POST /config": conflict() });
  await openStep(page, "Comes from");
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await page.getByRole("button", { name: "Check access" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  const panel = page.locator(".conflict");
  await expect(panel.getByRole("heading", { level: 2 })).toHaveText("Not saved: admin@contoso.example saved these settings at 09:30, after you opened them");
  await expect(panel).toContainText("Mail › From: added");
  await expect(panel).toContainText("Sales invoice › Company 1000 › Comes from: added");
  await expect(page.getByRole("heading", { level: 2, name: "Comes from", exact: true })).toBeVisible();
  await onePrimaryAtMost(page);

  await mock(request, { "POST /config": { ok: true }, "GET /config": sourceConfig() });
  await panel.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Sales invoices go to" })).toBeVisible();
  const posted = (await calls(request)).filter((c) => c.path === "/config");
  expect(posted).toHaveLength(2);
  expect(posted[0].body.saveBoth).toBeUndefined();
  expect(posted[1].body.saveBoth).toBe(true);
  expect(posted[1].body.baseVersion).toBe("v1");
});

for (const width of [1280, 390]) {
  test(`every step stays inside the screen at ${width} px with one primary button at most`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    for (const step of steps) {
      await mock(request, { "GET /status": step.status, "GET /config": step.config });
      await openStep(page, step.heading);
      await expect(currentStep(page)).toBeVisible();
      await noSideScroll(page);
      await onePrimaryAtMost(page);
    }
    await mock(request, { "POST /setup/test-run": testRun() });
    await page.getByRole("button", { name: "Run the test" }).click();
    await expect(page.getByRole("list", { name: "Steps of the test run" })).toBeVisible();
    await noSideScroll(page);
    await onePrimaryAtMost(page);
    await mock(request, { "GET /config": sourceConfig() });
    await openStep(page, "Sales invoices go to");
    await page.getByRole("checkbox", { name: "SharePoint library" }).check();
    await page.getByRole("checkbox", { name: "DocuWare file cabinet" }).check();
    await expect(page.getByRole("region", { name: "DocuWare file cabinet" }).getByLabel("Address of your DocuWare")).toBeVisible();
    await noSideScroll(page);
    await onePrimaryAtMost(page);
    const title = await page.locator("main h1").boundingBox();
    const tabs = await stepBar(page).boundingBox();
    expect(Math.abs(title!.x - tabs!.x), "title and step bar start at the same left edge").toBeLessThanOrEqual(1);
  });
}

for (const theme of ["light", "dark"]) {
  test(`every step passes the accessibility check in the ${theme} theme`, async ({ page, request }) => {
    await page.addInitScript((t) => localStorage.setItem("hivebdc-theme", t), theme);
    const check = async (where: string) => {
      // Dark: the assistant only; the shell's top bar keeps its light-theme licence colour (bdc.css, not part of the set-up).
      const axe = new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]");
      const result = await (theme === "dark" ? axe.include("main") : axe).analyze();
      const serious = result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious.map((v) => `${where} ${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
    };
    for (const step of steps) {
      await mock(request, { "GET /status": step.status, "GET /config": step.config });
      await openStep(page, step.heading);
      await page.waitForLoadState("networkidle");
      await check(step.name);
    }
    await mock(request, { "GET /config": sourceConfig() });
    await openStep(page, "Sales invoices go to");
    await page.getByRole("checkbox", { name: "Azure Blob container" }).check();
    await page.getByRole("checkbox", { name: "DocuWare file cabinet" }).check();
    await page.waitForLoadState("networkidle");
    await check("step 3 with places");
  });
}

/** Walks the screens of design version 3 and runs the check on each: Service Bus, web address, step 4 per source kind. */
async function versionThreeStates(page: Page, request: APIRequestContext, check: (where: string) => Promise<void>) {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-queue": queueRefused() });
  await openStep(page, "Comes from");
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await check("Service Bus, empty");
  await fillBus(page);
  await mock(request, { "GET /config": busConfig() });
  await page.getByRole("button", { name: "Save and check" }).click();
  await expect(page.locator(".setup-command code")).toHaveText(busRoleCommand);
  await check("Service Bus, refused with the role command");
  await page.getByRole("radio", { name: "Connection string" }).check();
  await page.getByRole("radio", { name: "A topic" }).check();
  await check("Service Bus, topic and connection string");

  await mock(request, { "GET /config": freshConfig() });
  await openStep(page, "Comes from");
  await page.getByRole("radio", { name: /Web address/ }).check();
  await page.getByLabel("Companies", { exact: true }).fill("1000, DEMF");
  await mock(request, { "GET /config": httpsConfig() });
  await page.getByRole("button", { name: "Save and create keys" }).click();
  await expect(page.getByText("It is not shown again.", { exact: false })).toBeVisible();
  await check("web address with the new keys");

  await mock(request, { "GET /config": busHttpsDestinationConfig() });
  await openStep(page, "First document");
  await check("step 4 without a test run");
  await mock(request, { "GET /config": mixedDestinationConfig(), "POST /setup/test-run": testRun() });
  await openStep(page, "Test run");
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("list", { name: "Steps of the test run" })).toBeVisible();
  await check("step 4 with a test run and a first document");
}

for (const width of [1280, 390]) {
  test(`the Service Bus and web address screens stay inside the screen at ${width} px with one primary button at most`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    const violations = await watchCsp(page);
    await versionThreeStates(page, request, async (where) => {
      await noSideScroll(page).catch((e: Error) => {
        throw new Error(`${where}: ${e.message}`);
      });
      await onePrimaryAtMost(page);
    });
    expect(violations).toEqual([]);
  });
}

for (const theme of ["light", "dark"]) {
  test(`the Service Bus and web address screens pass the accessibility check in the ${theme} theme`, async ({ page, request }) => {
    await page.addInitScript((t) => localStorage.setItem("hivebdc-theme", t), theme);
    await versionThreeStates(page, request, async (where) => {
      await page.waitForLoadState("networkidle");
      const axe = new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]");
      const result = await (theme === "dark" ? axe.include("main") : axe).analyze();
      const serious = result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious.map((v) => `${where} ${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
    });
  });
}
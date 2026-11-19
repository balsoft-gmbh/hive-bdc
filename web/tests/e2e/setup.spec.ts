import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {
  checkOk,
  checkRefused,
  conflict,
  connectionKinds,
  destinationConfig,
  freshConfig,
  licensed,
  noLicence,
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

test("tenant ID and instance address are read-only facts with icon-only copy buttons", async ({ page, request, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Licence");
  const origin = new URL(page.url()).origin;
  const facts = page.locator(".setup-facts");
  await expect(facts.locator("dt")).toHaveText(["Tenant ID", "Instance address"]);
  expect(await facts.locator("dt").first().evaluate((el) => getComputedStyle(el).textTransform)).toBe("uppercase");
  await expect(facts.locator("dd").first()).toContainText("00000000-0000-0000-0000-000000000002");
  await expect(facts.locator("dd").nth(1)).toContainText(origin);
  await expect(facts.locator("input")).toHaveCount(0);
  for (const [label, value] of [
    ["Copy the tenant ID", "00000000-0000-0000-0000-000000000002"],
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

test("the licence request mail names the tenant ID and the instance address", async ({ page, request }) => {
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
  await expect(licence.locator(".setup-facts dt")).toHaveText(["Licensed to", "Valid until", "Tenant ID", "Instance address", "Features"]);
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

test("Service Bus and a web address point to Outbound and offer no save", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig() });
  await openStep(page, "Comes from");
  await expect(page.getByRole("group", { name: "Comes from" }).getByRole("radio")).toHaveCount(3);
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await expect(page.getByText("The assistant checks and tests a Blob container.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Continue" })).toHaveCount(0);
  await page.getByRole("radio", { name: /Azure Blob Storage/ }).check();
  await expect(page.getByRole("button", { name: "Check access" })).toBeVisible();
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
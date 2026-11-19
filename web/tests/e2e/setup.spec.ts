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
import { calls, mock, noSideScroll, opened, watchCsp } from "./helpers";

// The set-up assistant (design 2026-11-19-web-console-nextjs.md, decision 7; failure cases 1, 7, 9, 11, 13).

const setupApi = {
  "GET /targets": targets(),
  "GET /connections/kinds": connectionKinds(),
  "POST /targets/choices": yesNo(),
  "POST /config": { ok: true },
};

const steps = [
  { name: "step 1", status: noLicence(), config: freshConfig(), heading: "Welcome to Hive BDC" },
  { name: "step 2", status: licensed(), config: freshConfig(), heading: "How does F&O hand over its documents?" },
  { name: "step 3", status: licensed(), config: sourceConfig(), heading: "Where should sales invoices go?" },
  { name: "step 4", status: licensed(), config: destinationConfig(), heading: "Test run" },
];

async function openStep(page: Page, heading: string, path = "/setup/") {
  await page.goto(path);
  await opened(page);
  await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
}

function currentStep(page: Page) {
  return page.getByRole("navigation", { name: "Set-up steps" }).locator("[aria-current='step']");
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
  await expect(page.getByRole("heading", { level: 1, name: "Welcome to Hive BDC" })).toBeVisible();
  await expect(currentStep(page)).toContainText("Licence");
  await expect(page.getByRole("heading", { name: "What you need" })).toBeVisible();
  await expect(page.getByText("The licence file from Balsoft")).toBeVisible();
  await expect(page.getByText("The container or queue F&O writes to")).toBeVisible();
  await expect(page.locator("main").getByText("No licence yet")).toBeVisible();
  const roles = page.getByRole("region", { name: "Who can do what" });
  await expect(roles).toContainText("Sees status, documents and logs.");
  await expect(roles).toContainText("Also retries documents that need attention.");
  await expect(roles).toContainText("Also changes settings, connections and the licence.");
  await expect(roles).toContainText("Microsoft Entra admin center > Enterprise applications > Hive BDC sign-in > Users and groups");
  await expect(page.getByRole("button", { name: "Choose file" })).toBeVisible();
  const file = page.locator("input[type=file]");
  expect(await file.evaluate((el) => [getComputedStyle(el).position, getComputedStyle(el).clip])).toEqual(["absolute", "rect(0px, 0px, 0px, 0px)"]);
  await expect(page.getByRole("button", { name: "Next: where F&O hands over" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Finish later" })).toHaveCount(0);
  expect(violations).toEqual([]);
});

test("the licence request mail names the tenant ID and the instance address", async ({ page, request }) => {
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Welcome to Hive BDC");
  const origin = new URL(page.url()).origin;
  await expect(page.locator(".setup-values")).toContainText("00000000-0000-0000-0000-000000000002");
  await expect(page.locator(".setup-values")).toContainText(origin);
  const href = await page.getByRole("link", { name: "Request your licence" }).getAttribute("href");
  expect(href).toMatch(/^mailto:support@balsoft\.de\?subject=Hive%20BDC%20licence&body=/);
  const body = decodeURIComponent(href!.split("&body=")[1]);
  expect(body).toContain("Tenant ID: 00000000-0000-0000-0000-000000000002");
  expect(body).toContain(`Instance address: ${origin}`);
  expect(href).not.toContain(" ");
});

test("a stored licence moves on to step 2", async ({ page, request }) => {
  await mock(request, { "GET /status": noLicence(), "GET /config": freshConfig() });
  await openStep(page, "Welcome to Hive BDC");
  await mock(request, {
    "GET /status": licensed(),
    "POST /license": { ok: true, state: "Valid", message: "Licensed to Contoso GmbH until 30.09.2027.", customer: "Contoso GmbH", expires: "2027-09-30" },
  });
  await page.locator("input[type=file]").setInputFiles({ name: "contoso.lic", mimeType: "text/plain", buffer: Buffer.from("LICENCE-CONTOSO-EXAMPLE") });
  await expect(page.getByRole("heading", { level: 1, name: "How does F&O hand over its documents?" })).toBeVisible();
  await expect(currentStep(page)).toContainText("Where F&O hands over");
  await expect(page.getByRole("link", { name: /STEP 1 · DONE/ })).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/license", body: { content: "LICENCE-CONTOSO-EXAMPLE", fileName: "contoso.lic" } }]);
});

test("the source check and the save send the bodies of the former console", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-source": checkRefused() });
  await openStep(page, "How does F&O hand over its documents?");
  await expect(page.locator(".setup-code code")).toHaveText("Enter the storage account address above.");
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await expect(page.locator(".setup-code code")).toHaveText(roleCommand);
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");

  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.getByText("This instance has no access to stcontosofo.blob.core.windows.net yet.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save and continue" })).toBeDisabled();

  await mock(request, { "POST /setup/check-source": checkOk() });
  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.getByText("Access works. 4 exports are waiting: 3 for company 1000, 1 for DEMF.")).toBeVisible();
  await expect(page.locator(".setup-plan")).toHaveText(
    "Hive BDC sets up sales invoices for companies 1000 and DEMF. Also waiting: 1 sales quotation. They stay in the container until you add them under Outbound.",
  );

  await mock(request, { "GET /config": sourceConfig() });
  await page.getByRole("button", { name: "Save and continue" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();
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
  await openStep(page, "How does F&O hand over its documents?");
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await page.getByLabel("Folder in the container (optional)").fill("/hivebdc/");
  await page.getByRole("radio", { name: /Use a connection string/ }).check();
  await expect(page.locator(".setup-code")).toHaveCount(0);
  await page.getByRole("textbox", { name: "Connection string" }).fill("DefaultEndpointsProtocol=https;AccountName=stcontosofo;AccountKey=ZXhhbXBsZQ==");
  await page.getByRole("button", { name: "Check access" }).click();
  await expect(page.locator(".setup-plan")).toBeVisible();
  await mock(request, { "GET /config": sourceConfig() });
  await page.getByRole("button", { name: "Save and continue" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();
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
  await openStep(page, "How does F&O hand over its documents?");
  await page.getByRole("radio", { name: /Azure Service Bus/ }).check();
  await expect(page.getByText("The assistant checks and tests a Blob container.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save and continue" })).toHaveCount(0);
  await page.getByRole("radio", { name: /Azure Blob Storage/ }).check();
  await expect(page.getByRole("button", { name: "Check access" })).toBeVisible();
});

test("the destinations step names what keeps it from saving, then saves the chosen place", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": sourceConfig() });
  await openStep(page, "Where should sales invoices go?");
  const save = page.getByRole("button", { name: "Save and continue" });
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
  await expect(page.getByRole("heading", { level: 1, name: "Test run" })).toBeVisible();

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
  await expect(page.getByRole("combobox", { name: "Test with the first waiting export of" })).toHaveText(/sales invoices · company 1000/);
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "One document went all the way through" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "SI-100042 · sales invoices · company 1000" })).toBeVisible();
  const lines = page.getByRole("list", { name: "Steps of the test run" }).getByRole("listitem");
  await expect(lines).toHaveText(["09:14:02Received from bdc-export", "09:14:03Made the XRechnung", "09:14:05Delivered to hivebdc-out"]);
  await expect(page.getByRole("button", { name: "Run the next export" })).toBeVisible();

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
  await openStep(page, "Where should sales invoices go?");
  await page.reload();
  await opened(page);
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();
  await expect(currentStep(page)).toContainText("Where documents go");

  await openStep(page, "Where should sales invoices go?", "/setup/?step=3");
  await page.reload();
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();

  await openStep(page, "Where should sales invoices go?", "/setup/?step=4");
  await openStep(page, "Welcome to Hive BDC", "/setup/?step=1");
  await expect(page.getByRole("link", { name: /STEP 2 · DONE/ })).toBeVisible();
  await page.getByRole("navigation", { name: "Set-up steps" }).getByRole("link", { name: /Where documents go/ }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();
});

test("a viewer follows every step without a button to act, and nothing is sent", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer });
  for (const step of [...steps, { name: "step 3 set", status: licensed(), config: destinationConfig(), heading: "Where should sales invoices go?", path: "/setup/?step=3" }]) {
    await mock(request, { "GET /status": step.status, "GET /config": step.config });
    await openStep(page, step.heading, "path" in step ? step.path : "/setup/");
    await expect(page.getByText("An administrator is setting Hive BDC up.", { exact: false })).toBeVisible();
    await expect(page.locator("main").getByRole("button"), step.name).toHaveCount(0);
    await expect(page.locator("main input[type=file]")).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Request your licence" })).toHaveCount(0);
    for (const input of await page.locator("main input").all()) await expect(input).toBeDisabled();
  }
  await expect(page.getByRole("region", { name: "Azure Blob container" }).getByLabel("Container", { exact: true })).toHaveValue("hivebdc-out");
  expect(await calls(request)).toEqual([]);
});

test("a save refused because someone else saved shows who did, and Save both sends both", async ({ page, request }) => {
  await mock(request, { "GET /status": licensed(), "GET /config": freshConfig(), "POST /setup/check-source": checkOk(), "POST /config": conflict() });
  await openStep(page, "How does F&O hand over its documents?");
  await page.getByLabel("Storage account address").fill("https://stcontosofo.blob.core.windows.net");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await page.getByRole("button", { name: "Check access" }).click();
  await page.getByRole("button", { name: "Save and continue" }).click();
  const panel = page.locator(".conflict");
  await expect(panel.getByRole("heading", { level: 2 })).toHaveText("Not saved: admin@contoso.example saved these settings at 09:30, after you opened them");
  await expect(panel).toContainText("Mail › From: added");
  await expect(panel).toContainText("Sales invoice › Company 1000 › Comes from: added");
  await expect(page.getByRole("heading", { level: 1, name: "How does F&O hand over its documents?" })).toBeVisible();

  await mock(request, { "POST /config": { ok: true }, "GET /config": sourceConfig() });
  await panel.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Where should sales invoices go?" })).toBeVisible();
  const posted = (await calls(request)).filter((c) => c.path === "/config");
  expect(posted).toHaveLength(2);
  expect(posted[0].body.saveBoth).toBeUndefined();
  expect(posted[1].body.saveBoth).toBe(true);
  expect(posted[1].body.baseVersion).toBe("v1");
});

for (const width of [1280, 390]) {
  test(`every step stays inside the screen at ${width} px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    for (const step of steps) {
      await mock(request, { "GET /status": step.status, "GET /config": step.config });
      await openStep(page, step.heading);
      await noSideScroll(page);
    }
    await mock(request, { "POST /setup/test-run": testRun() });
    await page.getByRole("button", { name: "Run the test" }).click();
    await expect(page.getByRole("list", { name: "Steps of the test run" })).toBeVisible();
    await noSideScroll(page);
    await mock(request, { "GET /config": sourceConfig() });
    await openStep(page, "Where should sales invoices go?");
    await page.getByRole("checkbox", { name: "SharePoint library" }).check();
    await page.getByRole("checkbox", { name: "DocuWare file cabinet" }).check();
    await expect(page.getByRole("region", { name: "DocuWare file cabinet" }).getByLabel("Address of your DocuWare")).toBeVisible();
    await noSideScroll(page);
  });
}

test("every step passes the accessibility check", async ({ page, request }) => {
  const check = async (where: string) => {
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]").analyze();
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
  await openStep(page, "Where should sales invoices go?");
  await page.getByRole("checkbox", { name: "Azure Blob container" }).check();
  await page.getByRole("checkbox", { name: "DocuWare file cabinet" }).check();
  await page.waitForLoadState("networkidle");
  await check("step 3 with places");
});
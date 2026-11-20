import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { conflict, viewer } from "./fixtures-outbound";
import { inboundAnswers, inboundConfig } from "./fixtures-inbound";
import { calls, mock, noRowButtons, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";

// The Inbound pages (design 2026-11-20-azure-inbound.md, boards I1 to I6; failure cases 1, 9, 11, 12, 13 and the
// phone width).

const type = (query: string) => `/inbound-flows/type/?${query}`;
const invoice = "type=Vendor%20invoice";
const pages = [
  "/inbound-flows/",
  type(invoice),
  type(`${invoice}&company=DEMF&tab=from`),
  type(`${invoice}&company=DEMF&tab=to`),
  type(`${invoice}&company=DEMF&tab=columns`),
];

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, inboundAnswers());
});

const saveBar = (page: Page) => page.getByRole("region", { name: "Changes not saved" });
const dialog = (page: Page) => page.locator("dialog[open]");
const grid = (page: Page) => page.getByRole("region", { name: "Inbound document types" });
const configPosts = async (request: Parameters<typeof calls>[0]) => (await calls(request)).filter((c) => c.path === "/config");

test("every Inbound page and dialog loads under the host's content security policy without a violation", async ({ page }) => {
  const violations = await watchCsp(page);
  for (const path of pages) {
    await page.goto(path);
    await opened(page);
  }
  await page.goto("/inbound-flows/");
  await opened(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await expect(dialog(page)).toBeVisible();
  expect(violations).toEqual([]);
});

test("the menu leads to Inbound, and the list shows each type with its companies, sources and destinations", async ({ page }) => {
  await page.goto("/");
  await opened(page);
  await page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: /Inbound/ }).click();
  await expect(page).toHaveURL(/\/inbound-flows\/$/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Inbound");
  await expect(page.getByText("Documents waiting in a document system, brought into your ERP.")).toBeVisible();
  const row = grid(page).getByRole("row", { name: /Vendor invoice/ });
  await expect(row).toContainText("DEMF");
  await expect(row).toContainText("SharePoint");
  await expect(row).toContainText("Blob container");
  await expect(row.locator(".badge")).toHaveText("Active");
  await noRowButtons(page);
  await onePrimaryAtMost(page);
  await grid(page).getByRole("link", { name: "Vendor invoice" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Vendor invoice");
  await expect(page.getByRole("row", { name: /Company DEMF/ })).toContainText("SharePoint, library Vendor invoices");
  await expect(page.getByRole("row", { name: /Company DEMF/ })).toContainText("Status is Ready for ERP");
  await expect(page.getByRole("row", { name: /Company DEMF/ })).toContainText("Blob container erp-inbound/pending/");
});

test("a new type starts with its first company from the chosen document system and saves into Blob Storage", async ({ page, request }) => {
  await page.goto("/inbound-flows/");
  await opened(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await dialog(page).getByLabel("Name").fill("Purchase order");
  await dialog(page).getByLabel("Company in the ERP").fill("usmf");
  await dialog(page).getByRole("button", { name: "Add" }).click();
  await expect(dialog(page)).toContainText("Choose where the documents come from.");
  await dialog(page).getByText("SharePoint", { exact: true }).click();
  await dialog(page).getByRole("button", { name: "Add" }).click();
  await expect(page).toHaveURL(/type=Purchase%20order&company=USMF&tab=from/);
  await expect(page.locator(".company-bar")).toContainText("Company USMF");
  await expect(page.locator(".company-bar")).toContainText("INBOUND");

  // Save waits until the company says where the ERP reads its CSV, and has a column.
  await expect(saveBar(page)).toContainText("Inbound Purchase order: company USMF added");
  await expect(saveBar(page).getByRole("button", { name: "Save changes" })).toBeDisabled();
  await expect(saveBar(page)).toContainText("Purchase order › USMF › Blob Storage: Container is missing.");

  await page.getByRole("link", { name: "Goes to" }).click();
  await expect(page.getByRole("group", { name: "Goes to" }).getByRole("button", { name: "Blob Storage" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("group", { name: "Goes to" }).getByRole("button", { name: "Folder the ERP reads" })).toHaveCount(0);
  await page.getByLabel("Container", { exact: true }).fill("erp-inbound");
  await page.getByLabel("Container", { exact: true }).press("Enter");
  await page.getByLabel("Folder in the container").fill("pending");
  await page.getByLabel("Folder in the container").press("Enter");

  await page.getByRole("link", { name: "CSV columns" }).click();
  await page.getByRole("button", { name: "Add column" }).click();
  await page.getByLabel("Column 1", { exact: true }).fill("DOCID");
  await page.getByLabel("Column 1", { exact: true }).press("Enter");
  await page.getByLabel("Value of DOCID", { exact: true }).fill("{ID}");
  await page.getByLabel("Value of DOCID", { exact: true }).press("Enter");

  await expect(saveBar(page).getByRole("button", { name: "Save changes" })).toBeEnabled();
  await onePrimaryAtMost(page);
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [post] = await configPosts(request);
  const added = post.body.flows.find((f: any) => f.Id === "purchase-order");
  expect(added).toEqual({
    Id: "purchase-order",
    Name: "Purchase order (USMF)",
    Enabled: true,
    DocumentType: "Purchase order",
    DataAreaId: "USMF",
    Source: { Kind: "sharepoint", Connection: "", Settings: {} },
    Produce: { Kind: "csv", Settings: { Delimiter: ";", Culture: "de-DE", RowsFrom: "", FileName: "{ID}.csv" }, Columns: [{ Name: "DOCID", Value: "{ID}" }] },
    Targets: [{ Kind: "blob", Connection: "fo-storage", Enabled: true, Settings: { SaveFile: "false", Container: "erp-inbound", Prefix: "pending" } }],
  });
  expect(post.body.flows.find((f: any) => f.Id === "vendor-invoice").DisplayName).toBeUndefined();
});

test("Comes from fills its dropdowns from the document system and keeps the chosen label", async ({ page, request }) => {
  await page.goto(type(`${invoice}&company=DEMF&tab=from`));
  await opened(page);
  await expect(page.getByLabel("Site address")).toHaveValue("https://contoso.sharepoint.com/sites/Finance");
  await expect(page.getByLabel("Library")).toHaveValue("lib-1");
  await expect(page.getByLabel("Ready when the column")).toHaveValue("Status");
  await page.getByLabel("Afterwards set the column to").selectOption("Ready for ERP");
  await expect(saveBar(page)).toContainText("Vendor invoice › DEMF: comes from SharePoint, library Vendor invoices, when Status is Ready for ERP");
  const choices = (await calls(request)).filter((c) => c.path === "/sources/choices");
  expect(choices.some((c) => c.body.lookup === "libraries" && c.body.settings.SiteUrl === "https://contoso.sharepoint.com/sites/Finance")).toBe(true);
  expect(choices.every((c) => c.body.kind === "sharepoint")).toBe(true);
});

test("a dropdown the document system leaves empty becomes a text box, so a value no document carries yet can be typed", async ({ page, request }) => {
  await mock(request, { "POST /sources/choices": { ok: true, message: "", choices: [] } });
  await page.goto(type(`${invoice}&company=DEMF&tab=from`));
  await opened(page);
  const value = page.getByLabel("has the value");
  await expect(value).toHaveJSProperty("tagName", "INPUT");
  await value.fill("Ready for SAP");
  await value.press("Enter");
  await expect(saveBar(page)).toContainText("when Status is Ready for SAP");
  await mock(request, { "POST /sources/choices": inboundAnswers()["POST /sources/choices"] });
});

test("a dropdown that waits for another setting names it", async ({ page }) => {
  const settings = inboundConfig();
  settings.flows[0].Source.Settings = { SiteUrl: "https://contoso.sharepoint.com/sites/Finance" } as any;
  await mock(page.request, { "GET /config": settings });
  await page.goto(type(`${invoice}&company=DEMF&tab=from`));
  await opened(page);
  await expect(page.getByText('Set "Library" first.')).toBeVisible();
  await expect(page.getByText('Set "Ready when the column" first.').first()).toBeVisible();
});

test("Goes to Service Bus with the file in Blob Storage; a file folder inside the ERP folder blocks the save (case 1)", async ({ page }) => {
  await page.goto(type(`${invoice}&company=DEMF&tab=to`));
  await opened(page);
  await page.getByLabel("Folder for the files").fill("pending/files");
  await page.getByLabel("Folder for the files").press("Enter");
  await expect(page.getByRole("alert").filter({ hasText: "lies inside the folder the ERP reads" })).toBeVisible();
  await expect(saveBar(page).getByRole("button", { name: "Save changes" })).toBeDisabled();

  await page.getByRole("group", { name: "Goes to" }).getByRole("button", { name: "Service Bus" }).click();
  await expect(page.getByLabel("Connection")).toHaveValue("fo-bus");
  await page.getByLabel("Queue").fill("bdc-inbound");
  await page.getByLabel("Queue").press("Enter");
  await expect(page.getByLabel("Storage for the files")).toHaveValue("fo-storage");
  await page.getByLabel("Container for the files").fill("erp-files");
  await page.getByLabel("Container for the files").press("Enter");
  await expect(saveBar(page)).toContainText("Vendor invoice › DEMF: goes to Service Bus queue bdc-inbound");
  await expect(saveBar(page).getByRole("button", { name: "Save changes" })).toBeEnabled();
  await page.getByRole("checkbox", { name: "Save the document file as well" }).uncheck();
  await expect(page.getByLabel("Container for the files")).toHaveCount(0);
});

test("CSV columns: one per field, from a CSV the ERP reads, and the preview of a ready document (case 13)", async ({ page, request }) => {
  await page.goto(type(`${invoice}&company=DEMF&tab=columns`));
  await opened(page);
  await page.getByRole("button", { name: "One column per field" }).click();
  await expect(page.getByLabel("Column 3", { exact: true })).toHaveValue("Status");
  await page.getByLabel("A CSV file the ERP already reads").setInputFiles({
    name: "erp-import.csv",
    mimeType: "text/csv",
    buffer: Buffer.from("\uFEFFDOCID,\"InvoiceId\",Amount\r\n1,2,3\r\n"),
  });
  await expect(page.getByText("2 column(s) taken from erp-import.csv. Say what fills each one.")).toBeVisible();
  await page.getByRole("button", { name: "Preview with a ready document" }).click();
  await expect(page.getByLabel("The CSV this document makes")).toHaveText("DOCID;InvoiceId\n42;RE-2026-1187");
  const preview = (await calls(request)).find((c) => c.path === "/sources/preview")!;
  expect(preview.body.produce.Settings.Delimiter).toBe(",");
  expect(preview.body.target.Kind).toBe("blob");
  expect(preview.body.dataAreaId).toBe("DEMF");
});

test("an Azure instance with a folder flow says to choose Blob Storage or Service Bus (case 9)", async ({ page, request }) => {
  const settings = inboundConfig();
  settings.flows[0].Targets = [{ Kind: "folder", Connection: "", Enabled: true, Settings: { Folder: "C:\\ERP\\in", SaveFile: "false" } as any, Fields: {} }];
  await mock(request, { "GET /config": settings });
  await page.goto(type(`${invoice}&company=DEMF&tab=to`));
  await opened(page);
  await expect(page.getByText("This company writes to the folder C:\\ERP\\in, which only the Windows install has. Choose Blob Storage or Service Bus.")).toBeVisible();
});

test("a save overtaken by someone else shows the conflict panel, and Save both sends the flows again (case 12)", async ({ page, request }) => {
  await mock(request, { "POST /config": conflict });
  await page.goto(type(`${invoice}&company=DEMF&tab=from`));
  await opened(page);
  await page.getByRole("checkbox", { name: "Company DEMF active" }).uncheck();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  const panel = page.getByRole("alert").filter({ hasText: "Not saved: Bernd Berger saved these settings" });
  await expect(panel).toBeVisible();
  await mock(request, { "POST /config": { ok: true } });
  await panel.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [first, second] = await configPosts(request);
  expect(first.body.flows[0].Enabled).toBe(false);
  expect(second.body).toEqual({ ...first.body, saveBoth: true });
});

test("a Viewer sees every Inbound setting and no control that changes one (case 11)", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer });
  await page.goto("/inbound-flows/");
  await opened(page);
  await expect(page.getByRole("button", { name: "New document type" })).toHaveCount(0);
  await page.goto(type(invoice));
  await opened(page);
  await expect(page.getByRole("button", { name: /^Remove|^Add company/ })).toHaveCount(0);
  await expect(page.getByLabel("Name")).toBeDisabled();
  await page.goto(type(`${invoice}&company=DEMF&tab=columns`));
  await opened(page);
  await expect(page.getByRole("button", { name: "One column per field" })).toHaveCount(0);
  await expect(page.getByLabel("Column 1", { exact: true })).toBeDisabled();
  await expect(saveBar(page)).toHaveCount(0);
});

test("the Inbound pages have no accessibility violations and no sideways scroll on a phone", async ({ page }) => {
  for (const path of pages) {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(path);
    await opened(page);
    const result = await new AxeBuilder({ page }).include("main").analyze();
    expect(result.violations.map((v) => `${path}: ${v.id} ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
    await page.setViewportSize({ width: 390, height: 844 });
    await noSideScroll(page);
  }
});

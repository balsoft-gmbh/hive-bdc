import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { config } from "./fixtures";
import { conflict, fullConfig, outboundAnswers, viewer } from "./fixtures-outbound";
import { calls, mock, noRowButtons, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";
import { postBody } from "../../src/lib/logic";
import * as X from "../../src/lib/exportlogic";

// The Outbound pages (port of export.js, exportlogic.js and draft.js; design 2026-11-19-web-console-nextjs.md, failure
// cases 1, 7, 8, 11 and 13, the save with the version it was read at, board W6, and version 2: the ERP list, the type
// as a record with the breadcrumb, one primary button per screen, V1 to V5).

const root = "/home/data/hivebdc/in";
const type = (query: string) => `/outbound/type/?${query}`;
const pages = [
  "/outbound/",
  type("id=SalesInvoice"),
  type("id=SalesInvoice&company=1000&tab=from"),
  type("id=SalesInvoice&company=2000&tab=from"),
  type("id=SalesInvoice&company=3000&tab=from"),
  type("id=SalesInvoice&company=1000&tab=to"),
  type("id=SalesInvoice&company=1000&tab=to&dest=sharepoint"),
  type("id=SalesInvoice&company=2000&tab=to&dest=docuware"),
];

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, outboundAnswers());
});

const saveBar = (page: Page) => page.getByRole("region", { name: "Changes not saved" });
const dialog = (page: Page) => page.locator("dialog[open]");
const grid = (page: Page) => page.getByRole("region", { name: "Outbound document types" });
const typeActive = (page: Page) => page.getByRole("checkbox", { name: "Active", exact: true });
const configPosts = async (request: Parameters<typeof calls>[0]) => (await calls(request)).filter((c) => c.path === "/config");

test("every Outbound page and dialog loads under the host's content security policy without a violation", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  const violations = await watchCsp(page);
  for (const path of pages) {
    await page.goto(path);
    await opened(page);
  }
  await page.goto("/outbound/");
  await opened(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await expect(dialog(page)).toBeVisible();
  expect(violations).toEqual([]);
});

test("the list shows each type with its companies, where they go and its state; the name opens the type", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/outbound/");
  await opened(page);
  const invoice = grid(page).getByRole("row", { name: /Sales invoice/ });
  await expect(invoice).toContainText("1000, 2000, 3000");
  await expect(invoice).toContainText("SharePoint, Blob container, DocuWare, mail");
  await expect(invoice.locator(".badge")).toHaveText("Active");
  await expect(grid(page).getByRole("row", { name: /Quotation/ })).toContainText("Nowhere yet");
  await expect(page.locator("main")).not.toContainText("OUTBOUND");
  await expect(page.getByText("2 document types")).toBeVisible();
  await noRowButtons(page);
  await onePrimaryAtMost(page);

  // Off types without a company stay out unless asked for, as the ERP's "Include archived".
  await expect(grid(page).getByRole("row", { name: /Collection letter/ })).toHaveCount(0);
  await page.getByRole("checkbox", { name: "Include unused" }).check();
  const letter = grid(page).getByRole("row", { name: /Collection letter/ });
  await expect(letter.locator(".badge")).toHaveText("Off");
  await noRowButtons(page);
  await page.getByLabel("Filter state").selectOption("off");
  await expect(grid(page).getByRole("row")).toHaveCount(2);
  await page.getByLabel("Filter state").selectOption("");

  await grid(page).getByRole("link", { name: "Sales invoice" }).click();
  await expect(page).toHaveURL(/\/outbound\/type\/\?id=SalesInvoice$/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Sales invoice");
  await expect(page.locator(".page-heading p")).toHaveText("SalesInvoice · E-invoice · Sent to customers");
});

test("the breadcrumb shows Outbound, the type and the company; every level but the last is a link", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  const crumbs = async () => {
    const nav = page.getByRole("navigation", { name: "Breadcrumb" });
    await expect(nav.locator('[aria-current="page"]')).toHaveCount(1);
    return {
      links: await nav.getByRole("link").evaluateAll((nodes) => nodes.map((n) => [n.textContent?.trim(), (n as HTMLAnchorElement).getAttribute("href")])),
      current: (await nav.locator('[aria-current="page"]').textContent())?.trim(),
    };
  };
  await page.goto(type("id=SalesInvoice"));
  await opened(page);
  expect(await crumbs()).toEqual({
    links: [
      ["Hive BDC", "/"],
      ["Outbound", "/outbound/"],
    ],
    current: "Sales invoice",
  });

  await page.goto(type("id=SalesInvoice&company=1000&tab=from"));
  await opened(page);
  expect(await crumbs()).toEqual({
    links: [
      ["Hive BDC", "/"],
      ["Outbound", "/outbound/"],
      ["Sales invoice", "/outbound/type/?id=SalesInvoice"],
    ],
    current: "Company 1000",
  });

  await page.goto(type("id=SalesInvoice&company=1000&tab=to&dest=sharepoint"));
  await opened(page);
  expect(await crumbs()).toEqual({
    links: [
      ["Hive BDC", "/"],
      ["Outbound", "/outbound/"],
      ["Sales invoice", "/outbound/type/?id=SalesInvoice"],
      ["Company 1000", "/outbound/type/?id=SalesInvoice&company=1000&tab=to"],
    ],
    current: "SharePoint library",
  });
  // No path line or badge inside the page any more (design version 2, change 4).
  await expect(page.locator("main .page-trail, main .badge-outbound")).toHaveCount(0);
  await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Company 1000" }).click();
  await expect(page).toHaveURL(/company=1000&tab=to$/);
  await expect(page.getByRole("heading", { name: "Goes to" })).toBeVisible();
});

test("switching a type off is saved with the version it was read at, then the bar says Saved", async ({ page, request }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await typeActive(page).uncheck();
  await expect(saveBar(page)).toContainText("1 change not saved");
  await expect(saveBar(page)).toContainText("Quotation: off");
  await expect(page.locator(".page-heading p")).toContainText("Off, its exports wait in their source");
  await onePrimaryAtMost(page);
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/config", body: X.setType(postBody(config()), "SalesQuotation", { enabled: false }) },
  ]);
});

test("a new document type is added in the record dialog and opens its page", async ({ page, request }) => {
  await page.goto("/outbound/");
  await opened(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await expect(dialog(page).getByRole("heading", { name: "New document type" })).toBeVisible();
  await onePrimaryAtMost(page);
  await dialog(page).getByLabel("ID the ERP writes as documentType").fill("Delivery Note");
  await dialog(page).getByRole("button", { name: "Add", exact: true }).click();
  await expect(dialog(page).getByRole("alert")).toBeVisible();
  await dialog(page).getByLabel("ID the ERP writes as documentType").fill("DeliveryNote");
  await dialog(page).getByLabel("Name", { exact: true }).fill("Lieferschein");
  await dialog(page).getByLabel("Sent to").selectOption("Vendor");
  await dialog(page).getByLabel("ID the ERP writes as documentType").press("Enter");
  await expect(page).toHaveURL(/\/outbound\/type\/\?id=DeliveryNote$/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Lieferschein");
  await expect(saveBar(page)).toContainText("Lieferschein added");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(X.addType(postBody(config()), "DeliveryNote", "Lieferschein", "Pdf", "Vendor"));
});

test("a new company, its Blob source and a Blob destination go out in one save", async ({ page, request }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await expect(page.getByText("No company yet.")).toBeVisible();
  await page.getByRole("button", { name: "Add company" }).click();
  await onePrimaryAtMost(page);
  await dialog(page).getByLabel("Company (dataAreaId)").fill("2000");
  await dialog(page).getByRole("button", { name: "Add", exact: true }).click();
  await expect(page).toHaveURL(/id=SalesQuotation&company=2000&tab=from$/);

  await expect(page.getByRole("heading", { name: "Company 2000" })).toBeVisible();
  await expect(page.getByRole("radio", { name: /Azure Blob Storage/ })).toBeChecked();
  await expect(page.getByLabel("Connection", { exact: true })).toHaveValue("fo-storage");
  await page.getByLabel("Container", { exact: true }).fill("bdc-export");
  await page.getByLabel("Container", { exact: true }).press("Enter");
  await expect(saveBar(page)).toContainText("Quotation › 2000: choose where its documents go.");

  await page.getByRole("link", { name: "Goes to" }).click();
  await page.getByRole("button", { name: "Add a destination" }).click();
  await onePrimaryAtMost(page);
  await dialog(page).getByRole("button", { name: /Azure Blob container/ }).click();
  await expect(page).toHaveURL(/tab=to&dest=blob$/);
  await expect(saveBar(page)).toContainText("Azure Blob container: Container is missing.");
  await page.getByLabel("Container", { exact: true }).fill("outbound");
  await page.getByLabel("Container", { exact: true }).blur();
  await expect(saveBar(page).locator(".save-bar-problems")).toHaveCount(0);
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();

  let expected = X.addCompany(postBody(config()), "SalesQuotation", "2000");
  expected = X.setSource(expected, "SalesQuotation", "2000", { kind: "blob", connection: "fo-storage", settings: { Container: "bdc-export" } }, root);
  expected = X.addDestination(expected, "SalesQuotation", "2000", "blob", "fo-storage");
  expected = X.applyDestination(expected, "SalesQuotation", "2000", {
    kind: "blob",
    connection: "fo-storage",
    enabled: true,
    settings: { Container: "outbound" },
    fields: {},
  });
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(expected);
  // Two companies reading the same container share one staging folder (failure case 69 of the connector design).
  expect(saved.body.documentTypes[1].companies[0].input).toBe(`${root}/fo-storage-bdc-export`);
});

test("a company just added shows in Companies with its source not chosen yet, and opens from its name", async ({ page }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await page.getByRole("button", { name: "Add company" }).click();
  await dialog(page).getByLabel("Company (dataAreaId)").fill("2000");
  await dialog(page).getByLabel("Company (dataAreaId)").press("Enter");
  await expect(page).toHaveURL(/company=2000&tab=from$/);
  await page.getByRole("link", { name: "Companies" }).click();
  const row = page.getByRole("row", { name: /Company 2000/ });
  await expect(row).toContainText("Blob container, not chosen yet");
  await expect(row).toContainText("Nowhere yet");
  await row.getByRole("link", { name: "Company 2000" }).click();
  await expect(page).toHaveURL(/company=2000&tab=from$/);
});

test("a web address source gets a key that is shown once and goes out only with Save", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=1000&tab=from"));
  await opened(page);
  await page.getByRole("radio", { name: /Web address \(HTTPS\)/ }).check();
  await expect(page.getByText("No key yet.")).toBeVisible();
  await expect(page.locator("code").first()).toHaveText(/\/inbound\/SalesInvoice\/1000$/);
  await page.getByRole("button", { name: "Create a key" }).click();
  await expect(page.getByText("New key for 1000, shown only now")).toBeVisible();
  const shown = (await page.locator(".keybox code").textContent()) ?? "";
  const key = shown.replace("x-api-key: ", "");
  expect(key).toMatch(/^[A-Za-z0-9]{32}$/);
  await expect(saveBar(page)).toContainText("New key for in-salesinvoice-1000");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = await configPosts(request);
  const expected = X.setHttpsKey(postBody(fullConfig()), "SalesInvoice", "1000", key, root, "in-salesinvoice-1000");
  expect(saved.body).toEqual(expected);
  // After the save the key is never shown again.
  await expect(page.getByText("New key for 1000, shown only now")).toHaveCount(0);
});

test("Check sends the source as the form has it and shows the role command when it is refused", async ({ page, request }) => {
  await page.goto(type("id=SalesInvoice&company=1000&tab=from"));
  await opened(page);
  await page.getByRole("button", { name: "Check" }).click();
  await expect(page.getByText("The instance has no data role on the container bdc-export.")).toBeVisible();
  await expect(page.locator(".check-line code")).toContainText("az role assignment create");
  await expect(page.getByRole("button", { name: "Copy the command" })).toBeVisible();
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/setup/check-source", body: { connection: "fo-storage", container: "bdc-export", prefix: "" } },
  ]);
});

test("a destination's columns are filled from the document, tested and saved", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=1000&tab=to&dest=sharepoint"));
  await opened(page);
  await expect(page.getByText("With the last document: Invoices/2026/SI-0042.pdf")).toBeVisible();
  await page.getByRole("button", { name: "Add a column" }).click();
  await page.getByLabel("Column 2", { exact: true }).selectOption("Customer");
  await page.getByLabel("Filled from the document 2", { exact: true }).selectOption("{recipientName}");
  await expect(page.getByText("Fabrikam GmbH", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Test", exact: true }).click();
  await expect(page.getByText("Signed in. 2 fields found, every mapped one exists.")).toBeVisible();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();

  const posts = await calls(request);
  const lookups = posts.filter((c) => c.path === "/targets/choices").map((c) => c.body);
  const settings = { SiteUrl: "https://contoso.sharepoint.example/sites/Finance", Library: "Invoices", Folder: "{issueDate:yyyy}" };
  expect(lookups).toContainEqual({ kind: "sharepoint", lookup: "libraries", connection: "", settings });
  expect(lookups).toContainEqual({ kind: "sharepoint", lookup: "columns", connection: "", settings });
  const [saved] = posts.filter((c) => c.path === "/config");
  expect(saved.body.documentTypes[0].companies[0].sharePoint.fields).toEqual({ Title: "{documentNumber}", Customer: "{recipientName}" });
});

test("a destination and the company's mail are switched in the Goes to list", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=2000&tab=to"));
  await opened(page);
  await page.getByRole("checkbox", { name: "DocuWare file cabinet on" }).uncheck();
  await page.getByRole("checkbox", { name: "Mail on" }).uncheck();
  await page.getByRole("checkbox", { name: "Company 2000 active" }).uncheck();
  await expect(saveBar(page)).toContainText("3 changes not saved");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  let expected = X.setDestinationEnabled(postBody(fullConfig()), "SalesInvoice", "2000", "docuware", false);
  expected = X.setMail(expected, "SalesInvoice", "2000", false);
  expected = X.setCompanyActive(expected, "SalesInvoice", "2000", false);
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(expected);
});

test("Compress PDF is switched under Before delivery and goes out with Save", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=2000&tab=to"));
  await opened(page);
  await expect(page.getByRole("heading", { name: "Before delivery" })).toBeVisible();
  await expect(page.getByText("fonts keep only the letters used, images above 300 ppi go to 200 ppi")).toBeVisible();
  await page.getByRole("checkbox", { name: "Compress PDF" }).check();
  await expect(saveBar(page)).toContainText(/1 change/);
  await page.screenshot({ path: test.info().outputPath("before-delivery.png"), fullPage: true });
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(X.setCompressPdf(postBody(fullConfig()), "SalesInvoice", "2000", true));
});

test("each company has its own Cc addresses in the Mail row, and they go out with Save", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=2000&tab=to"));
  await opened(page);
  await expect(page.getByText("Gets every mail of this company as a copy, from the Live ERP and from test systems.")).toBeVisible();
  const cc = page.getByLabel("Cc addresses of this company");
  await cc.fill("x@contoso.example; y@contoso.example, X@CONTOSO.example");
  await cc.press("Enter");
  await expect(saveBar(page)).toContainText("mail Cc x@contoso.example, y@contoso.example");
  await page.screenshot({ path: test.info().outputPath("mail-cc.png"), fullPage: true });
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(X.setMailCc(postBody(fullConfig()), "SalesInvoice", "2000", "x@contoso.example; y@contoso.example"));
});

test("Discard puts every setting back and nothing is sent", async ({ page, request }) => {
  await page.goto(type("id=SalesInvoice"));
  await opened(page);
  await page.getByLabel("Name", { exact: true }).fill("Invoice");
  await page.getByLabel("Name", { exact: true }).press("Enter");
  await page.getByRole("group", { name: "Sent to" }).getByRole("button", { name: "Vendors" }).click();
  await expect(saveBar(page)).toContainText("2 changes not saved");
  await expect(saveBar(page)).toContainText("Sales invoice: name is now Invoice");
  await saveBar(page).getByRole("button", { name: "Discard" }).click();
  await expect(saveBar(page)).toHaveCount(0);
  await expect(page.getByLabel("Name", { exact: true })).toHaveValue("Sales invoice");
  await expect(page.getByRole("group", { name: "Sent to" }).getByRole("button", { name: "Customers" })).toHaveAttribute("aria-pressed", "true");
  expect(await calls(request)).toEqual([]);
});

test("removing a type asks Keep or Remove on its page, and the last type cannot go", async ({ page, request }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await expect(page.getByRole("button", { name: "Remove Quotation" })).toHaveClass(/danger/);
  await page.getByRole("button", { name: "Remove Quotation" }).click();
  const ask = page.getByRole("alert").filter({ hasText: "Remove Quotation with its companies and destinations? Nothing changes until you save." });
  await expect(ask.getByRole("button", { name: "Keep" })).toBeFocused();
  await ask.getByRole("button", { name: "Keep" }).click();
  await expect(saveBar(page)).toHaveCount(0);
  await page.getByRole("button", { name: "Remove Quotation" }).click();
  await ask.getByRole("button", { name: "Remove" }).click();
  await expect(page).toHaveURL(/\/outbound\/$/);
  await expect(saveBar(page)).toContainText("Quotation removed");
  await expect(grid(page).getByRole("row", { name: /Quotation/ })).toHaveCount(0);
  await grid(page).getByRole("link", { name: "Sales invoice" }).click();
  await page.getByRole("button", { name: "Remove Sales invoice" }).click();
  await page.getByRole("alert").filter({ hasText: "Remove Sales invoice" }).getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Keep at least one document type. Switch it off instead." })).toBeVisible();
  expect(await calls(request)).toEqual([]);
});

test("a company and a destination are removed at the end of their own tab", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto(type("id=SalesInvoice&company=3000&tab=to"));
  await opened(page);
  await page.getByRole("button", { name: "Remove Company 3000" }).click();
  await page.getByRole("alert").getByRole("button", { name: "Remove" }).click();
  await expect(page).toHaveURL(/\?id=SalesInvoice$/);
  await expect(saveBar(page)).toContainText("Sales invoice: company 3000 removed");
  await page.getByRole("row", { name: /Company 1000/ }).getByRole("link").click();
  await page.getByRole("link", { name: "Goes to" }).click();
  await page.getByRole("link", { name: "SharePoint", exact: true }).click();
  await expect(page).toHaveURL(/dest=sharepoint$/);
  await page.getByRole("button", { name: "Remove SharePoint library" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Remove SharePoint library for company 1000? Documents already delivered stay there." })).toBeVisible();
  await page.getByRole("alert").getByRole("button", { name: "Remove" }).click();
  await expect(page).toHaveURL(/company=1000&tab=to$/);
  await expect(page.getByRole("link", { name: "SharePoint", exact: true })).toHaveCount(0);
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const expected = X.removeDestination(X.removeCompany(postBody(fullConfig()), "SalesInvoice", "3000"), "SalesInvoice", "1000", "sharepoint");
  const [saved] = await configPosts(request);
  expect(saved.body).toEqual(expected);
});

test("a company whose documents wait is offered, and adding it opens where they come from", async ({ page, request }) => {
  await mock(request, {
    "GET /history/attention": {
      events: [
        {
          id: 50,
          documentId: "SQ-1",
          documentType: "SalesQuotation",
          dataAreaId: "4000",
          step: "waiting",
          state: "waiting",
          message: "No company entry.",
        },
      ],
    },
  });
  await page.goto("/outbound/");
  await opened(page);
  await expect(page.getByText("1 document of company 4000 is waiting for Quotation.")).toBeVisible();
  await page.getByRole("button", { name: "Add company 4000" }).click();
  await expect(page).toHaveURL(/id=SalesQuotation&company=4000&tab=from$/);
  await expect(page.getByRole("heading", { name: "Company 4000" })).toBeVisible();
  await expect(saveBar(page)).toContainText("Quotation: company 4000 added");
});

test("leaving Outbound with unsaved changes asks first; Connections keeps the draft", async ({ page }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await typeActive(page).uncheck();
  const nav = page.getByRole("navigation", { name: "Main navigation" });

  await nav.getByRole("link", { name: "Overview" }).click();
  const ask = page.getByRole("dialog", { name: "Confirm action" });
  await expect(ask).toContainText("Your changes are not saved. Leave this page and lose them?");
  await ask.getByRole("button", { name: "Cancel" }).click();
  await expect(page).toHaveURL(/id=SalesQuotation$/);
  await expect(saveBar(page)).toContainText("Quotation: off");
  expect(
    await page.evaluate(() => {
      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    }),
  ).toBe(true);

  // The breadcrumb stays inside Outbound: no question, the list shows the type as off.
  await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Outbound" }).click();
  await expect(page).toHaveURL(/\/outbound\/$/);
  await expect(grid(page).getByRole("row", { name: /Quotation/ }).locator(".badge")).toHaveText("Off");
  await onePrimaryAtMost(page);

  await nav.getByRole("link", { name: "Connections" }).click();
  await expect(page).toHaveURL(/\/connections\/$/);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(saveBar(page)).toContainText("Quotation: off");

  await nav.getByRole("link", { name: "Document history" }).click();
  await page.getByRole("dialog", { name: "Confirm action" }).getByRole("button", { name: "Continue" }).click();
  await expect(page).toHaveURL(/\/history\/$/);
  await nav.getByRole("link", { name: /Outbound/ }).click();
  await opened(page);
  await expect(grid(page).getByRole("row", { name: /Quotation/ }).locator(".badge")).toHaveText("Active");
  await expect(saveBar(page)).toHaveCount(0);
});

test("a save overtaken by someone else shows both change lists, and Save both sends it again", async ({ page, request }) => {
  await mock(request, { "POST /config": conflict });
  await page.goto(type("id=SalesInvoice"));
  await opened(page);
  await typeActive(page).uncheck();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  const panel = page.getByRole("alert").filter({ hasText: "Not saved: Bernd Berger saved these settings at 10:30, after you opened them" });
  await expect(panel).toContainText("Quotation › Name: Quotation is now Sales quotation");
  await expect(panel).toContainText("Sales invoice › Active: on is now off");
  await onePrimaryAtMost(page);
  await mock(request, { "POST /config": { ok: true } });
  await panel.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [first, second] = await configPosts(request);
  expect(first.body).toEqual(X.setType(postBody(config()), "SalesInvoice", { enabled: false }));
  expect(second.body).toEqual({ ...first.body, saveBoth: true });
});

test("a Viewer sees every Outbound setting and no control that changes one", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer, "GET /config": fullConfig() });
  await page.goto("/outbound/");
  await opened(page);
  await expect(page.getByRole("link", { name: "Sales invoice" })).toBeVisible();
  await expect(page.getByRole("button", { name: "New document type" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^Remove|^Add company/ })).toHaveCount(0);
  await noRowButtons(page);

  await page.goto(type("id=SalesInvoice"));
  await opened(page);
  await expect(page.getByLabel("Name", { exact: true })).toBeDisabled();
  await expect(page.getByRole("group", { name: "Hive BDC makes" }).getByRole("button", { name: "PDF as it is" })).toBeDisabled();
  await expect(typeActive(page)).toBeDisabled();
  await expect(page.getByRole("button", { name: /^Add company|^Remove/ })).toHaveCount(0);

  await page.goto(type("id=SalesInvoice&company=1000&tab=from"));
  await opened(page);
  await expect(page.getByLabel("Container", { exact: true })).toHaveValue("bdc-export");
  await expect(page.getByLabel("Container", { exact: true })).toBeDisabled();
  await expect(page.getByRole("radio", { name: /Azure Service Bus/ })).toBeDisabled();
  await expect(page.getByRole("checkbox", { name: "Company 1000 active" })).toBeDisabled();
  await expect(page.getByRole("button", { name: /^Check|^Remove/ })).toHaveCount(0);

  await page.goto(type("id=SalesInvoice&company=1000&tab=to"));
  await opened(page);
  await expect(page.getByRole("checkbox", { name: "SharePoint on" })).toBeDisabled();
  await expect(page.getByRole("button", { name: /^Add a destination|^Remove/ })).toHaveCount(0);

  await page.goto(type("id=SalesInvoice&company=1000&tab=to&dest=sharepoint"));
  await opened(page);
  await expect(page.getByLabel("Site address")).toBeDisabled();
  await expect(page.getByRole("checkbox", { name: "SharePoint library on" })).toBeDisabled();
  for (const name of ["Add a column", "Test", "Remove SharePoint library", "Save changes", "Discard"]) {
    await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0);
  }
  // Only the company switcher stays usable: it changes the page shown, not a setting.
  await expect(page.locator("main :is(input, select, textarea):enabled:not(.company-bar select)")).toHaveCount(0);
  expect(await calls(request)).toEqual(expect.not.arrayContaining([expect.objectContaining({ path: "/config" })]));
});

test("empty states name the next step", async ({ page, request }) => {
  await page.goto(type("id=SalesQuotation"));
  await opened(page);
  await expect(page.getByText("No company yet.")).toBeVisible();
  await expect(page.getByText("Add company sets up the first one.")).toBeVisible();
  await page.getByRole("link", { name: "Comes from" }).click();
  await expect(page.getByText("Add one under Companies first.")).toBeVisible();
  await page.goto("/outbound/");
  await opened(page);
  await page.getByLabel("Search", { exact: true }).fill("Delivery");
  await expect(page.getByText("No document type matches.")).toBeVisible();
  await page.getByRole("button", { name: "Clear search" }).click();
  await expect(grid(page).getByRole("row")).toHaveCount(3);
  // No document type at all means the instance needs its set-up, so the console opens the assistant instead of an
  // empty Outbound list (shell.spec.ts covers that redirect).
  await page.goto(type("id=DeliveryNote"));
  await opened(page);
  await expect(page.getByText("There is no document type DeliveryNote.")).toBeVisible();
});

test("one primary button at most on every Outbound screen and dialog", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  for (const path of pages) {
    await page.goto(path);
    await opened(page);
    await onePrimaryAtMost(page);
  }
  await page.goto(type("id=SalesInvoice&company=1000&tab=to"));
  await opened(page);
  await page.getByRole("button", { name: "Add a destination" }).click();
  await expect(dialog(page)).toBeVisible();
  await onePrimaryAtMost(page);
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  // With a change waiting, Save is the one gold button; a dialog opened then hides the Save bar.
  await page.getByRole("checkbox", { name: "Company 1000 active" }).uncheck();
  await expect(saveBar(page)).toBeVisible();
  await onePrimaryAtMost(page);
  await page.getByRole("link", { name: "Companies" }).click();
  await page.getByRole("button", { name: "Add company" }).click();
  await expect(saveBar(page)).toHaveCount(0);
  await onePrimaryAtMost(page);
  await dialog(page).getByRole("button", { name: "Close dialog" }).click();
  await expect(saveBar(page)).toBeVisible();
  await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Outbound" }).click();
  await expect(page.getByRole("button", { name: "New document type" })).toHaveClass(/secondary/);
  await onePrimaryAtMost(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await onePrimaryAtMost(page);
});

for (const width of [1280, 390]) {
  test(`the Outbound pages stay inside the screen at ${width} px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    await mock(request, { "GET /config": fullConfig() });
    for (const path of pages) {
      await page.goto(path);
      await opened(page);
      await noSideScroll(page);
    }
    await page.goto("/outbound/");
    await opened(page);
    await page.getByRole("button", { name: "New document type" }).click();
    await expect(dialog(page)).toBeVisible();
    await noSideScroll(page);
  });
}

test("the Outbound pages and dialogs pass the accessibility check", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  const check = async (where: string) => {
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]").analyze();
    const serious = result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious.map((v) => `${where} ${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
  };
  for (const path of pages) {
    await page.goto(path);
    await opened(page);
    await page.waitForLoadState("networkidle");
    await check(path);
  }
  await page.goto("/outbound/");
  await opened(page);
  await page.getByRole("button", { name: "New document type" }).click();
  await check("new document type dialog");
  await page.goto(type("id=SalesInvoice"));
  await opened(page);
  await page.getByRole("button", { name: "Add company" }).click();
  await check("add company dialog");
  await page.goto(type("id=SalesInvoice&company=1000&tab=to"));
  await opened(page);
  await page.getByRole("button", { name: "Add a destination" }).click();
  await check("add destination dialog");
});
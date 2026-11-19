import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { config } from "./fixtures";
import { conflict, fullConfig, outboundAnswers, viewer } from "./fixtures-outbound";
import { calls, mock, noRowButtons, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";
import { postBody } from "../../src/lib/logic";
import * as X from "../../src/lib/exportlogic";

// The Connections page (port of connections.js; design 2026-11-19-web-console-nextjs.md: a DataGrid and a record dialog
// per connection with Test; failure cases 1, 7, 8, 11 and 13; version 2: an ERP list without row buttons, the name
// opens the dialog, Remove, Test, Cancel and Apply in its footer, the kind chosen first for a new one).

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, outboundAnswers());
});

const saveBar = (page: Page) => page.getByRole("region", { name: "Changes not saved" });
const grid = (page: Page) => page.getByRole("region", { name: "Connections", exact: true });
const apps = (page: Page) => page.getByRole("region", { name: "Microsoft 365 apps", exact: true });
const dialog = (page: Page) => page.locator("dialog[open]");
const state = (row: ReturnType<Page["getByRole"]>) => row.locator(".badge");

test("Connections loads under the host's content security policy and lists kind, sign-in, use and state", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  const violations = await watchCsp(page);
  await page.goto("/connections/");
  await opened(page);
  const docuware = grid(page).getByRole("row", { name: /docuware/ });
  await expect(docuware).toContainText("DocuWare");
  await expect(docuware).toContainText("contoso.docuware.example as hivebdc");
  const storage = grid(page).getByRole("row", { name: /fo-storage/ });
  await expect(storage).toContainText("stcontosofo as this instance");
  await expect(storage.locator("[title]").first()).toHaveAttribute("title", "Sales invoice › 1000 (source), Sales invoice › 3000 (PDFs)");
  await expect(state(storage)).toHaveText("Not tested");
  const key = grid(page).getByRole("row", { name: /Key in-salesinvoice-2000/ });
  await expect(key).toContainText("Web address key");
  await expect(state(key)).toHaveText("Key stored");
  await expect(apps(page).getByRole("row", { name: /SharePoint app/ })).toContainText("As client 11111111-2222-3333-4444-555555555555");
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("From invoices@contoso.example");
  await expect(apps(page).getByRole("row", { name: /Document mail/ }).locator("[title]").first()).toHaveAttribute("title", "Sales invoice › 2000");
  await noRowButtons(page);
  await onePrimaryAtMost(page);
  await page.getByRole("button", { name: "New connection" }).click();
  await expect(dialog(page)).toBeVisible();
  expect(violations).toEqual([]);
});

test("a new connection starts with its kind, is tested with the values typed, and goes out with its secret only on Save", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await page.getByRole("button", { name: "New connection" }).click();
  await expect(dialog(page).getByRole("heading", { name: "New connection" })).toBeVisible();
  await onePrimaryAtMost(page);
  await dialog(page).getByRole("button", { name: /^Azure Service Bus/ }).click();
  await expect(dialog(page).getByRole("heading", { name: "New Azure Service Bus connection" })).toBeVisible();
  await onePrimaryAtMost(page);
  await expect(dialog(page).getByLabel("Name", { exact: true })).toHaveValue("servicebus");
  await dialog(page).getByLabel("Namespace (only for a managed identity in Azure)").fill("sb-contoso.servicebus.windows.net");
  await dialog(page).getByLabel("Connection string").fill("Endpoint=sb://made-up.example/;SharedAccessKeyName=test-only");
  await dialog(page).getByRole("button", { name: "Test connection" }).click();
  await expect(dialog(page).getByText("Signed in.")).toBeVisible();
  await dialog(page).getByRole("button", { name: "Add", exact: true }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(grid(page).getByRole("row", { name: /servicebus/ })).toContainText("sb-contoso with a connection string");
  await expect(saveBar(page)).toContainText("Connection servicebus added");
  await expect(saveBar(page)).toContainText("New secret for servicebus");
  await onePrimaryAtMost(page);
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();

  const added = {
    id: "servicebus",
    kind: "servicebus",
    settings: { Namespace: "sb-contoso.servicebus.windows.net" },
    secret: "Endpoint=sb://made-up.example/;SharedAccessKeyName=test-only",
  };
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/connections/test", body: added },
    { method: "POST", path: "/config", body: X.addConnection(postBody(config()), added) },
  ]);
});

test("Other kind goes back to the kind tiles, and Cancel adds nothing", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await page.getByRole("button", { name: "New connection" }).click();
  await dialog(page).getByRole("button", { name: /^DocuWare/ }).click();
  await dialog(page).getByRole("button", { name: "Other kind" }).click();
  await expect(dialog(page).getByRole("heading", { name: "New connection" })).toBeVisible();
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(saveBar(page)).toHaveCount(0);
  expect(await calls(request)).toEqual([]);
});

test("a name opens its dialog, also from the address; a blank secret is kept and the new settings are saved", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("link", { name: "own-storage" }).click();
  await expect(page).toHaveURL(/\/connections\/\?open=own-storage$/);
  await expect(dialog(page).getByRole("heading", { name: "own-storage" })).toBeVisible();
  await onePrimaryAtMost(page);
  await expect(dialog(page).getByLabel("Connection string")).toHaveAttribute("placeholder", "");
  await dialog(page).getByLabel("Account address (only for a managed identity in Azure)").fill("https://stotherexample.blob.core.windows.net");
  await dialog(page).getByRole("button", { name: "Apply" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(page).toHaveURL(/\/connections\/$/);
  await expect(saveBar(page)).toContainText("Connection own-storage changed");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = (await calls(request)).filter((c) => c.path === "/config");
  expect(saved.body).toEqual(
    X.updateConnection(postBody(config()), "own-storage", { AccountUrl: "https://stotherexample.blob.core.windows.net" }, ""),
  );
  expect(saved.body.connections.find((c: { id: string }) => c.id === "own-storage")).not.toHaveProperty("secret");

  // A shared or reloaded address opens the same dialog; Escape closes it.
  await page.goto("/connections/?open=fo-storage");
  await opened(page);
  await expect(dialog(page).getByRole("heading", { name: "fo-storage" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog(page)).toHaveCount(0);
  await expect(page).toHaveURL(/\/connections\/$/);
});

test("Test in the dialog checks a storage connection on a container it is used with, other kinds by signing in", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  const close = () => dialog(page).getByRole("button", { name: "Cancel" }).click();

  await grid(page).getByRole("link", { name: "fo-storage" }).click();
  await dialog(page).getByRole("button", { name: "Test connection" }).click();
  await expect(dialog(page).getByText("Container bdc-export: The instance has no data role on the container bdc-export.")).toBeVisible();
  await close();
  const storage = grid(page).getByRole("row", { name: /fo-storage/ });
  await expect(state(storage)).toHaveText("Refused");
  await expect(storage.locator("span[title]").last()).toHaveAttribute("title", /^\d\d:\d\d: Container bdc-export: The instance has no data role/);

  await grid(page).getByRole("link", { name: "sb-fo" }).click();
  await dialog(page).getByRole("button", { name: "Test connection" }).click();
  await expect(dialog(page).getByText("Signed in.")).toBeVisible();
  await close();
  await expect(state(grid(page).getByRole("row", { name: /sb-fo/ }))).toHaveText("Works");

  await apps(page).getByRole("link", { name: "Document mail" }).click();
  await dialog(page).getByRole("button", { name: "Test mail" }).click();
  await expect(dialog(page).getByText("Signed in. The mailbox invoices@contoso.example may send.")).toBeVisible();
  await close();
  await apps(page).getByRole("link", { name: "SharePoint app" }).click();
  await dialog(page).getByRole("button", { name: "Test connection" }).click();
  await expect(dialog(page).getByText("Signed in. The library Invoices takes files.")).toBeVisible();
  await close();
  await expect(state(apps(page).getByRole("row", { name: /SharePoint app/ }))).toHaveText("Works");
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/setup/check-source", body: { connection: "fo-storage", container: "bdc-export", prefix: "" } },
    {
      method: "POST",
      path: "/connections/test",
      body: { id: "sb-fo", kind: "servicebus", settings: { Namespace: "sb-contoso.servicebus.windows.net" }, secret: "" },
    },
    {
      method: "POST",
      path: "/config/test-mail",
      body: {
        mail: {
          tenantId: "",
          clientId: "66666666-7777-8888-9999-000000000000",
          from: "invoices@contoso.example",
          redirectTo: ["test@contoso.example"],
        },
      },
    },
    {
      method: "POST",
      path: "/config/test-sharepoint",
      body: { sharePoint: { siteUrl: "https://contoso.sharepoint.example/sites/Finance", library: "Invoices", folder: "" } },
    },
  ]);
  // A test result goes with a change of its own connection only, and stays through Discard.
  await grid(page).getByRole("link", { name: "own-storage" }).click();
  await dialog(page).getByLabel("Account address (only for a managed identity in Azure)").fill("https://stotherexample.blob.core.windows.net");
  await dialog(page).getByRole("button", { name: "Apply" }).click();
  await saveBar(page).getByRole("button", { name: "Discard" }).click();
  await expect(state(grid(page).getByRole("row", { name: /sb-fo/ }))).toHaveText("Works");
  await expect(state(storage)).toHaveText("Refused");
});

test("a typed change is tested as typed, before it is applied", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("link", { name: "own-storage" }).click();
  await dialog(page).getByLabel("Connection string").fill("DefaultEndpointsProtocol=https;AccountName=made-up");
  await dialog(page).getByRole("button", { name: "Test connection" }).click();
  await expect(dialog(page).getByText("Signed in.")).toBeVisible();
  expect(await calls(request)).toEqual([
    {
      method: "POST",
      path: "/connections/test",
      body: {
        id: "own-storage",
        kind: "azure-storage",
        settings: { AccountUrl: "https://sthivebdcexample.blob.core.windows.net" },
        secret: "DefaultEndpointsProtocol=https;AccountName=made-up",
      },
    },
  ]);
});

test("a connection still in use cannot be removed; an unused one asks Keep or Remove in its dialog", async ({ page, request }) => {
  const withSpare = config();
  withSpare.connections.push({
    id: "spare",
    kind: "azure-storage",
    settings: { AccountUrl: "https://stspareexample.blob.core.windows.net" },
    hasSecret: false,
  });
  await mock(request, { "GET /config": withSpare });
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("link", { name: "fo-storage" }).click();
  await expect(dialog(page).getByRole("button", { name: "Remove" })).toHaveClass(/danger/);
  await dialog(page).getByRole("button", { name: "Remove" }).click();
  await expect(dialog(page).getByText("Still used by Sales invoice › 1000 (source). Change those first.")).toBeVisible();
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await grid(page).getByRole("link", { name: "spare" }).click();
  await dialog(page).getByRole("button", { name: "Remove" }).click();
  const ask = dialog(page).getByRole("alert").filter({ hasText: "Remove the connection spare? Nothing changes until you save." });
  await expect(ask.getByRole("button", { name: "Keep" })).toBeFocused();
  await ask.getByRole("button", { name: "Remove" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(grid(page).getByRole("row", { name: /spare/ })).toHaveCount(0);
  await expect(saveBar(page)).toContainText("Connection spare removed");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = (await calls(request)).filter((c) => c.path === "/config");
  expect(saved.body).toEqual(X.removeConnection(postBody(withSpare), "spare"));
});

test("a web address key gets a new one in its dialog, shown once, sent only with Save", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("link", { name: "Key in-salesinvoice-2000" }).click();
  await expect(dialog(page).getByText("Stored in Key Vault")).toBeVisible();
  await onePrimaryAtMost(page);
  await dialog(page).getByRole("button", { name: "Make a new key" }).click();
  await expect(dialog(page).getByText("New key, shown only now")).toBeVisible();
  const key = ((await dialog(page).locator(".keybox code").textContent()) ?? "").trim();
  expect(key).toMatch(/^[A-Za-z0-9]{32}$/);
  await dialog(page).getByRole("button", { name: "Close", exact: true }).click();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = (await calls(request)).filter((c) => c.path === "/config");
  expect(saved.body).toEqual(X.updateConnection(postBody(fullConfig()), "in-salesinvoice-2000", {}, key));
});

test("document mail names what is missing before it takes the values, and Discard puts them back", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await expect(state(apps(page).getByRole("row", { name: /Document mail/ }))).toHaveText("Not set up");
  await apps(page).getByRole("link", { name: "Document mail" }).click();
  await onePrimaryAtMost(page);
  await expect(dialog(page).getByRole("checkbox", { name: "Send documents by mail" })).toBeChecked();
  await dialog(page).getByLabel("Sent from").fill("invoices@contoso.example");
  await dialog(page).getByLabel("Documents of a test ERP go to").fill("test@contoso.example, nope");
  await dialog(page).getByRole("button", { name: "Apply" }).click();
  await expect(dialog(page)).toContainText("Enter the tenant ID.");
  await expect(dialog(page)).toContainText("Enter the client ID of the mail app.");
  await expect(dialog(page)).toContainText('"nope" is not an e-mail address.');
  await dialog(page).getByLabel("Tenant ID (empty: the SharePoint tenant)").fill("00000000-0000-0000-0000-000000000002");
  await dialog(page).getByLabel("Client ID of the mail app").fill("66666666-7777-8888-9999-000000000000");
  await dialog(page).getByLabel("Client secret").fill("made-up-secret");
  await dialog(page).getByLabel("Documents of a test ERP go to").fill("test@contoso.example");
  await dialog(page).getByRole("button", { name: "Apply" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("From invoices@contoso.example");
  await expect(saveBar(page)).toContainText("Mail on");
  await expect(saveBar(page)).toContainText("New secret for document mail");
  await apps(page).getByRole("link", { name: "Document mail" }).click();
  await expect(dialog(page).locator(".dialog-subtitle")).toHaveText(
    "From invoices@contoso.example. Documents of a test ERP go to test@contoso.example. The new secret goes to Key Vault when you save.",
  );
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await saveBar(page).getByRole("button", { name: "Discard" }).click();
  await expect(saveBar(page)).toHaveCount(0);
  await expect(state(apps(page).getByRole("row", { name: /Document mail/ }))).toHaveText("Not set up");
  expect(await calls(request)).toEqual([]);
});

test("a save overtaken by someone else offers Save both on Connections too", async ({ page, request }) => {
  await mock(request, { "POST /config": conflict });
  await page.goto("/connections/");
  await opened(page);
  await apps(page).getByRole("link", { name: "SharePoint app" }).click();
  await dialog(page).getByLabel("Client ID", { exact: true }).fill("11111111-2222-3333-4444-555555555555");
  await dialog(page).getByRole("button", { name: "Apply" }).click();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByRole("heading", { name: /Not saved: Bernd Berger saved these settings/ })).toBeVisible();
  await onePrimaryAtMost(page);
  await mock(request, { "POST /config": { ok: true } });
  await page.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [first, second] = (await calls(request)).filter((c) => c.path === "/config");
  expect(first.body.sharePointAuth).toEqual({ tenantId: "00000000-0000-0000-0000-000000000002", clientId: "11111111-2222-3333-4444-555555555555" });
  expect(second.body).toEqual({ ...first.body, saveBoth: true });
});

test("a Viewer opens every connection read-only and finds no control that changes or tests one", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer, "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await expect(grid(page).getByRole("row", { name: /docuware/ })).toBeVisible();
  await expect(page.getByRole("button", { name: "New connection" })).toHaveCount(0);
  await noRowButtons(page);
  for (const [where, name] of [
    [grid, "docuware"],
    [grid, "Key in-salesinvoice-2000"],
    [apps, "SharePoint app"],
    [apps, "Document mail"],
  ] as const) {
    await where(page).getByRole("link", { name }).click();
    await expect(dialog(page)).toBeVisible();
    for (const button of [/^Test/, /^Remove/, "Apply", "Make a new key", "Save changes"]) {
      await expect(dialog(page).getByRole("button", { name: button })).toHaveCount(0);
    }
    await expect(dialog(page).locator(":is(input, select, textarea):enabled")).toHaveCount(0);
    await dialog(page).getByRole("button", { name: "Close", exact: true }).click();
    await expect(dialog(page)).toHaveCount(0);
  }
  expect(await calls(request)).toEqual([]);
});

test("an instance without connections says how to add the first one", async ({ page, request }) => {
  await mock(request, { "GET /config": { ...config(), connections: [] } });
  await page.goto("/connections/");
  await opened(page);
  await expect(grid(page).getByText("No connection yet.")).toBeVisible();
  await expect(grid(page).getByText("New connection sets up the first one.")).toBeVisible();
});

test("search narrows the list by name or kind", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await expect(page.getByText("5 connections")).toBeVisible();
  await page.getByLabel("Search", { exact: true }).fill("storage");
  await expect(page.getByText("2 connections")).toBeVisible();
  await page.getByLabel("Search", { exact: true }).fill("web address");
  await expect(grid(page).getByRole("row", { name: /Key in-salesinvoice-2000/ })).toBeVisible();
  await page.getByLabel("Search", { exact: true }).fill("zzz");
  await expect(grid(page).getByText("No connection matches.")).toBeVisible();
});

for (const width of [1280, 390]) {
  test(`Connections stays inside the screen at ${width} px, also with a dialog open`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    await mock(request, { "GET /config": fullConfig() });
    await page.goto("/connections/");
    await opened(page);
    await noSideScroll(page);
    await apps(page).getByRole("link", { name: "Document mail" }).click();
    await expect(dialog(page)).toBeVisible();
    await noSideScroll(page);
    await dialog(page).getByRole("button", { name: "Cancel" }).click();
    await page.getByRole("button", { name: "New connection" }).click();
    await expect(dialog(page)).toBeVisible();
    await noSideScroll(page);
  });
}

test("Connections and its dialogs pass the accessibility check", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await page.waitForLoadState("networkidle");
  const check = async (where: string) => {
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]").analyze();
    const serious = result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious.map((v) => `${where} ${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
  };
  await check("page");
  await apps(page).getByRole("link", { name: "Document mail" }).click();
  await check("mail dialog");
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await grid(page).getByRole("link", { name: "docuware" }).click();
  await check("connection dialog");
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "New connection" }).click();
  await check("kind choice");
  await dialog(page).getByRole("button", { name: /^DocuWare/ }).click();
  await check("new connection dialog");
});
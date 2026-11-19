import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { config } from "./fixtures";
import { conflict, fullConfig, outboundAnswers, viewer } from "./fixtures-outbound";
import { calls, mock, noSideScroll, opened, watchCsp } from "./helpers";
import { postBody } from "../../src/lib/logic";
import * as X from "../../src/lib/exportlogic";

// The Connections page (port of connections.js; design 2026-11-19-web-console-nextjs.md: a DataGrid and a record dialog
// per connection with Test; failure cases 1, 7, 8, 11 and 13).

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, outboundAnswers());
});

const saveBar = (page: Page) => page.getByRole("region", { name: "Changes not saved" });
const grid = (page: Page) => page.getByRole("region", { name: "Connections", exact: true });
const apps = (page: Page) => page.getByRole("region", { name: "SharePoint and mail", exact: true });
const dialog = (page: Page) => page.locator("dialog[open]");

test("Connections loads under the host's content security policy without a violation", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  const violations = await watchCsp(page);
  await page.goto("/connections/");
  await opened(page);
  await expect(grid(page).getByRole("row", { name: /docuware/ })).toContainText(
    "DocuWare contoso.docuware.example as hivebdc. Password in Key Vault.",
  );
  await expect(grid(page).getByRole("row", { name: /fo-storage/ })).toContainText(
    "Used by Sales invoice › 1000 (source) and Sales invoice › 3000 (PDFs)",
  );
  await expect(grid(page).getByRole("row", { name: /Key in-salesinvoice-2000/ })).toContainText("Key stored");
  await expect(apps(page).getByRole("row", { name: /SharePoint app/ })).toContainText("Used by Sales invoice › 1000");
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("1 company wants to mail");
  expect(violations).toEqual([]);
});

test("a new connection is tested with the values typed, and goes out with its secret only on Save", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await page.getByRole("button", { name: /^Azure Service Bus/ }).click();
  await expect(dialog(page).getByRole("heading", { name: "New Azure Service Bus connection" })).toBeVisible();
  await expect(dialog(page).getByLabel("Name", { exact: true })).toHaveValue("servicebus");
  await dialog(page).getByLabel("Namespace (only for a managed identity in Azure)").fill("sb-contoso.servicebus.windows.net");
  await dialog(page).getByLabel("Connection string").fill("Endpoint=sb://made-up.example/;SharedAccessKeyName=test-only");
  await dialog(page).getByRole("button", { name: "Test the connection" }).click();
  await expect(dialog(page).getByText("Signed in.")).toBeVisible();
  await dialog(page).getByRole("button", { name: "Add", exact: true }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(grid(page).getByRole("row", { name: /servicebus/ })).toContainText("Azure Service Bus. Connection string in Key Vault.");
  await expect(saveBar(page)).toContainText("Connection servicebus added");
  await expect(saveBar(page)).toContainText("New secret for servicebus");
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

test("editing a connection keeps a blank secret, and the new settings are saved", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("button", { name: "Edit own-storage" }).click();
  await expect(dialog(page).getByLabel("Connection string")).toHaveAttribute("placeholder", "");
  await dialog(page).getByLabel("Account address (only for a managed identity in Azure)").fill("https://stotherexample.blob.core.windows.net");
  await dialog(page).getByRole("button", { name: "Done" }).click();
  await expect(saveBar(page)).toContainText("Connection own-storage changed");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = (await calls(request)).filter((c) => c.path === "/config");
  expect(saved.body).toEqual(
    X.updateConnection(postBody(config()), "own-storage", { AccountUrl: "https://stotherexample.blob.core.windows.net" }, ""),
  );
  expect(saved.body.connections.find((c: { id: string }) => c.id === "own-storage")).not.toHaveProperty("secret");
});

test("Test checks a storage connection on a container it is used with, other kinds by signing in", async ({ page, request }) => {
  await mock(request, { "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await grid(page).getByRole("button", { name: "Test fo-storage" }).click();
  const storage = grid(page).getByRole("row", { name: /fo-storage/ });
  await expect(storage).toContainText("Sign-in refused");
  await expect(storage).toContainText("Container bdc-export: The instance has no data role on the container bdc-export.");
  await grid(page).getByRole("button", { name: "Test sb-fo" }).click();
  await expect(grid(page).getByRole("row", { name: /sb-fo/ })).toContainText(/Works · tested \d\d:\d\d/);
  await apps(page).getByRole("button", { name: "Test document mail" }).click();
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("The mailbox invoices@contoso.example may send.");
  await apps(page).getByRole("button", { name: "Test the SharePoint app" }).click();
  await expect(apps(page).getByRole("row", { name: /SharePoint app/ })).toContainText(/Works · tested/);
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
  await grid(page).getByRole("button", { name: "Edit own-storage" }).click();
  await dialog(page).getByLabel("Account address (only for a managed identity in Azure)").fill("https://stotherexample.blob.core.windows.net");
  await dialog(page).getByRole("button", { name: "Done" }).click();
  await saveBar(page).getByRole("button", { name: "Discard" }).click();
  await expect(grid(page).getByRole("row", { name: /sb-fo/ })).toContainText(/Works · tested/);
  await expect(storage).toContainText("Sign-in refused");
});

test("a connection still in use cannot be removed; an unused one asks Keep or Remove", async ({ page, request }) => {
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
  await grid(page).getByRole("button", { name: "Remove fo-storage" }).click();
  await expect(page.getByText("Still used by Sales invoice › 1000 (source). Change those first.")).toBeVisible();
  await grid(page).getByRole("button", { name: "Remove spare" }).click();
  const ask = page.getByRole("alert").filter({ hasText: "Remove the connection spare?" });
  await ask.getByRole("button", { name: "Remove" }).click();
  await expect(grid(page).getByRole("row", { name: /spare/ })).toHaveCount(0);
  await expect(saveBar(page)).toContainText("Connection spare removed");
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [saved] = (await calls(request)).filter((c) => c.path === "/config");
  expect(saved.body).toEqual(X.removeConnection(postBody(withSpare), "spare"));
});

test("document mail names what is missing before it takes the values, and Discard puts them back", async ({ page, request }) => {
  await page.goto("/connections/");
  await opened(page);
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("Not set up");
  await apps(page).getByRole("button", { name: "Set up mail" }).click();
  await expect(dialog(page).getByRole("switch", { name: "Send documents by mail" })).toBeChecked();
  await dialog(page).getByLabel("Sent from").fill("invoices@contoso.example");
  await dialog(page).getByLabel("Documents of a test ERP go to").fill("test@contoso.example, nope");
  await dialog(page).getByRole("button", { name: "Done" }).click();
  await expect(dialog(page)).toContainText("Enter the tenant ID.");
  await expect(dialog(page)).toContainText("Enter the client ID of the mail app.");
  await expect(dialog(page)).toContainText('"nope" is not an e-mail address.');
  await dialog(page).getByLabel("Tenant ID (empty: the SharePoint tenant)").fill("00000000-0000-0000-0000-000000000002");
  await dialog(page).getByLabel("Client ID of the mail app").fill("66666666-7777-8888-9999-000000000000");
  await dialog(page).getByLabel("Client secret").fill("made-up-secret");
  await dialog(page).getByLabel("Documents of a test ERP go to").fill("test@contoso.example");
  await dialog(page).getByRole("button", { name: "Done" }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText(
    "From invoices@contoso.example. Documents of a test ERP go to test@contoso.example. The new secret goes to Key Vault when you save.",
  );
  await expect(saveBar(page)).toContainText("Mail on");
  await expect(saveBar(page)).toContainText("New secret for document mail");
  await saveBar(page).getByRole("button", { name: "Discard" }).click();
  await expect(saveBar(page)).toHaveCount(0);
  await expect(apps(page).getByRole("row", { name: /Document mail/ })).toContainText("Not set up");
  expect(await calls(request)).toEqual([]);
});

test("a save overtaken by someone else offers Save both on Connections too", async ({ page, request }) => {
  await mock(request, { "POST /config": conflict });
  await page.goto("/connections/");
  await opened(page);
  await apps(page).getByRole("button", { name: "Set up the SharePoint app" }).click();
  await dialog(page).getByLabel("Client ID", { exact: true }).fill("11111111-2222-3333-4444-555555555555");
  await dialog(page).getByRole("button", { name: "Done" }).click();
  await saveBar(page).getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByRole("heading", { name: /Not saved: Bernd Berger saved these settings/ })).toBeVisible();
  await mock(request, { "POST /config": { ok: true } });
  await page.getByRole("button", { name: "Save both" }).click();
  await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible();
  const [first, second] = (await calls(request)).filter((c) => c.path === "/config");
  expect(first.body.sharePointAuth).toEqual({ tenantId: "00000000-0000-0000-0000-000000000002", clientId: "11111111-2222-3333-4444-555555555555" });
  expect(second.body).toEqual({ ...first.body, saveBoth: true });
});

test("a Viewer sees every connection and no control that changes or tests one", async ({ page, request }) => {
  await mock(request, { "GET /me": viewer, "GET /config": fullConfig() });
  await page.goto("/connections/");
  await opened(page);
  await expect(grid(page).getByRole("row", { name: /docuware/ })).toBeVisible();
  for (const name of [/^Test/, /^Edit/, /^Remove/, /^Replace key/, /^Set up/, /^Azure Storage/, "Save changes"]) {
    await expect(page.getByRole("button", { name })).toHaveCount(0);
  }
  await expect(page.getByRole("heading", { name: "Add a connection" })).toHaveCount(0);
});

test("an instance without connections says how to add the first one", async ({ page, request }) => {
  await mock(request, { "GET /config": { ...config(), connections: [] } });
  await page.goto("/connections/");
  await opened(page);
  await expect(grid(page).getByText("No connection yet.")).toBeVisible();
  await expect(grid(page).getByText("Choose a kind under Add a connection to set up the first one.")).toBeVisible();
});

for (const width of [1280, 390]) {
  test(`Connections stays inside the screen at ${width} px, also with a dialog open`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    await mock(request, { "GET /config": fullConfig() });
    await page.goto("/connections/");
    await opened(page);
    await noSideScroll(page);
    await apps(page).getByRole("button", { name: "Edit" }).last().click();
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
  await apps(page).getByRole("button", { name: "Edit" }).last().click();
  await check("mail dialog");
  await dialog(page).getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: /^DocuWare/ }).click();
  await check("new connection dialog");
});
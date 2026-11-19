import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { readFile } from "node:fs/promises";
import { config } from "./fixtures";
import {
  cannotUpdate,
  done,
  fullLog,
  licenceRefused,
  licenceStored,
  logs,
  notOnAzure,
  offer,
  operator,
  returnedByItself,
  returning,
  running,
  runningEarly,
  scheduled,
  stopped,
  testRun,
  viewer,
} from "./fixtures-service";
import { calls, mock, noRowButtons, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";

// The Service page (design 2026-11-19-web-console-nextjs.md part 6 and version 2): the tabs Status, Licence, Updates
// and Technical log, the update line of the Overview, failure cases 1, 2, 7, 11, 13 and V1, V2, V5.

const tabs = [
  { path: "/service/", name: "Status", heading: "Taking documents" },
  { path: "/service/?tab=licence", name: "Licence", heading: "Licence" },
  { path: "/service/?tab=updates", name: "Updates", heading: "Installed" },
  { path: "/service/?tab=log", name: "Technical log", heading: "Technical log" },
];

const tabBar = (page: Page) => page.getByRole("navigation", { name: "Service sections" });
const panelHeading = (page: Page, name: string) => page.getByRole("heading", { level: 2, name, exact: true });
const panel = (page: Page, name: string) => page.getByRole("region", { name, exact: true });
// Next.js keeps an empty route announcer with role alert on every page.
const alert = (page: Page) => page.locator("[role='alert']:not(#__next-route-announcer__)");
const history = (page: Page) => page.getByRole("region", { name: "Version changes" });
const updateLine = (page: Page) => page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" });
/** The value of a read-only fact, found by its label (the label is upper case on screen only). */
const fact = (page: Page, label: string) =>
  page
    .locator(".service-facts > div")
    .filter({ has: page.locator("dt", { hasText: new RegExp(`^${label}$`) }) })
    .locator("dd");

// The running fixtures started at 16:31 and 17:02 local time; the page's clock stands just after, so the notice of a long
// restart (design 2026-11-19-update-page-after-restart.md, U5) shows only in the tests that move the clock on.
const FIXTURE_NOW = new Date("2026-11-19T15:32:00+00:00");

test.beforeEach(async ({ page, request }) => {
  await request.post("/__mock/reset");
  await mock(request, { "GET /logs?tail=200": logs, "GET /logs?tail=5000": fullLog });
  await page.clock.setFixedTime(FIXTURE_NOW);
});

test("every tab loads under the host's content security policy without a violation", async ({ page, request }) => {
  const violations = await watchCsp(page);
  for (const tab of tabs) {
    await page.goto(tab.path);
    await opened(page);
    await expect(panelHeading(page, tab.heading)).toBeVisible();
    await expect(tabBar(page).getByRole("link", { name: tab.name })).toHaveAttribute("aria-current", "page");
  }
  await mock(request, { "GET /service/updates": running() });
  await page.goto("/service/?tab=updates");
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  await page.goto("/service/?tab=log");
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download today's log" }).click();
  await download;
  expect(violations).toEqual([]);
});

test("the page is titled Service with one short line, and the breadcrumb names the tab", async ({ page }) => {
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(page.locator("main h1")).toHaveText("Service");
  const line = (await page.locator(".page-heading p").textContent()) ?? "";
  expect(line).toBe("Status, licence, updates and the log of this instance.");
  expect(line.length).toBeLessThanOrEqual(60);
  const crumbs = page.getByRole("navigation", { name: "Breadcrumb" });
  await expect(crumbs.getByRole("link", { name: "Service" })).toHaveAttribute("href", "/service/");
  await expect(crumbs.locator("[aria-current='page']")).toHaveText("Licence");
  await crumbs.getByRole("link", { name: "Service" }).click();
  await expect(page).toHaveURL(/\/service\/$/);
  await expect(crumbs.locator("[aria-current='page']")).toHaveText("Status");
});

test("?tab=licence opens the Licence tab directly and a reload keeps it; the tabs switch by link", async ({ page }) => {
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(tabBar(page).getByRole("link", { name: "Licence" })).toHaveAttribute("aria-current", "page");
  await expect(panel(page, "Licence").locator(".badge")).toHaveText("Valid");
  await expect(fact(page, "Licensed to")).toHaveText("Contoso GmbH");
  await expect(fact(page, "Valid until")).toHaveText("30.09.2027");
  await expect(fact(page, "Features")).toHaveText("e-invoice, documents");
  await expect(fact(page, "Tenant")).toHaveText("00000000-0000-0000-0000-000000000002");
  await expect(panel(page, "Licence").locator(".service-body > p")).toHaveCount(0);
  await page.reload();
  await expect(tabBar(page).getByRole("link", { name: "Licence" })).toHaveAttribute("aria-current", "page");
  await expect(panelHeading(page, "Licence")).toBeVisible();
  await expect(panelHeading(page, "Taking documents")).toHaveCount(0);

  await tabBar(page).getByRole("link", { name: "Technical log" }).click();
  await expect(page).toHaveURL(/\/service\/\?tab=log$/);
  await expect(panelHeading(page, "Technical log")).toBeVisible();
  await tabBar(page).getByRole("link", { name: "Status" }).click();
  await expect(page).toHaveURL(/\/service\/$/);
  await expect(panelHeading(page, "Taking documents")).toBeVisible();
});

test("the Overview's licence card opens the Licence tab, an unknown tab opens Status", async ({ page }) => {
  await page.goto("/");
  await opened(page);
  const card = page.getByRole("link", { name: /^Licence Valid/ });
  await expect(card).toHaveAttribute("href", "/service/?tab=licence");
  await card.click();
  await expect(page).toHaveURL(/\/service\/\?tab=licence$/);
  await expect(panelHeading(page, "Licence")).toBeVisible();
  await page.goto("/service/?tab=nonsense");
  await expect(tabBar(page).getByRole("link", { name: "Status" })).toHaveAttribute("aria-current", "page");
});

test("Status shows version and identity as facts, and the icon-only copy button copies the identity", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.goto("/service/");
  await opened(page);
  await expect(panel(page, "Taking documents").locator(".badge")).toHaveText("Running");
  await expect(fact(page, "Checks every source")).toHaveText("Every 30 seconds");
  await expect(fact(page, "Version")).toHaveText("1.9.0");
  await expect(fact(page, "Region")).toHaveText("germanywestcentral");
  await expect(fact(page, "Time zone")).toHaveText("Europe/Berlin");
  await expect(fact(page, "Managed identity")).toContainText("00000000-0000-0000-0000-000000000001");
  await expect(page.locator(".section-heading p")).toHaveCount(0);

  const copy = fact(page, "Managed identity").getByRole("button", { name: "Copy the managed identity" });
  await expect(copy).toHaveText("");
  await expect(copy).toHaveAttribute("title", "Copy the managed identity");
  await copy.click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("00000000-0000-0000-0000-000000000001");
  await expect(copy).toHaveAttribute("title", "Copied");
  await expect(fact(page, "Managed identity").locator("[aria-live='polite']")).toHaveText("Copied");
});

test("Pause sends POST /setup/pause, then the page offers Start, which sends POST /setup/start", async ({ page, request }) => {
  await mock(request, { "POST /setup/pause": { ok: true, paused: true }, "POST /setup/start": { ok: true, paused: false } });
  await page.goto("/service/");
  await opened(page);
  const pause = page.getByRole("button", { name: "Pause", exact: true });
  await expect(pause).toHaveClass(/\bsecondary\b/);
  await expect(pause).toHaveAttribute("title", "New documents wait in their source until you start again");
  await mock(request, { "GET /config": { ...config(), paused: true } });
  await pause.click();
  const start = page.getByRole("button", { name: "Start", exact: true });
  await expect(start).toBeVisible();
  await expect(start).toHaveClass(/\bsecondary\b/);
  await expect(panel(page, "Taking documents").locator(".badge")).toHaveText("Paused");
  await onePrimaryAtMost(page);

  await mock(request, { "GET /config": config() });
  await start.click();
  await expect(page.getByRole("button", { name: "Pause", exact: true })).toBeVisible();
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/setup/pause", body: {} },
    { method: "POST", path: "/setup/start", body: {} },
  ]);
});

test("a refused pause shows the reason and leaves the button", async ({ page, request }) => {
  await mock(request, { "POST /setup/pause": { __status: 500, ok: false, message: "The settings could not be written (Azure Files): disk full" } });
  await page.goto("/service/");
  await opened(page);
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await expect(alert(page)).toContainText("The settings could not be written (Azure Files): disk full");
  await expect(page.getByRole("button", { name: "Pause", exact: true })).toBeEnabled();
});

test("the test run sends the chosen type and company and shows every step", async ({ page, request }) => {
  await mock(request, { "POST /setup/test-run": testRun });
  await page.goto("/service/");
  await opened(page);
  await expect(page.getByLabel("Company for the test run")).toHaveValue("0");
  await expect(page.getByLabel("Company for the test run").locator("option")).toHaveText(["Sales invoices · Company 1000"]);
  const run = page.getByRole("button", { name: "Run the test" });
  await expect(run).toHaveClass(/\bsecondary\b/);
  await run.click();
  const card = page.getByRole("region", { name: "Steps of the test run" });
  await expect(card.getByRole("heading", { name: "SI-0043 · Sales invoices · Company 1000" })).toBeVisible();
  await expect(card).toContainText("Done");
  await expect(card).toContainText("One document went all the way through.");
  await expect(card.locator("li")).toHaveText([/09:01:02.*Taken from the Blob container bdc-export\./, /09:01:03.*E-invoice made\./, /09:01:05.*Delivered/]);
  expect(await calls(request)).toEqual([{ method: "POST", path: "/setup/test-run", body: { documentType: "SalesInvoice", company: "1000" } }]);
  await onePrimaryAtMost(page);

  await mock(request, { "POST /setup/test-run": { __status: 409, ok: false, message: "busy" } });
  await run.click();
  await expect(alert(page)).toHaveText("A test run is already going.");
});

test("without a Blob source the test run says why it cannot run", async ({ page, request }) => {
  const base = config();
  base.documentTypes[0].companies[0].source = { kind: "servicebus", connection: "fo-bus", settings: { Queue: "bdc-export" } } as never;
  await mock(request, { "GET /config": base });
  await page.goto("/service/");
  await opened(page);
  await expect(page.getByText("A test run takes its export from a Blob container; no company reads one yet.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Run the test" })).toBeDisabled();
});

test("a licence file is stored, and a refused one says why", async ({ page, request }) => {
  await mock(request, { "POST /license": licenceStored });
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(panel(page, "Licence").getByText("New licence file", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Choose file" })).toHaveClass(/\bsecondary\b/);
  const input = page.getByLabel("New licence file");
  await input.setInputFiles({ name: "contoso.lic", mimeType: "text/plain", buffer: Buffer.from("LICENCE-CONTOSO-0001") });
  await expect(page.getByText("The licence for Contoso GmbH is stored. It is valid until 30.09.2028.")).toBeVisible();

  await mock(request, { "POST /license": licenceRefused });
  await input.setInputFiles({ name: "other.lic", mimeType: "text/plain", buffer: Buffer.from("LICENCE-OTHER-0002") });
  await expect(alert(page)).toHaveText("This licence was issued for another tenant.");
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/license", body: { content: "LICENCE-CONTOSO-0001", fileName: "contoso.lic" } },
    { method: "POST", path: "/license", body: { content: "LICENCE-OTHER-0002", fileName: "other.lic" } },
  ]);
});

test("a licence that warns shows its message and state on the Licence tab; without one only the tenant shows", async ({ page, request }) => {
  const status = (await (await request.get("/api/status")).json()) as Record<string, any>;
  await mock(request, {
    "GET /status": { ...status, license: { ...status.license, state: "Grace", message: "The licence ended on 30.09.2026; documents still go through until 30.10.2026." } },
  });
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(page.locator(".notice-warn")).toHaveText("The licence ended on 30.09.2026; documents still go through until 30.10.2026.");
  await expect(panel(page, "Licence").locator(".badge")).toHaveText("Grace period");

  await mock(request, { "GET /status": { ...status, license: { state: "Missing", message: "" } } });
  await page.reload();
  await expect(panel(page, "Licence").locator(".badge")).toHaveText("No licence yet");
  await expect(page.locator(".service-facts dt")).toHaveText(["Tenant"]);
  await expect(page.getByLabel("Licence file", { exact: true })).toHaveCount(1);
});

test("an offered update shows one calm line on the Overview, and Update now starts it and opens the Updates tab", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/");
  await opened(page);
  const line = updateLine(page);
  await expect(line).toContainText("Released 18.11.2026");
  await expect(line.getByRole("link", { name: "What's new" })).toHaveAttribute("href", "/service/?tab=updates");
  const box = await line.boundingBox();
  expect(box?.height ?? 0, "one line at 1280 px").toBeLessThan(60);
  await expect(line.getByRole("button", { name: "Update now" })).toHaveClass(/\bprimary\b/);
  await expect(line.getByRole("button", { name: "Tonight at 22:00" })).toHaveClass(/\bsecondary\b/);
  await onePrimaryAtMost(page);
  await expect(page.getByRole("navigation", { name: "Main navigation" }).getByText("update available")).toHaveCount(1);
  await line.getByRole("button", { name: "Update now" }).click();
  await expect(page).toHaveURL(/\/service\/\?tab=updates$/);
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/run", body: {} }]);
});

test("the Overview plans the update for tonight at 22:00, and shows the plan once made", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/schedule": { ok: true, message: "Planned for 22:00." } });
  await page.goto("/");
  await opened(page);
  await page.getByRole("button", { name: "Tonight at 22:00" }).click();
  await expect(page).toHaveURL(/\/service\/\?tab=updates$/);
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/schedule", body: { at: "22:00" } }]);

  await mock(request, { "GET /service/updates": scheduled() });
  await page.goto("/");
  await expect(updateLine(page)).toContainText("Planned for 22:00");
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toHaveCount(0);
  await onePrimaryAtMost(page);
});

test("a refused start on the Overview says why and keeps the buttons", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { __status: 409, ok: false, message: "Another update runs." } });
  await page.goto("/");
  await opened(page);
  await page.getByRole("button", { name: "Update now" }).click();
  await expect(alert(page)).toHaveText("Another update runs.");
  await expect(page.getByRole("button", { name: "Update now" })).toBeEnabled();
  await expect(page).toHaveURL(/127\.0\.0\.1:\d+\/$/);
});

test("the Updates tab offers the new version as facts, with Update now as the one primary button", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(tabBar(page).getByRole("link", { name: "Updates" })).toContainText("1");
  await expect(panelHeading(page, "Hive BDC 1.9.1")).toBeVisible();
  await expect(panel(page, "Hive BDC 1.9.1").locator(".badge")).toHaveText("New version");
  await expect(fact(page, "Released")).toHaveText("18.11.2026");
  await expect(fact(page, "Download")).toHaveText("23 MB");
  await expect(fact(page, "Restart")).toHaveText("2 to 4 minutes");
  await expect(page.locator(".service-notes li")).toHaveText(["DocuWare date fields take German dates.", "The test run names the SharePoint column it could not fill."]);
  await expect(page.getByText("Documents wait in their source during the restart, and 1.9.0 stays ready to return to.")).toBeVisible();
  await expect(panelHeading(page, "Installed")).toBeVisible();
  await expect(fact(page, "Last looked")).toHaveText("19.11.2026 07:01");
  await expect(history(page).getByRole("row")).toHaveCount(2);
  await expect(history(page)).toContainText("Running");
  await expect(page.getByRole("button", { name: "Update now" })).toHaveClass(/\bprimary\b/);
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toHaveClass(/\bsecondary\b/);
  await expect(page.getByRole("button", { name: "Look now" })).toHaveCount(1);
  await onePrimaryAtMost(page);
  await noRowButtons(page);

  await mock(request, { "GET /service/updates": running() });
  await page.getByRole("button", { name: "Update now" }).click();
  await expect(page.getByRole("status").filter({ hasText: "The update to 1.9.1 started." })).toBeVisible();
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  await expect(tabBar(page).getByRole("link", { name: "Updates" })).toHaveText("Updates");
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/run", body: {} }]);
});

test("Tonight at 22:00 plans the update and Cancel the plan takes it back", async ({ page, request }) => {
  await mock(request, {
    "GET /service/updates": offer(),
    "POST /service/updates/schedule": { ok: true, message: "Planned for 19.11.2026 22:00." },
    "POST /service/updates/cancel": { ok: true, message: "The plan is cancelled." },
  });
  await page.goto("/service/?tab=updates");
  await opened(page);
  // The offer must be on screen before the next answer is mocked, or a first read still on its way gets the plan.
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toBeVisible();
  await mock(request, { "GET /service/updates": scheduled() });
  await page.getByRole("button", { name: "Tonight at 22:00" }).click();
  await expect(fact(page, "Planned")).toHaveText("19.11.2026 22:00 by Anna Weber");
  await expect(page.getByRole("button", { name: "Cancel the plan" })).toHaveClass(/\bsecondary\b/);
  await onePrimaryAtMost(page);
  await mock(request, { "GET /service/updates": offer() });
  await page.getByRole("button", { name: "Cancel the plan" }).click();
  await expect(page.getByText("The plan is cancelled.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toBeVisible();
  await expect(fact(page, "Planned")).toHaveCount(0);
  expect(await calls(request)).toEqual([
    { method: "POST", path: "/service/updates/schedule", body: { at: "22:00" } },
    { method: "POST", path: "/service/updates/cancel", body: {} },
  ]);
});

test("a refusal before the download is only a message, a stop after it shows the stopped screen", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { __status: 409, ok: false, message: "There is too little space for the download." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await page.getByRole("button", { name: "Update now" }).click();
  await expect(alert(page)).toHaveText("There is too little space for the download.");
  await expect(panelHeading(page, "Hive BDC 1.9.1")).toBeVisible();

  await mock(request, { "GET /service/updates": stopped() });
  await page.getByRole("button", { name: "Update now" }).click();
  await expect(alert(page)).toContainText("The update stopped before anything changed.");
  await expect(page.getByText("There is too little space for the download.")).toHaveCount(0);
});

test("a running update shows its steps and asks again every 5 seconds", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": runningEarly() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  await expect(page.getByText("Started 16:31 by Anna Weber")).toBeVisible();
  await expect(page.locator(".service-percent")).toHaveText("20 %");
  const steps = page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem");
  await expect(steps.nth(1)).toContainText("Checking the signature");
  await expect(steps.nth(1)).toContainText("now");
  await expect(page.getByRole("list", { name: "While it runs" }).getByRole("listitem")).toHaveText([
    "Documents wait in their source and are taken right after the restart.",
    "You can close this page. The update finishes on its own, and the history shows the result.",
    "1.9.0 stays in place. If 1.9.1 does not suit you, return to it with one click afterwards.",
  ]);
  await expect(panelHeading(page, "Update history")).toHaveCount(0);
  await expect(page.locator("main .button")).toHaveCount(0);

  await mock(request, { "GET /service/updates": running() });
  await expect(page.locator(".service-percent")).toHaveText("60 %", { timeout: 9000 });
  await expect(steps).toHaveText([
    /Downloaded from Balsoft.*hivebdc-1\.9\.1\.zip, 23\.2 MB.*16:31:04/,
    /Signature checked.*Made and signed by Balsoft GmbH.*16:31:05/,
    /Settings backed up.*16:31:05/,
    /Restarting with 1\.9\.1.*About 2 to 4 minutes; the console is away for about 2 of them.*now/,
    /Running 1\.9\.1.*The console reconnects by itself and shows the result/,
  ]);
  await expect(page.getByRole("progressbar", { name: "Progress" })).toHaveAttribute("value", "60");

  await mock(request, { "GET /service/updates": { __status: 503, error: "Service Unavailable" } });
  await expect(page.getByText("Hive BDC is restarting. This page reconnects by itself.")).toBeVisible({ timeout: 9000 });
});

test("once another version answers, the running page loads the whole console again", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": running() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  let loads = 0;
  page.on("load", () => loads++);
  await mock(request, { "GET /service/updates": done(), "GET /me": { name: "Anna Weber", roles: ["Administrator"], canRetry: true, canChange: true, version: "1.9.1" } });
  await expect(panelHeading(page, "Checked after the restart")).toBeVisible({ timeout: 12000 });
  expect(loads).toBe(1);
  await expect(page.locator("main")).toContainText("1.9.1");
});

test("after a return, a version before 1.9.0 answers 404, and the page opens the update page of that version", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": returning(), "GET /me": { name: "Anna Weber", roles: ["Administrator"], canRetry: true, canChange: true, version: "1.9.1" } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Returning to 1.9.0")).toBeVisible();
  const visited: string[] = [];
  page.on("framenavigated", (frame) => {
    if (frame === page.mainFrame()) visited.push(frame.url());
  });
  await mock(request, { "GET /service/updates": { __status: 404, error: "not found" } });
  await expect.poll(() => visited.some((url) => url.endsWith("/#/service/updates")), { timeout: 12000 }).toBe(true);
  await expect(page).toHaveURL(/\/service\/\?tab=updates$/);
});

test("a gap while the app restarts does not reload the page", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": running() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  // The running panel must be on screen before the next answer is mocked, or the first read still on its way gets it.
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  let loads = 0;
  page.on("load", () => loads++);
  await mock(request, { "GET /service/updates": { __status: 502, error: "Bad Gateway" } });
  await expect(page.getByText("Hive BDC is restarting. This page reconnects by itself.")).toBeVisible({ timeout: 9000 });
  await page.waitForTimeout(6000);
  expect(loads).toBe(0);
});

test("a restart that takes longer than 6 minutes says so and offers Reload page; after 10 it says what happens", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": running() });
  await page.clock.setFixedTime(new Date("2026-11-19T15:38:00+00:00"));
  await page.goto("/service/?tab=updates");
  await opened(page);
  const slow = page.getByRole("status").filter({ hasText: "This takes longer than usual. Hive BDC may already run 1.9.1; reload the page to see it." });
  await expect(slow).toBeVisible();
  const reload = page.getByRole("button", { name: "Reload page" });
  await expect(reload).toHaveCount(1);
  await expect(reload).toHaveClass(/\bsecondary\b/);
  await page.clock.setFixedTime(new Date("2026-11-19T15:42:00+00:00"));
  await expect(
    page.getByRole("status").filter({ hasText: "1.9.1 did not answer within 10 minutes. Hive BDC then returns to 1.9.0 by itself; reload the page to see where it stands." }),
  ).toBeVisible({ timeout: 20000 });
  await reload.click();
  await expect(page).toHaveURL(/\/service\/\?tab=updates$/);
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
});

test("the former address #/service/updates opens the Updates tab", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": done() });
  await page.goto("/#/service/updates");
  await expect(page).toHaveURL(/\/service\/\?tab=updates$/);
  await expect(panelHeading(page, "Checked after the restart")).toBeVisible();
});

test("leaving the Updates tab stops asking", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": running() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  await tabBar(page).getByRole("link", { name: "Status" }).click();
  await expect(panelHeading(page, "Taking documents")).toBeVisible();
  const asked: string[] = [];
  page.on("request", (r) => {
    if (r.url().includes("/api/service/updates")) asked.push(r.url());
  });
  await page.waitForTimeout(6000);
  expect(asked).toEqual([]);
});

test("a return shows only its restart", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": returning(), "GET /me": { name: "Anna Weber", roles: ["Administrator"], canRetry: true, canChange: true, version: "1.9.1" } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Returning to 1.9.0")).toBeVisible();
  await expect(page.locator(".service-percent")).toHaveCount(0);
  await expect(page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem")).toHaveText([/Restarting with 1\.9\.0/, /Running 1\.9\.0/]);
  await expect(page.getByText("1.9.1 is removed once 1.9.0 runs; you can update to it again from the feed.")).toBeVisible();
});

test("a stopped update shows what happened in a calm panel, copies the details and offers Try again", async ({ page, request, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mock(request, { "GET /service/updates": stopped(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(alert(page)).toHaveText("The update stopped before anything changed. 1.9.0 runs with your settings, and documents flow as before.");
  await expect(page.locator(".service-banner")).toHaveCount(0);
  await expect(panelHeading(page, "Update to 1.9.1")).toBeVisible();
  await expect(panel(page, "Update to 1.9.1").locator(".badge")).toHaveText("Stopped");
  await expect(fact(page, "When")).toHaveText("19.11.2026 16:31");
  await expect(fact(page, "By")).toHaveText("Anna Weber");
  const steps = page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem");
  await expect(steps.nth(1)).toContainText("Signature does not match");
  await expect(steps.nth(2)).toContainText("not started");
  await expect(history(page)).toContainText("Stopped");
  await expect(page.locator(".service-panel .warn-text")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Try again" })).toHaveClass(/\bsecondary\b/);
  await onePrimaryAtMost(page);
  await noRowButtons(page);

  await page.getByRole("button", { name: "Copy details for Balsoft support" }).click();
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied.split("\n").slice(0, 3)).toEqual([
    "Hive BDC 1.9.0, 1.9.1: The file is not the one Balsoft signed: the signature does not match.",
    "Downloaded from Balsoft: done (hivebdc-1.9.1.zip, 23.2 MB)",
    "Signature does not match: failed (The file is not the one Balsoft signed: the signature does not match.)",
  ]);

  await mock(request, { "GET /service/updates": running() });
  await page.getByRole("button", { name: "Try again" }).click();
  await expect(panelHeading(page, "Updating to 1.9.1")).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/run", body: {} }]);
});

test("a return by itself says the new version did not run properly", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": returnedByItself() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(alert(page)).toContainText("1.9.1 did not run properly. Hive BDC is back on 1.9.0");
  await expect(panel(page, "Update to 1.9.1").locator(".badge")).toHaveText("Returned");
  const last = page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem").last();
  await expect(last).toContainText("1.9.1 did not start properly");
  await expect(last).toContainText("Returned to 1.9.0 by itself: 1.9.1 did not start properly 3 times.");
  await expect(history(page)).toContainText("Returned");
});

test("a finished update shows its checks, and Return to the previous version asks first and sends the POST", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": done(), "POST /service/updates/return": { ok: true, message: "Hive BDC returns to 1.9.0 and restarts." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panel(page, "Hive BDC 1.9.1").locator(".badge")).toHaveText("Running");
  await expect(fact(page, "Since")).toHaveText("19.11.2026 16:34");
  await expect(fact(page, "What happened")).toHaveText("Updated from 1.9.0.");
  await expect(fact(page, "Kept for return")).toHaveText("1.9.0");
  const checks = page.getByRole("region", { name: "Checks after the restart" });
  await expect(checks.getByRole("row")).toHaveCount(4);
  await expect(checks).toContainText("Sources answer");
  await expect(checks).toContainText("16:34");
  await expect(history(page)).toContainText("Kept for return");
  await onePrimaryAtMost(page);
  await noRowButtons(page);

  const back = page.getByRole("button", { name: "Return to 1.9.0" });
  await expect(back).toHaveClass(/\bsecondary\b/);
  await back.click();
  const confirm = page.getByRole("dialog", { name: "Confirm action" });
  await expect(confirm).toContainText(
    "Return to 1.9.0? Hive BDC restarts and is away for about 2 minutes. Documents wait in their source, and the settings stay as they are.",
  );
  await onePrimaryAtMost(page);
  await confirm.getByRole("button", { name: "Cancel" }).click();
  await expect(confirm).toHaveCount(0);
  expect(await calls(request)).toEqual([]);

  await back.click();
  await mock(request, { "GET /service/updates": returning() });
  await page.getByRole("dialog", { name: "Confirm action" }).getByRole("button", { name: "Continue" }).click();
  await expect(page.getByText("Hive BDC returns to 1.9.0 and restarts.")).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/return", body: {} }]);
});

test("Look now sits in one place, asks the feed and shows its answer", async ({ page, request }) => {
  await mock(request, { "POST /service/updates/check": { ok: true, message: "1.9.0 is the newest version." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panel(page, "Installed").locator(".badge")).toHaveText("Newest version");
  await expect(fact(page, "Version")).toHaveText("1.9.0");
  await expect(fact(page, "Looks for updates")).toHaveText("Every day at 06:00");
  const look = page.getByRole("button", { name: "Look now" });
  await expect(look).toHaveCount(1);
  await expect(look).toHaveClass(/\bsecondary\b/);
  await look.click();
  await expect(page.getByRole("status").filter({ hasText: "1.9.0 is the newest version." }).first()).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/check", body: {} }]);
});

test("after an update the result screen also offers Look now, next to Return", async ({ page, request }) => {
  await mock(request, {
    "GET /service/updates": done(),
    "POST /service/updates/check": { ok: true, message: "Hive BDC 1.9.2 is ready." },
  });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Checked after the restart")).toBeVisible();
  const look = page.getByRole("button", { name: "Look now" });
  await expect(look).toHaveCount(1);
  await expect(look).toHaveClass(/\bsecondary\b/);
  await expect(page.getByRole("button", { name: /Return to 1\.9\.0/ })).toBeVisible();
  await look.click();
  await expect(page.getByRole("status").filter({ hasText: "Hive BDC 1.9.2 is ready." }).first()).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/check", body: {} }]);
});

const lookFailed = () => ({ ...done(), checks: [], lastCheck: { at: "2026-11-19T06:01:00+00:00", ok: false, message: "The feed did not answer: timeout." } });

test("a failed look for updates says why", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": lookFailed() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panel(page, "Installed").locator(".badge")).toHaveText("Last look failed");
  await expect(panel(page, "Installed").locator(".warn-text")).toHaveText("The feed did not answer: timeout.");
});

test("an instance that cannot update says why and offers no Update now", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": cannotUpdate() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(page.getByText("This instance runs from a package that Balsoft does not update; ask the person who installed it.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Update now" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toHaveCount(0);
});

test("an instance without self-update says how it is updated", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": notOnAzure });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(page.locator(".notice-info")).toHaveText("This instance is updated by Setup from the Windows app.");
});

test("the technical log shows only warnings and errors on request and downloads the whole day", async ({ page }) => {
  await page.goto("/service/?tab=log");
  await opened(page);
  const box = page.getByRole("region", { name: "Lines of the technical log" });
  await expect(box).toContainText("[INF] Hive BDC 1.9.0 started");
  await expect(page.locator(".toggle-pill")).toHaveText("Only warnings and errors");
  await page.getByLabel("Only warnings and errors").check();
  await expect(box).not.toContainText("[INF]");
  await expect(box).toContainText("[WRN] The container answered 403");
  await expect(box).toContainText("[ERR] SharePoint answered 404");
  await tabBar(page).getByRole("link", { name: "Status" }).click();
  await tabBar(page).getByRole("link", { name: "Technical log" }).click();
  await expect(page.getByLabel("Only warnings and errors")).toBeChecked();

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download today's log" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("hivebdc-20261119.log");
  const text = await readFile(await file.path(), "utf8");
  expect(text.split("\n")).toEqual(fullLog.lines);
});

test("an empty log says so, and a log without a file names why", async ({ page, request }) => {
  await mock(request, { "GET /logs?tail=200": { lines: [] } });
  await page.goto("/service/?tab=log");
  await opened(page);
  await expect(page.getByRole("region", { name: "Lines of the technical log" })).toHaveText("Nothing in the log yet.");
  await mock(request, { "GET /logs?tail=200": { lines: [], note: "console-only, no log file" } });
  await page.reload();
  await expect(page.getByRole("region", { name: "Lines of the technical log" })).toHaveText("console-only, no log file");
});

const actionButtons = ["Pause", "Start", "Run the test", "Choose file", "Update now", "Tonight at 22:00", "Cancel the plan", "Look now", "Try again", "Return to 1.9.0"];

async function noActions(page: Page) {
  for (const name of actionButtons) await expect(page.getByRole("button", { name, exact: true }), name).toHaveCount(0);
}

for (const who of [viewer, operator]) {
  test(`a ${who.roles[0]} sees versions, notes and history but no action`, async ({ page, request }) => {
    await mock(request, { "GET /me": who });
    for (const tab of tabs) {
      await page.goto(tab.path);
      await opened(page);
      await expect(panelHeading(page, tab.heading)).toBeVisible();
      await noActions(page);
    }
    await expect(page.getByRole("button", { name: "Download today's log" })).toBeEnabled();
    await expect(page.getByLabel("Only warnings and errors")).toBeEnabled();
    await page.goto("/service/");
    await expect(page.getByLabel("Company for the test run")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Copy the managed identity" })).toBeEnabled();
    await page.goto("/service/?tab=licence");
    await expect(fact(page, "Licensed to")).toHaveText("Contoso GmbH");
    await expect(page.getByLabel("New licence file")).toHaveCount(0);

    for (const view of [offer(), scheduled(), stopped(), done()]) {
      await mock(request, { "GET /service/updates": view });
      await page.goto("/service/?tab=updates");
      await opened(page);
      await expect(panelHeading(page, "Update history")).toBeVisible();
      await noActions(page);
    }
    await expect(fact(page, "Kept for return")).toHaveText("1.9.0");
    await mock(request, { "GET /service/updates": stopped() });
    await page.goto("/service/?tab=updates");
    await expect(page.getByRole("button", { name: "Copy details for Balsoft support" })).toBeEnabled();
    await mock(request, { "GET /service/updates": offer() });
    await page.goto("/service/?tab=updates");
    await expect(page.locator(".service-notes li").first()).toHaveText("DocuWare date fields take German dates.");
    await page.goto("/");
    await opened(page);
    await expect(updateLine(page)).toBeVisible();
    await expect(updateLine(page).getByRole("link", { name: "What's new" })).toBeVisible();
    await noActions(page);
  });
}

const states = [
  { name: "idle", view: undefined },
  { name: "look failed", view: lookFailed() },
  { name: "offer", view: offer() },
  { name: "scheduled", view: scheduled() },
  { name: "cannot update", view: cannotUpdate() },
  { name: "running", view: running() },
  { name: "returning", view: returning() },
  { name: "stopped", view: stopped() },
  { name: "returned", view: returnedByItself() },
  { name: "done", view: done() },
];

/** Opens the Updates tab with one state of GET /service/updates and waits until it is drawn. */
async function openUpdates(page: Page, request: Parameters<typeof mock>[0], view: unknown) {
  if (view) await mock(request, { "GET /service/updates": view });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(page.locator(".service-updates")).toBeVisible();
}

test("every tab and update state shows one primary button at most, list rows carry no buttons, icons are 16 px", async ({ page, request }) => {
  const icons = async (where: string) => {
    const wrong = await page
      .locator("main .service-panel svg, main .update-line svg")
      .evaluateAll((nodes) =>
        nodes
          .filter((n) => !n.closest(".drop-zone, .data-grid thead"))
          .filter((n) => n.getAttribute("width") !== "16" || n.getAttribute("stroke-width") !== "1.75")
          .map((n) => n.getAttribute("class") ?? ""),
      );
    expect(wrong, `icons not 16 px at 1.75 on ${where}`).toEqual([]);
  };
  await mock(request, { "POST /setup/test-run": testRun });
  for (const tab of tabs) {
    await page.goto(tab.path);
    await opened(page);
    await expect(panelHeading(page, tab.heading)).toBeVisible();
    await onePrimaryAtMost(page);
    await noRowButtons(page);
    await icons(tab.path);
    await expect(page.locator("main a:not([href])")).toHaveCount(0);
  }
  await page.goto("/service/");
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("region", { name: "Steps of the test run" })).toBeVisible();
  await onePrimaryAtMost(page);
  await icons("the test run");
  for (const state of states) {
    await openUpdates(page, request, state.view);
    await onePrimaryAtMost(page);
    await noRowButtons(page);
    await icons(`updates/${state.name}`);
    await expect(page.locator("main a:not([href])")).toHaveCount(0);
    const primary = page.locator("main .button.primary");
    if (state.name === "offer" || state.name === "scheduled") await expect(primary).toHaveText("Update now");
    else await expect(primary).toHaveCount(0);
  }
  await mock(request, { "GET /service/updates": offer() });
  await page.goto("/");
  await expect(updateLine(page)).toBeVisible();
  await onePrimaryAtMost(page);
  await icons("the overview");
});

for (const width of [1280, 390]) {
  test(`no horizontal scroll on any tab at ${width} px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    await mock(request, { "POST /setup/test-run": testRun });
    for (const tab of tabs) {
      await page.goto(tab.path);
      await opened(page);
      await expect(panelHeading(page, tab.heading)).toBeVisible();
      await noSideScroll(page);
    }
    await page.goto("/service/");
    await page.getByRole("button", { name: "Run the test" }).click();
    await expect(page.getByRole("region", { name: "Steps of the test run" })).toBeVisible();
    await noSideScroll(page);
    for (const state of states) {
      await openUpdates(page, request, state.view);
      await noSideScroll(page);
    }
    await mock(request, { "GET /service/updates": offer() });
    await page.goto("/");
    await expect(updateLine(page)).toBeVisible();
    await noSideScroll(page);
  });
}

async function axe(page: Page) {
  // Fluent UI's focus manager puts hidden sentinels on the page; the CRM excludes them the same way.
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]").analyze();
  return result.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

async function axeEverywhere(page: Page, request: Parameters<typeof mock>[0]) {
  const found: string[] = [];
  await mock(request, { "POST /setup/test-run": testRun });
  for (const tab of tabs) {
    await page.goto(tab.path);
    await opened(page);
    await expect(panelHeading(page, tab.heading)).toBeVisible();
    found.push(...(await axe(page)).map((f) => `${tab.path} ${f}`));
  }
  await page.goto("/service/");
  await page.getByRole("button", { name: "Run the test" }).click();
  await expect(page.getByRole("region", { name: "Steps of the test run" })).toBeVisible();
  found.push(...(await axe(page)).map((f) => `test run ${f}`));
  for (const state of states) {
    await openUpdates(page, request, state.view);
    found.push(...(await axe(page)).map((f) => `updates/${state.name} ${f}`));
  }
  await mock(request, { "GET /service/updates": offer() });
  await page.goto("/");
  await expect(updateLine(page)).toBeVisible();
  found.push(...(await axe(page)).map((f) => `overview ${f}`));
  return found;
}

test("every tab and every update state passes the accessibility check", async ({ page, request }) => {
  expect(await axeEverywhere(page, request)).toEqual([]);
});

test("every tab and every update state passes the accessibility check in the dark theme", async ({ page, request }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem("hivebdc-theme", "dark");
    } catch {
      // The test then runs in the light theme and the assertion below says so.
    }
  });
  await page.goto("/service/");
  await opened(page);
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  expect(await axeEverywhere(page, request)).toEqual([]);
});
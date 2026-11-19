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
import { calls, mock, noSideScroll, opened, watchCsp } from "./helpers";

// The Service page (design 2026-11-19-web-console-nextjs.md part 6; former service.js and updates.js): the tabs
// Status, Licence, Updates and Technical log, the update notice of the Overview, and failure cases 1, 2, 7, 11, 13.

const tabs = [
  { path: "/service/", name: "Status", heading: "Taking documents" },
  { path: "/service/?tab=licence", name: "Licence", heading: "Licence" },
  { path: "/service/?tab=updates", name: "Updates", heading: "Installed" },
  { path: "/service/?tab=log", name: "Technical log", heading: "Technical log" },
];

const tabBar = (page: Page) => page.getByRole("navigation", { name: "Service sections" });
const panelHeading = (page: Page, name: string) => page.getByRole("heading", { level: 2, name, exact: true });
// Next.js keeps an empty route announcer with role alert on every page.
const alert = (page: Page) => page.locator("[role='alert']:not(#__next-route-announcer__)");
const history = (page: Page) => page.getByRole("region", { name: "Version changes" });

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
  await mock(request, { "GET /logs?tail=200": logs, "GET /logs?tail=5000": fullLog });
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

test("?tab=licence opens the Licence tab directly and a reload keeps it; the tabs switch by link", async ({ page }) => {
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(tabBar(page).getByRole("link", { name: "Licence" })).toHaveAttribute("aria-current", "page");
  await expect(panelHeading(page, "Licence")).toBeVisible();
  await expect(page.locator(".service-panel .badge")).toHaveText("Valid until 30.09.2027");
  await expect(page.getByText("Contoso GmbH · e-invoice, documents · checked against tenant 00000000")).toBeVisible();
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

test("the Overview's licence tile opens the Licence tab, an unknown tab opens Status", async ({ page }) => {
  await page.goto("/");
  await opened(page);
  await page.getByRole("link", { name: /Licence valid until/ }).click();
  await expect(page).toHaveURL(/\/service\/\?tab=licence$/);
  await expect(panelHeading(page, "Licence")).toBeVisible();
  await page.goto("/service/?tab=nonsense");
  await expect(tabBar(page).getByRole("link", { name: "Status" })).toHaveAttribute("aria-current", "page");
});

test("Status shows version and identity from the installation", async ({ page }) => {
  await page.goto("/service/");
  await opened(page);
  await expect(page.getByText("Checks every source every 30 seconds.", { exact: false })).toBeVisible();
  await expect(page.locator(".service-panel .badge")).toHaveText("Running");
  const facts = page.locator(".service-facts");
  await expect(facts).toContainText("1.9.0");
  await expect(facts).toContainText("germanywestcentral");
  await expect(facts).toContainText("Europe/Berlin (from the installation)");
  await expect(facts).toContainText("00000000-0000-0000-0000-000000000001");
  await expect(page.getByRole("button", { name: "Copy", exact: true })).toBeVisible();
});

test("Pause sends POST /setup/pause, then the page offers Start, which sends POST /setup/start", async ({ page, request }) => {
  await mock(request, { "POST /setup/pause": { ok: true, paused: true }, "POST /setup/start": { ok: true, paused: false } });
  await page.goto("/service/");
  await opened(page);
  await mock(request, { "GET /config": { ...config(), paused: true } });
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await expect(page.getByRole("button", { name: "Start", exact: true })).toBeVisible();
  await expect(page.locator(".service-panel .badge").first()).toHaveText("Paused");

  await mock(request, { "GET /config": config() });
  await page.getByRole("button", { name: "Start", exact: true }).click();
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
  await expect(page.getByLabel("Company for the test run").locator("option")).toHaveText(["sales invoices · company 1000"]);
  await page.getByRole("button", { name: "Run the test" }).click();
  const card = page.getByRole("region", { name: "Steps of the test run" });
  await expect(card.getByRole("heading", { name: "SI-0043 · sales invoices · company 1000" })).toBeVisible();
  await expect(card).toContainText("Done");
  await expect(card).toContainText("One document went all the way through.");
  await expect(card.locator("li")).toHaveText([/09:01:02.*Taken from the Blob container bdc-export\./, /09:01:03.*E-invoice made\./, /09:01:05.*Delivered/]);
  expect(await calls(request)).toEqual([{ method: "POST", path: "/setup/test-run", body: { documentType: "SalesInvoice", company: "1000" } }]);

  await mock(request, { "POST /setup/test-run": { __status: 409, ok: false, message: "busy" } });
  await page.getByRole("button", { name: "Run the test" }).click();
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
  await expect(page.getByText("Renewal or another file:")).toBeVisible();
  await expect(page.getByRole("button", { name: "Choose file" })).toBeVisible();
  const input = page.getByLabel("Renewal or another file");
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

test("a licence that warns shows its message on the Licence tab", async ({ page, request }) => {
  const status = (await (await request.get("/api/status")).json()) as Record<string, any>;
  await mock(request, {
    "GET /status": { ...status, license: { ...status.license, state: "Grace", message: "The licence ended on 30.09.2026; documents still go through until 30.10.2026." } },
  });
  await page.goto("/service/?tab=licence");
  await opened(page);
  await expect(page.locator(".notice-warn")).toHaveText("The licence ended on 30.09.2026; documents still go through until 30.10.2026.");
});

test("an offered update shows Update now on the Overview, which starts it and opens the Updates tab", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/");
  await opened(page);
  const notice = page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" });
  await expect(notice).toContainText("DocuWare date fields take German dates. Your settings are kept, and 1.9.0 stays ready to return to.");
  await expect(notice.getByRole("link", { name: "What's new" })).toHaveAttribute("href", "/service/?tab=updates");
  await expect(page.getByRole("navigation", { name: "Main navigation" }).getByText("update available")).toHaveCount(1);
  await notice.getByRole("button", { name: "Update now" }).click();
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
  await expect(page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" })).toContainText("Planned for 22:00");
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toHaveCount(0);
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

test("the Updates tab offers the new version with what happens, and Update now sends the right POST", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": offer(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(tabBar(page).getByRole("link", { name: "Updates" })).toContainText("1");
  await expect(panelHeading(page, "Hive BDC 1.9.1")).toBeVisible();
  await expect(page.getByText("Released 18.11.2026 · 23 MB · you have 1.9.0")).toBeVisible();
  await expect(page.locator(".service-notes li")).toHaveText(["DocuWare date fields take German dates.", "The test run names the SharePoint column it could not fill."]);
  await expect(page.locator(".service-happens li")).toHaveText(["1Download from Balsoft", "2Check Balsoft's signature", "3Back up the settings", "4Switch to 1.9.1", "5Restart, 2 to 4 minutes"]);
  await expect(page.getByText("Documents wait in their source while it restarts; nothing is lost. 1.9.0 stays ready, so you can return to it with one click.")).toBeVisible();
  await expect(panelHeading(page, "Installed")).toBeVisible();
  await expect(page.locator(".service-pairs")).toContainText("Last looked19.11.2026 07:01");
  await expect(history(page).getByRole("row")).toHaveCount(2);
  await expect(history(page)).toContainText("Running");

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
  await mock(request, { "GET /service/updates": scheduled() });
  await page.getByRole("button", { name: "Tonight at 22:00" }).click();
  await expect(page.getByText("Planned for 19.11.2026 22:00 by Anna Weber.")).toBeVisible();
  await mock(request, { "GET /service/updates": offer() });
  await page.getByRole("button", { name: "Cancel the plan" }).click();
  await expect(page.getByText("The plan is cancelled.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Tonight at 22:00" })).toBeVisible();
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
  await expect(alert(page)).toContainText("The update to 1.9.1 stopped. Nothing changed.");
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
  await expect(panelHeading(page, "While it runs")).toBeVisible();
  await expect(page.getByText("1.9.0 stays in place. If 1.9.1 does not suit you, return to it with one click afterwards.")).toBeVisible();
  await expect(panelHeading(page, "Update history")).toHaveCount(0);

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
  await expect(page.locator(".instance-version")).toContainText("1.9.1");
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

test("a stopped update shows what happened, copies the details and offers Try again", async ({ page, request, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mock(request, { "GET /service/updates": stopped(), "POST /service/updates/run": { ok: true, message: "The update to 1.9.1 started." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  const banner = alert(page);
  await expect(banner).toContainText("The update to 1.9.1 stopped. Nothing changed.");
  await expect(banner).toContainText("1.9.0 runs with your settings, and documents flow as before.");
  await expect(panelHeading(page, "What happened")).toBeVisible();
  await expect(page.getByText("19.11.2026 16:31 · Anna Weber")).toBeVisible();
  const steps = page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem");
  await expect(steps.nth(1)).toContainText("Signature does not match");
  await expect(steps.nth(2)).toContainText("not started");
  await expect(history(page)).toContainText("Stopped");
  await expect(page.locator(".service-panel .warn-text")).toHaveCount(0);

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
  await expect(alert(page)).toContainText("1.9.1 did not run properly. Hive BDC is back on 1.9.0.");
  const last = page.getByRole("list", { name: "Steps of the update" }).getByRole("listitem").last();
  await expect(last).toContainText("1.9.1 did not start properly");
  await expect(last).toContainText("Returned to 1.9.0 by itself: 1.9.1 did not start properly 3 times.");
  await expect(history(page)).toContainText("Returned");
});

test("a finished update shows its checks, and Return to the previous version asks first and sends the POST", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": done(), "POST /service/updates/return": { ok: true, message: "Hive BDC returns to 1.9.0 and restarts." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(page.getByRole("status").filter({ hasText: "Hive BDC runs 1.9.1 since 16:34" })).toContainText("Updated from 1.9.0.");
  const checks = page.getByRole("region", { name: "Checks after the restart" });
  await expect(checks.getByRole("row")).toHaveCount(4);
  await expect(checks).toContainText("Sources answer");
  await expect(checks).toContainText("16:34");
  await expect(page.getByText("1.9.1 checks these itself before it calls the update done.")).toBeVisible();
  await expect(history(page)).toContainText("Kept for return");

  await page.getByRole("button", { name: "Return to 1.9.0" }).click();
  const confirm = alert(page).filter({ hasText: "Hive BDC restarts" });
  await expect(confirm).toContainText("Return to 1.9.0? Hive BDC restarts and is away for about 2 minutes.");
  await confirm.getByRole("button", { name: "Keep" }).click();
  await expect(confirm).toHaveCount(0);
  expect(await calls(request)).toEqual([]);

  await page.getByRole("button", { name: "Return to 1.9.0" }).click();
  await mock(request, { "GET /service/updates": returning() });
  await alert(page).filter({ hasText: "Hive BDC restarts" }).getByRole("button", { name: "Return to 1.9.0" }).click();
  await expect(page.getByText("Hive BDC returns to 1.9.0 and restarts.")).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/return", body: {} }]);
});

test("Look now asks the feed and shows its answer", async ({ page, request }) => {
  await mock(request, { "POST /service/updates/check": { ok: true, message: "1.9.0 is the newest version." } });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(panelHeading(page, "Hive BDC 1.9.0")).toBeVisible();
  await expect(page.locator(".service-body p.muted").first()).toHaveText("1.9.0 is the newest version.");
  await page.getByRole("button", { name: "Look now" }).first().click();
  await expect(page.getByRole("status").filter({ hasText: "1.9.0 is the newest version." }).first()).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/service/updates/check", body: {} }]);
});

test("an instance that cannot update says why and offers no Update now", async ({ page, request }) => {
  await mock(request, { "GET /service/updates": cannotUpdate() });
  await page.goto("/service/?tab=updates");
  await opened(page);
  await expect(page.getByText("This instance runs from a package that Balsoft does not update; ask the person who installed it.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Update now" })).toHaveCount(0);
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
    await expect(page.getByRole("button", { name: "Copy", exact: true })).toBeEnabled();
    await page.goto("/service/?tab=licence");
    await expect(page.getByLabel("Renewal or another file")).toHaveCount(0);

    for (const view of [offer(), scheduled(), stopped(), done()]) {
      await mock(request, { "GET /service/updates": view });
      await page.goto("/service/?tab=updates");
      await opened(page);
      await expect(panelHeading(page, "Update history")).toBeVisible();
      await noActions(page);
    }
    await expect(panelHeading(page, "Return to 1.9.0")).toBeVisible();
    await mock(request, { "GET /service/updates": stopped() });
    await page.goto("/service/?tab=updates");
    await expect(page.getByRole("button", { name: "Copy details for Balsoft support" })).toBeEnabled();
    await mock(request, { "GET /service/updates": offer() });
    await page.goto("/service/?tab=updates");
    await expect(page.locator(".service-notes li").first()).toHaveText("DocuWare date fields take German dates.");
    await page.goto("/");
    await opened(page);
    await expect(page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" })).toBeVisible();
    await noActions(page);
  });
}

const states = [
  { name: "offer", view: offer() },
  { name: "running", view: running() },
  { name: "stopped", view: stopped() },
  { name: "done", view: done() },
];

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
      await mock(request, { "GET /service/updates": state.view });
      await page.goto("/service/?tab=updates");
      await opened(page);
      await expect(page.locator(".service-update-grid")).toBeVisible();
      await noSideScroll(page);
    }
    await mock(request, { "GET /service/updates": offer() });
    await page.goto("/");
    await expect(page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" })).toBeVisible();
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

test("every tab and every update state passes the accessibility check", async ({ page, request }) => {
  const found: string[] = [];
  for (const tab of tabs) {
    await page.goto(tab.path);
    await opened(page);
    await expect(panelHeading(page, tab.heading)).toBeVisible();
    found.push(...(await axe(page)).map((f) => `${tab.path} ${f}`));
  }
  for (const state of [...states, { name: "returned", view: returnedByItself() }]) {
    await mock(request, { "GET /service/updates": state.view });
    await page.goto("/service/?tab=updates");
    await opened(page);
    await expect(page.locator(".service-update-grid")).toBeVisible();
    found.push(...(await axe(page)).map((f) => `updates/${state.name} ${f}`));
  }
  await mock(request, { "GET /service/updates": offer() });
  await page.goto("/");
  await expect(page.getByRole("region", { name: "Hive BDC 1.9.1 is ready" })).toBeVisible();
  found.push(...(await axe(page)).map((f) => `overview ${f}`));
  expect(found).toEqual([]);
});

test("the Service page passes the accessibility check in the dark theme", async ({ page, request }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem("hivebdc-theme", "dark");
    } catch {
      // The test then runs in the light theme and the assertion below says so.
    }
  });
  const found: string[] = [];
  for (const state of [offer(), stopped(), done()]) {
    await mock(request, { "GET /service/updates": state });
    await page.goto("/service/?tab=updates");
    await opened(page);
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await expect(page.locator(".service-update-grid")).toBeVisible();
    found.push(...(await axe(page)));
  }
  await page.goto("/service/");
  await opened(page);
  found.push(...(await axe(page)));
  expect(found).toEqual([]);
});
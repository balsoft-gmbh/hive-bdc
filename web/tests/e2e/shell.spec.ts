import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { config } from "./fixtures";
import { calls, mock, noRowButtons, noSideScroll, onePrimaryAtMost, opened, watchCsp } from "./helpers";

// The shell, the Overview and the Document history (design 2026-11-19-web-console-nextjs.md, failure cases 1, 2, 6,
// 7, 9, 10, 11, 13).

const pages = ["/", "/history/", "/outbound/", "/connections/", "/service/"];

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/reset");
});

test("every page loads under the host's content security policy without a violation", async ({ page }) => {
  const violations = await watchCsp(page);
  for (const path of pages) {
    await page.goto(path);
    await opened(page);
  }
  expect(violations).toEqual([]);
});

test("the logo and the name Hive BDC lead to the overview from every page", async ({ page }) => {
  for (const path of pages.slice(1)) {
    await page.goto(path);
    await opened(page);
    const brand = page.getByRole("link", { name: "Hive BDC, back to the overview" });
    await expect(brand).toContainText("Hive BDC");
    await expect(brand.locator("svg")).toBeVisible();
    await brand.click();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("heading", { name: "Needs attention" })).toBeVisible();
  }
});

test("a reload on a deep link opens that page with its query", async ({ page }) => {
  await page.goto("/history/?search=SI-0041");
  await opened(page);
  await expect(page.getByRole("heading", { name: "Document history" })).toBeVisible();
  await expect(page.getByLabel("Search", { exact: true })).toHaveValue("SI-0041");
  await page.reload();
  await expect(page.getByLabel("Search", { exact: true })).toHaveValue("SI-0041");
});

test("an unknown address shows the not-found page with a way back", async ({ page }) => {
  const response = await page.goto("/no-such-page/");
  expect(response?.status()).toBe(404);
  await expect(page.getByRole("heading", { name: "This page does not exist" })).toBeVisible();
  await page.getByRole("link", { name: "Back to the overview", exact: true }).click();
  await expect(page).toHaveURL(/\/$/);
});

test("the navigation icons sit in one column, so every label starts on the same line", async ({ page }) => {
  await page.goto("/");
  await opened(page);
  const lefts = await page.locator("nav[aria-label='Main navigation'] .nav-text").evaluateAll((nodes) =>
    nodes.map((n) => Math.round(n.getBoundingClientRect().left)),
  );
  expect(lefts.length).toBe(6);
  expect(new Set(lefts).size).toBe(1);
});

test("an operator retries a document from the overview", async ({ page, request }) => {
  await page.goto("/");
  await opened(page);
  await page.getByRole("button", { name: "Retry" }).click();
  await expect(page.getByText("Back in the queue.")).toBeVisible();
  expect(await calls(request)).toEqual([{ method: "POST", path: "/history/retry", body: { documentId: "SI-0041" } }]);
});

test("a viewer sees no Retry", async ({ page, request }) => {
  await mock(request, { "GET /me": { name: "Vera Viewer", roles: ["Viewer"], canRetry: false, canChange: false, version: "1.9.0" } });
  await page.goto("/");
  await opened(page);
  await expect(page.getByText("The container answered 403: the instance has no data role on it.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry" })).toHaveCount(0);
});

test("a fresh instance opens the set-up assistant full screen, and only until it is set up", async ({ page, request }) => {
  const fresh = { ...config(), setupOpen: true, documentTypes: config().documentTypes.map((t) => ({ ...t, companies: [] })) };
  await mock(request, { "GET /config": fresh });
  await page.goto("/history/");
  await expect(page).toHaveURL(/\/setup\/$/);
  await expect(page.getByRole("navigation", { name: "Main navigation" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Hive BDC, back to the overview" })).toBeVisible();

  await request.post("/__mock/reset");
  await page.goto("/setup/");
  await expect(page).toHaveURL(/127\.0\.0\.1:\d+\/$/);
  await expect(page.getByRole("navigation", { name: "Main navigation" })).toBeVisible();
});

test("an ended session reloads once to sign in again, then says so instead of reloading for ever", async ({ page, request }) => {
  await page.goto("/");
  await opened(page);
  await mock(request, { "GET /status": { __status: 401, error: "Signed out" } });
  let loads = 0;
  page.on("load", () => loads++);
  await page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Document history" }).click();
  await expect(page.locator(".error-banner")).toContainText("Signing in did not work. Reload the page to sign in again.");
  await page.waitForTimeout(1500);
  expect(loads).toBe(1);
});

for (const width of [1280, 390]) {
  test(`long names stay inside the screen at ${width} px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 });
    await mock(request, {
      "GET /me": {
        name: "maximiliane.alexandra.von.hohenzollern-sigmaringen@a-very-long-company-name.example",
        roles: ["Administrator"],
        canRetry: true,
        canChange: true,
        version: "1.9.0",
      },
    });
    for (const path of pages) {
      await page.goto(path);
      await opened(page);
      await noSideScroll(page);
    }
  });
}

test("overview and document history pass the accessibility check", async ({ page }) => {
  for (const path of ["/", "/history/"]) {
    await page.goto(path);
    await opened(page);
    // Fluent UI's focus manager puts hidden sentinels on the page; the CRM excludes them the same way.
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).exclude("[data-tabster-dummy]").analyze();
    const serious = result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
  }
});

test("overview and document history follow the CRM: one primary button at most, no buttons in rows but Retry", async ({ page }) => {
  for (const path of ["/", "/history/"]) {
    await page.goto(path);
    await opened(page);
    await onePrimaryAtMost(page);
    await noRowButtons(page);
  }
  await page.goto("/");
  await opened(page);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Overview");
  await expect(page.locator(".stats-grid .stat-card")).toHaveCount(3);
  await page.getByRole("link", { name: "SI-0041" }).click();
  await expect(page).toHaveURL(/\/history\/\?search=SI-0041$/);
});

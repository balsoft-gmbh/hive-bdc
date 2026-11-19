import { expect, type APIRequestContext, type Page } from "@playwright/test";

// Shared steps of the end-to-end tests against tests/e2e/serve.ts.

/** Replaces answers of the mocked API, keyed "METHOD /path" (optionally with the query); __status sets the code. */
export async function mock(request: APIRequestContext, answers: Record<string, unknown>) {
  const response = await request.post("/__mock/state", { data: answers });
  expect(response.ok()).toBe(true);
}

/** The non-GET calls the console made since the last reset. */
export async function calls(request: APIRequestContext): Promise<{ method: string; path: string; body: any }[]> {
  return (await request.get("/__mock/calls")).json();
}

/** Collects every content security policy violation of the page (failure case 1). */
export async function watchCsp(page: Page) {
  const violations: string[] = [];
  page.on("console", (message) => {
    if (/Content Security Policy/i.test(message.text())) violations.push(message.text());
  });
  await page.addInitScript(() => {
    document.addEventListener("securitypolicyviolation", (event) => {
      console.error(`Content Security Policy violation: ${event.violatedDirective} ${event.blockedURI}`);
    });
  });
  return violations;
}

/** Waits until the console has read the instance (the shell shows the page heading). */
export async function opened(page: Page) {
  await expect(page.locator("main h1").first()).toBeVisible();
}

/** No horizontal scroll at the current width (failure case 11). */
export async function noSideScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, "horizontal overflow in px").toBeLessThanOrEqual(1);
}

/** At most one primary (gold) button is visible on the screen (design version 2, V2). */
export async function onePrimaryAtMost(page: Page) {
  const visible = await page.locator(".button.primary").evaluateAll((nodes) =>
    nodes.filter((n) => (n as HTMLElement).offsetParent !== null).map((n) => (n.textContent ?? "").trim()),
  );
  expect(visible.length, `primary buttons: ${visible.join(", ")}`).toBeLessThanOrEqual(1);
}

/** List rows carry no action buttons except Retry; the record opens from its name (design version 2, V2). */
export async function noRowButtons(page: Page) {
  const buttons = await page.locator(".data-grid tbody tr button").evaluateAll((nodes) =>
    nodes.map((n) => (n.getAttribute("aria-label") || n.textContent || "").trim()).filter((t) => t !== "Retry"),
  );
  expect(buttons, "buttons in list rows").toEqual([]);
}

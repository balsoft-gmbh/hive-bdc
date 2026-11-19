import { defineConfig, devices } from "@playwright/test";

// End-to-end tests against the static export (npm run build first), served with the host's content security policy
// and a mocked API by tests/e2e/serve.ts. One worker: the mock state is shared.
const port = Number(process.env.E2E_PORT ?? 3310);

export default defineConfig({
  testDir: "tests/e2e",
  timeout: 30_000,
  fullyParallel: false,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    viewport: { width: 1280, height: 800 },
    trace: "off",
  },
  webServer: {
    command: "npx tsx tests/e2e/serve.ts",
    url: `http://127.0.0.1:${port}/__mock/health`,
    reuseExistingServer: !process.env.CI,
  },
  // Locally the installed Google Chrome (no browser download on the Macs); CI installs Playwright's Chromium.
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 }, ...(process.env.CI ? {} : { channel: "chrome" }) },
    },
  ],
});

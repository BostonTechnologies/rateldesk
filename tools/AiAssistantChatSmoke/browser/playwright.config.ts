import { defineConfig } from '@playwright/test';

const baseURL = process.env.HELPDESK_E2E_BASE_URL;
if (!baseURL || new URL(baseURL).origin !== baseURL || new URL(baseURL).hostname !== '127.0.0.1')
  throw new Error('Native scheduled-report acceptance requires an explicit disposable loopback Web URL.');

export default defineConfig({
  testDir: '.',
  testMatch: 'scheduled-reports.spec.ts',
  timeout: 120_000,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 960 },
    launchOptions: { executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH }
  }
});

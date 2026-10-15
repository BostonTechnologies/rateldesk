import { defineConfig } from '@playwright/test';
import admin from './playwright.admin.config';
import details from './playwright.details.config';
import email from './playwright.email.config';
import filters from './playwright.ticket-filters.config';
import operations from './playwright.operations.config';
import pending from './playwright.pending-mail.config';
const suites = { admin, details, email, 'ticket-filters': filters, operations, 'pending-mail': pending };
const suite = process.env.HELPDESK_NATIVE_ZOOM_SUITE as keyof typeof suites;
if (!suite || !suites[suite]) throw new Error('Set HELPDESK_NATIVE_ZOOM_SUITE to an owning workspace suite.');
const owner = suites[suite];
export default defineConfig({
  testDir: './tests/ux', testMatch: '**/native-zoom.spec.ts', workers: 1, retries: 0,
  timeout: 180_000, expect: { timeout: 15_000 }, forbidOnly: Boolean(process.env.CI),
  reporter: [['list'], ['json', { outputFile: `test-results/native-zoom-${suite}.json` }]],
  use: { baseURL: owner.use?.baseURL, ignoreHTTPSErrors: true },
  // Only the fixture API and administrator host are needed for geometry checks.
  webServer: (owner.webServer as any[]).slice(0, 2)
});

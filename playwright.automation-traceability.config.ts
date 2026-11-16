import { defineConfig } from '@playwright/test';
import admin from './playwright.admin.config';
import details from './playwright.details.config';
export default defineConfig({
 testDir: './tests/ux', testMatch: '**/automation-traceability.spec.ts', workers: 1, retries: 0,
 timeout: 90_000, expect: { timeout: 15_000 },
 webServer: [...(admin.webServer as any[]).slice(0, 2), ...(details.webServer as any[]).slice(0, 2)],
 reporter: [['list'], ['json', { outputFile: process.env.RATELDESK_TRACE_REPORT ?? 'test-results/automation-traceability.json' }]],
 outputDir: process.env.RATELDESK_TRACE_OUTPUT ?? 'test-results/automation-traceability',
 use: { baseURL: 'https://127.0.0.1:5180', ignoreHTTPSErrors: true,
 launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH }, trace: 'off' }
});

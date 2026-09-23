import { defineConfig } from '@playwright/test';

const testIgnore = process.env.HELPDESK_E2E_RUN_SETUP_WIZARD === 'true'
  ? '**/ai-assistant-chat.spec.ts'
  : ['**/ai-assistant-chat.spec.ts', '**/setup.spec.ts',
    ...(process.env.HELPDESK_E2E_MAILBOX_LIFECYCLE === 'true' ? [] : [
      '**/mailbox-lifecycle.spec.ts', '**/mailbox-pop-lifecycle.spec.ts',
      '**/mailbox-policy-lifecycle.spec.ts'])];

export default defineConfig({
  testDir: './tests/ux',
  testIgnore,
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.HELPDESK_E2E_BASE_URL ?? 'http://127.0.0.1:5157',
    viewport: { width: 1440, height: 900 },
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true',
    launchOptions: process.env.HELPDESK_E2E_CHROMIUM_PATH
      ? { executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH }
      : undefined,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure'
  }
});

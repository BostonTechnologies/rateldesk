import { defineConfig } from '@playwright/test';
const published = process.env.HELPDESK_CARDS_WEB_CWD;
const cwd = published ?? 'src/HelpDesk.NewWeb';
export default defineConfig({
  testDir: './tests/ux', testMatch: '**/premium-cards.spec.ts', fullyParallel: false, workers: 1, retries: 0, timeout: 180_000, expect: { timeout: 30_000 },
  reporter: [['list'], ['json', { outputFile: 'test-results/premium-cards-results.json' }]],
  use: { baseURL: 'https://127.0.0.1:5250', ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, trace: 'off', screenshot: 'only-on-failure', launchOptions: process.env.HELPDESK_E2E_CHROMIUM_PATH ? { executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH } : undefined },
  webServer: [
    { command: 'node tests/ux/premium-cards-fixture-api.mjs', url: 'http://127.0.0.1:18720/fixture/health', reuseExistingServer: false },
    { command: published ? 'dotnet HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd, url: 'https://127.0.0.1:5250/health', ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: false,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5250', ApiBaseUrl: 'http://127.0.0.1:18720/', DevelopmentOperator__Enabled: 'true', SYSTEM_TOKEN_SECRET: 'premium-cards-fixture', Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' } }
  ]
});

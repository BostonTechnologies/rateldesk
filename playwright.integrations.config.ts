import { defineConfig } from '@playwright/test';
const cwd = process.env.HELPDESK_INTEGRATIONS_WEB_CWD ?? 'src/HelpDesk.NewWeb';
const configuration = process.env.HELPDESK_INTEGRATIONS_CONFIGURATION ?? 'Debug';
export default defineConfig({
 testDir: './tests/ux', testMatch: '**/integrations-workspace.fixture.ts', fullyParallel: false, workers: 1, retries: 0, timeout: 60_000,
 reporter: [['list'], ['json', { outputFile: 'test-results/integrations-results.json' }]],
 use: { baseURL: 'https://127.0.0.1:5190', ignoreHTTPSErrors: true, viewport: { width: 1366, height: 768 }, trace: 'off', screenshot: 'only-on-failure', launchOptions: process.env.HELPDESK_E2E_CHROMIUM_PATH ? { executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH } : undefined },
 webServer: [
  { command: 'node tests/ux/integrations-fixture-api.mjs', url: 'http://127.0.0.1:18505/fixture/health', reuseExistingServer: !process.env.CI },
  ...['admin', 'reader'].map((mode, index) => ({
   command: `dotnet bin/${configuration}/net10.0/HelpDesk.NewWeb.dll`, cwd, url: `https://127.0.0.1:${5190 + index}/health`, ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: !process.env.CI,
   env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `https://127.0.0.1:${5190 + index}`, ApiBaseUrl: 'http://127.0.0.1:18505/', DevelopmentOperator__Enabled: 'true', SYSTEM_TOKEN_SECRET: `integrations-fixture-${mode}`, Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
  }))
 ]
});

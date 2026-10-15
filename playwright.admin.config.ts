import { defineConfig } from '@playwright/test';
const cwd = process.env.HELPDESK_ADMIN_WEB_CWD ?? 'src/HelpDesk.NewWeb';
const configuration = process.env.HELPDESK_ADMIN_CONFIGURATION ?? 'Debug';
export default defineConfig({
 testDir: './tests/ux', testMatch: '**/admin-workspaces.fixture.ts', fullyParallel: false, workers: 1, retries: 0, timeout: 60_000,
 reporter: [['list'], ['json', { outputFile: 'test-results/admin-results.json' }]],
 use: { baseURL: 'https://127.0.0.1:5180', ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, trace: 'off', screenshot: 'only-on-failure' },
 webServer: [
  { command: 'node tests/ux/admin-fixture-api.mjs', url: 'http://127.0.0.1:18500/fixture/health', reuseExistingServer: !process.env.CI },
  ...['admin', 'delegate', 'reader'].map((mode, index) => ({
   command: `dotnet bin/${configuration}/net10.0/HelpDesk.NewWeb.dll`, cwd, url: `https://127.0.0.1:${5180 + index}/health`, ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: !process.env.CI,
   env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `https://127.0.0.1:${5180 + index}`, ApiBaseUrl: 'http://127.0.0.1:18500/', DevelopmentOperator__Enabled: 'true', SYSTEM_TOKEN_SECRET: `admin-fixture-${mode}`, Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
  }))
 ]
});

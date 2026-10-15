import { defineConfig } from '@playwright/test';
import base from './playwright.config';
export default defineConfig(base, {
  testMatch: ['**/ticket-details.spec.ts'], testIgnore: [], retries: 0, workers: 1,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5190', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/ticket-details-fixture-api.mjs', url: 'http://127.0.0.1:18600/fixture/health', reuseExistingServer: !process.env.CI },
    ...[5190, 5191].map(port => ({
      command: process.env.HELPDESK_DETAILS_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd: 'src/HelpDesk.NewWeb',
      url: `https://127.0.0.1:${port}/health`, ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `https://127.0.0.1:${port}`,
        ApiBaseUrl: 'http://127.0.0.1:18600/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: port === 5191 ? 'local-details-reader-fixture' : 'local-details-fixture',
        Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
    }))
  ]
});

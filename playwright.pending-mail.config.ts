import { defineConfig } from '@playwright/test';
import base from './playwright.config';
export default defineConfig(base, {
  testMatch: ['**/pending-mail.spec.ts'], testIgnore: [], retries: 0, workers: 1,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5210', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/pending-mail-fixture-api.mjs', url: 'http://127.0.0.1:18700/fixture/health', reuseExistingServer: !process.env.CI },
    { command: `dotnet bin/${process.env.HELPDESK_PENDING_MAIL_CONFIGURATION ?? 'Debug'}/net10.0/HelpDesk.NewWeb.dll`, cwd: 'src/HelpDesk.NewWeb',
      url: 'https://127.0.0.1:5210/health', ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5210', ApiBaseUrl: 'http://127.0.0.1:18700/',
        DevelopmentOperator__Enabled: 'true', SYSTEM_TOKEN_SECRET: 'local-pending-synthetic', Authentication__Authentik__ClientId: 'ux-local',
        AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' } }
  ]
});

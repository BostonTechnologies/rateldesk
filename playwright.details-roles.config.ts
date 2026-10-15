import { defineConfig } from '@playwright/test';
import base from './playwright.config';

export default defineConfig(base, {
  timeout: 45_000, expect: { timeout: 10_000 },
  testMatch: ['**/ticket-details-roles.spec.ts'], testIgnore: [], retries: 0, workers: 1,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5194', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/ticket-details-fixture-api.mjs', url: 'http://127.0.0.1:18620/fixture/health',
      env: { HELPDESK_DETAILS_FIXTURE_PORT: '18620' }, reuseExistingServer: !process.env.CI },
    ...['contributor', 'manager'].map((role, index) => ({
      command: `dotnet bin/${process.env.HELPDESK_DETAILS_CONFIGURATION ?? 'Release'}/net10.0/HelpDesk.NewWeb.dll`,
      cwd: process.env.HELPDESK_DETAILS_ROLE_WEB_ROOT ?? 'src/HelpDesk.NewWeb',
      url: `https://127.0.0.1:${5194 + index}/health`, ignoreHTTPSErrors: true, timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `https://127.0.0.1:${5194 + index}`,
        ApiBaseUrl: 'http://127.0.0.1:18620/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: `local-details-${role}-fixture`, Authentication__Authentik__ClientId: 'ux-local',
        AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
    }))
  ]
});

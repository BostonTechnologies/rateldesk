import { defineConfig } from '@playwright/test';
import base from './playwright.config';
export default defineConfig(base, {
  testMatch: ['**/email-display.spec.ts'], testIgnore: [], retries: 0, timeout: 60_000, expect: { timeout: 15_000 },
  use: { ...base.use, baseURL: 'https://127.0.0.1:5166', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/email-fixture-api.mjs', url: 'http://127.0.0.1:18304/fixture/health', reuseExistingServer: !process.env.CI },
    { command: process.env.HELPDESK_EMAIL_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd: 'src/HelpDesk.NewWeb', url: 'https://127.0.0.1:5166/health', ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5166', ApiBaseUrl: 'http://127.0.0.1:18304/', DevelopmentOperator__Enabled: 'true', SYSTEM_TOKEN_SECRET: 'synthetic-email-fixture', Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' } }
  ]
});

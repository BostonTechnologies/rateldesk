import { defineConfig } from '@playwright/test';
import base from './playwright.config';

export default defineConfig(base, {
  testMatch: ['**/assignee-picker.spec.ts'], testIgnore: [], retries: 0,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5159', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/assignee-fixture-api.mjs', url: 'http://127.0.0.1:18300/fixture/health', reuseExistingServer: !process.env.CI },
    {
      command: process.env.HELPDESK_ASSIGNEE_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll',
      cwd: 'src/HelpDesk.NewWeb', url: 'https://127.0.0.1:5159/health', ignoreHTTPSErrors: true, timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5159',
        ApiBaseUrl: 'http://127.0.0.1:18300/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: 'local-ux-fixture', Authentication__Authentik__ClientId: 'ux-local',
        AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
    },
    {
      command: process.env.HELPDESK_ASSIGNEE_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll',
      cwd: 'src/HelpDesk.NewWeb', url: 'https://127.0.0.1:5160/health', ignoreHTTPSErrors: true, timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5160',
        ApiBaseUrl: 'http://127.0.0.1:18300/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: 'local-ux-reader-fixture', Authentication__Authentik__ClientId: 'ux-local',
        AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
    }
  ]
});

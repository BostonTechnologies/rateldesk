import { defineConfig } from '@playwright/test';
import base from './playwright.config';

export default defineConfig(base, {
  testMatch: ['**/self-service-workspace.spec.ts', '**/self-service-submission.spec.ts'], testIgnore: [], workers: 1, retries: 0, timeout: 60_000,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5180', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/self-service-fixture-api.mjs', url: 'http://127.0.0.1:18440/fixture/health', reuseExistingServer: false },
    { command: process.env.HELPDESK_SELF_SERVICE_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd: 'src/HelpDesk.NewWeb',
      url: 'https://127.0.0.1:5180/health', ignoreHTTPSErrors: true, timeout: 120_000, reuseExistingServer: false,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5180', ApiBaseUrl: 'http://127.0.0.1:18440/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: 'local-self-service-fixture', Authentication__Authentik__ClientId: 'ux-local', AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' } }
  ]
});

import { defineConfig } from '@playwright/test';
import base from './playwright.config';
export default defineConfig(base, {
  testMatch: ['**/operations-workspace.spec.ts'], testIgnore: [], retries: 0, workers: 1,
  use: { ...base.use, baseURL: 'https://127.0.0.1:5170', ignoreHTTPSErrors: true },
  webServer: [
    { command: 'node tests/ux/operations-fixture-api.mjs', url: 'http://127.0.0.1:18400/fixture/health', reuseExistingServer: !process.env.CI },
    ...['admin', 'sla', 'workflow', 'notifications'].map((role, i) => ({
      command: 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd: 'src/HelpDesk.NewWeb',
      url: `https://127.0.0.1:${5170 + i}/health`, ignoreHTTPSErrors: true, timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `https://127.0.0.1:${5170 + i}`,
        ApiBaseUrl: 'http://127.0.0.1:18400/', DevelopmentOperator__Enabled: 'true',
        SYSTEM_TOKEN_SECRET: `local-operations-${role}`, Authentication__Authentik__ClientId: 'ux-local',
        AUTHENTIK_CLIENT_SECRET: 'ux-local', Authentication__Authentik__ApiScope: 'helpdesk' }
    }))
  ]
});

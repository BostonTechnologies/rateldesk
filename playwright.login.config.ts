import { defineConfig } from '@playwright/test';
import base from './playwright.config';

const modes = ['Local', 'Oidc', 'Hybrid'];
export default defineConfig(base, {
  testMatch: '**/login-presentation.spec.ts', testIgnore: [], workers: 1, retries: 0,
  projects: modes.map((mode, index) => ({ name: mode, use: { baseURL: `http://127.0.0.1:${18500 + index}` } })),
  webServer: [
    {
      command: 'dotnet publish src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj -c Release --no-build --no-restore -m:1 --output artifacts/e2e/login-web && node tests/ux/login-fixture-api.mjs',
      url: 'http://127.0.0.1:18499/fixture/health', timeout: 120_000
    },
    ...modes.map((mode, index) => ({
      command: 'dotnet HelpDesk.NewWeb.dll', cwd: 'artifacts/e2e/login-web',
      url: `http://127.0.0.1:${18500 + index}/login`, timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: `http://127.0.0.1:${18500 + index}`,
        ApiBaseUrl: 'http://127.0.0.1:18499/', Authentication__Mode: mode,
        Authentication__AllowInsecureLocalhost: 'true', Authentication__Authentik__Authority: 'http://127.0.0.1:18499/id',
        Authentication__Authentik__RequireHttpsMetadata: 'false', Authentication__Authentik__ClientId: 'presentation-client',
        Authentication__Authentik__ApiScope: 'helpdesk-api', AUTHENTIK_CLIENT_SECRET: 'synthetic-presentation-secret',
        LoginUi__ProviderDisplayName: 'Example Organization'
      }
    }))
  ]
});

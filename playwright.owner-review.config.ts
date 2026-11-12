import { defineConfig } from '@playwright/test';
import admin from './playwright.admin.config';
import operations from './playwright.operations.config';
import details from './playwright.details.config';
import editor from './playwright.request-editor.config';
import selfService from './playwright.self-service.config';
import integrations from './playwright.integrations.config';
import premium from './playwright.premium-cards.config';
import ai from './playwright.ai-assistant.config';
import filters from './playwright.ticket-filters.config';
import pending from './playwright.pending-mail.config';
import path from 'node:path';
const setupRoot = path.resolve(process.env.HELPDESK_REVIEW_SETUP_ROOT ?? 'artifacts/e2e/owner-review-setup');
const setup = defineConfig({
  use: { baseURL: 'https://127.0.0.1:5255' },
  webServer: [
    { command: 'dotnet bin/Debug/net10.0/Helpdesk.API.dll', cwd: 'src/Helpdesk.API', url: 'http://127.0.0.1:5256/health/ready', timeout: 120_000,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'http://127.0.0.1:5256', Bootstrap__StateDirectory: path.join(setupRoot, 'state'), Bootstrap__DataDirectory: path.join(setupRoot, 'data'), Bootstrap__SetupCode: 'synthetic-owner-review-setup-code', DataProtection__KeyRingPath: path.join(setupRoot, 'keys'), ConnectionStrings__HelpdeskDb: '', DatabaseProvider: 'Sqlite', Authentication__Mode: 'Local' } },
    { command: 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll', cwd: 'src/HelpDesk.NewWeb', url: 'https://127.0.0.1:5255/health', ignoreHTTPSErrors: true, timeout: 120_000,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:5255', ApiBaseUrl: 'http://127.0.0.1:5256/', Authentication__Mode: 'Local' } }
  ]
});
const owners = { admin, email: admin, operations, details, editor, 'self-service': selfService, integrations, premium, ai, filters, pending, setup };
const name = process.env.RATELDESK_REVIEW_OWNER as keyof typeof owners;
if (!owners[name]) throw new Error('Set RATELDESK_REVIEW_OWNER to the owning synthetic suite.');
const owner = owners[name];
const servers = (owner.webServer as any[]).slice(0, name === 'admin' ? 3 : 2).map(server => ({ ...server, reuseExistingServer: false }));
if (name === 'admin' || name === 'email') for (const [index, server] of servers.slice(1).entries()) {
  server.url = `https://127.0.0.1:${5260 + index}/health`;
  server.env = { ...server.env, ASPNETCORE_URLS: `https://127.0.0.1:${5260 + index}` };
}
if (process.env.HELPDESK_REVIEW_WEB_CWD) for (const server of servers.slice(1)) server.cwd = process.env.HELPDESK_REVIEW_WEB_CWD;
if (process.env.HELPDESK_REVIEW_CONFIGURATION) for (const server of servers.slice(1))
  server.command = `dotnet bin/${process.env.HELPDESK_REVIEW_CONFIGURATION}/net10.0/HelpDesk.NewWeb.dll`;
export default defineConfig({
  testDir: './tests/ux', testMatch: `**/owner-review-${name}.spec.ts`, workers: 1, fullyParallel: false, retries: 0,
  timeout: 240_000, expect: { timeout: 15_000 }, webServer: servers,
  outputDir: process.env.RATELDESK_REVIEW_OUTPUT ?? `test-results/owner-review-${name}`,
  reporter: [['list'], ['json', { outputFile: process.env.RATELDESK_REVIEW_REPORT ?? `test-results/owner-review-${name}.json` }]],
  use: { baseURL: name === 'admin' || name === 'email' ? 'https://127.0.0.1:5260' : owner.use?.baseURL, ignoreHTTPSErrors: true, screenshot: 'off', trace: 'off', actionTimeout: 15_000, navigationTimeout: 30_000 }
});

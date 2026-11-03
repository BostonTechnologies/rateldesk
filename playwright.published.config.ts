import { defineConfig } from '@playwright/test';
import login from './playwright.login.config';
import ai from './playwright.ai-assistant.config';
import assignee from './playwright.assignee.config';
import email from './playwright.email.config';
import filters from './playwright.ticket-filters.config';
import details from './playwright.details.config';
import operations from './playwright.operations.config';
import admin from './playwright.admin.config';
import pending from './playwright.pending-mail.config';
import integrations from './playwright.integrations.config';

const suites = { login, ai, assignee, email, filters, details, operations, admin, pending, integrations };
const suite = process.env.HELPDESK_PUBLISHED_SUITE as keyof typeof suites;
if (!suite || !suites[suite] || !process.env.HELPDESK_PUBLISHED_WEB_IMAGE) throw new Error('Select an owning suite and an explicit published Web image');
const selected = suites[suite];
const servers = Array.isArray(selected.webServer) ? selected.webServer : [selected.webServer];
export default defineConfig(selected, {
  webServer: servers.map(server => {
    if (!server) throw new Error('Owning suite has no fixture servers');
    if (server.command.includes('HelpDesk.NewWeb.dll') || server.command.startsWith('dotnet run')) {
      return { ...server, command: 'node tools/ci/run-published-web.mjs', cwd: '.', reuseExistingServer: false,
        env: { ...server.env, HELPDESK_PUBLISHED_WEB_ENV: JSON.stringify(server.env ?? {}) } };
    }
    // The login API fixture normally publishes its host-side Web copy first.
    // Its API server is reused unchanged; the selected image owns Web execution.
    if (suite === 'login' && server.command.startsWith('dotnet publish ')) {
      return { ...server, command: 'node tests/ux/login-fixture-api.mjs', reuseExistingServer: false };
    }
    return { ...server, reuseExistingServer: false };
  })
});

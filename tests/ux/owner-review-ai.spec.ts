import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
const fixture = 'http://127.0.0.1:18299/fixture';
for (const mode of reviewModes) test(`AI and Netclaw table hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset?mode=tables`);
  await reviewPage(browser, baseURL!, mode, async page => {
    const open = (route: string) => reviewOpen(page, baseURL!, route);
    const capture = (name: string, ...ids: string[]) => reviewCapture(page, info, mode, name, ids);
    for (const route of ['/ai/chat?organizationId=org&conversationId=11111111-1111-4111-8111-111111111111', '/ai/ops?id=11111111-1111-4111-8111-111111111111', '/incidents/ux-chat']) {
      await open(route);
      if (route.startsWith('/incidents')) await page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
      await expect(page.getByTestId('ai-assistant-response').getByRole('table')).toHaveCount(2);
      await capture(route.startsWith('/incidents') ? 'ticket-markdown' : route.startsWith('/ai/ops') ? 'ops-markdown' : 'general-markdown', 'shared-aiassistantmarkdown-1');
      await expect(page.getByRole('button', { name: 'Copy table', exact: true }).first()).toBeEnabled();
    }
    await request.get(`${fixture}/netclaw?mode=legacy&tables=true`);
    await open('/admin/automation/integration/netclaw');
    await page.getByLabel('Netclaw address', { exact: true }).fill('https://netclaw.example.test');
    await page.getByLabel('Pairing code', { exact: true }).fill('SYNTHETIC-CODE');
    await page.getByTestId('pair-and-connect-netclaw').click();
    await page.getByTestId('review-legacy-sessions-separately').click();
    await capture('unbound-legacy-sessions', 'pages-admin-orchestration-netclawconnectivity-1');
    await request.get(`${fixture}/netclaw?mode=connected&tables=true`);
    await open('/admin/automation/integration/netclaw');
    await page.getByTestId('test-netclaw-connection').click();
    await page.getByText('Advanced diagnostics', { exact: true }).click();
    await capture('netclaw-probes', 'pages-admin-orchestration-netclawconnectivity-2');
  });
});

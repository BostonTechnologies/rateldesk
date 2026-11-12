import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`integration table hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get('http://127.0.0.1:18505/fixture/reset');
  await reviewPage(browser, baseURL!, mode, async page => {
    for (const [name, route] of [['account', '/account/integration-credentials?tab=credentials'], ['administration', '/admin/integration?tab=credentials']]) {
      await reviewOpen(page, baseURL!, route);
      await reviewCapture(page, info, mode, `integration-credentials-${name}`, ['pages-integrationcredentials-1']);
      await expect(page.getByRole('button', { name: 'Details', exact: true }).first()).toBeEnabled();
    }
    await reviewOpen(page, baseURL!, '/admin/operator-policies');
    await reviewCapture(page, info, mode, 'mcp-policies', ['pages-admin-mcppolicies-1']);
  });
});

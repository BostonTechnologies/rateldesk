import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`Operations Overview feed hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get('http://127.0.0.1:18720/fixture/reset');
  await reviewPage(browser, baseURL!, mode, async page => {
    await reviewOpen(page, baseURL!, '/home');
    for (const kind of ['incidents', 'requests', 'changes']) await expect(page.getByRole('region', { name: `Recent ${kind}`, exact: true }).locator('tbody tr').first()).toBeVisible();
    await reviewCapture(page, info, mode, 'recent-incidents-requests-changes', ['shared-operationsrecentpanel-1']);
  });
});

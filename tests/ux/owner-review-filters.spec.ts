import { expect, test } from '@playwright/test';
import { baselineReview, modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`shared ticket table hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get('http://127.0.0.1:18330/fixture/reset');
  await reviewPage(browser, baseURL!, mode, async page => {
    for (const route of ['/incidents', '/requests', '/changes']) {
      await reviewOpen(page, baseURL!, route);
      if (!baselineReview) await expect(page.getByRole('combobox', { name: 'Tenant', exact: true })).toBeVisible();
      await reviewCapture(page, info, mode, route.slice(1), ['shared-tickettable-1']);
    }
  });
});

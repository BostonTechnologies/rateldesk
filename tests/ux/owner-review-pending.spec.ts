import { test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`pending delivery table ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get('http://127.0.0.1:18700/fixture/reset');
  await reviewPage(browser, baseURL!, mode, async page => {
    await reviewOpen(page, baseURL!, '/admin/pending-emails');
    await reviewCapture(page, info, mode, 'pending-emails', ['pages-admin-pendingemails-1']);
  });
});

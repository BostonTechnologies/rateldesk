import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`internal templates and reciprocal automation report ${modeName(mode)}`, async ({ browser, request }, info) => {
 await request.get('http://127.0.0.1:18500/fixture/automation-trace');
 await request.get('http://127.0.0.1:18600/fixture/automation-trace');
 await reviewPage(browser, 'https://127.0.0.1:5180', mode, async page => {
  await reviewOpen(page, 'https://127.0.0.1:5180', '/admin/templates');
  await page.getByRole('tab', { name: 'NetRatel', exact: true }).click();
  await page.getByRole('button', { name: 'New from default', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Customer-safe subject', exact: true })).toBeVisible();
  const preview = page.frameLocator('iframe[title="Internal report — synthetic preview"]');
  await expect(preview.getByText('5 of 5 targets failed', { exact: false }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Save draft', exact: true }).focus();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeFocused();
  await reviewCapture(page, info, mode, 'internal-template-editor', []);
  await page.getByRole('button', { name: 'Publish immutable revision', exact: true }).click();
  await expect(page.getByText(/revision 1/)).toBeVisible();
 });
 await reviewPage(browser, 'https://127.0.0.1:5190', mode, async page => {
  for (const type of ['incidents', 'requests']) {
   await reviewOpen(page, 'https://127.0.0.1:5190', `/${type}/${type}-1`);
   await page.getByRole('tab', { name: 'Automation', exact: true }).click();
   const report = page.getByRole('region', { name: 'Automation reports', exact: true });
   await expect(report).toBeVisible();
   await expect(report.getByText('5 of 5 targets failed', { exact: false }).first()).toBeVisible();
   await expect(report.getByText('List requested directory', { exact: false })).toBeVisible();
   await expect(report.getByRole('link', { name: 'Job execution', exact: true })).toHaveAttribute('href', /jobs\/67\/runs\/639300000000000001/);
   await reviewCapture(page, info, mode, `automation-report-${type}`, []);
   await page.getByRole('tab', { name: /^Relations/ }).click();
   await expect(page.getByText(type === 'incidents' ? 'Automation failure of' : 'Has automation failure', { exact: true })).toBeVisible();
   await reviewCapture(page, info, mode, `reciprocal-relations-${type}`, ['shared-ticketrelations-1']);
  }
 });
});

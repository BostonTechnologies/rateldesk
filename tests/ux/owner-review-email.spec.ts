import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
const fixture = 'http://127.0.0.1:18500/fixture';
for (const mode of reviewModes) test(`authored preview after asynchronous HTML arrival ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset`);
  await request.get(`${fixture}/table-review`);
  await reviewPage(browser, baseURL!, mode, async page => {
    await reviewOpen(page, baseURL!, '/admin/layouts');
    await page.getByRole('button', { name: 'Edit layout', exact: true }).first().click();
    const iframe = page.locator('iframe[title="Layout Preview"]');
    await expect(iframe).toHaveAttribute('srcdoc', /View Ticket/);
    await iframe.scrollIntoViewIfNeeded();
    const authored = page.frameLocator('iframe[title="Layout Preview"]');
    await expect(authored.locator('table[role="presentation"]')).toBeVisible();
    await page.getByRole('combobox', { name: 'Preview as tenant', exact: true }).click();
    await page.getByRole('option', { name: 'Atlas Tenant', exact: true }).click();
    await expect(authored.locator('[data-preview-tenant="org-1"]')).toBeVisible();
    await expect(authored.getByRole('link', { name: 'View Ticket', exact: true })).toHaveCSS('color', 'rgb(255, 255, 255)');
    await authored.getByRole('link', { name: 'View Ticket', exact: true }).focus();
    await expect(authored.getByRole('link', { name: 'View Ticket', exact: true })).toBeFocused();
    await reviewCapture(page, info, mode, 'authored-email-semantic-exception', []);
  });
});

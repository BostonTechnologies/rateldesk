import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate } from './auth';

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

test('desktop integration hub exposes the three separate destinations', async ({ page }, testInfo) => {
  await page.goto('/admin/automation/integration');
  await expect(page.getByRole('heading', { name: 'Integration hub' })).toBeVisible();
  await expect(page.getByText('NetRatel orchestrator', { exact: true })).toBeVisible();
  await expect(page.getByText('Netclaw AI harness', { exact: true })).toBeVisible();
  await expect(page.getByText('My integration credentials', { exact: true })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('integration-hub-desktop-light.png'), fullPage: true });

  await page.getByRole('link', { name: 'Open destination' }).first().click();
  await expect(page.getByRole('heading', { name: 'NetRatel orchestrator' })).toBeVisible();
  await assertNoHorizontalOverflow(page);

  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
});

test('mobile dark integration hub remains compact and overflow-free', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'dark',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);
  await page.goto('/admin/automation/integration');
  await expect(page.getByRole('heading', { name: 'Integration hub' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('integration-hub-mobile-dark.png'), fullPage: true });
  await context.close();
});

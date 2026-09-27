import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate } from './auth';

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

test('desktop integration hub exposes the three separate destinations', async ({ page }, testInfo) => {
  await page.emulateMedia({ colorScheme: 'light' });
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
  await expect(page.getByRole('heading', { name: 'Connect Netclaw' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Effective profile' })).toBeVisible();
  await expect(page.getByLabel('One-time Netclaw pairing code')).toHaveAttribute('type', 'password');
  await expect(page.getByTestId('pair-and-save-netclaw')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save settings' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Test draft' })).toBeVisible();
  await expect(page.getByLabel('One-time Netclaw pairing code')).toHaveValue('');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('netclaw-desktop-light.png'), fullPage: true });

  const endpoint = page.getByLabel('Netclaw session endpoint');
  const instance = page.getByLabel('Instance');
  const privateHttp = page.getByLabel('Allow private HTTP (local development only)');
  const pairingCode = page.getByLabel('One-time Netclaw pairing code');
  await endpoint.focus();
  await expect(endpoint).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(instance).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(privateHttp).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(pairingCode).toBeFocused();
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

  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Connect Netclaw' })).toBeVisible();
  await expect(page.getByLabel('Netclaw session endpoint')).toBeVisible();
  await expect(page.getByLabel('One-time Netclaw pairing code')).toBeVisible();
  await expect(page.getByTestId('pair-and-save-netclaw')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Test draft' })).toBeVisible();
  const netclawHubLink = page.getByTestId('netclaw-settings-page').getByRole('link', { name: 'Integration hub' });
  await expect(netclawHubLink).toBeVisible();
  await expect(netclawHubLink).toHaveCSS('white-space', 'nowrap');
  await expect.poll(() => netclawHubLink.evaluate(link => link.scrollWidth - link.clientWidth)).toBeLessThanOrEqual(1);
  await expect(page.getByLabel('One-time Netclaw pairing code')).toHaveValue('');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('netclaw-mobile-dark.png'), fullPage: true });
  await context.close();
});

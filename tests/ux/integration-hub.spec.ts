import { expect, test, type Page } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate, selectTheme } from './auth';

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

async function expectNetclawRoute(page: Page): Promise<void> {
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await expect(page.getByTestId('netclaw-connection-status')).toBeVisible();

  const address = page.getByLabel('Netclaw address', { exact: true });
  if (await address.isVisible()) {
    const pairingCode = page.getByLabel('Pairing code', { exact: true });
    await expect(address).toBeVisible();
    await expect(pairingCode).toHaveAttribute('type', 'password');
    await expect(pairingCode).toHaveValue('');
    await expect(page.getByTestId('pair-and-connect-netclaw')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save settings' })).not.toBeVisible();
    await expect(page.getByRole('button', { name: 'Test draft' })).not.toBeVisible();
    await expect(page.getByTestId('netclaw-legacy-ownership')).not.toBeVisible();
  } else {
    await expect(page.getByTestId('netclaw-connected-summary')).toBeVisible();
    await expect(page.getByRole('button', { name: /Test (saved )?connection/ })).toBeVisible();
  }

  await assertNoHorizontalOverflow(page);
}

async function setStoredTheme(page: Page, mode: 'light' | 'dark'): Promise<void> {
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), mode);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', mode);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

test('integration hub routes to Netclaw and renders desktop light and dark states', async ({ page }, testInfo) => {
  await page.goto('/admin/automation/integration');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('heading', { name: 'Integration hub' })).toBeVisible();
  await expect(page.getByText('NetRatel orchestrator', { exact: true })).toBeVisible();
  await expect(page.getByText('Netclaw AI harness', { exact: true })).toBeVisible();
  await expect(page.getByText('My integration credentials', { exact: true })).toBeVisible();
  await expect(page.getByText('Pair your daemon once to verify the authenticated chat connection used by the ticket AI Assistant.')).toBeVisible();
  await selectTheme(page, 'Light');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'light');
  await assertNoHorizontalOverflow(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('integration-hub-desktop-light.png'), fullPage: true });

  await page.getByRole('link', { name: 'Open destination' }).first().click();
  await expect(page.getByRole('heading', { name: 'NetRatel orchestrator' })).toBeVisible();
  await assertNoHorizontalOverflow(page);

  await page.goto('/admin/automation/integration/netclaw');
  await expectNetclawRoute(page);
  await selectTheme(page, 'Light');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'light');
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-desktop-light.png'), fullPage: true });

  await selectTheme(page, 'Dark');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'dark');
  await expectNetclawRoute(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-desktop-dark.png'), fullPage: true });

  const address = page.getByLabel('Netclaw address', { exact: true });
  if (await address.isVisible()) {
    const pairingCode = page.getByLabel('Pairing code', { exact: true });
    await address.focus();
    await expect(address).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(pairingCode).toBeFocused();
  }
});

test('Netclaw route fits a 390px viewport in light and dark themes', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'dark',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);
  await page.goto('/admin/automation/integration');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('heading', { name: 'Integration hub' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await setStoredTheme(page, 'dark');
  await expect(page.getByRole('heading', { name: 'Integration hub' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('integration-hub-mobile-dark.png'), fullPage: true });

  await page.goto('/admin/automation/integration/netclaw');
  await expectNetclawRoute(page);

  const netclawHubLink = page.getByTestId('netclaw-settings-page').getByRole('link', { name: 'Integration hub' });
  await expect(netclawHubLink).toBeVisible();
  await expect(netclawHubLink).toHaveCSS('white-space', 'nowrap');
  await expect.poll(() => netclawHubLink.evaluate(link => link.scrollWidth - link.clientWidth)).toBeLessThanOrEqual(1);

  await setStoredTheme(page, 'light');
  await expectNetclawRoute(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-mobile-light.png'), fullPage: true });

  await setStoredTheme(page, 'dark');
  await expectNetclawRoute(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-mobile-dark.png'), fullPage: true });
  await context.close();
});

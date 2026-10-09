import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate, selectTheme } from './auth';

// Pairing codes belong only in the form; omit automatic secret-bearing artifacts.
test.use({ trace: 'off', screenshot: 'off' });

test.beforeEach(async ({ page }) => { await authenticate(page); });
test.afterEach(async ({ page }) => {
  if (!page.isClosed()) await page.evaluate(() => document.documentElement.replaceChildren()).catch(() => {});
});

test('System connections keeps the two-field pairing form compact in desktop and narrow light/dark views', async ({ page }, testInfo) => {
  await page.goto('/account/integration-credentials');
  await expect(page.getByTestId('integration-credentials-page')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('tab', { name: 'System connections', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('generate-pairing-code')).toBeVisible();
  await page.getByTestId('create-system-connection').click();
  const form = page.getByTestId('system-pairing-form');
  await expect(form.locator('input')).toHaveCount(2);
  await expect(form.getByLabel('Address', { exact: true })).toBeVisible();
  await expect(form.getByLabel('Pairing code', { exact: true })).toHaveAttribute('type', 'password');
  await expect(page.getByTestId('pair-and-connect')).toBeDisabled();
  await form.getByLabel('Address', { exact: true }).fill('http://192.168.1.20:5000/');
  await form.getByLabel('Pairing code', { exact: true }).fill('ABCD-EFGH');
  await expect(page.getByTestId('pair-and-connect')).toBeEnabled();
  await expect(form.getByRole('checkbox')).toHaveCount(0);
  // Clear the synthetic code before deliberate screenshots.
  await form.getByLabel('Pairing code', { exact: true }).fill('');
  for (const width of [390, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['Light', 'Dark'] as const) {
      await selectTheme(page, theme);
      await assertNoHorizontalOverflow(page);
      await expect(form).toBeVisible();
      await expect(page.getByTestId('pair-and-connect')).toBeVisible();
      await page.screenshot({ path: testInfo.outputPath(`system-pairing-${width}-${theme.toLowerCase()}.png`), fullPage: true, animations: 'disabled' });
    }
  }
  await form.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('tab', { name: 'API & MCP credentials', exact: true }).click();
  await expect(page.getByTestId('integration-credential-create')).toBeVisible();
  await expect(page.getByTestId('system-connections-panel')).not.toBeVisible();
  const stored = await page.evaluate(() => Object.keys(localStorage).concat(Object.keys(sessionStorage)));
  expect(stored.some(key => /pairing|verifier|browser.?state/i.test(key))).toBe(false);
});

test('alternate NetRatel orchestrator destination opens the same account connection model', async ({ page }) => {
  await page.goto('/admin/automation/integration/orchestrator');
  await expect(page.getByRole('heading', { name: 'NetRatel orchestrator', exact: true })).toBeVisible();
  await expect(page.getByTestId('manage-system-connections')).toHaveAttribute('href', '/account/integration-credentials');
  await expect(page.getByRole('textbox')).toHaveCount(0);
  await page.getByTestId('manage-system-connections').click();
  await expect(page.getByTestId('system-connections-panel')).toBeVisible();
  await expect(page.getByTestId('generate-pairing-code')).toBeVisible();
});

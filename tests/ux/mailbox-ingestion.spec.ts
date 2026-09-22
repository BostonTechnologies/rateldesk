import { expect, test } from '@playwright/test';
import { authenticate, assertNoHorizontalOverflow, selectTheme } from './auth';

test.beforeEach(async ({ page }) => { await authenticate(page); });

test('provider selection, failed draft test, discard, save and explicit tenant revert', async ({ page }, testInfo) => {
  await page.goto('/admin/email-settings');
  await page.getByRole('button', { name: 'Add tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).click();
  await page.getByRole('option').first().click();
  await page.getByRole('combobox', { name: 'Inbound provider', exact: true }).click();
  await page.getByRole('option', { name: 'POP3', exact: true }).click();
  await expect(page.getByLabel('Mailbox folder', { exact: true })).toHaveCount(0);
  await expect(page.getByText('POP3 keeps messages on the server.', { exact: false })).toBeVisible();
  await page.getByLabel('Mailbox display name', { exact: true }).fill('Fixture dedicated POP3');
  await page.getByRole('textbox', { name: /^Mail host/ }).fill('127.0.0.1');
  await page.getByRole('textbox', { name: /^Mailbox address/ }).fill('support@fixture.example.test');
  await page.getByLabel('Protocol username', { exact: true }).fill('fixture');
  await page.getByLabel('Protocol password', { exact: true }).fill('synthetic-fixture-password');
  await page.getByRole('button', { name: 'Test Connection' }).click();
  await expect(page.getByText('Connection test failed. Check credentials, TLS, folder access and operator egress policy.', { exact: true }).first()).toBeVisible();
  await expect(page.getByLabel('Mailbox display name', { exact: true })).toHaveValue('Fixture dedicated POP3');
  await page.getByRole('textbox', { name: /^Mail host/ }).fill('mail.example.test');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Protocol password', { exact: true })).toHaveValue('');
  await page.getByLabel('Mailbox display name', { exact: true }).fill('Unsaved name');
  await page.getByRole('button', { name: 'Discard changes' }).click();
  await expect(page.getByLabel('Mailbox display name', { exact: true })).toHaveValue('Fixture dedicated POP3');
  await page.screenshot({ path: testInfo.outputPath('dedicated-pop3-paused.png'), fullPage: true });
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Revert to global mailbox' }).click();
  await expect(page.getByRole('button', { name: 'Archive global mailbox' })).toBeVisible();
});

test('long rule name and description wrap with reachable actions at desktop and mobile widths', async ({ page }, testInfo) => {
  const ruleName = 'Wrapping fixture ' + 'N'.repeat(160);
  await page.goto('/admin/email-rules');
  await page.getByRole('button', { name: 'Clone rule', exact: true }).first().click();
  await page.getByLabel('Cloned rule name', { exact: true }).fill(ruleName);
  await page.getByLabel('Cloned rule description', { exact: true }).fill('Description '.repeat(35) + 'unbroken'.repeat(60));
  await page.getByRole('button', { name: 'Save disabled copy' }).click();
  await expect(page.getByText(ruleName, { exact: true })).toBeVisible();
  for (const theme of ['Light', 'Dark'] as const) {
    await selectTheme(page, theme);
    for (const width of [1440, 1280, 1024, 768, 390]) {
      await page.setViewportSize({ width, height: 950 });
      await assertNoHorizontalOverflow(page);
      const row = page.locator('tr').filter({ hasText: ruleName });
      await expect(row).toBeVisible();
      const bounds = await row.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return { width: rect.width, scroll: element.scrollWidth, client: element.clientWidth };
      });
      expect(bounds.scroll - bounds.client).toBeLessThanOrEqual(2);
      await expect(row.getByRole('button').last()).toBeVisible();
      await page.screenshot({ path: testInfo.outputPath(`rules-${theme.toLowerCase()}-${width}.png`), fullPage: true });
    }
  }
});

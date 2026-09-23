import { expect, test } from '@playwright/test';
import { authenticate, assertNoHorizontalOverflow, selectTheme } from './auth';

// These cases include CRUD round trips or ten responsive screenshots.
test.setTimeout(120_000);
test.beforeEach(async ({ page }) => { await authenticate(page); });

test('provider selection, failed draft test, discard, save and explicit tenant revert', async ({ page }, testInfo) => {
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).fill('Fixture');
  await expect(page.getByRole('option', { name: /Fixture Organization/ })).toBeVisible();
  await page.getByRole('option', { name: /Fixture Organization/ }).click();
  await page.getByRole('combobox', { name: 'Inbound provider', exact: true }).click();
  await page.getByRole('option', { name: 'POP3', exact: true }).click();
  await expect(page.getByLabel('Mailbox folder', { exact: true })).toHaveCount(0);
  await expect(page.getByText('POP3 keeps messages on the server.', { exact: false })).toBeVisible();
  await page.getByLabel('Mailbox display name', { exact: true }).fill('Fixture dedicated POP3');
  await page.getByRole('textbox', { name: /^Mail host/ }).fill('127.0.0.1');
  await page.getByRole('textbox', { name: /^Mailbox address/ }).fill('support@fixture.example.test');
  await page.getByLabel('Protocol username', { exact: true }).fill('fixture');
  await page.getByLabel('Protocol password', { exact: true }).fill('synthetic-fixture-password');
  await page.getByRole('button', { name: 'Test', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Incoming connection' }).click();
  await expect(page.getByText('Connection test failed. Check credentials, TLS, folder access and operator egress policy.', { exact: true }).first()).toBeVisible();
  await expect(page.getByLabel('Mailbox display name', { exact: true })).toHaveValue('Fixture dedicated POP3');
  await page.getByRole('textbox', { name: /^Mail host/ }).fill('mail.example.test');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Protocol password', { exact: true })).toHaveValue('');
  await page.getByLabel('Mailbox display name', { exact: true }).fill('Unsaved name');
  await page.getByRole('button', { name: 'Discard', exact: true }).click();
  await expect(page.getByLabel('Mailbox display name', { exact: true })).toHaveValue('Fixture dedicated POP3');
  await page.screenshot({ path: testInfo.outputPath('dedicated-pop3-paused.png'), fullPage: true });
  await page.getByRole('button', { name: 'More mailbox actions' }).click();
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('menuitem', { name: 'Revert to global' }).click();
  const picker = page.getByRole('combobox', { name: 'Selected mailbox' });
  await expect(picker).toContainText('Instance-global');
  await picker.click();
  await expect(page.getByRole('option', { name: /Instance-global/ })).toBeVisible();
  await expect(page.getByRole('option', { name: /Fixture dedicated POP3/ })).toHaveCount(0);
});

test('long rule name and description wrap with reachable actions at desktop and mobile widths', async ({ page }, testInfo) => {
  const ruleName = 'Wrapping fixture ' + 'N'.repeat(160);
  await page.goto('/admin/email-rules');
  await expect(page.getByTestId('mailbox-rules')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Clone rule', exact: true }).first().click();
  await page.getByLabel('Cloned rule name', { exact: true }).fill(ruleName);
  await page.getByLabel('Cloned rule description', { exact: true }).fill('Description '.repeat(35) + 'unbroken'.repeat(60));
  await page.getByRole('button', { name: 'Save disabled copy' }).click();
  await expect(page.getByText(ruleName, { exact: true })).toBeVisible();
  for (const theme of ['Light', 'Dark'] as const) {
    await page.setViewportSize({ width: 1440, height: 950 });
    await selectTheme(page, theme);
    for (const width of [1440, 1280, 1024, 768, 390]) {
      await page.setViewportSize({ width, height: 950 });
      await assertNoHorizontalOverflow(page);
      if (width === 1440) await expect(page.getByRole('columnheader', { name: 'Name', exact: true })).toBeVisible();
      const row = page.locator('tr').filter({ hasText: ruleName });
      await expect(row).toBeVisible();
      const bounds = await row.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return { width: rect.width, scroll: element.scrollWidth, client: element.clientWidth };
      });
      expect(bounds.scroll - bounds.client).toBeLessThanOrEqual(2);
      const action = row.getByRole('button').last();
      await action.scrollIntoViewIfNeeded();
      await expect(action).toBeInViewport();
      await page.evaluate(() => window.scrollTo(0, 0));
      await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
      await page.screenshot({ path: testInfo.outputPath(`rules-${theme.toLowerCase()}-${width}.png`), fullPage: true });
    }
  }
});

test('mailbox controls remain usable when component JavaScript arrives after Blazor', async ({ page }) => {
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let observed!: () => void;
  const requested = new Promise<void>(resolve => { observed = resolve; });
  let negotiations = 0;
  page.on('request', request => { if (request.url().includes('/_blazor/negotiate')) negotiations++; });
  await page.route('**/_content/MudBlazor/*.js', async route => { observed(); await held; await route.continue(); });
  try {
    await page.goto('/admin/email-settings', { waitUntil: 'commit' });
    await requested;
    await page.waitForFunction(() => typeof (window as any).Blazor?.start === 'function');
    expect(negotiations).toBe(0);
    await expect(page.getByTestId('navigation-toggle')).toBeDisabled();
    release();
    await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
    await expect(page.getByTestId('navigation-toggle')).toBeEnabled();
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Tenant override' }).click();
    await expect(page.getByRole('combobox', { name: /^RatelDesk organization/ })).toBeVisible();
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  } finally { release(); }
});

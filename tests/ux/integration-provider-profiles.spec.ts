import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { authenticate } from './auth';

// This flow fills password inputs. Disable automatic artifacts so a failure
// before the explicit post-save screenshots cannot record the draft values.
test.use({ trace: 'off', screenshot: 'off' });

async function settingValue(page: Page, name: string): Promise<string> {
  const currentPath = new URL(page.url()).pathname;
  if (currentPath === '/admin/automation/integration/netclaw') {
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    const netclawProfile = page.getByTestId('netclaw-runtime-profile');
    if (!await netclawProfile.isVisible())
      await page.getByText('Advanced diagnostics', { exact: true }).click();
    await expect(netclawProfile).toBeVisible();
    const field = netclawProfile.locator('.netclaw-profile-item').filter({ has: page.getByText(name, { exact: true }) });
    await expect(field).toHaveCount(1);
    return (await field.locator('dd').innerText()).trim();
  }

  const row = page.getByRole('row').filter({ has: page.getByText(name, { exact: true }) });
  await expect(row).toHaveCount(1);
  return (await row.locator('td').nth(1).innerText()).trim();
}

async function expectSecretInputBlank(page: Page, label: string): Promise<void> {
  // Only expose a boolean in a failure message, never the input value itself.
  await expect.poll(async () =>
    (await page.getByLabel(label, { exact: true }).inputValue()).length > 0).toBe(false);
}

async function expectSecretAbsentFromPage(page: Page, secret: string): Promise<void> {
  const pageContent = await page.evaluate(() => ({
    text: document.body.innerText,
    html: document.documentElement.outerHTML,
    inputValues: Array.from(document.querySelectorAll('input'), (input) => (input as HTMLInputElement).value)
  }));
  expect(JSON.stringify(pageContent).includes(secret)).toBe(false);
}

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

test.afterEach(async ({ page }) => {
  if (page.isClosed()) return;
  // Playwright can attach an automatic error-context.md aria snapshot even
  // with trace and screenshot capture disabled. Clear the rendered page first.
  await page.evaluate(() => document.documentElement.replaceChildren());
});

test('admin UI saves and reloads PostgreSQL provider profiles without exposing protected secrets', async ({ page }, testInfo) => {
  test.setTimeout(90_000);
  const netclawToken = `synthetic-e2e-netclaw-token-${randomUUID()}`;
  const netratelSecret = `synthetic-e2e-netratel-secret-${randomUUID()}`;

  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await expect.poll(() => settingValue(page, 'Runtime')).toBe('Available');
  const initialNetclawRevision = Number(await settingValue(page, 'Revision'));

  await page.getByText('Manual token / recovery', { exact: true }).click();
  await page.getByLabel('Enable native Netclaw chat', { exact: true }).check();
  await page.getByLabel('Session endpoint', { exact: true }).fill('https://netclaw-e2e.invalid/hub/session');
  await page.getByLabel('Paired-device token', { exact: true }).fill(netclawToken);
  const saveNetclawSettings = page.getByRole('button', { name: 'Save settings' });
  await expect(saveNetclawSettings).toBeEnabled();
  await saveNetclawSettings.click();
  await expect(page.getByText('Netclaw settings saved and applied to new chat connections.', { exact: true })).toBeVisible();
  await expectSecretInputBlank(page, 'Paired-device token');
  await expect.poll(() => settingValue(page, 'Paired token')).toBe('Configured (protected)');
  const savedNetclawRevision = Number(await settingValue(page, 'Revision'));
  expect(savedNetclawRevision).toBeGreaterThan(initialNetclawRevision);
  await expectSecretAbsentFromPage(page, netclawToken);

  await page.reload();
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await page.getByText('Manual token / recovery', { exact: true }).click();
  await expect(page.getByLabel('Session endpoint', { exact: true })).toHaveValue('https://netclaw-e2e.invalid/hub/session');
  await expectSecretInputBlank(page, 'Paired-device token');
  await expect.poll(() => settingValue(page, 'Revision')).toBe(String(savedNetclawRevision));
  await expect.poll(() => settingValue(page, 'Paired token')).toBe('Configured (protected)');
  await expect.poll(() => settingValue(page, 'Source')).toBe('Saved administrator profile');
  await expectSecretAbsentFromPage(page, netclawToken);
  await page.screenshot({ path: testInfo.outputPath('integration-netclaw-protected-secret.png'), fullPage: true });

  await page.goto('/admin/automation/integration/orchestrator');
  await expect(page.getByRole('heading', { name: 'NetRatel orchestrator' })).toBeVisible();
  const initialNetRatelRevision = Number(await settingValue(page, 'Revision'));

  await page.getByLabel('Enable NetRatel automation').check();
  await page.getByLabel('NetRatel API base URL').fill('https://netratel-e2e.invalid');
  await page.getByLabel('Dedicated M2M client id').fill('rateldesk-e2e-client');
  await page.getByLabel('Dedicated M2M client secret').fill(netratelSecret);
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByText('NetRatel orchestrator settings saved.', { exact: true })).toBeVisible();
  await expectSecretInputBlank(page, 'Dedicated M2M client secret');
  await expect.poll(() => settingValue(page, 'Client secret')).toBe('Configured (protected)');
  const savedNetRatelRevision = Number(await settingValue(page, 'Revision'));
  expect(savedNetRatelRevision).toBeGreaterThan(initialNetRatelRevision);
  await expectSecretAbsentFromPage(page, netratelSecret);

  await page.reload();
  await expect(page.getByRole('heading', { name: 'NetRatel orchestrator' })).toBeVisible();
  await expect(page.getByLabel('NetRatel API base URL')).toHaveValue('https://netratel-e2e.invalid');
  await expectSecretInputBlank(page, 'Dedicated M2M client secret');
  await expect.poll(() => settingValue(page, 'Revision')).toBe(String(savedNetRatelRevision));
  await expect.poll(() => settingValue(page, 'Client secret')).toBe('Configured (protected)');
  await expect.poll(() => settingValue(page, 'Source')).toBe('Saved administrator profile');
  await expectSecretAbsentFromPage(page, netratelSecret);
  await page.screenshot({ path: testInfo.outputPath('integration-netratel-protected-secret.png'), fullPage: true });
});

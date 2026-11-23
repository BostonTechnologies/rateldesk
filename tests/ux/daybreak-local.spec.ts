import { createHmac } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';

async function login(page: Page) {
  await page.goto('/login');
  const form = page.getByTestId('local-login-form');
  await expect(form).toHaveAttribute('data-interactive', 'true');
  await form.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await form.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await form.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.waitForURL('**/home');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

for (const entry of ['desktop', 'mobile', 'nav'] as const) {
  test('real Local credentials sign out through the ' + entry + ' form', async ({ page }) => {
    if (entry === 'mobile') await page.setViewportSize({ width: 390, height: 844 });
    await login(page);
    for (const method of ['GET', 'HEAD']) {
      const response = await page.request.fetch('/logout', { method, maxRedirects: 0 });
      expect(response.status()).toBe(405);
      expect((await page.request.get('/session/access')).status()).toBe(200);
    }
    if (entry === 'desktop') await page.getByRole('button', { name: 'Account settings' }).click();
    if (entry === 'mobile') await page.locator('.helpdesk-appbar-mobile-menu button').click();
    await page.getByTestId(entry + '-logout').click();
    await expect(page).toHaveURL(/\/login$/);
    expect((await page.request.get('/session/access')).status()).toBe(401);
    await login(page);
  });
}

test('legacy document assets stay blocked on direct navigation with a real local session', async ({ page }) => {
  await login(page);
  const signingKey = process.env.HELPDESK_E2E_ASSET_SIGNING_KEY!;
  const expiry = Math.floor(Date.now() / 1000) + 60;
  for (const [collection, scope] of [['incidents', 'incident'], ['worklogs', 'worklog']]) {
    for (const filename of ['sample.svg', 'sample.png']) {
      const payload = [scope, 'browser-fixture', filename, expiry].join('|');
      const signature = createHmac('sha256', signingKey).update(payload).digest('base64url');
      const token = Buffer.from(payload + '|' + signature).toString('base64url');
      const response = await page.goto('/api/' + collection + '/browser-fixture/images/' + filename + '?token=' + token);
      expect(response?.status()).toBe(404);
      expect(response?.headers()['x-content-type-options']).toBe('nosniff');
      expect(response?.headers()['content-security-policy']).toContain('sandbox');
      await expect(page.getByText('Synthetic raster fixture marker')).toHaveCount(0);
    }
  }
  expect((await page.request.get('/session/access')).status()).toBe(200);
});

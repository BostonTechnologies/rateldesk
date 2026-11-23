import { expect, test, type Page } from '@playwright/test';
import { openPublicTicketReview } from './public-ticket-form';
import { expectSignedRasterIsolation } from './signed-raster-checks';

async function submitLocalLogin(page: Page) {
  await page.goto('/login');
  const form = page.getByTestId('local-login-form');
  await expect(form).toHaveAttribute('data-interactive', 'true');
  await form.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await form.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await form.getByRole('button', { name: 'Sign in', exact: true }).click();
}

async function login(page: Page) {
  await submitLocalLogin(page);
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
    if (entry === 'desktop') await page.locator('[aria-label="Account settings"] button').click();
    if (entry === 'mobile') await page.locator('.helpdesk-appbar-mobile-menu button').click();
    await page.getByTestId(entry + '-logout').click();
    await expect(page).toHaveURL(/\/login$/);
    expect((await page.request.get('/session/access')).status()).toBe(401);
    await login(page);
  });
}

test('legacy document assets stay blocked on direct navigation with a real local session', async ({ page }) => {
  await login(page);
  await expectSignedRasterIsolation(page, process.env.HELPDESK_E2E_ASSET_SIGNING_KEY!);
  expect((await page.request.get('/session/access')).status()).toBe(200);
});

test('Web login and the API proxy share a budget while another client still signs in', async ({ page, browser, baseURL }) => {
  const first = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: { 'X-Forwarded-For': '192.0.2.10' } });
  try {
    const limitedPage = await first.newPage();
    for (let attempt = 0; attempt < 30; attempt++) {
      const response = await limitedPage.request.post('/api/v1/local-auth/login', { data: { email: '', password: '' } });
      expect(response.status()).toBe(401);
    }
    await submitLocalLogin(limitedPage);
    await expect(limitedPage.getByTestId('local-login-error')).toHaveText('Too many sign-in attempts. Please retry shortly.');
    await login(page);
  } finally {
    await first.close();
  }
});

test('Web CAPTCHA refresh and direct proxy requests share the trusted client limit', async ({ page, browser, baseURL }) => {
  const first = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: { 'X-Forwarded-For': '192.0.2.11' } });
  try {
    const limitedPage = await first.newPage();
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await limitedPage.request.get('/api/v1/captcha');
      expect(response.status()).toBe(200);
      expect(response.headers()['cache-control']).toContain('no-store');
      expect((await response.json()).imageBase64).toMatch(/^data:image\/png;base64,iVBOR/);
    }
    await openPublicTicketReview(limitedPage);
    const captcha = limitedPage.getByAltText('CAPTCHA');
    await expect(captcha).toBeVisible();
    const initial = await captcha.getAttribute('src');
    const refresh = limitedPage.getByRole('button', { name: 'New code' });
    await refresh.click();
    await expect(captcha).not.toHaveAttribute('src', initial!);
    await refresh.click();
    await expect(limitedPage.getByText(/Too many code requests/i)).toBeVisible();
    const denied = await limitedPage.request.get('/api/v1/captcha');
    expect(denied.status()).toBe(429);
    expect(Number(denied.headers()['retry-after'])).toBeGreaterThan(0);
    await openPublicTicketReview(page);
    await expect(page.getByAltText('CAPTCHA')).toBeVisible();
  } finally {
    await first.close();
  }
});

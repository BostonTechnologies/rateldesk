import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, selectTheme } from './auth';
import { openPublicTicketReview } from './public-ticket-form';
import { expectSignedRasterIsolation } from './signed-raster-checks';

for (const width of [1440, 360]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`sign-in actions and logos at ${width}px in ${theme}`, async ({ page, request }, info) => {
      await request.get('http://127.0.0.1:18499/fixture/branding');
      await page.setViewportSize({ width, height: 960 });
      await page.addInitScript(value => {
        if (localStorage.getItem('helpdesk.theme.preference') === null)
          localStorage.setItem('helpdesk.theme.preference', value);
      }, theme);
      await page.goto('/login');
      // OIDC-only mode has no local form to signal that prerendering is finished.
      await expect(page.getByTestId('login-page')).toHaveAttribute('data-interactive', 'true');
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      const local = page.getByTestId('local-login-form');
      const provider = page.getByRole('link', { name: 'Sign in with Example Organization' });
      await expect(local).toHaveCount(info.project.name === 'Oidc' ? 0 : 1);
      await expect(provider).toHaveCount(info.project.name === 'Local' ? 0 : 1);
      if (info.project.name !== 'Oidc') {
        await expect(local).toHaveAttribute('data-interactive', 'true');
        await expect(local.getByRole('button', { name: 'Sign in' })).toHaveCount(1);
      }
      const logo = page.locator('.helpdesk-login-logo');
      await expect(logo).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await expect(logo).toHaveCSS('padding', '0px');
      await expect(logo).toHaveCSS('object-fit', 'contain');
      await expect.poll(() => logo.evaluate((image: HTMLImageElement) => image.complete && image.naturalWidth > 0)).toBe(true);
      await assertNoHorizontalOverflow(page);
      if (info.project.name !== 'Local') {
        await expect(provider).toHaveAttribute('href', '/login-authentik');
        expect(await provider.evaluate(link => link.closest('form') === null)).toBe(true);
        expect((await provider.boundingBox())!.height).toBeGreaterThanOrEqual(44);
        await provider.focus();
        await page.keyboard.press('Tab');
        await page.keyboard.press('Shift+Tab');
        await expect(provider).toBeFocused();
        await expect(provider).toHaveCSS('outline-style', 'solid');
      }
      await page.screenshot({ path: info.outputPath(`login-${theme}-${width}.png`), fullPage: true });
      const selectedTheme = theme === 'dark' ? 'light' : 'dark';
      await selectTheme(page, selectedTheme === 'light' ? 'Light' : 'Dark');
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', selectedTheme);
      await expect.poll(() => page.evaluate(() => localStorage.getItem('helpdesk.theme.preference'))).toBe(selectedTheme);
      await expect(logo).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await page.reload();
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', selectedTheme);
      await page.goto('/');
      await expect(page.locator('.helpdesk-portal-logo')).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await page.screenshot({ path: info.outputPath(`portal-${width}.png`), fullPage: true });
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    });
  }
}

test('external link navigates without local credentials or form validation', async ({ page }, info) => {
  if (info.project.name === 'Local') {
    await page.goto('/login');
    await expect(page.getByTestId('oidc-login-link')).toHaveCount(0);
    return;
  }
  await page.route('http://127.0.0.1:18499/authorize**', route => route.fulfill({ body: 'Synthetic provider challenge reached' }));
  const localPosts: string[] = [];
  page.on('request', req => { if (req.method() === 'POST' && req.url().endsWith('/local-login')) localPosts.push(req.url()); });
  await page.goto('/login');
  await page.getByRole('link', { name: 'Sign in with Example Organization' }).click();
  await expect(page).toHaveURL(/18499\/authorize/);
  expect(localPosts).toEqual([]);
});

test('custom opaque and transparent logos keep their intrinsic proportions', async ({ page, request }, info) => {
  for (const kind of ['opaque', 'transparent']) {
    await request.get(`http://127.0.0.1:18499/fixture/branding?logo=${encodeURIComponent(`http://127.0.0.1:18499/fixture/${kind}.svg`)}`);
    await page.goto('/login');
    const logo = page.locator('.helpdesk-login-logo');
    await expect(logo).toHaveAttribute('src', new RegExp(`${kind}\\.svg$`));
    await expect.poll(() => logo.evaluate((image: HTMLImageElement) => image.naturalWidth / image.naturalHeight)).toBe(3);
    for (const theme of ['Dark', 'Light'] as const) {
      await selectTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme.toLowerCase());
      await expect(logo).toHaveCSS('object-fit', 'contain');
      await expect(logo).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await page.screenshot({ path: info.outputPath(`${kind}-${theme}.png`), fullPage: true });
    }
  }
});

for (const width of [1440, 320, 390]) {
  test(`long branding keeps the local action compact at ${width}px`, async ({ page, request }, info) => {
    await request.get('http://127.0.0.1:18499/fixture/branding?name=Example%20Organization%20Service%20Desk%20Command%20Center');
    await page.setViewportSize({ width, height: 960 });
    await page.goto('/login');
    if (info.project.name === 'Oidc') { await expect(page.getByTestId('local-login-form')).toHaveCount(0); return; }
    await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
    const action = page.getByRole('button', { name: 'Sign in', exact: true });
    await expect(action).toBeVisible();
    expect((await action.boundingBox())!.height).toBeLessThan(70);
    await action.focus();
    await expect(action).toBeFocused();
    await assertNoHorizontalOverflow(page);
    await page.locator('html').evaluate(element => { element.style.fontSize = '200%'; });
    await expect(action).toBeVisible();
    await assertNoHorizontalOverflow(page);
  });
}

test('long brand heading is fully visible at 320px with 200 percent text', async ({ page, request }, info) => {
  for (const name of ['Example Organization Service Desk Command Center', 'ExampleOrganizationWithAnUnbrokenLongServiceDeskName']) {
    await request.get(`http://127.0.0.1:18499/fixture/branding?name=${encodeURIComponent(name)}`);
    await page.setViewportSize({ width: 320, height: 960 });
    await page.goto('/login');
    await page.locator('html').evaluate(element => { element.style.fontSize = '200%'; });
    for (const theme of ['Light', 'Dark'] as const) {
      await selectTheme(page, theme);
      const title = page.locator('.helpdesk-login-title');
      await expect(title).toHaveText(name);
      const geometry = await title.evaluate(element => {
        const panel = element.closest('.helpdesk-login-panel')!.getBoundingClientRect();
        const range = document.createRange(); range.selectNodeContents(element);
        return { panel: { left: panel.left, right: panel.right, top: panel.top, bottom: panel.bottom },
          text: Array.from(range.getClientRects(), box => ({ left: box.left, right: box.right, top: box.top, bottom: box.bottom })) };
      });
      expect(geometry.text.length).toBeGreaterThan(0);
      for (const box of geometry.text) {
        expect(box.left).toBeGreaterThanOrEqual(geometry.panel.left - 1);
        expect(box.right).toBeLessThanOrEqual(geometry.panel.right + 1);
        expect(box.top).toBeGreaterThanOrEqual(geometry.panel.top - 1);
        expect(box.bottom).toBeLessThanOrEqual(geometry.panel.bottom + 1);
      }
      await assertNoHorizontalOverflow(page);
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    }
  }
});

test('public exchange from email keeps independent ticket tabs, clean history and scoped raster access', async ({ page, request, baseURL, context }) => {
  await request.get('http://127.0.0.1:18499/fixture/public-stats?reset=1');
  await page.goto('http://localhost:18499/fixture/email?to=' + encodeURIComponent(baseURL!));
  await page.getByRole('link', { name: 'Open ticket from email' }).click();
  await expect(page).toHaveURL(baseURL + '/public/link');
  expect((await (await request.get('http://127.0.0.1:18499/fixture/public-stats')).json()).exchanges).toBe(0);
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { name: 'Signed public fixture ticket' })).toBeVisible();
  const clean = page.url();
  expect(new URL(clean).search + new URL(clean).hash).toBe('');
  const grant = (await context.cookies(clean)).find(cookie => cookie.name === '__Secure-RatelDeskPublicGrant');
  expect(grant).toMatchObject({ secure: true, httpOnly: true, sameSite: 'Strict', path: new URL(clean).pathname.replace('/ticket', '') });
  await expect.poll(() => page.getByAltText('Public fixture screenshot').evaluate((image: HTMLImageElement) => image.naturalWidth)).toBe(1);

  const second = await context.newPage();
  await second.goto('/public/link#' + 'b'.repeat(43));
  await second.getByRole('button', { name: 'Continue' }).click();
  await expect(second.getByRole('heading', { name: 'Second synthetic ticket' })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Signed public fixture ticket' })).toBeVisible();
  await page.goBack();
  expect(new URL(page.url()).search + new URL(page.url()).hash).toBe('');
  await page.goto('/public/link#' + 'a'.repeat(43));
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page).toHaveURL(clean);
  const response = await page.request.get(clean);
  expect(response.headers()['referrer-policy']).toBe('no-referrer');
  expect(response.headers()['cache-control']).toContain('no-store');
  await page.evaluate(() => {
    const link = document.createElement('a');
    link.textContent = 'Open external fixture';
    link.href = 'http://localhost:18499/fixture/referrer';
    document.body.append(link);
  });
  const external = page.waitForResponse(result => new URL(result.url()).pathname === '/fixture/referrer');
  await page.getByRole('link', { name: 'Open external fixture' }).click();
  expect(await (await external).json()).toEqual({ referrer: null });
});

test('public approval requires explicit antiforgery-protected form submission', async ({ page, request }) => {
  await request.get('http://127.0.0.1:18499/fixture/public-stats?reset=1');
  await page.goto('/public/link#' + 'c'.repeat(43));
  await page.getByRole('button', { name: 'Continue' }).click();
  const approve = page.getByRole('button', { name: 'Approve', exact: true });
  await expect(approve).toBeVisible();
  const action = page.url() + '/decision';
  expect((await page.request.get(action)).status()).toBe(405);
  expect((await page.request.post(action, { form: { decision: 'approve' } })).status()).toBe(400);
  await page.reload();
  await expect(approve).toBeVisible();
  await approve.click();
  await expect(page.getByText('This approval is no longer pending.')).toBeVisible();
});

test('public ticket CAPTCHA refresh and submission use independent server-side request proofs', async ({ page }) => {
  await openPublicTicketReview(page);
  await expect(page.getByAltText('CAPTCHA')).toBeVisible();
  await page.getByRole('textbox', { name: 'CAPTCHA', exact: true }).fill('123456');
  await expect(page.getByRole('button', { name: 'Submit Ticket', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'New code' }).click();
  const answer = page.getByRole('textbox', { name: 'CAPTCHA', exact: true });
  await expect(answer).toHaveValue('');
  await answer.fill('123456');
  await page.getByRole('button', { name: 'Submit Ticket', exact: true }).click();
  await expect(page.getByText('Ticket submitted successfully', { exact: true })).toBeVisible();
  await expect(page.getByText('INC-BROWSER-FIXTURE', { exact: true })).toBeVisible();
});

test('legacy signed raster navigation with an OIDC session uses the real asset API', async ({ page }, info) => {
  const key = process.env.HELPDESK_E2E_ASSET_SIGNING_KEY;
  test.skip(info.project.name === 'Local' || !key, 'Requires the real asset API and OIDC fixture');
  await page.goto('/login');
  await page.getByRole('link', { name: 'Sign in with Example Organization' }).click();
  await page.waitForURL('**/home');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expectSignedRasterIsolation(page, key!);
  expect((await page.request.get('/session/access')).status()).toBe(200);
});

for (const entry of ['desktop', 'mobile', 'nav'] as const) {
  test('OIDC logout uses the provider end-session through the ' + entry + ' form', async ({ page, context }, info) => {
    test.skip(info.project.name === 'Local', 'The OIDC fixture serves OIDC and Hybrid modes');
    if (entry === 'mobile') await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/login');
    await page.getByRole('link', { name: 'Sign in with Example Organization' }).click();
    await page.waitForURL('**/home');
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    const appCookie = '__Host-Helpdesk.Auth';
    expect((await context.cookies()).some(cookie => cookie.name === appCookie)).toBe(true);
    for (const method of ['GET', 'HEAD']) {
      const harmless = await page.request.fetch('/logout', { method, maxRedirects: 0 });
      expect(harmless.status()).toBe(405);
      expect((await context.cookies()).some(cookie => cookie.name === appCookie)).toBe(true);
    }
    if (entry === 'desktop') await page.locator('[aria-label="Account settings"] button').click();
    if (entry === 'mobile') await page.locator('.helpdesk-appbar-mobile-menu button').click();
    const ended = page.waitForResponse(response => new URL(response.url()).pathname === '/end-session');
    await page.getByTestId(entry + '-logout').click();
    expect((await ended).status()).toBe(302);
    await expect.poll(async () => (await context.cookies()).some(cookie => cookie.name === appCookie)).toBe(false);
    await page.goto('/login');
    await expect(page.getByRole('link', { name: 'Sign in with Example Organization' })).toBeVisible();
  });
}

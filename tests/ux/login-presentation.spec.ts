import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, selectTheme } from './auth';

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
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      const local = page.getByTestId('local-login-form');
      const provider = page.getByRole('link', { name: 'Sign in with Example Organization' });
      await expect(local).toHaveCount(info.project.name === 'Oidc' ? 0 : 1);
      await expect(provider).toHaveCount(info.project.name === 'Local' ? 0 : 1);
      if (info.project.name !== 'Oidc') {
        await expect(local).toHaveAttribute('data-interactive', 'true');
        await expect(local.getByRole('button', { name: 'Sign in to RatelDesk' })).toHaveCount(1);
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

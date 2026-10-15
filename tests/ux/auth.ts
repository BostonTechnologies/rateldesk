import { expect, Page } from '@playwright/test';

const themeStorageKey = 'helpdesk.theme.preference';

export async function authenticate(page: Page): Promise<void> {
  const aiAgentToken = process.env.HELPDESK_E2E_AI_TOKEN;
  const authMode = process.env.HELPDESK_E2E_AUTH_MODE;

  if (aiAgentToken) {
    await page.goto('/login-ai-agent');
    await page.evaluate(async (token) => {
      await fetch('/auth/ai-agent/exchange', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token, returnUrl: '/home' }),
        redirect: 'manual'
      });
    }, aiAgentToken);
  } else if (authMode === 'development') {
    await page.goto('/auth/development');
  } else {
    throw new Error(
      'Set HELPDESK_E2E_AI_TOKEN for Authentik-backed dev, or HELPDESK_E2E_AUTH_MODE=development for a local Development-only run.');
  }

  await page.goto('/home');
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

export async function clearThemeOverride(page: Page): Promise<void> {
  await page.evaluate((key) => window.localStorage.removeItem(key), themeStorageKey);
}

export async function assertNoHorizontalOverflow(page: Page): Promise<void> {
  await expect.poll(
    () => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth),
    { message: 'page should not require horizontal scrolling' }
  ).toBeLessThanOrEqual(2);
}

export async function selectTheme(page: Page, theme: 'System' | 'Light' | 'Dark'): Promise<void> {
  const desktopButton = page.getByTestId('theme-preference-menu').locator('button');
  const useDesktopMenu = await desktopButton.isVisible();
  const menuButton = useDesktopMenu
    ? desktopButton
    : page.locator('.helpdesk-appbar-mobile-menu').getByRole('button');
  const option = useDesktopMenu
    ? page.getByTestId(`theme-option-${theme.toLowerCase()}`)
    : page.getByRole('menuitem', { name: theme, exact: true });

  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await option.waitFor({ state: 'visible', timeout: 2_000 });
    } catch {
      await menuButton.click();
    }

    try {
      await option.waitFor({ state: 'visible', timeout: 5_000 });
      await option.click();
      const expected = theme === 'System'
        ? await page.evaluate(() => window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme.toLowerCase();
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', expected);
      return;
    } catch {
      // MudBlazor can recreate its portal while the theme provider settles after a reload.
    }
  }

  throw new Error(`The ${theme} theme option did not become available.`);
}

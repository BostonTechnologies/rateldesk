import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate } from './auth';

async function expectDrawerFullyClosed(page: Parameters<typeof authenticate>[0]): Promise<void> {
  const drawer = page.getByTestId('app-navigation-drawer');

  await expect.poll(async () => drawer.evaluate((element) => {
    const drawerRect = element.getBoundingClientRect();
    const mainRect = document.querySelector<HTMLElement>('[data-testid="app-main-content"]')?.getBoundingClientRect();
    const visibleNavigation = Array.from(element.querySelectorAll<HTMLElement>('.mud-icon-root, .mud-nav-link'))
      .some((navigation) => {
        const rect = navigation.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0 && rect.left < window.innerWidth && rect.right > 0;
      });

    return element.classList.contains('mud-drawer--closed') &&
      drawerRect.right <= 0 &&
      (mainRect?.left ?? Number.POSITIVE_INFINITY) <= 1 &&
      !visibleNavigation;
  })).toBe(true);
}

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

test('Email Settings opens as a standalone page from the expanded administration navigation', async ({ page }, testInfo) => {
  await page.goto('/admin/email-settings');

  await expect(page.getByRole('heading', { name: 'Email Settings / Mailbox Configuration' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Inbound provider' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save' })).toBeVisible();
  await expect(page.locator('.mud-dialog')).toHaveCount(0);

  const drawer = page.getByTestId('app-navigation-drawer');
  await expect(drawer.getByRole('link', { name: 'Mailbox Configuration' })).toBeVisible();
  await expect(drawer.getByRole('link', { name: 'Pending Emails' })).toBeVisible();
  await expect(drawer.getByText('Email Design', { exact: true })).toHaveCount(0);
  await expect(drawer.getByText('Connectivity', { exact: true })).toHaveCount(0);
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('email-settings-desktop.png'), fullPage: true });
});

test('Automation navigation preserves the Orchestrator destination and collapsible groups', async ({ page }) => {
  const drawer = page.getByTestId('app-navigation-drawer');
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');

  await drawer.getByRole('button', { name: 'Toggle Automation' }).click();
  const orchestrator = drawer.getByRole('link', { name: 'Orchestrator' });
  await expect(orchestrator).toBeVisible();
  await orchestrator.click();

  await expect(page).toHaveURL(/\/settings\/connectivity$/);
  await expect(page.getByRole('heading', { name: 'Orchestrator' })).toBeVisible();
  await expect(page.getByText('Connectivity and validation', { exact: true })).toBeVisible();
});

test('Email Settings is usable from the phone drawer without horizontal overflow', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await expectDrawerFullyClosed(page);

  await page.getByTestId('navigation-toggle').click();
  const drawer = page.locator('[data-testid="app-navigation-drawer"]:visible');
  await expect(drawer.getByRole('link', { name: 'Home' })).toBeInViewport();
  await drawer.getByRole('button', { name: 'Toggle Administration' }).click();
  await drawer.getByText('Email Settings', { exact: true }).click();
  await drawer.getByRole('link', { name: 'Mailbox Configuration' }).click();

  await expect(page).toHaveURL(/\/admin\/email-settings$/);
  await expect(page.getByRole('heading', { name: 'Email Settings / Mailbox Configuration' })).toBeVisible();
  await expectDrawerFullyClosed(page);
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('email-settings-mobile.png'), fullPage: true });
});

import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate, selectTheme } from './auth';

test.setTimeout(120_000);

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
  await expect(page.getByRole('combobox', { name: 'Inbound provider' })).toHaveCount(0);
  await expect(page.getByRole('complementary', { name: 'Selected mailbox status' })).toBeVisible();
  await expect(page.getByTestId('mailbox-command-bar')).toBeVisible();
  await expect(page.getByRole('tab', { name: /Outgoing/ })).toBeVisible();
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

test('mailbox workspace remains readable across themes and responsive widths', async ({ page }, testInfo) => {
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  for (const theme of ['Light', 'Dark', 'System'] as const) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await selectTheme(page, theme);
    for (const width of [1440, 1280, 1024, 768, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await assertNoHorizontalOverflow(page);
      await expect(page.getByRole('complementary', { name: 'Selected mailbox status' })).toBeVisible();
      const picker = page.locator('.mailbox-picker');
      await expect(picker).toBeVisible();
      const pickerWidth = await picker.evaluate(element => element.getBoundingClientRect().width);
      if (width >= 1280) expect(pickerWidth).toBeLessThanOrEqual(420);
      if (width === 390) await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeInViewport();
      if (width === 1280 && theme !== 'System') {
        await expect(page.getByTestId('app-navigation-drawer')
          .getByRole('link', { name: 'Mailbox Configuration' })).toBeVisible();
        const sections = page.getByRole('group', { name: 'Mailbox sections' });
        for (const name of ['Incoming', 'Outgoing', 'Processing', 'Activity']) {
          const tab = sections.getByRole('button', { name, exact: true });
          await expect(tab).toBeInViewport({ ratio: 1 });
          const withinEditor = await tab.evaluate(element => {
            const tabRect = element.getBoundingClientRect();
            const editorRect = element.closest('.mailbox-editor')!.getBoundingClientRect();
            return tabRect.left >= editorRect.left && tabRect.right <= editorRect.right;
          });
          expect(withinEditor).toBe(true);
          await tab.click();
          await expect(tab).toHaveAttribute('aria-pressed', 'true');
          if (name === 'Outgoing')
            await expect(page.getByRole('button', { name: 'Save outgoing' })).toBeVisible();
          else if (name === 'Processing')
            await expect(page.getByText(/Initial import:/)).toBeVisible();
          else if (name === 'Activity')
            await expect(page.getByRole('heading', { name: 'Incoming activity' })).toBeVisible();
          if (name !== 'Incoming')
            await page.screenshot({ path: testInfo.outputPath(`mailbox-${theme.toLowerCase()}-1280-${name.toLowerCase()}-full.png`),
              fullPage: true, animations: 'disabled' });
        }
        await sections.getByRole('button', { name: 'Incoming' }).click();
      }
      await page.screenshot({ path: testInfo.outputPath(`mailbox-${theme.toLowerCase()}-${width}-viewport.png`) });
      await page.screenshot({ path: testInfo.outputPath(`mailbox-${theme.toLowerCase()}-${width}-full.png`), fullPage: true });
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await selectTheme(page, 'Light');
  await page.getByTestId('navigation-toggle').click();
  await expectDrawerFullyClosed(page);
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('mailbox-drawer-collapsed-1440-viewport.png') });
  await page.screenshot({ path: testInfo.outputPath('mailbox-drawer-collapsed-1440-full.png'), fullPage: true });
  await page.getByTestId('navigation-toggle').click();
  await expect(page.getByTestId('app-navigation-drawer')
    .getByRole('link', { name: 'Mailbox Configuration' })).toBeVisible();
  for (const name of ['Outgoing', 'Processing', 'Activity']) {
    const tab = page.getByRole('tab', { name: new RegExp(name) });
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');
    if (name === 'Processing') {
      await expect(page.getByRole('combobox', { name: 'Initial import' })).toHaveCount(0);
      await expect(page.getByText(/Initial import:/)).toBeVisible();
    }
    if (name === 'Activity') {
      await expect(page.getByRole('heading', { name: 'Incoming activity' })).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Outgoing activity' })).toBeVisible();
    }
    await page.screenshot({ path: testInfo.outputPath(`mailbox-${name.toLowerCase()}-full.png`),
      fullPage: true, animations: 'disabled' });
  }
  await page.setViewportSize({ width: 390, height: 900 });
  await expectDrawerFullyClosed(page);
  await expect(page.getByRole('tab', { name: /Outgoing/ })).toHaveCount(0);
  const mobileTabs = page.getByRole('group', { name: 'Mailbox sections' });
  await mobileTabs.scrollIntoViewIfNeeded();
  for (const name of ['Incoming', 'Outgoing', 'Processing', 'Activity'])
    await expect(mobileTabs.getByRole('button', { name })).toBeInViewport({ ratio: 0.95 });
  for (const name of ['Outgoing', 'Processing', 'Activity']) {
    const tab = mobileTabs.getByRole('button', { name });
    await tab.click();
    await expect(tab).toHaveAttribute('aria-pressed', 'true');
    await assertNoHorizontalOverflow(page);
    if (name === 'Outgoing')
      await expect(page.getByRole('button', { name: 'Save outgoing' })).toBeVisible();
    else if (name === 'Processing')
      await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeInViewport();
    else
      await expect(page.getByRole('button', { name: 'Refresh activity' })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`mailbox-${name.toLowerCase()}-390-full.png`),
      fullPage: true, animations: 'disabled' });
  }
  const incoming = mobileTabs.getByRole('button', { name: 'Incoming' });
  await incoming.focus();
  await expect(incoming).toBeFocused();
  await incoming.press('Space');
  await expect(incoming).toHaveAttribute('aria-pressed', 'true');
});

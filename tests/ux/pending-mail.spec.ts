import { expect, test, Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
test.setTimeout(60_000);
const fixture = 'http://127.0.0.1:18700/fixture';
async function ready(page: Page, route = '/admin/pending-emails') {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
}
test.beforeEach(async ({ request, page }) => { await request.get(`${fixture}/reset`); await page.goto('/auth/development'); });
for (const theme of ['light', 'dark']) for (const width of [320, 768, 1440, 1920]) {
  test(`pending mail ${theme} ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    await ready(page);
    await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
    await expect(page.locator('.pending-mail tbody tr')).toHaveCount(25);
    await page.getByRole('button', { name: 'Details', exact: true }).first().click();
    await expect(page.getByRole('region', { name: 'Delivery details' })).toBeVisible();
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`pending-${theme}-${width}.png`), fullPage: true });
  });
}
test('server paging, filter URL, Back and explicit selection scope', async ({ page, request }) => {
  await ready(page);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByText('Page 2 of 49', { exact: true })).toBeVisible();
  await page.locator('.pending-mail tbody input[type="checkbox"]').first().check();
  await expect(page.getByText('1205 deliveries · 1 selected records on this page')).toBeVisible();
  await page.getByRole('textbox', { name: 'Search ticket or recipient' }).fill('INC-SYN-0001');
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
  await expect(page.getByText('1 deliveries · 0 selected records on this page')).toBeVisible();
  await expect(page.getByRole('link', { name: 'INC-SYN-0001' })).toHaveAttribute('href', '/incidents/synthetic-1');
  await page.goBack();
  await expect(page.getByText('Page 2 of 49', { exact: true })).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.some((q: any) => q.page === '1')).toBe(true);
  expect(state.requests.some((q: any) => q.search === 'INC-SYN-0001' && q.page === '0')).toBe(true);
});
test('applied filter removal is reachable by Tab and resets URL, page and selection', async ({ page, request }) => {
  await ready(page, '/admin/pending-emails?search=INC-SYN-0&page=1');
  await expect(page.getByText('Page 2 of 40', { exact: true })).toBeVisible();
  await page.locator('.pending-mail tbody input[type="checkbox"]').first().check();
  await expect(page.getByText('999 deliveries · 1 selected records on this page')).toBeVisible();
  await page.getByRole('button', { name: 'Apply filters', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Clear filters', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Remove filter search: INC-SYN-0', exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
  await expect(page.getByText('Page 1 of 49', { exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('search')).toBeNull();
  expect(new URL(page.url()).searchParams.get('page')).toBe('0');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.actions).toHaveLength(0);
  expect(state.requests.at(-1).page).toBe('0');
  expect(state.requests.at(-1).search ?? '').toBe('');
});
test('bounded selection, cancelled confirmation, mixed results, removal and refreshed totals', async ({ page, request }) => {
  await ready(page);
  await page.locator('.pending-mail thead input[type="checkbox"]').check();
  await expect(page.getByText('1205 deliveries · 25 selected records on this page')).toBeVisible();
  // A Blazor click may complete before its server-side confirmation reaches the browser.
  // Finish dismissing that dialog before registering a handler for the next action.
  const cancellationHandled = new Promise<void>((resolve, reject) => {
    page.once('dialog', async dialog => {
      try {
        expect(dialog.type()).toBe('confirm');
        expect(dialog.message()).toContain('25 explicitly selected record(s)');
        await dialog.dismiss();
        resolve();
      } catch (error) { reject(error); }
    });
  });
  await Promise.all([cancellationHandled, page.getByRole('button', { name: 'Cancel selected', exact: true }).click()]);
  expect((await (await request.get(`${fixture}/state`)).json()).actions).toHaveLength(0);
  page.on('dialog', dialog => dialog.type() === 'prompt' ? dialog.accept('Synthetic operator review') : dialog.accept());
  await page.getByRole('button', { name: 'Retry selected', exact: true }).click();
  await expect(page.getByText(/Action results \(25 selected records\)/)).toBeVisible();
  await expect(page.getByText(/ReviewRequired — Individual review required/).first()).toBeVisible();
  await page.getByRole('button', { name: 'Remove queue entry', exact: true }).first().click();
  await expect(page.getByText(/Action results \(1 selected records\)/)).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.actions[0].ids).toHaveLength(25); expect(state.actions[0].confirmed).toBe(true);
  expect(state.actions[1].ids).toHaveLength(1);
  await expect(page.getByText(/0 selected records on this page/)).toBeVisible();
});
test('failure and zero states remain distinct and recover, keyboard at 200 percent zoom', async ({ page, request }) => {
  await ready(page);
  await request.get(`${fixture}/fail?enabled=true`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText('Delivery list unavailable. Refresh to try again.')).toBeVisible({ timeout: 35_000 });
  await request.get(`${fixture}/fail?enabled=false`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
  await page.getByRole('textbox', { name: 'Search ticket or recipient' }).fill('does-not-exist');
  await page.getByRole('button', { name: 'Apply filters', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByText('No deliveries match these filters.')).toBeVisible();
  await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
  await assertNoHorizontalOverflow(page);
});

import { expect, test, type Page } from '@playwright/test';
const fixture = 'http://127.0.0.1:18500/fixture';
const baseline = process.env.HELPDESK_DIRECTORY_BASELINE === 'true';
const routes = [
  ['/admin/customers', 'Customers', 133],
  ['/admin/organizations', 'Organizations', 137],
  ['/admin/categories', 'Ticket Categories', 143]
] as const;
async function open(page: Page, route: string) {
  await page.goto(route, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function select(page: Page, label: string, option: string) {
  await page.getByRole('combobox', { name: label, exact: true }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}
async function settle(page: Page) {
  await page.evaluate(async () => { await document.fonts.ready; });
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation =>
    animation.playState !== 'finished' && animation.playState !== 'idle' && Number.isFinite(animation.effect?.getComputedTiming().endTime)).length)).toBe(0);
}
test.beforeEach(async ({ page, request }) => {
  await request.get(`${fixture}/reset`);
  await request.get(`${fixture}/directories`);
  await page.goto('/auth/development');
});
test('directory synthetic desktop tablet and mobile evidence in both themes', async ({ page }, info) => {
  test.setTimeout(300_000);
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    for (const [width, height] of [[1920, 1080], [1440, 900], [1366, 768], [768, 1024], [390, 844]]) {
      await page.setViewportSize({ width, height });
      for (const [route, heading, total] of routes) {
        await open(page, route);
        await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
        if (!baseline) {
          await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
          await expect(page.getByRole('status').filter({ hasText: `${total} results` })).toBeVisible();
          await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(25);
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
          expect(overflow).toBeLessThanOrEqual(2);
          const actions = page.locator('.directory-actions').first();
          const tops = await actions.locator('button').evaluateAll(elements => elements.map(element => element.getBoundingClientRect().top));
          expect(Math.max(...tops) - Math.min(...tops)).toBeLessThanOrEqual(1);
          await page.locator('.mud-table-pagination').scrollIntoViewIfNeeded();
          await expect(page.locator('.mud-table-pagination')).toBeInViewport({ ratio: 0.95 });
          const container = page.locator('.directory-table-region .mud-table-container');
          const headerBefore = await container.locator('th').first().boundingBox();
          const footerBefore = await page.locator('.mud-table-pagination').boundingBox();
          await container.evaluate(element => { element.scrollTop = element.scrollHeight; });
          const headerAfter = await container.locator('th').first().boundingBox();
          const footerAfter = await page.locator('.mud-table-pagination').boundingBox();
          expect(Math.abs(headerAfter!.y - headerBefore!.y)).toBeLessThanOrEqual(1);
          expect(Math.abs(footerAfter!.y - footerBefore!.y)).toBeLessThanOrEqual(1);
          await container.evaluate(element => { element.scrollTop = 0; });
        }
        await settle(page);
        await page.screenshot({ path: info.outputPath(`${baseline ? 'before' : 'after'}-${route.split('/').at(-1)}-${theme}-${width}x${height}.png`), fullPage: true });
      }
    }
  }
});

test('complete pagers and URL query restore keep bounded customer loading', async ({ page, request }) => {
  test.skip(baseline, 'Before evidence uses the previous rendered pages.');
  await open(page, '/admin/customers');
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(25);
  await page.getByRole('button', { name: /Last page/i }).click();
  await expect(page).toHaveURL(/page=5/);
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(8);
  const search = page.getByRole('textbox', { name: 'Search name, email or organization', exact: true });
  await search.fill('person132');
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(1);
  await expect(page.getByText('person132@example.invalid', { exact: true })).toBeVisible();
  await expect(page).not.toHaveURL(/page=5/);
  await search.focus(); await expect(search).toBeFocused();
  const retained = page.url();
  await page.goto('/home'); await page.goBack();
  await expect(page).toHaveURL(retained);
  await expect(page.getByRole('textbox', { name: 'Search name, email or organization', exact: true })).toHaveValue('person132');
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(1);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.reads.filter((read: any) => /customers\/[^/]+\/auth\/status/.test(read.path))).toHaveLength(0);
  const directory = await (await request.get(`${fixture}/directories/state`)).json();
  expect(directory.actions).toEqual([]);
});

test('query changes cancel superseded searches and recover from error without discarding rows', async ({ page, request }) => {
  test.skip(baseline, 'Before evidence uses the previous rendered pages.');
  await open(page, '/admin/customers');
  const search = page.getByRole('textbox', { name: 'Search name, email or organization', exact: true });
  await search.fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/directories/state`)).json()).reads.some((row: any) => row.query.includes('search=slow'))).toBe(true);
  await search.fill('person132');
  await expect(page.getByText('person132@example.invalid', { exact: true })).toBeVisible();
  await request.get(`${fixture}/directories/fault?mode=error`);
  await page.getByRole('button', { name: 'Refresh directory', exact: true }).click();
  await expect(page.getByText(/directory could not be loaded \(HTTP 503\)/)).toBeVisible();
  await expect(page.getByText('person132@example.invalid', { exact: true })).toBeVisible();
  await request.get(`${fixture}/directories/fault`);
  await page.getByRole('button', { name: 'Refresh directory', exact: true }).click();
  await expect(page.getByText(/directory could not be loaded/)).not.toBeVisible();
  await search.fill('no-matching-customer');
  await expect(page.getByText('No customers match these filters.')).toBeVisible();
});

test('exact category type controls and explicit create type retain accessible protections', async ({ page }) => {
  test.skip(baseline, 'Before evidence uses the previous rendered pages.');
  await open(page, '/admin/categories0');
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(25);
  await select(page, 'Ticket type', 'Request');
  await expect(page).toHaveURL(/type=2/);
  await expect(page.locator('.directory-table-region tbody tr')).toHaveCount(25);
  await expect(page.locator('.directory-table-region tbody tr').first().getByText('Request', { exact: true })).toBeVisible();
  await select(page, 'Ticket type', 'All types');
  await page.getByRole('button', { name: 'New category', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('combobox', { name: /^Type/ })).toHaveText('');
  await dialog.getByRole('textbox', { name: /^Name/ }).fill('Explicit type required');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByText('Choose a ticket type.')).toBeVisible();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  const builtIn = page.locator('tbody tr').filter({ hasText: 'Built-in · read-only' }).first();
  await expect(builtIn.getByRole('button', { name: 'Delete category', exact: true })).toBeDisabled();
});

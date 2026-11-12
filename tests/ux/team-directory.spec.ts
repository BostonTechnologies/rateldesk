import { captureTables } from './table-evidence';
import { expect, test, Page } from '@playwright/test';
const fixture = process.env.HELPDESK_TEAM_FIXTURE_BASE ?? 'http://127.0.0.1:18500/fixture/team';
const baseline = process.env.HELPDESK_ADMIN_BASELINE === 'true';
test.use({ screenshot: 'off', trace: 'off' });
async function open(page: Page) {
  await page.goto('/admin/users');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
test.beforeEach(async ({ page, request }) => { await request.get(fixture); await page.goto('/auth/development'); });

test('Team synthetic before and after evidence and sticky action geometry', async ({ page }, info) => {
  test.setTimeout(240_000);
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    for (const [width, height] of [[1920, 1080], [1440, 900], [1366, 768], [768, 1024], [390, 844]]) {
      await page.setViewportSize({ width, height }); await open(page);
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      await page.evaluate(async () => { await document.fonts.ready; });
      if (!baseline) {
        const directory = page.locator('.team-directory');
        await expect(directory.getByText('137 team members', { exact: true })).toBeVisible();
        await expect(directory.locator('.team-row-actions')).toHaveCount(25);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 2);
        expect(overflow).toBe(false);
        const actions = directory.locator('.team-row-actions').first();
        expect(await actions.locator('button').evaluateAll(buttons => new Set(buttons.map(button => Math.round(button.getBoundingClientRect().top))).size)).toBe(1);
        const scroll = directory.locator('.mud-table-container');
        await scroll.scrollIntoViewIfNeeded();
        const before = await directory.locator('thead th').first().boundingBox();
        const pagerBefore = await directory.locator('.mud-table-pagination').boundingBox();
        await scroll.evaluate(element => element.scrollTop = 200);
        const after = await directory.locator('thead th').first().boundingBox();
        const pagerAfter = await directory.locator('.mud-table-pagination').boundingBox();
        expect(Math.abs(before!.y - after!.y)).toBeLessThanOrEqual(2);
        expect(Math.abs(pagerBefore!.y - pagerAfter!.y)).toBeLessThanOrEqual(2);
        await directory.locator('.mud-table-pagination').scrollIntoViewIfNeeded();
        await expect(directory.locator('.mud-table-pagination')).toBeInViewport();
        await scroll.evaluate(element => element.scrollTop = 0);
      }
      await page.evaluate(() => window.scrollTo(0, 0));
      await captureTables(page); await page.screenshot({ path: info.outputPath(`${baseline ? 'before' : 'after'}-team-${theme}-${width}.png`), fullPage: true });
    }
  }
});

test.describe('Team bounded queries and explicit lifecycle actions', () => {
  test.skip(baseline, 'Baseline is used for before evidence only.');
  test('search reaches beyond one hundred records and no per-row account HTTP requests occur', async ({ page, request }) => {
    await open(page);
    await expect(page.locator('tbody tr')).toHaveCount(25);
    await page.getByRole('textbox', { name: 'Search name or email', exact: true }).fill('person136');
    await expect(page.getByText('Synthetic person 136', { exact: true })).toBeVisible();
    await expect(page.getByText('1 team members', { exact: true })).toBeVisible();
    const state = await (await request.get(`${fixture}/state`)).json();
    expect(state.requests.some((entry: { path: string }) => entry.path.endsWith('/status'))).toBe(false);
    await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
    await expect(page.locator('tbody tr')).toHaveCount(25);
    const pageSize = page.locator('.mud-table-pagination').getByRole('combobox');
    for (const size of [50, 100, 25]) {
      await pageSize.click(); await page.getByRole('option', { name: String(size), exact: true }).click();
      await expect(page.locator('tbody tr')).toHaveCount(size);
    }
    const emailSort = page.getByRole('button', { name: 'Sort by email', exact: true });
    await emailSort.focus(); await emailSort.press('Enter');
    await expect(page.locator('th[aria-sort=ascending]')).toContainText('Email');
    await expect(emailSort).toBeFocused();
    await page.getByRole('button', { name: 'Last page', exact: true }).click();
    await expect(page.locator('tbody tr')).toHaveCount(12);
    await expect(page).toHaveURL(/page=6/);
    await page.reload(); await expect(page.locator('tbody tr')).toHaveCount(12);
  });
  test('Delete and Disable are separately named operations and errors retain the dialog', async ({ page, request }) => {
    await open(page);
    const row = page.locator('tbody tr').first();
    await row.getByRole('button', { name: 'Delete user', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('permanently retires');
    await request.get(`${fixture}/reject-delete?value=true`);
    await page.getByRole('dialog').getByRole('button', { name: 'Delete user', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Synthetic deletion rejected');
    await request.get(`${fixture}/reject-delete?value=false`);
    await page.getByRole('dialog').getByRole('button', { name: 'Delete user', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeHidden();
    await expect(page.getByText('136 team members', { exact: true })).toBeVisible();
    await page.locator('tbody tr').first().getByRole('button', { name: 'Disable sign-in', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('reversible account setting');
    await page.getByRole('dialog').getByRole('button', { name: 'Disable sign-in', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeHidden();
    const state = await (await request.get(`${fixture}/state`)).json();
    expect(state.requests.filter((entry: { method: string }) => entry.method === 'DELETE')).toHaveLength(2);
    expect(state.requests.filter((entry: { path: string }) => entry.path.endsWith('/disable'))).toHaveLength(1);
  });
  test('state, cohort, role and organization compose over the full directory and clear together', async ({ page }) => {
    await open(page);
    await page.getByRole('combobox', { name: 'Application access', exact: true }).click();
    await page.getByRole('option', { name: 'Disabled', exact: true }).click();
    await expect(page.getByText('28 team members', { exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'Self-service cohort', exact: true }).click();
    await page.getByRole('option', { name: 'Testing', exact: true }).click();
    await expect(page.getByText('14 team members', { exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Role or scoped role key', exact: true }).fill('Technician');
    await expect(page.getByText('5 team members', { exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'Organization', exact: true }).fill('Synthetic team 0');
    await page.getByRole('option', { name: 'Synthetic team 0', exact: true }).click();
    await expect(page.getByText('2 team members', { exact: true })).toBeVisible();
    await expect(page).toHaveURL(/organizationId=team-org-0/);
    await page.reload();
    await expect(page.getByText('2 team members', { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Organization', exact: true })).toHaveValue('Synthetic team 0');
    await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
    await expect(page.getByText('137 team members', { exact: true })).toBeVisible();
    await expect(page.locator('tbody tr')).toHaveCount(25);
  });
  test('late search results cannot replace a newer query, and errors are retryable', async ({ page, request }) => {
    await open(page);
    const search = page.getByRole('textbox', { name: 'Search name or email', exact: true });
    await search.fill('held');
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).requests.some((entry: { query?: { search: string } }) => entry.query?.search === 'held')).toBe(true);
    await search.fill('person132');
    await expect(page.getByText('Synthetic person 132', { exact: true })).toBeVisible();
    await request.get(`${fixture}/release`);
    await expect(page.getByText('Synthetic person 132', { exact: true })).toBeVisible();
    await request.get(`${fixture}/unavailable?value=true`);
    await page.getByRole('button', { name: 'Refresh team', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('query is retained', { timeout: 30_000 });
    await request.get(`${fixture}/unavailable?value=false`);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(page.getByText('Synthetic person 132', { exact: true })).toBeVisible();
    await search.fill('no such identity'); await expect(page.getByText('No team members match these filters.', { exact: true })).toBeVisible();
  });
  test('deleting the final remaining row clamps the current final page and returns focus to the directory', async ({ page, request }) => {
    await request.get(`${fixture}/count?value=126`);
    await open(page);
    await page.getByRole('button', { name: 'Last page', exact: true }).click();
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await page.getByRole('button', { name: 'Delete user', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete user', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeHidden();
    await expect(page.getByText('125 team members', { exact: true })).toBeVisible();
    await expect(page.locator('tbody tr')).toHaveCount(25);
    await expect(page).toHaveURL(/page=5/);
    await expect(page.getByRole('region', { name: /^Team directory\./ })).toBeFocused();
  });
});

// Opt-in geometry and synthetic screenshots for the owner table inventory.
test.afterEach(async ({ page }) => { await captureTables(page); });

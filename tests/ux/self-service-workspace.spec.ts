import { expect, test, type Page } from '@playwright/test';
import { copyFile } from 'node:fs/promises';
import path from 'node:path';
import { assertNoHorizontalOverflow } from './auth';

const fixture = 'http://127.0.0.1:18440/fixture';
async function ready(page: Page, route: string) {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 30_000 });
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function geometry(page: Page) {
  return page.locator('.self-service-card-grid').evaluate(el => ({
    tracks: getComputedStyle(el).gridTemplateColumns.split(' ').length,
    widths: [...el.querySelectorAll('.self-service-catalog-card')].map(x => x.getBoundingClientRect().width),
    heights: [...el.querySelectorAll('.self-service-catalog-card')].map(x => x.getBoundingClientRect().height)
  }));
}
test.beforeEach(async ({ page, request }) => {
  await request.get(`${fixture}/reset`);
  await page.goto('/auth/development');
});

for (const theme of ['light', 'dark']) for (const [width, height] of [[1920, 1080], [1440, 900], [1366, 768], [768, 1024], [390, 844]]) {
  test(`Self Service catalog and request table ${theme} ${width}x${height}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height });
    await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    await ready(page, '/self-service');
    await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
    await expect(page.locator('.self-service-catalog-card')).toHaveCount(7);
    const result = await geometry(page);
    expect(result.tracks).toBe(width >= 1366 ? 3 : width >= 768 ? 2 : 1);
    expect(Math.max(...result.widths) - Math.min(...result.widths)).toBeLessThan(2);
    expect(Math.max(...result.heights) - Math.min(...result.heights)).toBeLessThan(2);
    const sections = page.getByRole('navigation', { name: 'Self Service sections' });
    await expect(sections.getByRole('link')).toHaveCount(2);
    await expect(sections.getByRole('link', { name: 'Service Catalog' })).toHaveAttribute('aria-current', 'page');
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: info.outputPath('catalog-after.png'), fullPage: true });
    await copyFile(path.resolve(`docs/implementation/assets/381-self-service/catalog-${width}x${height}-${theme}.png`), info.outputPath('catalog-before.png'));
    await copyFile(path.resolve('docs/implementation/assets/381-self-service/capture-receipt.json'), info.outputPath('baseline-source.json'));

    await sections.getByRole('link', { name: 'My Requests', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'My Requests', exact: true })).toBeVisible();
    await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(25);
    await expect(page.getByRole('status').filter({ hasText: '137 matching requests' })).toBeVisible();
    await expect(page.locator('.mud-table-pagination')).toContainText('1-25 of 137');
    const container = page.locator('.my-requests-table .mud-table-container');
    await container.scrollIntoViewIfNeeded();
    const headerBefore = await page.locator('.my-requests-table thead th').first().boundingBox();
    const footerBefore = await page.locator('.mud-table-pagination').boundingBox();
    await container.evaluate(el => { el.scrollTop = el.scrollHeight; });
    const headerAfter = await page.locator('.my-requests-table thead th').first().boundingBox();
    const footerAfter = await page.locator('.mud-table-pagination').boundingBox();
    expect(Math.abs(headerAfter!.y - headerBefore!.y)).toBeLessThan(2);
    expect(Math.abs(footerAfter!.y - footerBefore!.y)).toBeLessThan(2);
    await page.locator('.mud-table-pagination').scrollIntoViewIfNeeded();
    await expect(page.locator('.mud-table-pagination')).toBeInViewport({ ratio: 1 });
    const pagerCenters = await page.locator('.mud-table-pagination-toolbar').evaluate(element => [...element.children]
      .filter(child => child.getBoundingClientRect().width > 0)
      .map(child => { const box = child.getBoundingClientRect(); return box.y + box.height / 2; }));
    expect(pagerCenters.length).toBeGreaterThan(1);
    expect(Math.max(...pagerCenters) - Math.min(...pagerCenters)).toBeLessThan(2);
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: info.outputPath('my-requests-after.png'), fullPage: true });
    await copyFile(path.resolve(`docs/implementation/assets/381-self-service/my-requests-${width}x${height}-${theme}.png`), info.outputPath('my-requests-before.png'));
  });
}

for (const theme of ['light', 'dark']) for (const width of [1440, 1920]) {
  test(`sparse catalog preserves three slots and drawer geometry ${theme} at ${width}px`, async ({ page, request }, info) => {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    await copyFile(path.resolve('docs/implementation/assets/381-self-service/capture-receipt.json'), info.outputPath('baseline-source.json'));
    let expectedWidth = 0;
    for (const count of [7, 1, 2, 3, 4]) {
      await request.get(`${fixture}/catalogue?count=${count}`);
      await ready(page, '/self-service');
      await expect(page.locator('.self-service-catalog-card')).toHaveCount(count);
      const result = await geometry(page);
      expect(result.tracks).toBe(3);
      if (expectedWidth) expect(Math.abs(result.widths[0] - expectedWidth)).toBeLessThan(2);
      expectedWidth = result.widths[0];
      await page.screenshot({ path: info.outputPath(`catalog-after-${count}-cards.png`), fullPage: true });
      if (count === 1 || count === 2)
        await copyFile(path.resolve(`docs/implementation/assets/381-self-service/catalog-${count}-cards-${width}-${theme}.png`), info.outputPath(`catalog-before-${count}-cards.png`));
    }
    const search = page.getByRole('textbox', { name: 'Search services and requests' });
    await search.fill('Service 1');
    await expect(page.locator('.self-service-catalog-card')).toHaveCount(1);
    expect(Math.abs((await geometry(page)).widths[0] - expectedWidth)).toBeLessThan(2);
    const card = page.locator('.self-service-catalog-card');
    await card.focus();
    await expect(card).toBeFocused();
    expect(await card.getAttribute('aria-label')).toContain('longer description');
    await page.getByTestId('navigation-toggle').click();
    await expect.poll(async () => (await geometry(page)).tracks).toBe(3);
    await assertNoHorizontalOverflow(page);
    await request.get(`${fixture}/catalogue?count=0`);
    await ready(page, '/self-service');
    await expect(page.getByText('No services or requests are available in this folder.')).toBeVisible();
    await page.screenshot({ path: info.outputPath('catalog-empty.png'), fullPage: true });
  });
}

test('query, native page sizes, sorts and section return state survive navigation', async ({ page, request }) => {
  await ready(page, '/self-service/service-0?search=Service%201');
  await expect(page.locator('.self-service-catalog-card')).toHaveCount(1);
  const sections = page.getByRole('navigation', { name: 'Self Service sections' });
  await sections.getByRole('link', { name: 'My Requests', exact: true }).click();
  const next = page.getByRole('button', { name: 'Next page', exact: true });
  await next.click();
  await expect(page).toHaveURL(/page=2/);
  await expect(page.locator('.mud-table-pagination')).toContainText('26-50 of 137');
  await page.getByRole('button', { name: 'Last page', exact: true }).click();
  await expect(page.locator('.mud-table-pagination')).toContainText('126-137 of 137');
  await page.locator('.mud-table-pagination').getByRole('combobox').click();
  await page.getByRole('option', { name: '50', exact: true }).click();
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(50);
  await expect(page.locator('.mud-table-pagination')).toContainText('1-50 of 137');
  await page.locator('.mud-table-pagination').getByRole('combobox').click();
  await page.getByRole('option', { name: '100', exact: true }).click();
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(100);
  await expect(page.locator('.mud-table-pagination')).toContainText('1-100 of 137');
  await page.locator('.mud-table-pagination').getByRole('combobox').click();
  await page.getByRole('option', { name: '50', exact: true }).click();
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(50);
  await page.getByRole('button', { name: 'Sort by title', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/sort=title/);
  await expect(page.getByRole('button', { name: 'Sort by title', exact: true })).toBeFocused();
  await expect(page.locator('th[aria-sort="ascending"]')).toContainText('Title');
  const search = page.getByRole('textbox', { name: 'Search tracking ID or title' });
  await search.fill('Needle');
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  await expect(page.getByRole('link', { name: /Open request REQ-SYN-136/ })).toBeVisible();
  await page.getByRole('button', { name: 'Refresh requests', exact: true }).click();
  await expect(search).toHaveValue('Needle');
  await sections.getByRole('link', { name: 'Service Catalog' }).click();
  await expect(page).toHaveURL(/service-0\?search=Service/);
  await expect(page.getByRole('textbox', { name: 'Search services and requests' })).toHaveValue('Service 1');
  await sections.getByRole('link', { name: 'My Requests', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Search tracking ID or title' })).toHaveValue('Needle');
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.filter((x: any) => x.path.includes('self-service/requests')).some((x: any) => x.query.q === 'Needle' && x.query.pageSize === '50')).toBe(true);
});

test('status, release and UTC date controls represent the complete server query', async ({ page, request }) => {
  await ready(page, '/self-service/my-requests?q=Needle&createdFrom=2026-11-01&createdThrough=2026-11-01&updatedFrom=2026-11-02&updatedThrough=2026-11-02');
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  await page.getByRole('combobox', { name: 'Status', exact: true }).click();
  await page.getByRole('option', { name: 'In progress', exact: true }).click();
  await page.getByRole('combobox', { name: 'Release stage', exact: true }).click();
  await page.getByRole('option', { name: 'In testing', exact: true }).click();
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  await expect(page).toHaveURL(/state=3/);
  await expect(page).toHaveURL(/release=0/);
  await page.getByText('Date filters (4 active)', { exact: true }).click();
  for (const label of ['Created from (UTC)', 'Created through (UTC)', 'Updated from (UTC)', 'Updated through (UTC)'])
    await expect(page.getByRole('textbox', { name: label, exact: true })).not.toHaveValue('');
  await page.getByRole('combobox', { name: 'Status', exact: true }).click();
  await page.getByRole('option', { name: 'Resolved', exact: true }).click();
  await expect(page.getByText('No requests match these filters. Try clearing a filter.')).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.some((entry: any) => entry.query.createdFromUtc === '2026-11-01T00:00:00Z' &&
    entry.query.createdBeforeUtc.startsWith('2026-11-02T00:00:00') && entry.query.updatedFromUtc === '2026-11-02T00:00:00Z' &&
    entry.query.updatedBeforeUtc.startsWith('2026-11-03T00:00:00') && entry.query.state === '3' && entry.query.releaseStage === '0')).toBe(true);
});

test('superseded query, errors, no matches and page clamping remain truthful', async ({ page, request }, info) => {
  await ready(page, '/self-service/my-requests');
  const search = page.getByRole('textbox', { name: 'Search tracking ID or title' });
  await search.fill('request');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).requests.some((x: any) => x.query.q === 'request')).toBe(true);
  await search.fill('Needle');
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  await expect(page.getByRole('link', { name: /Open request REQ-SYN-136/ })).toBeVisible();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).requests.some((x: any) => x.query.q === 'request' && x.completed)).toBe(true);
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(1);
  await search.fill('absent synthetic record');
  await expect(page.getByText('No requests match these filters. Try clearing a filter.')).toBeVisible();
  await page.screenshot({ path: info.outputPath('my-requests-no-matches.png'), fullPage: true });
  await request.get(`${fixture}/fault?mode=requests`);
  await page.getByRole('button', { name: 'Refresh requests', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Requests could not be loaded', { timeout: 30_000 });
  await expect(search).toHaveValue('absent synthetic record');
  await page.screenshot({ path: info.outputPath('my-requests-error.png'), fullPage: true });
  await request.get(`${fixture}/fault`);
  await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
  await expect(page.locator('.my-requests-table tbody tr')).toHaveCount(25);
  await page.getByRole('button', { name: 'Last page', exact: true }).click();
  await expect(page.locator('.mud-table-pagination')).toContainText('126-137 of 137');
  await request.get(`${fixture}/remove-last-page`);
  await page.getByRole('button', { name: 'Refresh requests', exact: true }).click();
  await expect(page.locator('.mud-table-pagination')).toContainText('76-100 of 100');
  await expect(page).toHaveURL(/page=4/);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

for (const theme of ['light', 'dark']) test(`catalog error, empty and no-match states are distinct in ${theme}`, async ({ page, request }, info) => {
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  await request.get(`${fixture}/fault?mode=catalogue`);
  await ready(page, '/self-service');
  await expect(page.getByRole('alert')).toContainText('Service Catalog could not be loaded', { timeout: 30_000 });
  await page.screenshot({ path: info.outputPath('catalog-error.png'), fullPage: true });
  await request.get(`${fixture}/fault`);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.self-service-catalog-card')).toHaveCount(7);
  await page.getByRole('textbox', { name: 'Search services and requests' }).fill('No synthetic match');
  await expect(page.getByText('No services or requests match your search.')).toBeVisible();
  await page.screenshot({ path: info.outputPath('catalog-no-matches.png'), fullPage: true });
  await page.getByRole('button', { name: 'Clear search', exact: true }).click();
  await expect(page.locator('.self-service-catalog-card')).toHaveCount(7);
  const first = page.locator('.self-service-catalog-card').first();
  await first.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/self-service\/service-0/);
  await expect(page.getByRole('heading', { name: 'Synthetic hardware folder', exact: true })).toBeVisible();
  await assertNoHorizontalOverflow(page);
});

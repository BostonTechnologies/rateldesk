import { expect, test, Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
const fixture = 'http://127.0.0.1:18400/fixture';
async function ready(page: Page, route: string) {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}
async function signIn(page: Page, port = 5170) { await page.goto(`https://127.0.0.1:${port}/auth/development`); }
test.beforeEach(async ({ request, page }) => { await request.get(`${fixture}/reset`); await signIn(page); });

for (const theme of ['light', 'dark']) for (const width of [320, 390, 768, 1024, 1440, 1920]) {
  test(`catalogue and console ${theme} ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    await ready(page, '/self-service');
    const search = page.getByRole('textbox', { name: 'Search services and requests' });
    await search.fill('hardware');
    await expect(page.locator('.self-service-catalog-card')).toHaveCount(1);
    await search.fill('missing synthetic option');
    await expect(page.getByText('No items found.')).toBeVisible();
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(page.locator('.self-service-catalog-card')).toHaveCount(2);
    const bounds = await page.locator('.self-service-toolbar').boundingBox();
    expect(bounds!.width).toBeLessThanOrEqual(512);
    if (width >= 1440) expect(bounds!.width).toBeGreaterThanOrEqual(440);
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('catalogue.png'), fullPage: true });
    await ready(page, '/ops/workflows');
    await expect(page.getByRole('tab')).toHaveCount(2);
    await expect(page.locator('.workflow-operations table')).toHaveCount(1);
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('workflows.png'), fullPage: true });
    await ready(page, '/ops/slas');
    await expect(page.getByText('75%', { exact: true })).toBeVisible();
    await expect(page.locator('.sla-operations table')).toHaveCount(1);
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('sla.png'), fullPage: true });
  });
}

test('legacy bookmarks, categories, metrics, Back/Forward and job proxy remain usable', async ({ page, request }) => {
  await ready(page, '/ops/slas?tenantId=org-synthetic&thresholdPercent=80&metric=0');
  await expect(page.getByText('6 / 8', { exact: true })).toBeVisible();
  await expect(page.getByText('7 / 8', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Workflows' }).click();
  await expect(page).toHaveURL(/tab=workflows/);
  for (const category of ['Overdue', 'Escalated', 'Retries pending', 'Callback rejections', 'Binding issues', 'Critical failures']) {
    await page.getByRole('button', { name: new RegExp(`^${category}`) }).click();
    await expect(page.locator('.workflow-operations table')).toHaveCount(1);
  }
  await page.goBack(); await expect(page.getByRole('button', { name: /^Binding issues/ })).toHaveAttribute('aria-pressed', 'true');
  await page.goForward(); await expect(page.getByRole('button', { name: /^Critical failures/ })).toHaveAttribute('aria-pressed', 'true');
  await ready(page, '/ops/hangfire?synthetic=preserved');
  await expect(page.getByRole('tab', { name: 'Workflows' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('link', { name: 'Open Dashboard' })).toHaveAttribute('href', '/hangfire');
  await expect(page.getByRole('tab')).toHaveCount(2);
  expect((await page.request.get('/hangfire/css/dashboard.css')).status()).toBe(200);
  expect((await page.request.post('/hangfire/jobs/enqueue', { data: 'synthetic' })).status()).toBe(200);
  const state = await (await request.get(`${fixture}/state`)).json();
  const compliance = state.requests.find((r: any) => r.path.endsWith('/compliance'));
  expect(compliance.query.tenantId).toBe('org-synthetic');
  expect(Math.round((Date.parse(compliance.query.toUtc) - Date.parse(compliance.query.fromUtc)) / 86400000)).toBe(7);
});

for (const [port, role] of [[5171, 'sla'], [5172, 'workflow'], [5173, 'notifications']] as const) {
  test(`${role} keeps independent console and notification permissions`, async ({ page, request }) => {
    await signIn(page, port); await ready(page, `https://127.0.0.1:${port}/notifications`);
    const nav = page.getByRole('navigation', { name: 'Primary navigation' });
    await expect(nav.locator('a[href="notifications"]')).toHaveCount(1);
    await expect(nav.locator('a[href="/ops"]')).toHaveCount(role === 'notifications' ? 0 : 1);
    await request.get(`${fixture}/reset`);
    if (role !== 'notifications') {
      await ready(page, `https://127.0.0.1:${port}/ops`);
      await expect(page.getByRole('tab')).toHaveCount(2);
      await expect(page.getByRole('link', { name: 'Advanced / Background jobs' })).toHaveCount(0);
      const state = await (await request.get(`${fixture}/state`)).json();
      expect(state.requests.some((r: any) => r.path.includes(role === 'sla' ? '/reports/sla/' : '/ops/tasks/'))).toBe(true);
      expect(state.requests.some((r: any) => r.path.includes(role === 'sla' ? '/ops/tasks/' : '/reports/sla/'))).toBe(false);
      expect((await page.request.get(`https://127.0.0.1:${port}/hangfire/css/dashboard.css`)).status()).toBe(401);
    }
  });
}

test('unavailable diagnostics are explicit and refresh recovers', async ({ page, request }) => {
  await request.get(`${fixture}/fault?category=critical-failures`);
  await ready(page, '/ops/workflows?category=critical');
  await expect(page.getByRole('button', { name: /^Critical failures/ })).toContainText('Unavailable');
  await expect(page.locator('.workflow-operations table')).toHaveCount(0);
  await request.get(`${fixture}/fault`); await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.locator('.workflow-operations table')).toHaveCount(1);
});

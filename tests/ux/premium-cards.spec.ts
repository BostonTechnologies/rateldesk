import { expect, test, type Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';

const fixture = 'http://127.0.0.1:18720/fixture';
async function signIn(page: Page) {
  await page.goto('/auth/development');
  await page.goto('/home');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}
async function open(page: Page, route: string) {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
test.beforeEach(async ({ page, request }) => {
  await request.get(`${fixture}/reset`);
  await signIn(page);
});

for (const theme of ['light', 'dark'] as const) {
  for (const [width, height] of [[1920, 1080], [1440, 900], [1366, 768], [768, 1024], [390, 844]]) {
    test(`operational cards and overview ${width}x${height} ${theme}`, async ({ page }, info) => {
      await page.setViewportSize({ width, height });
      await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
      for (const [name, route] of [['home', '/home'], ['services', '/admin/requests/services'], ['connections', '/account/integration-credentials?tab=connections']]) {
        await open(page, route);
        await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
        if (name === 'home') {
          await expect(page.getByRole('heading', { name: 'Operations Overview', exact: true })).toBeVisible();
          await expect(page.getByTestId('operations-recent-panel')).toHaveCount(3);
        } else if (name === 'services') {
          await expect(page.getByTestId('service-explorer-card')).toHaveCount(4);
          const geometry = await page.getByTestId('service-explorer-card').evaluateAll(cards => cards.map(card => {
            const box = card.getBoundingClientRect(), footer = card.querySelector('.svc-actions')!.getBoundingClientRect();
            return { x: box.x, y: box.y, height: box.height, footer: footer.y };
          }));
          const firstRow = geometry.filter(card => Math.abs(card.y - geometry[0].y) < 2);
          expect(firstRow.length).toBe(width >= 1366 ? 3 : width >= 768 ? 2 : 1);
          for (const card of firstRow) {
            expect(Math.abs(card.height - firstRow[0].height)).toBeLessThanOrEqual(2);
            expect(Math.abs(card.footer - firstRow[0].footer)).toBeLessThanOrEqual(2);
          }
          const actionRows = await page.locator('.svc-actions').evaluateAll(footers => footers.map(footer => [...footer.querySelectorAll('button')].map(button => button.getBoundingClientRect().y)));
          for (const row of actionRows) expect(Math.max(...row) - Math.min(...row)).toBeLessThanOrEqual(2);
        } else {
          await expect(page.getByTestId('system-connection-row')).toHaveCount(6);
          const rows = await page.locator('.pairing-row-actions').evaluateAll(footers => footers.map(footer => [...footer.querySelectorAll('button')].map(button => button.getBoundingClientRect().y)));
          for (const row of rows) expect(Math.max(...row) - Math.min(...row)).toBeLessThanOrEqual(2);
        }
        await assertNoHorizontalOverflow(page);
        await page.screenshot({ path: info.outputPath(`after-${name}-${width}x${height}-${theme}.png`), fullPage: true, animations: 'disabled' });
      }
    });
  }
}

test('all assignments remain reachable and description/actions retain their behavior', async ({ page, request }) => {
  await open(page, '/admin/requests/services');
  const cards = page.getByTestId('service-explorer-card');
  await expect(cards).toHaveCount(4);
  for (const [index, count] of [0, 2, 20, 100].entries()) {
    const region = cards.nth(index).getByRole('region', { name: new RegExp(`Assigned organizations.*: ${count}$`) });
    await expect(region).toBeAttached();
    await expect(region.locator('.mud-chip')).toHaveCount(count);
    await region.focus();
    await expect(region).toBeFocused();
  }
  const largeList = cards.nth(3).getByRole('region');
  expect(await largeList.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await largeList.getByText('Example organization 100', { exact: true }).scrollIntoViewIfNeeded();
  await expect(largeList.getByText('Example organization 100', { exact: true })).toBeInViewport();
  await cards.nth(3).getByRole('button', { name: 'Manage organization access for Example service 4', exact: true }).click();
  const access = page.getByRole('dialog');
  await expect(access.getByText('100 assigned organizations', { exact: true })).toBeVisible();
  await access.getByRole('textbox', { name: 'Search available organizations', exact: true }).fill('organization 100');
  await access.getByRole('combobox', { name: 'Permitted organizations', exact: true }).click();
  await expect(page.getByRole('option')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(access.getByText('100 assigned organizations', { exact: true })).toBeVisible();
  await access.getByRole('button', { name: 'Cancel', exact: true }).click();
  await cards.nth(1).getByRole('button', { name: /^Read full description/ }).click();
  const description = page.getByRole('dialog');
  await expect(description).toContainText('A longer description that explains the example service.');
  await description.getByRole('button', { name: 'Close', exact: true }).click();
  await cards.first().getByRole('button', { name: 'Open folder Example service 1', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/requests\/services\/service-1$/);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.mutations).toEqual([]);
});

test('connection configuration, test freshness and exact saved mapping operations remain distinct', async ({ page, request }) => {
  await open(page, '/account/integration-credentials?tab=connections');
  const panel = page.getByTestId('system-connections-panel');
  const cards = panel.getByTestId('system-connection-row');
  await expect(cards).toHaveCount(6);
  await expect(cards.nth(0)).toContainText('Configured');
  await expect(cards.nth(0)).toContainText('Never tested');
  await expect(cards.nth(1)).toHaveAttribute('data-test-state', 'passed');
  await expect(cards.nth(2)).toContainText('Stale snapshot');
  await expect(cards.nth(3)).toContainText('Last test failed');
  await expect(cards.nth(4)).toContainText('Setup required');
  await expect(panel.getByText('Healthy', { exact: true })).toHaveCount(0);
  await cards.nth(3).getByText('Test details', { exact: true }).click();
  await expect(cards.nth(3)).toContainText('The peer is unavailable. Retry this saved connection.');
  const targetId = await cards.nth(0).getAttribute('data-connection-id');
  const testButton = cards.nth(0).getByTestId('test-system-connection');
  await testButton.click();
  await expect(testButton).toBeDisabled();
  await expect(cards.nth(0)).toContainText('Testing connection…');
  await expect(cards.nth(0)).toHaveAttribute('data-test-state', 'passed');
  const afterTest = await (await request.get(`${fixture}/state`)).json();
  expect(afterTest.mutations).toHaveLength(1);
  expect(afterTest.mutations[0].path).toBe(`/api/v1/admin/system-connections/${'a'.repeat(64)}/mappings/${targetId}/test`);
  await cards.nth(0).getByRole('button', { name: 'Add mapping', exact: true }).click();
  const review = page.getByRole('dialog');
  await expect(review).toBeVisible();
  await expect(review.getByLabel('Connection name')).not.toHaveValue('Example incident intake');
  await review.getByRole('button', { name: 'Back to connections', exact: true }).click();
  await cards.nth(0).getByTestId('delete-system-connection').click();
  await page.getByTestId('confirm-system-connection-delete').click();
  await expect(cards).toHaveCount(5);
  await expect(panel.locator(`[data-connection-id="${targetId}"]`)).toHaveCount(0);
  const afterDelete = await (await request.get(`${fixture}/state`)).json();
  expect(afterDelete.mutations).toHaveLength(2);
  expect(afterDelete.mutations[1].path).toBe(`/api/v1/admin/system-connections/${'a'.repeat(64)}/mappings/${targetId}`);
});

test('sparse connections keep intentional slots and search/status filters retain truthful results', async ({ page, request }) => {
  for (const count of [0, 1, 2, 6]) {
    await request.get(`${fixture}/reset?connectionCount=${count}`);
    await open(page, '/account/integration-credentials?tab=connections');
    const panel = page.getByTestId('system-connections-panel');
    await expect(panel.getByTestId('system-connection-row')).toHaveCount(count);
    if (count === 0) await expect(panel.getByText(/No system connections yet/)).toBeVisible();
    else {
      await panel.getByRole('textbox', { name: 'Search system connections', exact: true }).fill('no matching synthetic connection');
      await expect(panel.getByTestId('system-connection-row')).toHaveCount(0);
      await expect(panel.getByText('No matching system connections.', { exact: true })).toBeVisible();
      await panel.getByRole('textbox', { name: 'Search system connections', exact: true }).fill('');
      await expect(panel.getByTestId('system-connection-row')).toHaveCount(count);
    }
    await assertNoHorizontalOverflow(page);
  }
  const panel = page.getByTestId('system-connections-panel');
  await panel.getByRole('combobox', { name: 'Status', exact: true }).click();
  await page.getByRole('option', { name: 'Test is stale', exact: true }).click();
  await expect(panel.getByTestId('system-connection-row')).toHaveCount(1);
  await expect(panel.getByTestId('system-connection-row')).toContainText('Stale snapshot');
});

test('Service Explorer separates an empty folder from an unavailable query and supports correction/retry', async ({ page, request }) => {
  await request.get(`${fixture}/reset?serviceCount=0`);
  await open(page, '/admin/requests/services');
  await expect(page.getByText(/No items here yet/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'New Request', exact: true })).toBeDisabled();
  await request.post(`${fixture}/options`, { data: { mode: 'services-error' } });
  await open(page, '/admin/requests/services');
  await expect(page.getByRole('alert')).toContainText('Service Explorer could not be loaded.');
  await expect(page.getByText(/No items here yet/)).toHaveCount(0);
  await request.get(`${fixture}/reset`);
  await page.getByRole('button', { name: 'Retry Service Explorer', exact: true }).click();
  await expect(page.getByTestId('service-explorer-card')).toHaveCount(4);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Operations Overview preserves every counter and feed while partial failures stay visible and retryable', async ({ page, request }) => {
  await expect(page.getByTestId('operations-overview')).toBeVisible();
  const summary = page.getByRole('complementary', { name: 'Current workload' });
  await expect(summary.locator('a')).toHaveCount(12);
  await expect(summary.getByRole('link', { name: 'In progress 7', exact: true })).toHaveAttribute('href', '/incidents?state=InProgress');
  await expect(summary.getByRole('link', { name: 'On hold 2', exact: true })).toHaveAttribute('href', '/requests?state=OnHold');
  await expect(summary.getByRole('link', { name: 'Implementation in progress 1', exact: true })).toHaveAttribute('href', '/changes?lifecycleState=ImplementationInProgress');
  for (const kind of ['incidents', 'requests', 'changes']) {
    const feed = page.getByRole('region', { name: `Recent ${kind}`, exact: true });
    await expect(feed.locator('tbody tr')).toHaveCount(5);
    await expect(feed.getByRole('link', { name: `View all ${kind}`, exact: true })).toHaveAttribute('href', `/${kind}`);
    await expect(feed.locator('time').first()).toHaveAttribute('title', /2026-11-11T09:00:00/);
  }
  await request.post(`${fixture}/options`, { data: { mode: 'incidents-error' } });
  await page.getByRole('button', { name: 'Refresh Operations Overview', exact: true }).click();
  const incidents = page.getByRole('region', { name: 'Recent incidents', exact: true });
  await expect(incidents.getByRole('alert')).toContainText('Showing the last loaded data.');
  await expect(incidents.locator('tbody tr')).toHaveCount(5);
  await expect(page.getByRole('region', { name: 'Recent requests', exact: true }).getByRole('alert')).toHaveCount(0);
  const before = await (await request.get(`${fixture}/state`)).json();
  await request.post(`${fixture}/options`, { data: { mode: '' } });
  await incidents.getByRole('button', { name: 'Retry incidents', exact: true }).click();
  await expect(incidents.getByRole('alert')).toHaveCount(0);
  const after = await (await request.get(`${fixture}/state`)).json();
  expect(after.reads.filter((item: { path: string }) => item.path === '/api/v1/incidents').length).toBe(before.reads.filter((item: { path: string }) => item.path === '/api/v1/incidents').length + 1);
  expect(after.reads.filter((item: { path: string }) => item.path === '/api/v1/dashboard/admin-summary').length).toBe(before.reads.filter((item: { path: string }) => item.path === '/api/v1/dashboard/admin-summary').length);
});

test('empty and unavailable feeds are different, and role-specific home variants fetch no administrator data', async ({ page, request }) => {
  await request.get(`${fixture}/reset?mode=empty`);
  await open(page, '/home');
  for (const kind of ['incidents', 'requests', 'changes']) await expect(page.getByText(`No recent ${kind}.`, { exact: true })).toBeVisible();
  await expect(page.getByText('No incident workload.', { exact: true })).toBeVisible();
  await request.post(`${fixture}/options`, { data: { mode: 'requests-error' } });
  await open(page, '/home');
  await expect(page.getByRole('region', { name: 'Recent requests', exact: true }).getByRole('alert')).toContainText('Retry this panel.');
  await expect(page.getByText('No recent requests.', { exact: true })).toHaveCount(0);
  for (const [role, heading] of [['SelfService.User', 'Welcome to Helpdesk'], ['Tenant.Roles.Assign', 'Tenant administration'], ['Incident.Read', 'Welcome to Helpdesk'], ['No.Modules', 'No module access assigned']]) {
    await request.get(`${fixture}/reset?role=${encodeURIComponent(role)}`);
    await signIn(page);
    await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
    await expect(page.getByTestId('operations-overview')).toHaveCount(0);
    const state = await (await request.get(`${fixture}/state`)).json();
    expect(state.reads.filter((item: { path: string }) => item.path === '/api/v1/dashboard/admin-summary' || /^\/api\/v1\/(incidents|requests|changes)$/.test(item.path))).toEqual([]);
  }
});

test('branded global search encodes long names, keeps shortcuts and returns focus', async ({ page, request }) => {
  for (const brand of ['RatelDesk', 'Example Support', '非常に長いサポートサービス名 '.repeat(8), '<Example & Support>', '']) {
    await request.post(`${fixture}/options`, { data: { brand } });
    await open(page, '/home');
    const label = `Search ${brand.trim() || 'RatelDesk'}`;
    const trigger = page.getByTestId('global-search-trigger');
    await expect(trigger).toHaveAccessibleName(label);
    await expect(trigger.locator('span')).toHaveText(label);
    await trigger.click();
    const dialog = page.getByRole('dialog', { name: label, exact: true });
    await expect(dialog.getByRole('textbox', { name: label, exact: true })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
    await page.keyboard.press('Shift+S');
    await expect(dialog).toBeVisible();
    await page.keyboard.press('Escape');
    for (const width of [390, 1366]) { await page.setViewportSize({ width, height: 768 }); await assertNoHorizontalOverflow(page); }
  }
  await request.post(`${fixture}/options`, { data: { mode: 'branding-unavailable' } });
  await open(page, '/home');
  await expect(page.getByTestId('global-search-trigger')).toHaveAccessibleName('Search RatelDesk');
  await page.evaluate(() => { const input = document.createElement('input'); input.id = 'synthetic-typing-field'; input.setAttribute('aria-label', 'Synthetic typing field'); document.body.append(input); });
  await page.getByRole('textbox', { name: 'Synthetic typing field', exact: true }).focus();
  await page.keyboard.press('Shift+S');
  await expect(page.getByRole('dialog', { name: 'Search RatelDesk', exact: true })).toHaveCount(0);
});

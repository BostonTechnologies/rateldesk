import { expect, test, Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
const fixture = 'http://127.0.0.1:18400/fixture';
async function ready(page: Page, route: string) {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 30_000 });
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
  await page.getByRole('tab', { name: 'SLA', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Workflows' })).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(page.getByRole('tab', { name: 'SLA', exact: true })).toBeFocused();
  await page.getByRole('tab', { name: 'Workflows' }).click();
  await expect(page).toHaveURL(/tab=workflows/);
  for (const [category, key] of [['Overdue', 'overdue'], ['Escalated', 'escalated'], ['Retries pending', 'retries'], ['Callback rejections', 'callbacks'], ['Binding issues', 'bindings'], ['Critical failures', 'critical']]) {
    const selected = page.getByRole('button', { name: new RegExp(`^${category}`) });
    await selected.click();
    await expect(page).toHaveURL(url => url.pathname === '/ops' && url.searchParams.get('category') === key);
    await expect(selected).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.workflow-operations table')).toHaveCount(1);
    if (category === 'Callback rejections') await expect(page.locator('.workflow-operations a[href*=correlationId]')).toHaveAttribute('href', '/notifications?category=DomainEvent.Orchestration&correlationId=synthetic%20correlation');
  }
  await page.goBack(); await expect(page.getByRole('button', { name: /^Binding issues/ })).toHaveAttribute('aria-pressed', 'true');
  await page.goForward(); await expect(page.getByRole('button', { name: /^Critical failures/ })).toHaveAttribute('aria-pressed', 'true');
  await ready(page, '/ops/hangfire?synthetic=preserved');
  await expect(page.getByRole('tab', { name: 'Workflows' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('link', { name: 'Open Dashboard' })).toHaveAttribute('href', '/hangfire');
  await expect(page.getByRole('tab')).toHaveCount(2);
  await page.getByLabel('Enable global Hangfire runtime', { exact: true }).uncheck();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).enabled).toBe(false);
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
      await request.get(`${fixture}/reset`);
      await ready(page, `https://127.0.0.1:${port}/ops/${role === 'sla' ? 'workflows' : 'slas'}`);
      await expect(page.getByText(/not available with your permissions/)).toBeVisible();
      expect((await (await request.get(`${fixture}/state`)).json()).requests).toHaveLength(0);
    }
  });
}

test('unavailable diagnostics are explicit and refresh recovers', async ({ page, request }) => {
  await request.get(`${fixture}/fault?category=critical-failures`);
  await ready(page, '/ops/workflows?category=critical');
  await expect(page.getByRole('button', { name: /^Critical failures/ })).toContainText('Unavailable', { timeout: 30_000 });
  await expect(page.locator('.workflow-operations table')).toHaveCount(0);
  await request.get(`${fixture}/fault`); await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.locator('.workflow-operations table')).toHaveCount(1);
});

for (const theme of ['light', 'dark']) for (const width of [320, 390, 768, 1024, 1440, 1920]) {
  test(`navigation single-line labels and keyboard ${theme} ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1100 });
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    await ready(page, '/admin/support-routing');
    const drawer = page.getByTestId('app-navigation-drawer');
    if (width < 1280) await page.getByTestId('navigation-toggle').click();
    await expect(drawer.getByRole('link', { name: 'Support Routing', exact: true })).toBeVisible();
    const emails = drawer.getByRole('button', { name: 'Toggle Email Settings', exact: true });
    await emails.click();
    const labels = drawer.locator('.mud-nav-link-text');
    const wrapped = await labels.evaluateAll(elements => elements.filter(element => {
      if (!element.getClientRects().length) return false;
      const style = getComputedStyle(element);
      return style.whiteSpace !== 'nowrap' || element.getBoundingClientRect().height > parseFloat(style.lineHeight) + 2;
    }).map(element => element.textContent));
    expect(wrapped).toEqual([]);
    for (const name of ['Support Routing', 'Tenant SLA Settings', 'Mailbox Configuration']) {
      const label = drawer.getByRole('link', { name, exact: true }).locator('.mud-nav-link-text');
      expect(await label.evaluate(element => element.scrollWidth - element.clientWidth), `${name} fits at the configured drawer width`).toBeLessThanOrEqual(1);
      await label.evaluate(element => { (element as HTMLElement).style.fontFamily = 'Arial, sans-serif'; });
      expect(await label.evaluate(element => element.scrollWidth - element.clientWidth), `${name} also fits with a fallback font`).toBeLessThanOrEqual(1);
      await label.evaluate(element => { (element as HTMLElement).style.fontFamily = ''; });
    }
    const support = drawer.getByRole('link', { name: 'Support Routing', exact: true });
    await support.focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
    await expect(support).toBeFocused();
    expect(await support.evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe('none');
    const icon = await support.locator('svg').boundingBox();
    const row = await support.boundingBox();
    expect(icon!.width).toBeGreaterThanOrEqual(20);
    expect(icon!.x).toBeGreaterThanOrEqual(row!.x);
    await expect(page.locator('.mud-tooltip.mud-popover-open')).toContainText('Support Routing');
    await page.screenshot({ path: testInfo.outputPath('navigation.png'), fullPage: true });
    const longName = drawer.locator('a[href="auth"]');
    await longName.focus();
    await expect(longName).toHaveAccessibleName('Synthetic Operations User With An Exceptionally Long Display Name');
    await expect(page.locator('.mud-tooltip.mud-popover-open')).toContainText('Synthetic Operations User With An Exceptionally Long Display Name');
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('navigation-tooltip.png'), fullPage: true });
    if (width < 1280) {
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('navigation-toggle')).toBeFocused();
      await expect(drawer).toHaveClass(/mud-drawer--closed/);
    }
  });
}

test('200 percent layout retains search, operations and keyboard navigation in both themes', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1200 });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    for (const route of ['/self-service', '/ops/slas', '/ops/workflows']) {
      await ready(page, route);
      await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
      await assertNoHorizontalOverflow(page);
      if (route === '/self-service') {
        await page.getByRole('textbox', { name: 'Search services and requests' }).fill('hardware');
        await expect(page.locator('.self-service-catalog-card')).toHaveCount(1);
      } else await expect(page.getByRole('tab')).toHaveCount(2);
      await page.evaluate(() => { document.documentElement.style.zoom = ''; });
    }
    await ready(page, '/admin/support-routing');
    await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
    const support = page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Support Routing', exact: true });
    await support.focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
    await expect(support).toBeFocused();
    expect(await support.locator('.mud-nav-link-text').evaluate(element => getComputedStyle(element).whiteSpace)).toBe('nowrap');
    await expect(page.locator('.mud-tooltip.mud-popover-open')).toContainText('Support Routing');
    await assertNoHorizontalOverflow(page);
    await page.evaluate(() => { document.documentElement.style.zoom = ''; });
  }
});

test('task dashboard starts with all authorized assignments and pages before opting in and out', async ({ page, request }) => {
  await request.get(`${fixture}/premium`); await ready(page, '/tasks');
  await expect(page.locator('.task-workspace tbody tr')).toHaveCount(25);
  let state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.find((x: any) => x.path === '/api/v1/request-tasks').query.assignedToMe).toBe('false');
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.getByRole('link', { name: 'REQ-0025', exact: true })).toBeVisible();
  await page.getByRole('switch', { name: 'Assigned to me', exact: true }).check();
  await expect(page.getByRole('link', { name: 'REQ-0000', exact: true })).toBeVisible();
  state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.filter((x: any) => x.path === '/api/v1/request-tasks').at(-1).query).toMatchObject({ page: '1', assignedToMe: 'true' });
  await page.getByRole('switch', { name: 'Assigned to me', exact: true }).uncheck();
  await expect(page.getByRole('link', { name: 'REQ-0001', exact: true })).toBeVisible();
  expect(await page.getByRole('button', { name: 'Running', exact: true }).count()).toBe(0);
  await expect(page.getByText('Running', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open recovery', exact: true }).first()).toHaveAttribute('href', /notifications/);
  const firstRow = page.locator('.task-workspace tbody tr').filter({ has: page.getByRole('link', { name: 'REQ-0000', exact: true }) });
  await firstRow.getByRole('button', { name: 'Start task', exact: true }).evaluate((button: HTMLElement) => { button.click(); button.click(); });
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).mutations.filter((x: any) => x.path === '/api/v1/request-tasks/task-0/start').length).toBe(1);
  await expect(firstRow.getByRole('button', { name: 'Complete task', exact: true })).toBeVisible();
  await expect(firstRow.locator('input[type=checkbox]')).not.toBeChecked();
  await firstRow.locator('input[type=checkbox]').check();
  await expect(page.getByText('1 selected', { exact: true })).toBeVisible();
  await page.getByRole('switch', { name: 'Assigned to me', exact: true }).check();
  await expect(page.getByText('1 selected', { exact: true })).toHaveCount(0);
});

test('rule paging and filtered reordering retain full scope; browsing, clone and toggles make only reviewed mutations', async ({ page, request }) => {
  await request.get(`${fixture}/premium`); await ready(page, '/admin/email-rules');
  await expect(page.locator('.email-rules-table tbody tr')).toHaveCount(25);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.getByText('26-50 of 64', { exact: true })).toBeVisible();
  expect((await (await request.get(`${fixture}/state`)).json()).mutations).toEqual([]);
  await page.getByRole('textbox', { name: 'Search rules', exact: true }).fill('Rule 25:');
  await expect(page.locator('.email-rules-table tbody tr')).toHaveCount(1);
  await page.getByRole('button', { name: 'Move up', exact: true }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).mutations.length).toBe(1);
  const reordered = (await (await request.get(`${fixture}/state`)).json()).mutations[0].body.rules;
  expect(reordered).toHaveLength(60);
  expect(reordered.find((x: any) => x.id === 'rule-25').priority).toBe(24);
  expect(reordered.find((x: any) => x.id === 'rule-24').priority).toBe(25);
  expect(reordered.some((x: any) => Number(x.id.slice(5)) >= 60)).toBe(false);
  await page.getByRole('button', { name: 'View details', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Rule 25:');
  await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: 'Enable rule', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).mutations).toHaveLength(1);
  await page.getByRole('button', { name: 'Clone rule', exact: true }).click();
  await page.getByRole('textbox', { name: 'Cloned rule name', exact: true }).fill('Synthetic disabled copy');
  await page.getByRole('button', { name: 'Save disabled copy', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Cloned rule name', exact: true })).toHaveCount(0);
  const state = await (await request.get(`${fixture}/state`)).json();
  const copy = state.mutations.find((x: any) => x.path === '/api/v1/inbound-email-rules');
  expect(copy.body).toMatchObject({ enabled: false, name: 'Synthetic disabled copy', scopeType: 0, tenantId: null, mailboxId: null });
  expect(copy.body.conditions).toEqual(state.rules.find((x: any) => x.id === 'rule-25').conditions);
  expect(copy.body.actions).toEqual(state.rules.find((x: any) => x.id === 'rule-25').actions);
});

test('audit pages the complete history and older failure, aligns all cells, and retains exact diagnostics', async ({ page, request }) => {
  await request.get(`${fixture}/premium`); await ready(page, '/admin/email-rules');
  await page.getByRole('tab', { name: 'Audit', exact: true }).click();
  await expect(page.locator('.email-audit-table tbody tr')).toHaveCount(25);
  const headers = await page.locator('.email-audit-table thead th').count();
  for (const row of await page.locator('.email-audit-table tbody tr').all()) expect(await row.locator('td').count()).toBe(headers);
  await page.getByRole('combobox', { name: 'Outcome', exact: true }).click();
  await page.getByRole('option', { name: 'Failed', exact: true }).click();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('.email-audit-table tbody tr')).toHaveCount(1);
  await expect(page.getByText('Unknown rule', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Audit details', exact: true }).click();
  const details = page.getByRole('region', { name: 'Audit details', exact: true });
  await expect(details).toContainText('Older synthetic failure outside newest 200');
  await expect(details).toContainText('message-0@example.test');
  await expect(details.getByRole('button', { name: 'Copy diagnostics', exact: true })).toBeEnabled();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.filter((x: any) => x.path.endsWith('/audit/page')).at(-1).query.status).toBe('Failed');
});

test('notification arrivals and reconnect retain identities, selection and position; failed receipt does not claim success', async ({ page, request }) => {
  await request.get(`${fixture}/premium`); await ready(page, '/notifications');
  await expect(page.locator('.notifications-workspace tbody tr')).toHaveCount(25);
  await expect(page.getByRole('columnheader', { name: 'Reference', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.locator('[data-notification-id]').first()).toHaveAttribute('data-notification-id', '56000000-0000-4000-8000-000000000026');
  const ids = await page.locator('[data-notification-id]').evaluateAll(rows => rows.map(row => row.getAttribute('data-notification-id')));
  await page.locator('.notifications-workspace tbody input[type=checkbox]').first().check();
  const before = await page.locator('.notifications-workspace .mud-table-container').evaluate(el => { el.scrollTop = 120; return el.scrollTop; });
  await request.get(`${fixture}/arrival?duplicate=true`);
  await expect(page.getByRole('button', { name: '1 new events · Refresh', exact: true })).toBeVisible();
  expect(await page.locator('[data-notification-id]').evaluateAll(rows => rows.map(row => row.getAttribute('data-notification-id')))).toEqual(ids);
  expect(await page.locator('.notifications-workspace .mud-table-container').evaluate(el => el.scrollTop)).toBe(before);
  await expect(page.getByRole('button', { name: 'Mark Selected Read (1)', exact: true })).toBeEnabled();
  await request.get(`${fixture}/disconnect`);
  await expect(page.getByText('Reconnecting', { exact: true })).toBeVisible();
  await expect(page.getByText('Live', { exact: true })).toBeVisible();
  expect(await page.locator('[data-notification-id]').evaluateAll(rows => rows.map(row => row.getAttribute('data-notification-id')))).toEqual(ids);
  await request.get(`${fixture}/fault?category=receipts`);
  await page.getByRole('button', { name: 'Mark Selected Read (1)', exact: true }).click();
  // The real named API client retains its default bounded resilience retries on 503.
  await expect(page.getByText('Read receipts could not be confirmed. Selection is retained; refresh to reconcile.', { exact: true })).toBeVisible({ timeout: 35_000 });
  expect((await (await request.get(`${fixture}/state`)).json()).receipts).toEqual([]);
  await expect(page.getByRole('button', { name: 'Mark Selected Read (1)', exact: true })).toBeEnabled();
  await request.get(`${fixture}/fault`);
  await page.getByRole('button', { name: 'Mark Selected Read (1)', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Mark Selected Read (0)', exact: true })).toBeDisabled();
  expect((await (await request.get(`${fixture}/state`)).json()).receipts).toHaveLength(1);
  await page.getByRole('textbox', { name: 'Search', exact: true }).fill('56000000-0000-4000-8000-000000001001');
  await page.getByRole('textbox', { name: 'Search', exact: true }).press('Enter');
  await expect(page.locator('.notifications-workspace tbody tr')).toHaveCount(1);
  await page.getByRole('button', { name: 'Event details', exact: true }).click();
  await expect(page.getByText('Reference: 56000000-0000-4000-8000-000000001001', { exact: true })).toBeVisible();
});

for (const theme of ['light', 'dark']) for (const [width, height] of [[1440, 900], [1366, 768], [390, 844]]) {
  test(`premium operations ${theme} ${width}x${height}`, async ({ page, request }, info) => {
    await request.get(`${fixture}/premium`); await page.setViewportSize({ width, height });
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    for (const route of ['/tasks', '/notifications', '/admin/email-rules']) {
      await ready(page, route);
      await expect(page.locator('.premium-workspace tbody tr').first()).toBeVisible();
      if (route === '/admin/email-rules') {
        await expect(page.locator('.premium-workspace .mud-table-pagination').last()).toBeInViewport();
        await page.screenshot({ path: info.outputPath(`rules-${theme}-${width}.png`) });
        await page.getByRole('tab', { name: 'Audit', exact: true }).click();
      }
      await expect(page.locator('.premium-workspace .mud-table-pagination').last()).toBeInViewport();
      await assertNoHorizontalOverflow(page);
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
      await page.screenshot({ path: info.outputPath(`${route.replaceAll('/', '-')}-${theme}-${width}.png`) });
    }
  });
}


test('notification table CSS does not restyle the separate workflow table', async ({ page, request }) => {
  await request.get(`${fixture}/premium`); await ready(page, '/ops/workflows');
  const before = await page.locator('.workflow-operations .mud-table-root').evaluate(element => getComputedStyle(element).tableLayout);
  await ready(page, '/notifications'); await ready(page, '/ops/workflows');
  const geometry = await page.locator('.workflow-operations .mud-table-root').evaluate(element => ({ layout: getComputedStyle(element).tableLayout, premium: Boolean(element.closest('.premium-workspace')) }));
  expect(geometry.premium).toBe(false); expect(geometry.layout).toBe(before);
});

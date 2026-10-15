import { expect, test, Page } from '@playwright/test';
const fixture = 'http://127.0.0.1:18500/fixture';
const baseline = process.env.HELPDESK_ADMIN_BASELINE === 'true';
const routes = [
 ['roles', '/admin/roles', 'Roles & Permissions', '.role-workspace'],
 ['datasets', '/admin/resources/data-management', 'Data Management', '.data-management-page'],
 ['routing', '/admin/support-routing', 'Support Routing', '.support-routing-workspace'],
 ['sla', '/admin/tenant-sla-settings', 'Tenant SLA Settings', '.tenant-sla-workspace']
] as const;
async function open(page: Page, path: string) {
 await page.goto(path, { waitUntil: 'domcontentloaded' });
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function reload(page: Page) {
 await page.reload();
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
}
async function select(page: Page, label: string, name: string) {
 await page.getByRole('combobox', { name: label, exact: true }).click();
 await page.getByRole('option', { name, exact: true }).click();
}
test.beforeEach(async ({ page, request }) => {
 await request.get(`${fixture}/reset`);
 await page.goto('/auth/development', { waitUntil: 'domcontentloaded' });
});

test('synthetic before and after workspace evidence', async ({ page }, info) => {
 test.setTimeout(240_000);
 for (const theme of ['light', 'dark']) {
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  for (const [name, path, , selector] of routes) {
   for (const width of baseline ? [1440] : [320, 768, 1440, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    await open(page, path);
    await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
    await page.waitForTimeout(350);
    if (!baseline) {
     await expect(page.locator(selector)).toBeVisible();
     await expect(page.getByRole('tab').first()).toBeVisible();
     const geometry = await page.locator(selector).evaluate(el => ({ scroll: el.scrollWidth, client: el.clientWidth }));
     expect(geometry.scroll).toBeLessThanOrEqual(geometry.client + 2);
    }
    await page.screenshot({ path: info.outputPath(`${baseline ? 'before' : 'after'}-${name}-${theme}-${width}.png`), fullPage: true });
   }

  }
 }
});

test.describe('focused admin tasks', () => {
 test.skip(baseline, 'Baseline is captured as before evidence only.');
 test('every workspace tab is keyboard reachable without creating work', async ({ page, request }, info) => {
  await page.setViewportSize({ width: 320, height: 900 });
  for (const [, path] of routes) {
   await open(page, path);
   const tabs = page.getByRole('tab');
   for (let index = 0; index < await tabs.count(); index++) {
    await tabs.nth(index).focus(); await page.keyboard.press('Enter');
    await expect(tabs.nth(index)).toHaveAttribute('aria-selected', 'true');
    await expect.poll(async () => tabs.nth(index).evaluate(el => {
     const tab = el.getBoundingClientRect(); const strip = el.closest('.mud-tabs-tabbar-content')!.getBoundingClientRect();
     return tab.left >= strip.left - 2 && tab.right <= strip.right + 2;
    })).toBe(true);
    if (path.endsWith('/data-management') && (await tabs.nth(index).innerText()).trim().toLowerCase() === 'schema') {
     await expect(page.getByRole('textbox', { name: 'Column name', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('combobox', { name: 'Column data type', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('checkbox', { name: 'Use as key column', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('checkbox', { name: 'Use as display column', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('checkbox', { name: 'Search this column', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('button', { name: 'Remove column id', exact: true })).toBeVisible();
     await page.screenshot({ path: info.outputPath('after-dataset-schema-light-320.png'), fullPage: true });
    }
   }
  }
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('role metadata separates identical names, retains tab navigation and assignment details', async ({ page }) => {
  await open(page, '/admin/roles');
  await expect(page.getByRole('tab', { name: 'Custom roles (1)' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('synthetic-custom', { exact: true })).toBeVisible();
  await expect(page.getByText('builtin-reader', { exact: true })).not.toBeVisible();
  const permissions = page.getByRole('button', { name: '2 permissions', exact: true });
  await permissions.click();
  await expect(page.getByRole('dialog')).toContainText('Read requests');
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeHidden();
  // Dialog disposal restores focus asynchronously; finish that handoff before
  // moving to a tab so Enter cannot activate the permissions opener again.
  await expect(permissions).toBeFocused();
  const builtInTab = page.getByRole('tab', { name: 'Built-in roles (1)' });
  await builtInTab.focus();
  await expect(builtInTab).toBeFocused();
  await builtInTab.press('Enter');
  await expect(builtInTab).toHaveAttribute('aria-selected', 'true');
  await expect(page).toHaveURL(/tab=built-in/);
  await expect(page.getByText('builtin-reader', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit role' })).toHaveCount(0);
  const assignments = page.getByRole('button', { name: '1250 assignments', exact: true });
  await assignments.click();
  await page.getByRole('dialog').getByRole('tab', { name: 'Assignments', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Synthetic Operator');
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeHidden();
  await expect(assignments).toBeFocused();
  await reload(page); await expect(page.getByText('builtin-reader', { exact: true })).toBeVisible();
  await page.goBack(); await expect(page.getByText('synthetic-custom', { exact: true })).toBeVisible();
  await page.getByRole('textbox', { name: 'Search roles' }).fill('does-not-match');
  await expect(page.getByText('No roles match this search.')).toBeVisible();
 });
 test('empty and many custom roles retain bounded rows and searchable results', async ({ page, request }) => {
  await request.get(`${fixture}/fault?mode=no-custom`); await open(page, '/admin/roles');
  await expect(page.getByText('No custom roles yet. Create a role to define permissions for an organization.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create custom role' })).toBeVisible();
  await request.get(`${fixture}/reset`); await request.get(`${fixture}/fault?mode=many-roles`); await reload(page);
  await expect(page.locator('.role-management-table tbody tr')).toHaveCount(25);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await page.getByRole('textbox', { name: 'Search roles' }).fill('Custom role 59');
  await expect(page.getByText('Custom role 59', { exact: true })).toBeVisible();
  await expect(page.locator('.role-management-table tbody tr')).toHaveCount(1);
 });
 test('custom role save retains owner and permissions', async ({ page, request }) => {
  await open(page, '/admin/roles');
  await page.getByRole('button', { name: 'Edit role', exact: true }).click();
  await page.getByRole('textbox', { name: 'Role name' }).fill('Synthetic responder');
  await page.getByRole('button', { name: 'Save role', exact: true }).click();
  await expect(page.getByText('Synthetic responder', { exact: true })).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1);
  expect(state.saves[0].path).toBe('/api/v1/admin/role-definitions/custom');
  expect(state.saves[0].payload.permissions).toEqual(['Incident.Read', 'Request.Read']);
 });
 test('dataset paging, retained drafts, explicit discard and stale searches', async ({ page, request }) => {
  await open(page, '/admin/resources/data-management');
  await expect(page.getByRole('tab', { name: 'Rows', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Inventory item 1', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /page 2/i }).click();
  await expect(page.getByText('Inventory item 65', { exact: true })).toBeVisible();
  const search = page.getByRole('textbox', { name: 'Search rows' });
  await search.fill('slow'); await page.waitForTimeout(300); await search.fill('item 64');
  await expect(page.getByText('Inventory item 64', { exact: true })).toBeVisible();
  await page.waitForTimeout(1100); await expect(page.getByText('Inventory item 64', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('textbox', { name: 'Dataset Name' }).fill('Draft inventory');
  await page.getByRole('tab', { name: 'Rows', exact: true }).click();
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Draft inventory');
  await select(page, 'Organization', 'Birch Tenant');
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Organization', exact: true })).toContainText('Atlas Tenant');
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Draft inventory');
  await select(page, 'Organization', 'Birch Tenant');
  await page.getByRole('button', { name: 'Discard', exact: true }).click();
  await expect(page.getByText('Birch inventory').first()).toBeVisible();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('dataset failed save retains draft and delete cancellation does not mutate', async ({ page, request }) => {
  await open(page, '/admin/resources/data-management'); await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('textbox', { name: 'Dataset Name' }).fill('Edited inventory');
  await request.get(`${fixture}/fault?mode=save`); await page.getByRole('button', { name: 'Save Dataset', exact: true }).click();
  await expect(page.getByText('Dataset save failed.')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Edited inventory');
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Atlas inventory');
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('65 row(s)');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('dataset save reloads the existing schema and retains both saved columns', async ({ page, request }) => {
  await open(page, '/admin/resources/data-management');
  await page.getByRole('tab', { name: 'Ingest API', exact: true }).click();
  await page.getByRole('textbox', { name: 'API key name', exact: true }).fill('unfinished-key');
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('textbox', { name: 'Dataset Name' }).fill('Saved inventory');
  await page.getByRole('button', { name: 'Save Dataset', exact: true }).click();
  await expect(page.getByText('Dataset saved.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Ingest API', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'API key name', exact: true })).toHaveValue('unfinished-key');
  await page.getByRole('textbox', { name: 'API key name', exact: true }).fill('');
  await page.getByRole('tab', { name: 'Rows', exact: true }).click();
  await reload(page); await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Saved inventory');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1); expect(state.saves[0].payload.columns.map(x => x.name)).toEqual(['id', 'name']);
 });
 test('dataset deletion preserves unrelated Graph draft and sync preserves API key draft', async ({ page, request }) => {
  await open(page, '/admin/resources/data-management');
  await page.getByRole('tab', { name: 'Graph sync', exact: true }).click();
  await page.getByText('Connector settings', { exact: true }).click();
  await page.getByRole('textbox', { name: 'Client Id', exact: true }).fill('unsaved-connector');
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText('Dataset deleted.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Graph sync', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Client Id', exact: true })).toHaveValue('unsaved-connector');
  await page.getByRole('button', { name: 'Revert settings', exact: true }).click();
  await request.get(`${fixture}/reset`); await reload(page);
  await page.getByRole('tab', { name: 'Graph sync', exact: true }).click();
  await page.getByText('Connector settings', { exact: true }).click();
  await page.getByLabel('Users', { exact: true }).check();
  await page.getByRole('button', { name: 'Save Graph Settings', exact: true }).click();
  await expect(page.getByText('Graph settings saved.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Ingest API', exact: true }).click();
  await page.getByRole('textbox', { name: 'API key name', exact: true }).fill('unsaved-key');
  await page.getByRole('tab', { name: 'Graph sync', exact: true }).click();
  await page.getByRole('button', { name: 'Sync Built-Ins', exact: true }).click();
  await expect(page.getByText('Synced 1 built-in datasets.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Ingest API', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'API key name', exact: true })).toHaveValue('unsaved-key');
 });
 test('late SLA response remains bound to the previous tenant', async ({ page, request }) => {
  await request.get(`${fixture}/fault?mode=slow-tenant`);
  await open(page, '/admin/tenant-sla-settings');
  await select(page, 'Tenant', 'Birch Tenant');
  await page.getByRole('tab', { name: 'Working calendars', exact: true }).click();
  await expect(page.getByLabel('Use business hours', { exact: true })).not.toBeChecked();
  await page.waitForTimeout(1200);
  await expect(page.getByLabel('Use business hours', { exact: true })).not.toBeChecked();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('SLA tabs preserve reporting drafts and keep calendar edits explicit', async ({ page, request }) => {
  await open(page, '/admin/tenant-sla-settings');
  await expect(page.getByRole('tab')).toHaveCount(3);
  await page.getByRole('tab', { name: 'Reporting', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save subscription' })).toHaveCount(0);
  await page.getByRole('tabpanel', { name: 'Reporting', exact: true }).getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('textbox', { name: 'Timezone', exact: true }).fill('UTC');
  await page.getByRole('tab', { name: 'Policies', exact: true }).click();
  await page.getByRole('tab', { name: 'Reporting', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Timezone', exact: true })).toHaveValue('UTC');
  await select(page, 'Tenant', 'Birch Tenant'); await page.getByRole('button', { name: 'Keep editing' }).click();
  await expect(page.getByRole('combobox', { name: 'Tenant', exact: true })).toContainText('Atlas Tenant');
  await request.get(`${fixture}/fault?mode=save`); await page.getByRole('button', { name: 'Save subscription' }).click();
  await expect(page.getByText('Subscription could not be saved. Your edits are retained.')).toBeVisible();
  await page.getByRole('button', { name: 'Save subscription' }).click();
  await expect(page.getByRole('textbox', { name: 'Timezone', exact: true })).toHaveCount(0);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1); expect(state.saves[0].payload.tenantId).toBe('org-1'); expect(state.saves[0].payload.timeZoneId).toBe('UTC');
  await page.getByRole('tab', { name: 'Working calendars', exact: true }).click();
  await expect(page.getByText(/^Inherited:/)).toBeVisible();
  await page.getByLabel('Use business hours', { exact: true }).click();
  await page.getByRole('tab', { name: 'Policies', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(1);
  await page.getByRole('tab', { name: 'Working calendars', exact: true }).click();
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
  await select(page, 'Tenant', 'Birch Tenant'); await page.getByRole('tab', { name: 'Reporting', exact: true }).click();
  await expect(page.getByText('No report subscriptions for this tenant.')).toBeVisible();
 });
 test('routing concerns are focused; opening and cancelling edits or diagnostics performs no writes', async ({ page, request }) => {
  await open(page, '/admin/support-routing');
  await expect(page.getByRole('tab')).toHaveCount(3);
  await expect(page.getByText('Support Coverage', { exact: true })).toBeVisible();
  await expect(page.getByText('Notification Rules', { exact: true })).not.toBeVisible();
  await page.getByRole('button', { name: 'Add Route' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await select(page, 'Routing concern', 'Notification rules');
  await expect(page.getByText('Notification Rules', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Diagnostics', exact: true }).click();
  await expect(page.getByText('Recipient Preview', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Bootstrap Defaults', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('coverage enable toggle retains explicit provider, group and role', async ({ page, request }) => {
  await open(page, '/admin/support-routing');
  await page.getByRole('button', { name: 'Disable', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Enable', exact: true })).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1);
  expect(state.saves[0].payload).toMatchObject({ customerOrganizationId: 'org-1', providerOrganizationId: 'provider', supportGroupId: 'group-1', role: 0, isEnabled: false });
 });
 test('delegated role editor retains permission ceiling and read-only users cannot enter admin routes', async ({ page }) => {
  await page.goto('https://127.0.0.1:5181/auth/development'); await open(page, 'https://127.0.0.1:5181/admin/roles');
  await page.getByRole('button', { name: 'Create custom role' }).click();
  await page.getByRole('combobox', { name: 'Permissions', exact: true }).click();
  await expect(page.getByRole('option', { name: 'HelpdeskAdmin', exact: true })).toHaveCount(0);
  await expect(page.getByRole('option', { name: 'DataManagement.Admin', exact: true })).toHaveCount(0);
  await page.keyboard.press('Escape'); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await open(page, 'https://127.0.0.1:5181/admin/resources/data-management');
  await expect(page.locator('.data-management-rail').getByText('Atlas Tenant', { exact: true })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Organization', exact: true })).toHaveCount(0);
  await expect(page.getByText('Birch Tenant', { exact: true })).toHaveCount(0);
  for (const [, path, , selector] of routes.filter(([name]) => name === 'routing' || name === 'sla')) {
   await page.goto(`https://127.0.0.1:5181${path}`);
   await expect(page.locator(selector)).toHaveCount(0);
  }
  await page.goto('https://127.0.0.1:5182/auth/development');
  for (const [, path, , selector] of routes) {
   await page.goto(`https://127.0.0.1:5182${path}`);
   await expect(page.locator(selector)).toHaveCount(0);
  }
 });
});

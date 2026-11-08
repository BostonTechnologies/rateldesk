import { expect, test, Page } from '@playwright/test';
const fixture = 'http://127.0.0.1:18500/fixture';
const baseline = process.env.HELPDESK_ADMIN_BASELINE === 'true';
// Explicit evidence screenshots below use synthetic non-secret states. Automatic failure captures must never include one-time ingestion keys.
test.use({screenshot:'off',trace:'off'});
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
async function settleEvidence(page: Page) {
 await page.evaluate(async()=>{await document.fonts.ready;});
 await expect.poll(()=>page.evaluate(()=>document.getAnimations().filter(animation=>
  animation.playState!=='finished'&&animation.playState!=='idle'&&Number.isFinite(animation.effect?.getComputedTiming().endTime)
 ).length)).toBe(0);
 await page.evaluate(()=>new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve()))));
}
async function select(page: Page, label: string, name: string) {
 const selector=page.getByRole('combobox',{name:new RegExp(`^${label}[*]?$`)});
 if(await selector.evaluate(element=>element.tagName==='INPUT'))await selector.fill(name);else await selector.click();
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
     await expect(page.getByRole('radio', { name: 'Use id as key column', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('radio', { name: 'Use name as display column', exact: true }).first()).toBeVisible();
     await expect(page.getByRole('checkbox', { name: 'Search name column', exact: true }).first()).toBeVisible();
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
  await expect(page.getByRole('combobox', { name: /^Organization\*?$/ })).toHaveValue('Atlas Tenant');
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
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('textbox', { name: 'Dataset Name' }).fill('Saved inventory');
  await page.getByRole('button', { name: 'Save Dataset', exact: true }).click();
  await expect(page.getByText('Dataset saved.', { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'API access', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create ingestion API key', exact: true })).toBeVisible();
  await expect(page.getByRole('textbox', { name: /^API key name\*?$/ })).toHaveCount(0);
  await reload(page); await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Dataset Name' })).toHaveValue('Saved inventory');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1); expect(state.saves[0].payload.columns.map(x => x.name)).toEqual(['id', 'name']);
 });
 test('built-in setup and cancelled Graph draft remain separate from custom schema editing', async ({ page, request }) => {
  await open(page, '/admin/resources/data-management');
  await expect(page.getByRole('heading', { name: 'Custom datasets', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Built-in integrations', exact: true })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Graph sync', exact: true })).toHaveCount(0);
  await page.getByRole('button',{name:'Set up Microsoft Graph',exact:true}).click();
  const wizard=page.getByRole('dialog',{name:'Microsoft Graph setup',exact:true});
  await wizard.getByRole('textbox',{name:/^Application \/ client ID\*?$/}).fill('unsaved-connector');
  await wizard.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole('dialog').filter({has:page.getByText('Discard Graph draft?',{exact:true})}).getByRole('button',{name:'Discard',exact:true}).click();
  await expect(wizard).toBeHidden();
  await page.getByRole('tab', { name: 'Schema', exact: true }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText('Dataset deleted.', { exact: true })).toBeVisible();
  await page.getByRole('button',{name:'Set up Microsoft Graph',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:/^Application \/ client ID\*?$/})).toHaveValue('');
  await wizard.getByRole('button',{name:'Cancel',exact:true}).click();
  const state=await(await request.get(`${fixture}/state`)).json();
  expect(state.saves.map(item=>item.method)).toEqual(['DELETE']);expect(state.graphTests).toEqual([]);
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
  const roleEditor=page.locator('.role-editor-dialog');
  await expect(roleEditor.locator('.permission-choice')).not.toHaveCount(0);
  await expect(roleEditor.locator('.permission-choice').filter({hasText:'HelpdeskAdmin'})).toHaveCount(0);
  await expect(roleEditor.locator('.permission-choice').filter({hasText:'DataManagement.Admin'})).toHaveCount(0);
  await roleEditor.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(roleEditor).toBeHidden();
  await open(page, 'https://127.0.0.1:5181/admin/resources/data-management');
  await expect(page.getByRole('combobox', { name: /^Organization\*?$/ })).toHaveValue('Atlas Tenant');
  await page.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Birch');
  await expect(page.getByRole('option',{name:'Birch Tenant',exact:true})).toHaveCount(0);
  await page.keyboard.press('Escape');
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

const datasetWizard = (page: Page) => page.getByRole('dialog', { name: /^(New custom dataset|Dataset created)$/ });
const graphWizard = (page: Page) => page.getByRole('dialog', { name: 'Microsoft Graph setup', exact: true });
async function beginDataset(page: Page, name: string) {
 await page.getByRole('button', { name: 'New dataset', exact: true }).click();
 const wizard=datasetWizard(page);
 await expect(wizard).toBeVisible();
 await wizard.getByRole('textbox',{name:/^Dataset name\*?$/}).fill(name);
 await wizard.getByRole('button',{name:'Continue',exact:true}).click();
 await expect(wizard.getByRole('heading',{name:'Design schema',exact:true})).toBeVisible();
 return wizard;
}
async function graphToTest(page: Page, selected: string[] = ['Users']) {
 await page.getByRole('button',{name:/^(Set up Microsoft Graph|Manage connection)$/}).click();
 const wizard=graphWizard(page);
 await expect(wizard).toBeVisible();
 const tenant=wizard.getByRole('textbox',{name:/^Tenant ID\*?$/});
 if(!await tenant.inputValue()) {
  await tenant.fill('synthetic-tenant');
  await wizard.getByRole('textbox',{name:/^Application \/ client ID\*?$/}).fill('synthetic-client');
  await wizard.getByLabel('Client secret',{exact:true}).fill('synthetic-draft-only');
 }
 await wizard.getByRole('button',{name:'Continue',exact:true}).click();
 for(const source of ['Users','Computers/devices','Groups','SharePoint sites']) {
  const checkbox=wizard.getByRole('checkbox',{name:source,exact:true});
  if(selected.includes(source))await checkbox.check();else await checkbox.uncheck();
 }
 await wizard.getByRole('button',{name:'Continue',exact:true}).click();
 await expect(wizard.getByRole('heading',{name:'Test access',exact:true})).toBeVisible();
 return wizard;
}

test.describe('guided datasets and Graph integrations',()=>{
 test.skip(baseline,'Baseline is captured as before evidence only.');
 test('organization context searches beyond the first page and source catalog exists before import',async({page,request})=>{
  await request.get(`${fixture}/fault?mode=many-organizations`);await open(page,'/admin/resources/data-management');
  await expect(page.locator('.graph-source-card')).toHaveCount(4);
  for(const source of ['Users','Computers/devices','Groups','SharePoint sites'])await expect(page.locator('.graph-source-card').filter({has:page.getByText(source,{exact:true})})).toContainText('Not imported');
  const organization=page.getByRole('combobox',{name:/^Organization\*?$/});
  await organization.fill('oRgAnIzAtIoN 39');
  await page.getByRole('option',{name:'Synthetic organization 39',exact:true}).click();
  await expect(organization).toHaveValue('Synthetic organization 39');
  await expect(page.getByText('No custom datasets yet. Create a dataset to define its schema.')).toBeVisible();
  await expect(page.getByText('Choose a dataset',{exact:true})).toBeVisible();
  await expect(page.getByRole('heading',{name:'New dataset',exact:true})).toHaveCount(0);
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.saves).toEqual([]);
  expect(state.reads.some(item=>item.path==='/api/v1/ticketing/organizations/search'&&item.query.includes('module=data-management'))).toBeTruthy();
 });
 test('zero-row built-ins stay visible and an unavailable count is never shown as zero',async({page,request})=>{
  test.setTimeout(120_000);
  await request.get(`${fixture}/fault?mode=built-in-zero`);await open(page,'/admin/resources/data-management');
  await expect(page.locator('.graph-source-card')).toHaveCount(4);
  await expect(page.locator('.graph-source-card').getByText('0 row(s)',{exact:true})).toHaveCount(4);
  await request.get(`${fixture}/reset`);await request.get(`${fixture}/fault?mode=unknown-graph-count`);await reload(page);
  // Each source read exhausts the configured GET retries before the page can report an unavailable count.
  await expect(page.locator('.graph-source-card').getByText('Row count unavailable',{exact:true})).toHaveCount(4,{timeout:90_000});
  await expect(page.locator('.graph-source-card').getByText('0 row(s)',{exact:true})).toHaveCount(0);
 });
 test('dataset wizard validates fields, preserves Back, and Cancel creates no definition or key',async({page,request})=>{
  await open(page,'/admin/resources/data-management');await page.getByRole('button',{name:'New dataset',exact:true}).click();
  const wizard=datasetWizard(page);
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByText('Enter a dataset name.',{exact:true})).toBeVisible();
  await wizard.getByRole('textbox',{name:/^Dataset name\*?$/}).fill('Retained draft');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await wizard.getByRole('textbox',{name:'Column name',exact:true}).nth(1).fill('ID');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByText('Field names must be unique, ignoring case.',{exact:true})).toBeVisible();
  await expect(wizard.getByRole('textbox',{name:'Column name',exact:true}).nth(1)).toBeFocused();
  await wizard.getByRole('textbox',{name:'Column name',exact:true}).nth(1).fill('name');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByRole('heading',{name:'Review & create',exact:true})).toBeVisible();
  await wizard.getByRole('button',{name:'Back',exact:true}).click();await wizard.getByRole('button',{name:'Back',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:/^Dataset name\*?$/})).toHaveValue('Retained draft');
  await wizard.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole('dialog').filter({has:page.getByText('Discard dataset draft?',{exact:true})}).getByRole('button',{name:'Keep editing',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:/^Dataset name\*?$/})).toHaveValue('Retained draft');
  await wizard.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole('dialog').filter({has:page.getByText('Discard dataset draft?',{exact:true})}).getByRole('button',{name:'Discard',exact:true}).click();
  await expect(wizard).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.saves).toEqual([]);expect(state.datasets).toHaveLength(2);expect(state.credentials).toEqual({});
 });
 test('custom dataset schema saves exactly once and skipping a key is complete',async({page,request})=>{
  await open(page,'/admin/resources/data-management');const wizard=await beginDataset(page,'Synthetic measurements');
  await wizard.getByRole('button',{name:'Add field',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:'Column name',exact:true})).toHaveCount(3);
  await wizard.getByRole('textbox',{name:'Column name',exact:true}).last().fill('capacity');
  await wizard.getByRole('combobox',{name:'Column data type',exact:true}).last().click();await page.getByRole('option',{name:'Number',exact:true}).click();
  await wizard.getByRole('button',{name:'Move capacity up',exact:true}).click();
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await request.get(`${fixture}/fault?mode=create-slow`);
  await wizard.getByRole('button',{name:'Create dataset',exact:true}).dblclick();
  await expect(wizard.getByText('Skipping key creation is a complete successful setup.',{exact:true})).toBeVisible();
  await wizard.getByRole('button',{name:'View dataset',exact:true}).click();
  await expect(page.getByText('Synthetic measurements',{exact:true}).first()).toBeVisible();
  const state=await(await request.get(`${fixture}/state`)).json();const creates=state.saves.filter(item=>item.path==='/api/v1/resources/datasets');
  expect(creates).toHaveLength(1);expect(state.datasets.filter(item=>item.name==='Synthetic measurements')).toHaveLength(1);expect(state.credentials).toEqual({});
  expect(creates[0].payload.columns.map(item=>item.name)).toEqual(['id','capacity','name']);
  expect(creates[0].payload.columns.find(item=>item.name==='capacity').dataType).toBe(1);
  expect(creates[0].payload.keyColumn).toBe('id');expect(creates[0].payload.displayColumn).toBe('name');
 });
 test('dataset server validation retains draft and a lost create response reconciles without duplicate save',async({page,request})=>{
  await open(page,'/admin/resources/data-management');let wizard=await beginDataset(page,'Conflicting dataset');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();await request.get(`${fixture}/fault?mode=create-validation`);
  await wizard.getByRole('button',{name:'Create dataset',exact:true}).click();
  await expect(wizard.getByText('A dataset with this name already exists.',{exact:true})).toBeVisible();
  await expect(wizard.getByRole('textbox',{name:/^Dataset name\*?$/})).toHaveValue('Conflicting dataset');
  await wizard.getByRole('textbox',{name:/^Dataset name\*?$/}).fill('Reconciled dataset');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await request.get(`${fixture}/fault?mode=create-lost`);await wizard.getByRole('button',{name:'Retry create',exact:true}).click();
  await expect(wizard.getByRole('button',{name:'Check creation status',exact:true})).toBeVisible();
  await expect(wizard.getByRole('button',{name:'Retry create',exact:true})).toBeDisabled();
  await wizard.getByRole('button',{name:'Check creation status',exact:true}).click();
  await expect(wizard.getByText('Skipping key creation is a complete successful setup.',{exact:true})).toBeVisible();
  await wizard.getByRole('button',{name:'Done',exact:true}).click();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.datasets.filter(item=>item.name==='Reconciled dataset')).toHaveLength(1);expect(state.credentials).toEqual({});
  expect(state.attempts.filter(item=>item.path==='/api/v1/resources/datasets')).toHaveLength(2);
  expect(state.saves.filter(item=>item.path==='/api/v1/resources/datasets')).toHaveLength(1);
 });
 test('Graph failed authentication keeps the draft and source-denied access blocks import until a fresh passing test',async({page,request})=>{
  await open(page,'/admin/resources/data-management');const wizard=await graphToTest(page,['Users','Groups']);
  await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeDisabled();
  await request.get(`${fixture}/fault?mode=graph-test-invalid`);await wizard.getByRole('button',{name:'Test connection',exact:true}).click();
  await expect(wizard.getByText(/Authentication: Failed/)).toBeVisible();
  await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeDisabled();
  let state=await(await request.get(`${fixture}/state`)).json();expect(state.saves).toEqual([]);expect(state.datasets).toHaveLength(2);expect(state.graph['org-1'].hasStoredSecret).toBe(false);
  await wizard.getByRole('button',{name:'Back',exact:true}).click();await wizard.getByRole('button',{name:'Back',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:/^Application \/ client ID\*?$/})).toHaveValue('synthetic-client');
  await wizard.getByRole('textbox',{name:/^Application \/ client ID\*?$/}).fill('corrected-client');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await request.get(`${fixture}/fault?mode=graph-test-denied`);await wizard.getByRole('button',{name:'Test connection',exact:true}).click();
  await expect(wizard.getByText(/Read permission is missing/)).toBeVisible();await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeDisabled();
  await wizard.getByRole('button',{name:'Back',exact:true}).click();await wizard.getByRole('checkbox',{name:'Groups',exact:true}).uncheck();await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByText(/Authentication: Passed/)).toHaveCount(0);await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeDisabled();
  await wizard.getByRole('button',{name:'Test connection',exact:true}).click();await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeEnabled();
  state=await(await request.get(`${fixture}/state`)).json();expect(state.graphTests).toHaveLength(3);expect(state.saves).toEqual([]);expect(state.importStatuses).toEqual({});expect(state.credentials).toEqual({});
 });
 test('Graph explicit import shows partial outcomes, preserves saved configuration and can reopen safely',async({page,request})=>{
  await open(page,'/admin/resources/data-management');const wizard=await graphToTest(page,['Users','Groups']);
  await wizard.getByRole('button',{name:'Test connection',exact:true}).click();await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByText(/removes records absent from each selected source/)).toBeVisible();
  expect((await(await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
  await request.get(`${fixture}/fault?mode=graph-import-partial`);await wizard.getByRole('button',{name:'Save and import',exact:true}).click();
  await expect(wizard.getByText(/Configuration saved\. Import: PartiallySucceeded/)).toBeVisible();
  await expect(wizard.getByRole('row').filter({has:page.getByRole('cell',{name:'Users',exact:true})})).toContainText('Succeeded');
  await expect(wizard.getByRole('row').filter({has:page.getByRole('cell',{name:'Groups',exact:true})})).toContainText('Failed');
  await wizard.getByRole('button',{name:'Done',exact:true}).click();
  await expect(page.getByRole('button',{name:'Manage connection',exact:true})).toBeVisible();
  await reload(page);await expect(page.getByText(/Run PartiallySucceeded/)).toBeVisible();
  await page.getByRole('button',{name:'Manage connection',exact:true}).click();
  await expect(wizard.getByRole('textbox',{name:/^Tenant ID\*?$/})).toHaveValue('synthetic-tenant');
  await expect(wizard.getByRole('checkbox',{name:'Use configured secret for this organization',exact:true})).toBeChecked();
  await expect(wizard.getByLabel('Client secret',{exact:true})).toBeDisabled();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.saves.filter(item=>item.path.endsWith('/import'))).toHaveLength(1);
  expect(state.graph['org-1'].backgroundSyncEnabled).toBe(false);expect(state.graph['org-1'].clientSecret).toBeNull();
 });
 test('Graph uncertain import reads persisted result before retry and retains existing UTC schedule',async({page,request})=>{
  await request.get(`${fixture}/fault?mode=graph-existing`);await open(page,'/admin/resources/data-management');
  await page.getByRole('button',{name:'Schedule',exact:true}).click();
  const schedule=page.getByRole('dialog',{name:'Recurring Graph import',exact:true});
  await expect(schedule.getByRole('switch',{name:'Enable recurring import',exact:true})).toBeChecked();
  await expect(schedule.getByRole('textbox',{name:'Cron Expression',exact:true})).toHaveValue('0 2 * * *');
  await expect(schedule.getByText(/Cron expressions use UTC/)).toBeVisible();
  await schedule.getByRole('textbox',{name:'Cron Expression',exact:true}).fill('0 5 * * *');
  await schedule.getByRole('button',{name:'Cancel',exact:true}).click();
  expect((await(await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
  const wizard=await graphToTest(page,['Users','Computers/devices']);
  await wizard.getByRole('button',{name:'Test connection',exact:true}).click();await wizard.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(wizard.getByText('Preserve existing UTC schedule: 0 2 * * *',{exact:true})).toBeVisible();
  await request.get(`${fixture}/fault?mode=graph-import-lost`);await wizard.getByRole('button',{name:'Save and import',exact:true}).click();
  await expect(wizard.getByText(/Import completion is uncertain/)).toBeVisible();
  await expect(wizard.getByRole('button',{name:'Save and import',exact:true})).toHaveCount(0);
  await wizard.getByRole('button',{name:'Check import status',exact:true}).click();
  await expect(wizard.getByText(/Configuration saved\. Import: Succeeded/)).toBeVisible();
  await wizard.getByRole('button',{name:'Done',exact:true}).click();
  await expect(page.locator('.graph-source-card').filter({has:page.getByText('Computers/devices',{exact:true})})).toContainText('0 row(s)');
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.saves.filter(item=>item.path.endsWith('/import'))).toHaveLength(1);expect(state.graph['org-1'].backgroundSyncEnabled).toBe(true);expect(state.graph['org-1'].backgroundSyncCronExpression).toBe('0 2 * * *');
 });
});

test.describe('optional ingestion key lifecycle',()=>{
 test.skip(baseline,'Baseline is captured as before evidence only.');
 // A one-time synthetic key is deliberately visible here. Never capture secret-bearing screenshots or traces, including failure artifacts.
 test.use({permissions:['clipboard-read','clipboard-write']});
 test('failed post-key metadata refresh leaves the page usable and reload confirms one key',async({page,request})=>{
  test.setTimeout(90_000);
  await open(page,'/admin/resources/data-management');await page.getByRole('tab',{name:'API access',exact:true}).click();
  await page.getByRole('button',{name:'Create ingestion API key',exact:true}).click();
  const key=page.getByRole('dialog',{name:'Create ingestion API key',exact:true});
  await key.getByRole('textbox',{name:/^API key name\*?$/}).fill('Metadata readback producer');
  await key.getByRole('button',{name:'Create key',exact:true}).click();
  await expect(key.getByRole('textbox',{name:'One-time API key',exact:true})).toBeVisible();
  await request.get(`${fixture}/fault?mode=credentials-read-failure`);await key.getByRole('button',{name:'Done',exact:true}).click();
  await expect(key).toBeHidden();
  await expect(page.getByRole('alert').filter({hasText:/key metadata.*(could not|unavailable)|could not.*key metadata/i})).toBeVisible({timeout:30_000});
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  await expect(page.getByRole('button',{name:'Create ingestion API key',exact:true})).toBeEnabled();
  await expect(page.getByRole('textbox',{name:'One-time API key',exact:true})).toHaveCount(0);
  await request.get(`${fixture}/fault?mode=`);await reload(page);await page.getByRole('tab',{name:'API access',exact:true}).click();
  await expect(page.getByText('Metadata readback producer',{exact:true})).toBeVisible();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.credentials['dataset-1']).toHaveLength(1);
  expect(state.saves.filter(item=>item.path.endsWith('/credentials'))).toHaveLength(1);expect(state.datasets).toHaveLength(2);
 });
 test('failed optional key creation retains the saved dataset and requires metadata readback',async({page,request})=>{
  await open(page,'/admin/resources/data-management');const wizard=await beginDataset(page,'Dataset without confirmed key');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();await wizard.getByRole('button',{name:'Create dataset',exact:true}).click();
  await wizard.getByRole('button',{name:'Create ingestion API key',exact:true}).click();
  const key=page.getByRole('dialog',{name:'Create ingestion API key',exact:true});
  await key.getByRole('textbox',{name:/^API key name\*?$/}).fill('Unconfirmed producer');
  await request.get(`${fixture}/fault?mode=save`);await key.getByRole('button',{name:'Create key',exact:true}).click();
  await expect(key.getByText(/check (the )?key metadata before creating another key/)).toBeVisible();
  await expect(key.getByRole('button',{name:'Create key',exact:true})).toBeDisabled();
  await expect(key.getByRole('textbox',{name:/^API key name\*?$/})).toHaveValue('Unconfirmed producer');
  await key.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole('dialog').filter({has:page.getByText('Discard key name?',{exact:true})}).getByRole('button',{name:'Discard',exact:true}).click();
  await wizard.getByRole('button',{name:'View dataset',exact:true}).click();await page.getByRole('tab',{name:'API access',exact:true}).click();
  await expect(page.getByText('Dataset without confirmed key',{exact:true}).first()).toBeVisible();
  await expect(page.getByText('Unconfirmed producer',{exact:true})).toHaveCount(0);
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.datasets.filter(item=>item.name==='Dataset without confirmed key')).toHaveLength(1);
  expect(state.saves.filter(item=>item.path==='/api/v1/resources/datasets')).toHaveLength(1);expect(state.attempts.filter(item=>item.path.endsWith('/credentials'))).toHaveLength(1);expect(state.credentials).toEqual({});
  expect(state.reads.some(item=>item.path.endsWith('/credentials'))).toBeTruthy();
 });
 test('optional named key copies once, clears on close, lists metadata and requires confirmed revoke',async({page,request})=>{
  await open(page,'/admin/resources/data-management');const wizard=await beginDataset(page,'Keyed dataset');
  await wizard.getByRole('button',{name:'Continue',exact:true}).click();await wizard.getByRole('button',{name:'Create dataset',exact:true}).click();
  await wizard.getByRole('button',{name:'Create ingestion API key',exact:true}).click();
  const key=page.getByRole('dialog',{name:'Create ingestion API key',exact:true});
  await key.getByRole('button',{name:'Create key',exact:true}).click();await expect(key.getByText('Enter a name for this integration.',{exact:true})).toBeVisible();
  await key.getByRole('textbox',{name:/^API key name\*?$/}).fill('Synthetic inventory producer');await key.getByRole('button',{name:'Create key',exact:true}).click();
  await expect(key.getByRole('textbox',{name:'One-time API key',exact:true})).toHaveValue('synthetic-one-time-dataset-key');
  await key.getByRole('button',{name:'Copy API key',exact:true}).focus();await page.keyboard.press('Enter');await expect(key.getByRole('status')).toHaveText('API key copied.');
  expect(await page.evaluate(()=>navigator.clipboard.readText())).toBe('synthetic-one-time-dataset-key');
  await key.getByRole('button',{name:'Done',exact:true}).click();await wizard.getByRole('button',{name:'View dataset',exact:true}).click();
  await expect(page.getByRole('textbox',{name:'One-time API key',exact:true})).toHaveCount(0);
  await page.getByRole('tab',{name:'API access',exact:true}).click();await expect(page.getByText('Synthetic inventory producer',{exact:true})).toBeVisible();
  await expect(page.getByText('synthetic-one-time-dataset-key',{exact:true})).toHaveCount(0);
  await page.getByRole('button',{name:'Revoke',exact:true}).click();await page.getByRole('dialog').getByRole('button',{name:'Cancel',exact:true}).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  let state=await(await request.get(`${fixture}/state`)).json();expect(state.saves.filter(item=>item.method==='DELETE')).toHaveLength(0);
  await page.getByRole('button',{name:'Revoke',exact:true}).click();await page.getByRole('dialog').getByRole('button',{name:'Revoke',exact:true}).click();
  await expect(page.getByText(/Revoked · Created/)).toBeVisible();
  await select(page,'Organization','Birch Tenant');await expect(page.getByText('Synthetic inventory producer',{exact:true})).toHaveCount(0);
  state=await(await request.get(`${fixture}/state`)).json();expect(state.saves.filter(item=>item.path.endsWith('/credentials'))).toHaveLength(1);expect(JSON.stringify(state)).not.toContain('synthetic-one-time-dataset-key');
 });
});

for(const theme of ['light','dark'])for(const width of [320,1440])test(`guided wizard responsive evidence ${width}px ${theme}`,async({page,request},info)=>{
 test.skip(baseline,'Baseline is captured as before evidence only.');
 await page.addInitScript(value=>localStorage.setItem('helpdesk.theme.preference',value),theme);await page.setViewportSize({width,height:900});await open(page,'/admin/resources/data-management');
 const wizard=await beginDataset(page,'Synthetic responsive schema');
 await expect(wizard.getByRole('button',{name:'Continue',exact:true})).toBeInViewport();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 await settleEvidence(page);
 await wizard.screenshot({path:info.outputPath(`dataset-wizard-${width}-${theme}.png`)});
 await wizard.getByRole('button',{name:'Cancel',exact:true}).click();await page.getByRole('dialog').filter({has:page.getByText('Discard dataset draft?',{exact:true})}).getByRole('button',{name:'Discard',exact:true}).click();
 const graph=await graphToTest(page,['Users','Computers/devices','Groups','SharePoint sites']);await graph.getByRole('button',{name:'Test connection',exact:true}).click();
 await expect(graph.getByText(/Authentication: Passed/)).toBeVisible();
 await expect(graph.getByRole('button',{name:'Continue',exact:true})).toBeInViewport();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy();
 await settleEvidence(page);
 await graph.screenshot({path:info.outputPath(`graph-wizard-${width}-${theme}.png`)});
 expect((await(await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
});

test.describe('grouped custom-role dialog',()=>{
 test('edit family selection, search, server rejection and clone retain exact grants',async({page,request})=>{
  await open(page,'/admin/roles');await page.getByRole('button',{name:'Edit role',exact:true}).click();
  const editor=page.locator('.role-editor-dialog');await expect(editor).toBeVisible();
  await expect(editor.getByRole('checkbox',{name:'Select all Incidents permissions',exact:true})).toHaveAttribute('aria-checked','mixed');
  await editor.getByRole('checkbox',{name:'Select all Incidents permissions',exact:true}).click();
  await editor.getByRole('searchbox',{name:'Search permissions',exact:true}).fill('Requests');
  await expect(editor.locator('.permission-family')).toHaveCount(1);
  await editor.getByRole('button',{name:'Select shown permissions',exact:true}).click();
  await editor.getByRole('textbox',{name:'Role name'}).fill('Synthetic responder');
  await request.get(`${fixture}/fault?mode=save`);await editor.getByRole('button',{name:'Save role',exact:true}).click();
  await expect(editor.getByRole('alert')).toContainText('could not be saved');
  await expect(editor.getByRole('textbox',{name:'Role name'})).toHaveValue('Synthetic responder');
  await editor.getByRole('button',{name:'Save role',exact:true}).click();await expect(editor).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1);expect(state.saves[0].payload.permissions).toEqual(['Incident.Delete','Incident.Manager','Incident.Read','Incident.User','Incident.Write','Request.Delete','Request.Execute','Request.Manager','Request.Read','Request.User','Request.Write']);
  await page.getByRole('button',{name:'Clone role',exact:true}).click();await expect(editor.getByRole('textbox',{name:'Role name'})).toHaveValue('Synthetic responder copy');
  await editor.getByRole('button',{name:'Save role',exact:true}).click();await expect(editor).toBeHidden();
  const cloned=await(await request.get(`${fixture}/state`)).json();expect(cloned.saves).toHaveLength(2);expect(cloned.saves[1].method).toBe('POST');expect(cloned.saves[1].payload.ownerOrganizationId).toBe('org-1');expect(cloned.saves[1].payload.permissions).toEqual(state.saves[0].payload.permissions);
 });
 test('create dependency validation and explicit cancel preserve saved roles',async({page,request})=>{
  await open(page,'/admin/roles');await page.getByRole('button',{name:'Create custom role',exact:true}).click();const editor=page.locator('.role-editor-dialog');
  await editor.getByRole('textbox',{name:'Role name'}).fill('Synthetic unsaved role');
  await editor.locator('.permission-choice').filter({hasText:'Incident.Write'}).getByRole('checkbox').check();
  await editor.getByRole('button',{name:'Save role',exact:true}).click();await expect(editor.getByRole('alert')).toContainText('matching Read permission');
  expect((await(await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await editor.getByRole('button',{name:'Cancel',exact:true}).click();await page.getByRole('button',{name:'Keep editing',exact:true}).click();await expect(editor.getByRole('textbox',{name:'Role name'})).toHaveValue('Synthetic unsaved role');
  await editor.getByRole('button',{name:'Cancel',exact:true}).click();await page.getByRole('button',{name:'Discard',exact:true}).click();await expect(editor).toBeHidden();expect((await(await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
 });
 test('dialog remains bounded with reachable actions at desktop and mobile widths',async({page},info)=>{
  for(const theme of ['light','dark']){
   await page.evaluate(value=>localStorage.setItem('helpdesk.theme.preference',value),theme);
   for(const width of [1440,390]){
    await page.setViewportSize({width,height:900});await open(page,'/admin/roles');await page.getByRole('button',{name:'Edit role',exact:true}).click();const editor=page.locator('.role-editor-dialog');await expect(editor).toBeVisible();
    await expect(editor.getByRole('button',{name:'Save role',exact:true})).toBeInViewport();await expect(editor.getByRole('button',{name:'Cancel',exact:true})).toBeInViewport();
    const box=await editor.evaluate(el=>({scroll:el.scrollWidth,client:el.clientWidth,top:el.getBoundingClientRect().top,bottom:el.getBoundingClientRect().bottom}));expect(box.scroll).toBeLessThanOrEqual(box.client+2);expect(box.top).toBeGreaterThanOrEqual(-1);expect(box.bottom).toBeLessThanOrEqual(901);
    await settleEvidence(page);await page.screenshot({path:info.outputPath(`role-dialog-${theme}-${width}.png`)});await editor.getByRole('button',{name:'Cancel',exact:true}).click();await expect(editor).toBeHidden();
   }
  }
 });
});

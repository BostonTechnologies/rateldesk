import { expect, test, type Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
import { expectConsentAcknowledged } from '../../tools/ci/mcp-host-browser-acknowledgement.mjs';
const fixture = 'http://127.0.0.1:18505/fixture';
async function open(page: Page, path = '/account/integration-credentials?tab=credentials') {
 await page.goto(path, { waitUntil: 'domcontentloaded' });
 await expect(page.getByTestId('integration-credentials-page').or(page.getByTestId('integration-pairing-approval')).first()).toBeVisible();
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function reviewClient(page: Page, code = 'ABCD-TEST') {
 await page.getByLabel('Client code', { exact: true }).fill(code);
 await page.getByRole('button', { name: 'Review client', exact: true }).click();
 const dialog = page.getByRole('dialog', { name: 'Connection consent', exact: true });
 await expect(dialog).toBeVisible();
 return dialog;
}
test.beforeEach(async ({ page, request }) => {
 const diagnostics: unknown[] = [];
 (page as any).__integrationDiagnostics = diagnostics;
 page.on('pageerror', error => diagnostics.push({ pageError: error.message }));
 page.on('console', message => { if (message.type() === 'error') diagnostics.push({ browserError: message.text() }); });
 page.on('framenavigated', frame => { if (frame === page.mainFrame()) diagnostics.push({ navigation: frame.url() }); });
 page.on('response', async response => {
  if (new URL(response.url()).pathname === '/session/access')
   diagnostics.push({ sessionStatus: response.status(), profile: await response.json().catch(() => null) });
 });
 await request.get(`${fixture}/reset`);
 await page.goto('/auth/development', { waitUntil: 'domcontentloaded' });
});
test.afterEach(async ({ page }, info) => {
 if (info.status !== info.expectedStatus)
  await info.attach('synthetic-navigation-session-diagnostics', { body: JSON.stringify((page as any).__integrationDiagnostics ?? [], null, 2), contentType: 'application/json' });
});
for (const theme of ['light', 'dark']) for (const width of [390, 1366, 1920]) {
 test(`compact ordinary review ${theme} ${width}px full catalog and accessible footer`, async ({ page, request }, info) => {
  await page.setViewportSize({ width, height: 768 });
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  await open(page);
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
  if (width >= 1366) {
   const drawer = page.getByTestId('app-navigation-drawer');
   if (await drawer.evaluate(el => el.getBoundingClientRect().right) <= 1) await page.getByRole('button', { name: 'Toggle navigation', exact: true }).click();
   await expect.poll(() => drawer.evaluate(el => el.getBoundingClientRect().right)).toBeGreaterThan(100);
   const host = await page.getByRole('region', { name: 'HTTP MCP host deployment' }).boundingBox();
   const client = await page.getByRole('region', { name: 'CLI / MCP client access' }).boundingBox();
   expect(Math.abs(host!.y - client!.y)).toBeLessThanOrEqual(2);
  }
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`entries-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  const dialog = await reviewClient(page);
  await dialog.getByLabel('Organization', { exact: true }).selectOption('org-a');
  const catalog = (await (await request.get(`${fixture}/state`)).json()).permissions as string[];
  const selector = dialog.getByTestId('credential-permission-selector');
  await expect(selector.getByRole('checkbox')).toHaveCount(catalog.length);
  await expect(selector.getByRole('checkbox', { checked: true })).toHaveCount(0);
  await selector.getByRole('button', { name: `Select ${catalog.length} displayed eligible permissions`, exact: true }).click();
  await expect(selector.getByRole('checkbox', { checked: true })).toHaveCount(catalog.length);
  await dialog.getByRole('checkbox', { name: 'I approve this client connection with the permissions selected above.', exact: true }).check();
  const approve = dialog.getByRole('button', { name: 'Approve connection', exact: true });
  await expect(approve).toBeEnabled();
  const rect = await dialog.boundingBox();
  expect(rect!.x).toBeGreaterThanOrEqual(0); expect(rect!.y).toBeGreaterThanOrEqual(0);
  expect(rect!.x + rect!.width).toBeLessThanOrEqual(width + 1); expect(rect!.y + rect!.height).toBeLessThanOrEqual(769);
  const footer = await dialog.locator('footer').boundingBox();
  expect(footer!.y + footer!.height).toBeLessThanOrEqual(769);
  expect(await selector.locator('label').first().evaluate(el => parseFloat(getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(14);
  await approve.focus(); await page.keyboard.press('Tab');
  expect(await dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`ordinary-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  // The wrapped focus target is the native organization select, whose own
  // Escape handling may close its picker. Exercise dialog Escape from its footer.
  await dialog.getByRole('button', { name: 'Close', exact: true }).focus();
  await page.keyboard.press('Escape'); await expect(dialog).toBeHidden();
  await expect(page.getByRole('button', { name: 'Review client', exact: true })).toBeFocused();
 });
}

test('exact filtered ordinary grants, organization changes and transient consent preserve review state', async ({ page, request }) => {
 await open(page);
 const dialog = await reviewClient(page, ' abcd test ');
 await dialog.getByLabel('Organization', { exact: true }).selectOption('org-a');
 const selector = dialog.getByTestId('credential-permission-selector');
 await selector.getByRole('searchbox').fill('Incident.Read');
 await selector.getByRole('button', { name: 'Select 1 displayed eligible permissions', exact: true }).click();
 await selector.getByRole('searchbox').fill('Request');
 await expect(selector.getByText('Selected (including search-hidden): Incident.Read', { exact: true })).toBeVisible();
 await selector.getByRole('button', { name: 'Select displayed Request', exact: true }).click();
 await selector.getByRole('button', { name: 'Clear Request', exact: true }).click();
 const consent = dialog.getByRole('checkbox', { name: 'I approve this client connection with the permissions selected above.', exact: true });
 await consent.check();
 await dialog.getByLabel('Organization', { exact: true }).selectOption('org-b');
 await expect(consent).not.toBeChecked();
 await expect(dialog.locator('input[name="permissions"]')).toHaveCount(0);
 await dialog.getByRole('checkbox', { name: 'Incident.Read', exact: true }).check();
 await consent.check();
 await request.get(`${fixture}/fault?mode=consent`);
 await dialog.getByRole('button', { name: 'Approve connection', exact: true }).click();
 await expect(dialog.getByRole('alert')).toContainText('Your selection is retained');
 await expect(dialog.locator('input[name="permissions"]')).toHaveValue('Incident.Read');
 await expect(consent).toBeChecked();
 await dialog.getByRole('button', { name: 'Approve connection', exact: true }).click();
 await expectConsentAcknowledged(page);
 const calls = (await (await request.get(`${fixture}/state`)).json()).calls;
 const approvals = calls.filter((c: any) => c.path.endsWith('/pairing/approve'));
 expect(approvals).toHaveLength(2);
 expect(approvals[1].payload).toMatchObject({ organizationId: 'org-b', permissions: ['Incident.Read'], lifetimeDays: 30, consent: true });
});

test('temporary verifier has only explicit organization and short status consent', async ({ page, request }) => {
 await open(page);
 const dialog = await reviewClient(page, 'TEMP-TEST');
 await expect(dialog).toContainText('Temporary verification client');
 await expect(dialog.getByTestId('credential-permission-selector')).toHaveCount(0);
 await expect(dialog.locator('[name="lifetimeDays"]')).toHaveCount(0);
 await expect(dialog).toContainText('five minutes');
 await dialog.getByLabel('Organization', { exact: true }).selectOption('org-a');
 await dialog.getByRole('checkbox', { name: 'I approve only this temporary status verification.', exact: true }).check();
 await dialog.getByRole('button', { name: 'Approve connection', exact: true }).click();
 await expectConsentAcknowledged(page);
 const calls = (await (await request.get(`${fixture}/state`)).json()).calls;
 expect(calls.find((c: any) => c.path.endsWith('/pairing/approve')).payload).toMatchObject({ permissions: [], lifetimeDays: null, organizationId: 'org-a', consent: true });
});

test('host and client codes never switch approval kind and host consent binds exact references', async ({ page, request }) => {
 await open(page);
 await page.getByLabel('Client code', { exact: true }).fill('HOST-TEST');
 await page.getByRole('button', { name: 'Review client', exact: true }).click();
 await expect(page.getByRole('alert')).toContainText('connection kind');
 await expect(page.getByRole('dialog')).toHaveCount(0);
 await page.getByLabel('Host code', { exact: true }).fill('HOST-TEST');
 await page.getByLabel('Host code', { exact: true }).press('Enter');
 const dialog = page.getByRole('dialog', { name: 'Host deployment consent', exact: true });
 await expect(dialog).toContainText('office-private');
 await expect(dialog).toContainText('AllowedOrigins'); await expect(dialog).toContainText('Compose environment');
 await expect(dialog.getByTestId('credential-permission-selector')).toHaveCount(0);
 await dialog.getByRole('checkbox', { name: 'I approve this exact baseline or configuration proposal and its source generation.', exact: true }).check();
 await dialog.getByRole('button', { name: 'Approve host', exact: true }).click();
 await expectConsentAcknowledged(page);
 await expect(page.locator('.notification-badge').getByRole('status')).toHaveText('1');
 await expect(page.getByRole('status')).toHaveCount(2);
 const calls = (await (await request.get(`${fixture}/state`)).json()).calls;
 expect(calls.find((c: any) => c.path.endsWith('/host-proposals/approve')).payload).toMatchObject({ proposalHash: 'a'.repeat(64), sourceHash: 'b'.repeat(64), consent: true });
});

for (const state of ['missing', 'denied']) {
 test(`unrelated page status cannot acknowledge ${state} consent`, async ({ page }) => {
  await page.setContent(`<aside role="status">Connection approved</aside>
   <section data-testid="integration-pairing-approval">
    ${state === 'denied' ? '<p role="status">Connection denied. No access was granted.</p>' : '<p>No approval result.</p>'}
   </section>`);
  await expect(expectConsentAcknowledged(page)).rejects.toThrow(/toContainText/);
 });
}

test('manual credential organization selection ignores the native status filter and reviews only exact grants', async ({ page }) => {
 await open(page);
 await expect(page.getByRole('combobox', { name: 'Status', exact: true }).getByRole('option', { name: 'All (25)', exact: true })).toHaveCount(1);
 await page.getByTestId('integration-credential-create').click();
 const integration = page.getByRole('dialog', { name: 'Create integration', exact: true });
 await integration.getByLabel('Connection type', { exact: true }).selectOption('api');
 await integration.getByLabel('Integration name', { exact: true }).fill('Synthetic manual credential');
 await integration.getByRole('button', { name: 'Next', exact: true }).click();
 await expect(integration.getByRole('heading', { name: 'Server access', exact: true })).toBeVisible();
 await integration.getByRole('button', { name: 'Next', exact: true }).click();
 const organization = page.getByRole('combobox', { name: 'Organization', exact: true });
 await organization.selectOption({ label: 'Synthetic Beta' });
 await expect(organization.locator('option:checked')).toHaveText('Synthetic Beta');
 const permissions = page.getByTestId('credential-permission-selector');
 await expect(permissions.getByRole('checkbox', { checked: true })).toHaveCount(0);
 await permissions.getByRole('checkbox', { name: /Incident\.User$/ }).check();
 await expect(permissions.getByRole('checkbox', { checked: true })).toHaveCount(1);
 await integration.getByRole('button', { name: 'Next', exact: true }).click();
 await expect(integration.getByRole('heading', { name: 'Synthetic Beta', exact: true })).toBeVisible();
 await expect(integration.getByText('No server access', { exact: true })).toBeVisible();
 await integration.getByText('Selected permissions', { exact: true }).click();
 await expect(integration.locator('.setup-permission-list li')).toHaveText(['Use incidents']);
 await expect(integration.getByRole('button', { name: 'Create integration', exact: true })).toBeDisabled();
 await integration.getByRole('checkbox', { name: 'I have reviewed this connection and its access, and approve creating the integration.', exact: true }).check();
 await expect(integration.getByRole('button', { name: 'Create integration', exact: true })).toBeEnabled();
});

test('ordinary account cannot see host approval entry', async ({ page, request }) => {
 await page.goto('https://127.0.0.1:5191/auth/development');
 await open(page, 'https://127.0.0.1:5191/account/integration-credentials/approve?kind=host');
 await expect(page.getByLabel('Host code', { exact: true })).toHaveCount(0);
 await expect(page.getByRole('button', { name: 'Review host', exact: true })).toHaveCount(0);
 await expect(page.getByRole('button', { name: 'Review client', exact: true })).toBeVisible();
 expect((await (await request.get(`${fixture}/state`)).json()).calls.filter((c: any) => c.path.includes('host-proposals'))).toHaveLength(0);
});

test('selected inactive cleanup retains only partial failures and survives a fresh list', async ({ page, request }, info) => {
 await open(page);
 await expect(page.getByRole('combobox', { name: 'Status', exact: true }).getByRole('option', { name: 'All (25)', exact: true })).toHaveCount(1);
 await page.getByRole('checkbox', { name: 'Select inactive Synthetic credential 01', exact: true }).check();
 await page.getByRole('checkbox', { name: 'Select inactive Synthetic credential 02', exact: true }).check();
 await request.get(`${fixture}/fault?mode=partial`);
 await page.getByRole('button', { name: 'Delete selected inactive', exact: true }).click();
 const dialog = page.getByRole('dialog', { name: 'Credential lifecycle', exact: true });
 await expect(dialog).toContainText('Synthetic credential 01'); await expect(dialog).toContainText('Synthetic credential 02');
 await dialog.getByRole('button', { name: 'Confirm delete', exact: true }).click();
 await expect(dialog.getByRole('alert')).toContainText('Synthetic credential 02: action failed');
 await expect(dialog.locator('li')).toHaveCount(1);
 await page.screenshot({ path: info.outputPath('selected-inactive-partial-failure.png'), fullPage: false, animations: 'disabled' });
 await request.get(`${fixture}/fault?mode=`);
 await dialog.getByRole('button', { name: 'Confirm delete', exact: true }).click();
 await expect(dialog).toBeHidden();
 await page.getByRole('button', { name: 'Refresh', exact: true }).click();
 await expect(page.getByRole('cell', { name: 'Synthetic credential 01', exact: false })).toHaveCount(0);
 await expect(page.getByRole('cell', { name: 'Synthetic credential 02', exact: false })).toHaveCount(0);
 const state = await (await request.get(`${fixture}/state`)).json();
 expect(state.credentials).toHaveLength(23);
 const deleted = state.calls.filter((c: any) => c.method === 'DELETE'); expect(deleted).toHaveLength(3);
 expect(deleted.every((c: any) => /00000000000[23]\/record$/.test(c.path))).toBe(true);
});

async function assertSystemReviewFits(page: Page, name: string, width: number, height = 768) {
 const dialog = page.getByRole('dialog', { name, exact: true });
 await expect(dialog).toBeVisible();
 const bounds = await dialog.boundingBox();
 expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.y).toBeGreaterThanOrEqual(0);
 expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1);
 expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(height + 1);
 const footer = await dialog.locator('footer').boundingBox();
 expect(footer!.y + footer!.height).toBeLessThanOrEqual(height + 1);
 expect(await dialog.evaluate(el => el.scrollHeight - el.clientHeight)).toBeLessThanOrEqual(2);
 await assertNoHorizontalOverflow(page);
 return dialog;
}
async function selectSystemChoice(page: Page, label: string, name: string) {
 const selected = page.getByRole('combobox', { name: label, exact: true });
 await selected.click();
 const options = page.getByRole('listbox');
 await options.getByRole('option', { name, exact: true }).click();
 await expect(selected).toHaveText(name);
 await expect(options).toBeHidden();
}
for (const theme of ['light', 'dark']) for (const width of [390, 1366, 1920]) {
 test(`compact system connections ${theme} ${width}px bounded reviews and authorized context`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 768 });
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  await open(page, '/account/integration-credentials?tab=connections&organizationId=org-b');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
  const panel = page.getByTestId('system-connections-panel');
  await expect(panel.getByTestId('system-connection-row')).toHaveCount(7);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  if (width >= 1366) {
   const drawer = page.getByTestId('app-navigation-drawer');
   if (await drawer.evaluate(el => el.getBoundingClientRect().right) <= 1) await page.getByRole('button', { name: 'Toggle navigation', exact: true }).click();
   await expect.poll(() => drawer.evaluate(el => el.getBoundingClientRect().right)).toBeGreaterThan(100);
  }
  const first = panel.locator('[data-connection-id="mapping-1"]');
  const second = panel.locator('[data-connection-id="mapping-2"]');
  const a = await first.boundingBox(), b = await second.boundingBox();
  if (width === 1920) expect(Math.abs(a!.y - b!.y)).toBeLessThanOrEqual(2);
  else expect(b!.y).toBeGreaterThan(a!.y + 10);
  await expect(first).toContainText('Configured');
  await expect(first).toContainText('Not tested. Configuration alone does not verify current access.');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`systems-list-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  await first.getByRole('button', { name: 'View', exact: true }).click();
  let dialog = await assertSystemReviewFits(page, 'Connection configuration', width);
  // A link to Beta never rewrites a saved Alpha mapping.
  await expect(dialog.getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('Synthetic Alpha');
  await expect(dialog.getByRole('combobox', { name: 'RatelDesk customer', exact: true })).toHaveText('Synthetic Alpha customer');
  await expect(dialog.getByTestId('save-system-connection')).toBeVisible();
  await page.screenshot({ path: info.outputPath(`systems-configuration-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  await dialog.getByRole('button', { name: 'Back to connections', exact: true }).click();
  await expect(first.getByRole('button', { name: 'View', exact: true })).toBeFocused();
  await panel.getByTestId('create-system-connection').click();
  dialog = await assertSystemReviewFits(page, 'Create Connection', width);
  await expect(dialog.locator('input')).toHaveCount(2);
  await expect(dialog.getByLabel('Pairing code', { exact: true })).toHaveAttribute('type', 'password');
  await expect(dialog.getByTestId('pair-and-connect')).toBeDisabled();
  await page.screenshot({ path: info.outputPath(`systems-create-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).focus();
  await page.keyboard.press('Escape'); await expect(dialog).toBeHidden();
  await expect(panel.getByTestId('create-system-connection')).toBeFocused();
  await panel.getByTestId('generate-pairing-code').click();
  dialog = await assertSystemReviewFits(page, 'System pairing code', width);
  await expect(dialog.getByLabel('Pairing code', { exact: true })).toHaveValue('FAKE-CODE');
  await expect(dialog).toContainText('Single use');
  await expect(dialog.getByRole('button', { name: 'Copy code', exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath(`systems-code-synthetic-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await first.getByTestId('delete-system-connection').click();
  dialog = await assertSystemReviewFits(page, 'Delete system connection?', width);
  await expect(dialog).toContainText('Synthetic system connection 1');
  await expect(dialog.getByTestId('confirm-system-connection-delete')).toBeVisible();
  await page.screenshot({ path: info.outputPath(`systems-delete-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Search system connections', exact: true }).fill('unfinished');
  await expect(panel.getByTestId('system-connection-row')).toHaveCount(1);
  await panel.getByRole('button', { name: 'Continue setup', exact: true }).click();
  dialog = await assertSystemReviewFits(page, 'Systems paired', width);
  await expect(dialog.getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('Synthetic Beta');
  await expect(dialog.getByTestId('save-system-connection')).toBeDisabled();
  await page.screenshot({ path: info.outputPath(`systems-resume-${theme}-${width}.png`), fullPage: false, animations: 'disabled' });
 });
}

test('system connection context, interrupted save, durable resume and row-local retries preserve exact identities', async ({ page, request }) => {
 await page.goto('/admin/automation/integration/orchestrator?organizationId=org-b');
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
 const manage = page.getByTestId('manage-system-connections');
 await expect(manage).toHaveAttribute('href', /organizationId=org-b/);
 await manage.click();
 await expect(page).toHaveURL(/organizationId=org-b/);
 await expect(page.getByRole('link', { name: 'Return to setup', exact: true })).toHaveAttribute('href', '/admin/automation/integration/orchestrator?organizationId=org-b');
 await page.getByRole('tab', { name: 'API & MCP credentials', exact: true }).click();
 await expect(page).toHaveURL(/tab=credentials/);
 expect(new URL(page.url()).searchParams.get('organizationId')).toBe('org-b');
 expect(new URL(page.url()).searchParams.get('returnUrl')).toBe('/admin/automation/integration/orchestrator?organizationId=org-b');
 await page.getByRole('tab', { name: 'System connections', exact: true }).click();
 await expect(page.getByRole('dialog')).toHaveCount(0);
 const panel = page.getByTestId('system-connections-panel');
 await panel.locator('[data-connection-id="pair-awaiting"]').getByRole('button', { name: 'Continue setup', exact: true }).click();
 const dialog = page.getByRole('dialog', { name: 'Systems paired', exact: true });
 await expect(dialog.getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('Synthetic Beta');
 await selectSystemChoice(page, 'NetRatel tenant', 'Synthetic monitoring');
 const connectionName = dialog.getByLabel('Connection name', { exact: true });
 await connectionName.fill('Synthetic resumed connection');
 await connectionName.press('Tab');
 await dialog.getByRole('checkbox', { name: 'Run automation — RatelDesk → NetRatel', exact: true }).check();
 await expect(dialog.getByTestId('save-system-connection')).toBeEnabled();
 await expect(connectionName).toHaveValue('Synthetic resumed connection');
 await request.get(`${fixture}/fault?mode=mapping-save`);
 await dialog.getByTestId('save-system-connection').click();
 await expect(dialog.getByRole('alert')).toContainText('Synthetic mapping save unavailable');
 await expect(dialog.getByLabel('Connection name', { exact: true })).toHaveValue('Synthetic resumed connection');
 await dialog.getByTestId('save-system-connection').click();
 await expect(dialog).toBeHidden();
 let state = await (await request.get(`${fixture}/state`)).json();
 const saves = state.calls.filter((call: any) => call.method === 'PUT');
 expect(saves).toHaveLength(2);
 expect(saves[0].payload).toEqual(saves[1].payload);
 expect(saves[1].payload).toMatchObject({ pairId: 'pair-awaiting', netRatelTenantId: 'tenant-a', ratelDeskOrganizationId: 'org-b', runAutomation: true });
 const mappingId = saves[1].payload.id;
 await page.reload();
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
 await expect(panel.locator(`[data-connection-id="${mappingId}"]`)).toContainText('Synthetic resumed connection');
 await expect(page.getByRole('dialog')).toHaveCount(0);
 await panel.locator(`[data-connection-id="${mappingId}"]`).getByRole('button', { name: 'View', exact: true }).click();
 await expect(page.getByRole('dialog').getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('Synthetic Beta');
 await page.getByRole('dialog').getByRole('button', { name: 'Back to connections', exact: true }).click();
 await open(page, '/account/integration-credentials?tab=connections&organizationId=unavailable-org');
 await panel.locator('[data-connection-id="mapping-1"]').getByRole('button', { name: 'Add connection', exact: true }).click();
 await expect(page.getByTestId('connection-organization-hint-error')).toBeVisible();
 await expect(page.getByRole('dialog').getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('');
 await expect(page.getByTestId('save-system-connection')).toBeDisabled();
 await page.getByRole('dialog').getByRole('button', { name: 'Back to connections', exact: true }).click();
 const first = panel.locator('[data-connection-id="mapping-1"]'), second = panel.locator('[data-connection-id="mapping-2"]');
 await request.get(`${fixture}/fault?mode=system-test`);
 await first.getByTestId('test-system-connection').click();
 await expect(first).toContainText('Testing connection…');
 await expect(first.getByTestId('connection-row-error')).toContainText('Synthetic connection test unavailable');
 await expect(second.getByTestId('connection-row-error')).toHaveCount(0);
 await first.getByTestId('test-system-connection').click();
 await expect(first.getByTestId('connection-test-result')).toContainText('Synthetic authorized connection test passed');
 await first.getByTestId('delete-system-connection').click();
 await page.getByTestId('confirm-system-connection-delete').click();
 await panel.getByRole('button', { name: 'Refresh connections', exact: true }).click();
 await expect(first).toHaveCount(0);
 await expect(second).toBeVisible();
 state = await (await request.get(`${fixture}/state`)).json();
 expect(state.systemConnections.some((connection: any) => connection.id === mappingId && connection.pairId === 'pair-awaiting')).toBe(true);
});


async function openWorkspace(page: Page, path: string, testId: string) {
 await page.goto(path, { waitUntil: 'domcontentloaded' });
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
 await expect(page.getByTestId(testId)).toBeVisible();
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function monacoEditor(page: Page) {
 const surface = page.getByTestId('monaco-text-editor');
 await expect(surface.locator('.monaco-editor').first()).toBeVisible({ timeout: 20_000 });
 await expect(surface.getByText('The code editor could not load.', { exact: false })).toHaveCount(0);
 return surface;
}
async function typeGuidance(page: Page, text: string) {
 const surface = await monacoEditor(page);
 await surface.getByRole('textbox', { name: 'Prompt guidance', exact: true }).focus();
 await page.keyboard.press('ControlOrMeta+A');
 await page.keyboard.insertText(text);
 await expect(page.getByTestId('prompt-save')).toBeEnabled();
 return surface;
}
async function credentialDetails(page: Page, name = 'Synthetic credential 03') {
 const row = page.getByRole('row').filter({ hasText: name });
 await row.getByRole('button', { name: 'Details', exact: true }).click();
 const dialog = page.getByRole('dialog', { name: `Integration credential: ${name}`, exact: true });
 await expect(dialog.getByTestId('credential-edit-access')).toBeVisible();
 return { row, dialog };
}

for (const theme of ['light', 'dark']) for (const width of [390, 1440]) {
 test(`aligned credential, investigation and Monaco workspaces ${theme} ${width}px`, async ({ page, request }, info) => {
  await page.setViewportSize({ width, height: 900 });
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  await request.get(`${fixture}/seed-ai`);
  await open(page);
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
  const { row, dialog } = await credentialDetails(page);
  await assertSystemReviewFits(page, 'Integration credential: Synthetic credential 03', width, 900);
  await expect(dialog.getByRole('heading', { name: 'Configured access', exact: true })).toBeVisible();
  await expect(dialog).toContainText('2030-01-01T00:00:00.0000000Z');
  await page.screenshot({ path: info.outputPath(`aligned-credential-details-${theme}-${width}.png`), animations: 'disabled' });
  await dialog.getByTestId('credential-edit-access').click();
  const selector = dialog.getByTestId('credential-permission-selector');
  await selector.getByRole('searchbox').fill('Incident.Read');
  await expect(selector.getByRole('checkbox', { name: 'Incident.Read', exact: true })).toBeChecked();
  await selector.getByRole('searchbox').fill('Request.Read');
  await selector.getByRole('checkbox', { name: 'Request.Read', exact: true }).check();
  await dialog.getByRole('button', { name: 'Review changes', exact: true }).click();
  await expect(dialog.getByTestId('credential-access-review')).toContainText('Request.Read');
  await expect(dialog.getByRole('button', { name: 'Apply access changes', exact: true })).toBeDisabled();
  await assertSystemReviewFits(page, 'Integration credential: Synthetic credential 03', width, 900);
  await dialog.getByRole('button', { name: 'Cancel edit', exact: true }).focus();
  await page.keyboard.press('Shift+Tab');
  expect(await dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.screenshot({ path: info.outputPath(`aligned-credential-review-${theme}-${width}.png`), animations: 'disabled' });
  await dialog.getByRole('button', { name: 'Cancel edit', exact: true }).click();
  await dialog.getByRole('button', { name: 'Close', exact: true }).focus();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(row.getByRole('button', { name: 'Details', exact: true })).toBeFocused();

  await openWorkspace(page, '/ai/ops?id=00000000-0000-0000-0000-000000000201', 'ai-investigations');
  const history = page.getByRole('complementary', { name: 'Recent investigations', exact: true });
  const conversation = page.getByRole('region', { name: 'Investigation workspace', exact: true });
  await expect(history.locator('.ai-investigation')).toHaveCount(4);
  await expect(conversation.locator('.ai-chat-transcript')).toContainText('The selected incident records repeated delivery delays');
  await expect(conversation.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
  const sidebar = await history.boundingBox(), detail = await conversation.boundingBox();
  if (width > 900) {
   expect(sidebar!.width).toBeLessThanOrEqual(311);
   expect(detail!.x).toBeGreaterThan(sidebar!.x + sidebar!.width);
   expect(Math.abs(sidebar!.y - detail!.y)).toBeLessThanOrEqual(2);
  } else expect(detail!.y).toBeGreaterThan(sidebar!.y + sidebar!.height);
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`aligned-investigations-${theme}-${width}.png`), fullPage: true, animations: 'disabled' });
  await history.getByRole('combobox', { name: 'Status', exact: true }).selectOption('approval');
  await expect(history.locator('.ai-investigation')).toHaveCount(1);
  await expect(history).toContainText('Review proposed remediation');
  await page.getByRole('combobox', { name: 'Organization', exact: true }).selectOption('org-a');
  await page.locator('.ai-toolbar').getByRole('button', { name: 'New investigation', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: /INC-1.*Synthetic incident/ })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`aligned-new-investigation-${theme}-${width}.png`), fullPage: true, animations: 'disabled' });

  await openWorkspace(page, '/ai/prompts', 'prompt-library');
  await expect(page.getByTestId('prompt-item')).toHaveCount(2);
  const editor = await monacoEditor(page);
  await expect(editor.locator('.view-lines')).toContainText('Investigation guidance');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: info.outputPath(`aligned-prompts-monaco-${theme}-${width}.png`), fullPage: true, animations: 'disabled' });
  await editor.getByRole('button', { name: 'Fullscreen', exact: true }).click();
  await expect(editor).toHaveClass(/fullscreen/);
  const bounds = await editor.boundingBox();
  expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.y).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1); expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(901);
  await editor.getByRole('combobox', { name: 'Editor theme', exact: true }).selectOption(theme === 'light' ? 'vs' : 'vs-dark');
  await editor.getByRole('button', { name: 'Exit fullscreen', exact: true }).focus();
  await page.keyboard.press('Escape');
  await expect(editor).not.toHaveClass(/fullscreen/);
  await assertNoHorizontalOverflow(page);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
 });
}

test('credential access review, failed apply and refreshed retry retain exact identity and draft', async ({ page, request }) => {
 await open(page);
 const before = (await (await request.get(`${fixture}/state`)).json()).credentials.find((c: any) => c.name === 'Synthetic credential 03');
 const { row, dialog } = await credentialDetails(page);
 await dialog.getByTestId('credential-edit-access').click();
 const selector = dialog.getByTestId('credential-permission-selector');
 await selector.getByRole('searchbox').fill('Request.Read');
 await selector.getByRole('checkbox', { name: 'Request.Read', exact: true }).check();
 await dialog.getByRole('button', { name: 'Review changes', exact: true }).click();
 await expect(dialog.getByTestId('credential-access-review')).toContainText('Add (1)');
 await expect(dialog.getByTestId('credential-access-review')).toContainText('Remove (0)');
 let state = await (await request.get(`${fixture}/state`)).json();
 expect(state.calls.filter((c: any) => c.path.endsWith('/access/apply'))).toHaveLength(0);
 expect(state.credentials.find((c: any) => c.id === before.id).permissions).toEqual(['Incident.Read']);
 await dialog.getByRole('checkbox', { name: 'I approve these exact permission changes.', exact: true }).check();
 await request.get(`${fixture}/fault?mode=credential-apply`);
 await dialog.getByRole('button', { name: 'Apply access changes', exact: true }).click();
 await expect(dialog.getByRole('alert')).toContainText('your draft is preserved');
 await expect(selector.getByRole('checkbox', { name: 'Request.Read', exact: true })).toBeChecked();
 await dialog.getByRole('button', { name: 'Refresh current access', exact: true }).click();
 await expect(dialog.getByRole('button', { name: 'Review changes', exact: true })).toBeEnabled();
 await dialog.getByRole('button', { name: 'Review changes', exact: true }).click();
 await dialog.getByRole('checkbox', { name: 'I approve these exact permission changes.', exact: true }).check();
 await dialog.getByRole('button', { name: 'Apply access changes', exact: true }).click();
 await expect(dialog.getByRole('status')).toContainText('Access updated');
 await expect(dialog.locator('.credential-access')).toContainText('2 permissions');
 await expect(dialog.locator('.credential-admission-card')).toContainText('2 of 2');
 await expect(dialog.locator('.credential-admission-card')).toContainText('does not create policy approval');
 await dialog.getByRole('button', { name: 'Close', exact: true }).click();
 await expect(row).toContainText('2 permissions');
 await page.getByRole('button', { name: 'Refresh', exact: true }).click();
 await expect(row).toContainText('2 permissions');
 state = await (await request.get(`${fixture}/state`)).json();
 const after = state.credentials.find((c: any) => c.id === before.id);
 expect(after).toEqual({ ...before, permissions: ['Incident.Read', 'Request.Read'] });
 expect(state.calls.filter((c: any) => c.path.endsWith('/access/apply'))).toHaveLength(2);
 expect(state.calls.filter((c: any) => c.path.endsWith('/access/review'))).toHaveLength(2);
 expect(state.calls.some((c: any) => c.path.includes('mcp-policies'))).toBe(false);
});

test('Monaco prompt editing preserves a rejected draft and saves the next revision', async ({ page, request }) => {
 await request.get(`${fixture}/seed-ai`);
 await openWorkspace(page, '/ai/prompts', 'prompt-library');
 await page.getByTestId('prompt-item').filter({ hasText: 'Mail delivery diagnostics' }).click();
 await page.getByTestId('prompt-edit').click();
 await page.getByTestId('prompt-name').fill('Mail delivery review');
 const guidance = '# Delivery review\n\nRead ticket evidence and propose the smallest next check.';
 await typeGuidance(page, guidance);
 await request.get(`${fixture}/fault?mode=prompt-save`);
 await page.getByTestId('prompt-save').click();
 await expect(page.getByTestId('prompt-library').getByRole('alert')).toContainText('Your draft is preserved');
 await expect(page.getByTestId('prompt-name')).toHaveValue('Mail delivery review');
 await expect(page.getByTestId('prompt-save')).toBeEnabled();
 await page.getByTestId('prompt-save').click();
 await expect(page.getByRole('heading', { name: 'Mail delivery review', exact: true })).toBeVisible();
 await expect(page.locator('.prompt-detail .prompt-status')).toHaveText('Revision 3');
 await expect(page.getByTestId('prompt-edit')).toBeVisible();
 await monacoEditor(page);
 const state = await (await request.get(`${fixture}/state`)).json();
 const writes = state.calls.filter((c: any) => c.method === 'PUT' && c.path.includes('/ai/ops/prompts/'));
 expect(writes).toHaveLength(2); expect(writes[0].payload).toEqual(writes[1].payload);
 expect(writes[1].payload).toMatchObject({ organizationId: 'org-a', name: 'Mail delivery review', content: guidance, expectedRevision: 2 });
 await page.getByRole('searchbox', { name: 'Search prompts', exact: true }).fill('delivery');
 await expect(page.getByTestId('prompt-item')).toHaveCount(1);
 await page.getByRole('searchbox', { name: 'Search prompts', exact: true }).fill('');
 await page.locator('.prompt-toolbar').getByRole('checkbox', { name: 'Archived', exact: true }).check();
 await expect(page.getByTestId('prompt-item')).toHaveCount(3);
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('AI prompts and multi-ticket investigation save preserve selected context without sending', async ({ page, request }) => {
 await openWorkspace(page, '/ai/prompts', 'prompt-library');
 await expect(page.getByRole('heading', { name: 'Prompts', exact: true })).toBeVisible();
 await page.getByTestId('prompt-organization').selectOption('org-a');
 await page.getByTestId('prompt-new').click();
 await page.getByTestId('prompt-name').fill('Synthetic triage guidance');
 await page.getByTestId('prompt-description').fill('Inspect the selected records');
 await typeGuidance(page, 'Read evidence before proposing action');
 await page.getByRole('checkbox', { name: 'Include in every new investigation for this organization', exact: true }).check();
 await page.getByTestId('prompt-save').click();
 await expect(page.getByTestId('prompt-item').filter({ hasText: 'Synthetic triage guidance' })).toContainText('Revision 1');
 await openWorkspace(page, '/ai/ops', 'ai-investigations');
 await page.getByRole('combobox', { name: 'Organization', exact: true }).selectOption('org-a');
 await page.locator('.ai-toolbar').getByRole('button', { name: 'New investigation', exact: true }).click();
 await expect(page.getByText('Always included:', { exact: false })).toContainText('Synthetic triage guidance');
 await page.getByLabel('Investigation title', { exact: true }).fill('Synthetic incident and task investigation');
 await page.getByRole('checkbox', { name: /INC-1.*Synthetic incident/ }).check();
 await page.getByRole('checkbox', { name: /TASK-1.*Synthetic task/ }).check();
 await request.get(`${fixture}/fault?mode=investigation-save`);
 await page.getByRole('button', { name: 'Save investigation context', exact: true }).click();
 await expect(page.getByTestId('ai-investigations').getByRole('alert')).toContainText('Your selections are preserved');
 await expect(page.getByRole('checkbox', { name: /TASK-1.*Synthetic task/ })).toBeChecked();
 await page.getByRole('button', { name: 'Save investigation context', exact: true }).click();
 await expect(page).toHaveURL(/ai\/ops\?id=/);
 await page.getByText('Saved guidance and ticket context', { exact: true }).click();
 await expect(page.locator('.ai-snapshot pre')).toContainText('Read evidence before proposing action');
 await expect(page.locator('.ai-snapshot pre')).toContainText('ticket-task');
 const state = await (await request.get(`${fixture}/state`)).json();
 const saves = state.calls.filter((call: any) => call.path === '/api/v1/ai/ops/investigations');
 expect(saves).toHaveLength(2); expect(saves[0].payload).toEqual(saves[1].payload);
 expect(saves[1].payload.ticketIds.sort()).toEqual(['ticket-incident', 'ticket-task']);
 expect(state.calls.filter((call: any) => call.path.endsWith('/messages'))).toEqual([]);
 await page.locator('.ai-toolbar').getByRole('button', { name: 'New investigation', exact: true }).click();
 await expect(page.getByLabel('Investigation title', { exact: true })).toHaveValue('');
 await expect(page.getByRole('button', { name: 'Save investigation context', exact: true })).toBeDisabled();
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('MCP policy preview keeps the reviewed fields locked until explicit confirmation', async ({ page, request }) => {
 await page.goto('/admin/operator-policies');
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
 await page.getByLabel('Separate operator credential', { exact: true }).click();
 await page.getByRole('option', { name: /Separate synthetic operator/ }).click();
 await page.getByLabel('Organization', { exact: true }).click();
 await page.getByRole('option', { name: 'Synthetic Alpha', exact: true }).click();
 await page.getByLabel('Permission ceiling', { exact: true }).click();
 await page.getByRole('option', { name: 'Incident.Read', exact: true }).click();
 await page.keyboard.press('Escape');
 await page.getByRole('button', { name: 'Preview exact policy', exact: true }).click();
 await expect(page.getByText('Narrow the separate operator credential')).toBeVisible();
 await expect(page.getByRole('combobox', { name: 'Organization', exact: true })).toBeDisabled();
 let state = await (await request.get(`${fixture}/state`)).json();
 expect(state.calls.filter((call: any) => call.path.endsWith('/confirm'))).toHaveLength(0);
 await page.getByRole('button', { name: 'Confirm this policy', exact: true }).click();
 await expect(page.getByText(/Separate synthetic operator.*revision 1/)).toBeVisible();
 state = await (await request.get(`${fixture}/state`)).json();
 expect(state.calls.find((call: any) => call.path.endsWith('/preview')).payload.permissions).toEqual(['Incident.Read']);
 expect(state.calls.find((call: any) => call.path.endsWith('/confirm')).payload.confirmation).toBe('synthetic-confirmation');
 await page.getByRole('button', { name: 'Preview exact policy', exact: true }).click();
 await expect(page.getByRole('button', { name: 'Confirm this policy', exact: true })).toBeVisible();
 const nextPreview = (await (await request.get(`${fixture}/state`)).json()).calls.filter((call: any) => call.path.endsWith('/preview')).at(-1);
 expect(nextPreview.payload.expectedRevision).toBe(1);
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

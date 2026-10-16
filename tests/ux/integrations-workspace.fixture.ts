import { expect, test, type Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
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
 await expect(page.getByRole('status')).toContainText('Connection approved');
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
 await expect(page.getByRole('status')).toContainText('Connection approved');
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
 await dialog.getByRole('checkbox', { name: 'I authorize these exact deployment settings and source generation.', exact: true }).check();
 await dialog.getByRole('button', { name: 'Approve host', exact: true }).click();
 await expect(page.getByRole('status')).toContainText('Connection approved');
 const calls = (await (await request.get(`${fixture}/state`)).json()).calls;
 expect(calls.find((c: any) => c.path.endsWith('/host-proposals/approve')).payload).toMatchObject({ proposalHash: 'a'.repeat(64), sourceHash: 'b'.repeat(64), consent: true });
});

test('manual credential organization selection ignores the native status filter and reviews only exact grants', async ({ page }) => {
 await open(page);
 await expect(page.getByRole('combobox', { name: 'Status', exact: true }).getByRole('option', { name: 'All (25)', exact: true })).toHaveCount(1);
 await page.getByTestId('integration-credential-create').click();
 await page.getByLabel('Name', { exact: true }).fill('Synthetic manual credential');
 await page.getByRole('button', { name: 'Choose access', exact: true }).click();
 const organization = page.getByRole('combobox', { name: 'Organization', exact: true });
 await organization.click();
 await page.getByRole('listbox').getByRole('option', { name: 'Synthetic Beta', exact: true }).click();
 await expect(organization).toHaveText('Synthetic Beta');
 const permissions = page.getByTestId('credential-permission-selector');
 await expect(permissions.getByRole('checkbox', { checked: true })).toHaveCount(0);
 await permissions.getByRole('checkbox', { name: 'Incident.User', exact: true }).check();
 await expect(permissions.getByRole('checkbox', { checked: true })).toHaveCount(1);
 await page.getByRole('button', { name: 'Review credential', exact: true }).click();
 await expect(page.getByText('Organization: Synthetic Beta', { exact: true })).toBeVisible();
 await expect(page.getByText('Permissions: Incident User', { exact: true })).toBeVisible();
 await expect(page.getByRole('button', { name: 'Create and reveal', exact: true })).toBeEnabled();
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
 const dialog = page.getByRole('dialog', { name: 'Credential details and lifecycle', exact: true });
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

import { expect, test, Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';

const fixture = 'http://127.0.0.1:18300/fixture';
const trigger = (page: Page) => page.getByRole('button', { name: /^Choose (assignee|implementor):/ }).first();
const search = (page: Page) => page.getByRole('combobox', { name: 'Search name or email' });
async function grid(page: Page, module = 'incidents') {
  await page.goto(new URL(`/${module}`, page.url()).toString(), { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('heading', { name: module, exact: false })).toBeVisible();
}
async function noError(page: Page) { await expect(page.locator('#blazor-error-ui')).not.toBeVisible(); }

test.beforeEach(async ({ page, request }) => {
  await request.get(`${fixture}/reset`);
  await page.goto('/auth/development', { waitUntil: 'domcontentloaded' });
});

test.afterEach(async ({ request }, info) => {
  if (info.status !== info.expectedStatus) {
    await info.attach('synthetic-picker-state', {
      body: await (await request.get(`${fixture}/state`)).body(), contentType: 'application/json'
    });
  }
});

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} visible trigger searches beyond initial results and persists after reload`, async ({ page, request }) => {
    await grid(page, module);
    expect((await (await request.get(`${fixture}/state`)).json()).searches).toHaveLength(0);
    await trigger(page).click();
    await expect(search(page)).toBeFocused();
    await expect(page.getByRole('option')).toHaveCount(20);
    await expect(page.getByText('More users available. Refine your search.')).toBeVisible();
    await expect(page.getByRole('option', { name: /Zulu/ })).toHaveCount(0);
    await search(page).fill(' SuRNaMe ');
    await page.getByRole('option', { name: /Zulu Surname late@example.test/ }).click();
    await expect(trigger(page)).toHaveAccessibleName(/Zulu Surname/);
    await expect(page).toHaveURL(new RegExp(`/${module}$`));
    await page.reload();
    await expect(trigger(page)).toHaveAccessibleName(/Zulu Surname/);
    await trigger(page).click();
    await search(page).fill('late@');
    await expect(page.getByRole('option', { name: /Zulu/ })).toHaveAttribute('aria-selected', 'true');
    await search(page).fill('no matches');
    await expect(page.getByText('No matching users.')).toBeVisible();
    await expect(page.getByText('Current: Zulu Surname')).toBeVisible();
    await search(page).fill('');
    await expect(page.getByRole('option')).toHaveCount(20);
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(1);
    if (module === 'changes') await expect(page.getByRole('button', { name: 'Unassigned', exact: true })).toHaveCount(0);
    else {
      await page.getByRole('button', { name: 'Unassigned', exact: true }).click();
      await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
      await page.reload();
      await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
      await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
    }
    await noError(page);
  });
}

test('keyboard activation, Tab and Escape do not assign; Enter selects a duplicate by ID', async ({ page, request }) => {
  await grid(page);
  await trigger(page).focus(); await page.keyboard.press('Enter');
  await expect(search(page)).toBeFocused();
  await expect(page.getByRole('option')).toHaveCount(20);
  await search(page).press('ArrowDown'); await search(page).press('Tab');
  await expect(page.getByRole('button', { name: 'Unassigned', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(search(page)).toHaveCount(0);
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await trigger(page).click();
  await search(page).fill('Duplicate');
  await expect(page.getByRole('option')).toHaveCount(2);
  await search(page).press('Escape');
  await expect(trigger(page)).toBeFocused();
  await trigger(page).press('Space'); await search(page).fill('Duplicate');
  await expect(page.getByRole('option')).toHaveCount(2);
  await search(page).press('ArrowDown'); await search(page).press('ArrowDown'); await search(page).press('Enter');
  await expect(trigger(page)).toHaveAccessibleName(/Duplicate Name/);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.tickets.incidents.assignedToId).toBe('duplicate-2');
  await noError(page);
});

test('lookup retry, save failure and superseded query retain authoritative state', async ({ page, request }) => {
  await grid(page); await request.get(`${fixture}/fault?mode=lookup`);
  await trigger(page).click();
  await expect(page.getByText('Unable to load assignees.')).toBeVisible();
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('option')).toHaveCount(20);
  await search(page).fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).searches.some(x => x.q === 'slow')).toBe(true);
  await search(page).fill('late@');
  await expect(page.getByRole('option', { name: /Zulu/ })).toBeVisible();
  await request.get(`${fixture}/fault?mode=save`);
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(page.getByText(/Assignment failed/)).toBeVisible();
  await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
  await trigger(page).click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(trigger(page)).toHaveAccessibleName(/Zulu/);
  await noError(page);
});

test('empty eligible population has its own state', async ({ page, request }) => {
  await grid(page); await request.get(`${fixture}/fault?mode=empty`);
  await trigger(page).click(); await expect(page.getByText('No eligible users.')).toBeVisible();
  await search(page).press('Escape');
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await noError(page);
});

test('delayed lookup and save stay attached to their originating context', async ({ page, request }) => {
  await grid(page); await trigger(page).click(); await search(page).fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).searches.some(x => x.q === 'slow')).toBe(true);
  await grid(page, 'requests'); await trigger(page).click();
  await expect(page.getByRole('option')).toHaveCount(20);
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).completedQueries.includes('slow')).toBe(true);
  await expect(page.getByRole('option')).toHaveCount(20);
  await search(page).fill('late@'); await request.get(`${fixture}/fault?mode=slow-save`);
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(page.getByText('Saving assignment…')).toBeVisible();
  await grid(page, 'incidents');
  await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).saves.length).toBe(1);
  await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.tickets.requests.assignedToId).toBe('late');
  expect(state.tickets.incidents.assignedToId).toBeNull();
  await noError(page);
});

test('read-only grids expose assignment without candidate lookup', async ({ page, request }) => {
  await page.goto('https://127.0.0.1:5160/auth/development');
  for (const module of ['incidents', 'requests', 'changes']) {
    await grid(page, module);
    await expect(trigger(page)).toHaveCount(0);
    await expect(page.locator('.assignee-current')).toBeVisible();
  }
  expect((await (await request.get(`${fixture}/state`)).json()).searches).toHaveLength(0);
});

for (const module of ['incidents', 'requests']) {
  test(`${module} create dialog selection stays local and organization changes reset it`, async ({ page, request }) => {
    await grid(page, module); await page.getByRole('button', { name: 'Create', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    await dialog.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Customer organization');
    await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
    const chooser = dialog.getByRole('button', { name: /Choose technician assignment/ });
    await chooser.click(); await search(page).fill('late@');
    await page.getByRole('option', { name: /Zulu/ }).click();
    await expect(chooser).toHaveAccessibleName(/Zulu/);
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
    await dialog.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Second organization');
    await page.getByRole('option', { name: 'Second organization', exact: true }).click();
    await expect(chooser).toHaveAccessibleName(/Unassigned/);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
    await noError(page);
  });
}

test('incident detail selection follows Save and Cancel', async ({ page, request }) => {
  await page.goto('/incidents/incidents-1', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await trigger(page).click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(trigger(page)).toHaveAccessibleName(/Zulu/);
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
  await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
  await trigger(page).click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  await page.getByRole('button', { name: /Save/, exact: false }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).saves.length).toBe(1);
  await page.reload(); await expect(trigger(page)).toHaveAccessibleName(/Zulu/);
  await noError(page);
});

test('required change implementor create field does not save on selection or Cancel', async ({ page, request }) => {
  await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Customer organization');
  await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
  await dialog.getByRole('button', { name: /^3\. Schedule/ }).click();
  const chooser = dialog.getByRole('button', { name: /^Choose implementor:/ });
  await chooser.click(); await search(page).fill('late@');
  await expect(page.getByRole('button', { name: 'Unassigned', exact: true })).toHaveCount(0);
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(chooser).toHaveAccessibleName(/Zulu/);
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await noError(page);
});

test('change detail uses the shared implementor selector with deferred saving', async ({ page, request }) => {
  await page.goto('/changes/changes-1', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await trigger(page).click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  await expect(trigger(page)).toHaveAccessibleName(/Zulu/);
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
  await expect(trigger(page)).toHaveAccessibleName(/Unassigned/);
  await noError(page);
});

test('bulk selection requires explicit Assign after choosing', async ({ page, request }) => {
  await grid(page);
  await page.getByRole('row').filter({ hasText: 'UX-146' }).getByRole('checkbox').check();
  const chooser = page.getByRole('button', { name: /^Choose bulk assignee:/ });
  await chooser.click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await page.getByRole('button', { name: 'Assign', exact: true }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).saves.length).toBe(1);
  await expect(trigger(page)).toHaveAccessibleName(/Zulu/);
  await noError(page);
});

for (const theme of ['light', 'dark']) {
  test(`${theme} narrow picker fits viewport and table overflow`, async ({ page }, testInfo) => {
    await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    await page.setViewportSize({ width: 390, height: 844 }); await grid(page);
    await trigger(page).click(); await expect(page.getByRole('option')).toHaveCount(20);
    await expect(search(page)).toBeFocused();
    await expect.poll(() => page.getByRole('dialog', { name: 'Choose assignee', exact: true })
      .evaluate(element => getComputedStyle(element.closest('.mud-popover')!).opacity)).toBe('1');
    const box = await page.getByRole('dialog', { name: 'Choose assignee', exact: true }).boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(390);
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`${theme}-mobile-picker.png`), fullPage: true });
    await noError(page);
  });
}

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} creation organization search covers authorized choices beyond the first page`, async ({ page, request }) => {
    await grid(page, module); await page.getByRole('button', { name: 'Create', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
    await organization.click();
    await expect(page.getByRole('option')).toHaveCount(20);
    await expect(page.getByText('More organizations are available. Refine your search.')).toBeVisible();
    await organization.fill('pArTnEr');
    await expect(page.getByRole('option')).toHaveCount(1);
    await organization.press('ArrowDown'); await organization.press('Enter');
    await expect(organization).toHaveValue('Zulu Partner with a long organization label for regional operations');
    await expect(organization).toBeEnabled();
    await organization.click(); await organization.press('Escape');
    await expect(organization).toHaveValue(/Zulu Partner/);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
    await noError(page);
  });
}

test('change participant searches keep duplicate-label approver chips and reset scope without losing draft text', async ({ page, request }) => {
  await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByRole('textbox', { name: /^Title\*?$/ }).fill('Retain this draft');
  const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
  await organization.fill('Customer organization');
  await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
  const requester = dialog.getByRole('combobox', { name: /^Requested for\*?$/ });
  await requester.fill('remote-participant@');
  await page.getByRole('option', { name: /Zulu Participant/ }).click();
  await expect(requester).toHaveValue(/remote-participant@example.test/);
  await dialog.getByRole('button', { name: /^3\. Schedule/ }).click();
  const approvers = dialog.getByRole('combobox', { name: 'Approvers', exact: true });
  await approvers.fill('Duplicate'); await page.getByRole('option', { name: /one@example.test/ }).click();
  await expect(approvers).toBeEditable();
  await expect(approvers).toBeFocused();
  await expect(approvers).toHaveValue('');
  await page.keyboard.type('Duplicate');
  await expect(page.getByRole('option', { name: /two@example.test/ })).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('option', { name: /two@example.test/ })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  const selected = dialog.getByLabel('Selected approvers', { exact: true });
  await expect(selected).toContainText('one@example.test'); await expect(selected).toContainText('two@example.test');
  await approvers.fill('no match');
  await expect(page.getByText('No matching participants.')).toBeVisible();
  await expect(selected).toContainText('one@example.test');
  await approvers.press('Escape');
  await dialog.getByRole('button', { name: /^1\. Overview/ }).click();
  await organization.fill('Second organization'); await page.getByRole('option', { name: 'Second organization', exact: true }).click();
  await expect(requester).toHaveValue('');
  await dialog.getByRole('button', { name: /^3\. Schedule/ }).click();
  await expect(selected).toBeEmpty();
  await dialog.getByRole('button', { name: /^1\. Overview/ }).click();
  await expect(dialog.getByRole('textbox', { name: /^Title\*?$/ })).toHaveValue('Retain this draft');
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click(); await noError(page);
});

test('customer lookup searches beyond initial results and explicit Create persists the chosen identifiers', async ({ page, request }) => {
  await grid(page, 'requests'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByRole('textbox', { name: /^Title\*?$/ }).fill('Saved searchable creation');
  await dialog.getByPlaceholder('Enter the ticket description...').fill('Synthetic request description.');
  await dialog.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Partner');
  await page.getByRole('option', { name: /Zulu Partner/ }).click();
  const customer = dialog.getByRole('combobox', { name: /^User the ticket is for\*?$/ });
  await customer.fill('remote-customer@'); await page.getByRole('option', { name: /Zulu Customer/ }).click();
  await expect(customer).toHaveValue(/remote-customer@example.test/);
  await customer.click();
  await expect(page.getByRole('option')).toHaveCount(20);
  await customer.press('Escape');
  await expect(customer).toHaveValue(/remote-customer@example.test/);
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.saves).toHaveLength(1);
  expect(state.saves[0]).toMatchObject({ kind: 'create', organizationId: 'late-org', customerId: 'late-customer', title: 'Saved searchable creation' });
  await page.reload(); await expect(page.getByRole('row').filter({ hasText: 'Saved searchable creation' })).toBeVisible();
  await noError(page);
});

test('creation lookup failure can retry and superseded remote searches cannot replace current choices', async ({ page, request }) => {
  // Allow the bounded terminal-failure wait plus the explicit recovery and stale-query journey.
  test.setTimeout(60_000);
  await grid(page, 'requests'); await request.get(`${fixture}/fault?mode=creation-lookup`);
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  // The real HTTP client exhausts its configured retry budget before presenting a terminal failure.
  await expect(dialog.getByText('Could not load organizations.', { exact: true })).toBeVisible({ timeout: 30_000 });
  await request.get(`${fixture}/fault?mode=`);
  await dialog.getByRole('button', { name: 'Retry organization', exact: true }).click();
  const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
  await organization.fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).creationSearches.some(x => x.q === 'slow')).toBe(true);
  await organization.fill('Partner'); await expect(page.getByRole('option', { name: /Zulu Partner/ })).toBeVisible();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).completedCreationQueries.includes('slow')).toBe(true);
  await expect(page.getByRole('option')).toHaveCount(1);
  await page.getByRole('option', { name: /Zulu Partner/ }).click();
  await expect(organization).toHaveValue(/Zulu Partner/);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click(); await noError(page);
});

for (const theme of ['light', 'dark']) {
  test(`${theme} narrow creation combobox and long labels fit the viewport`, async ({ page }, testInfo) => {
    await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    await page.setViewportSize({ width: 390, height: 844 }); await grid(page, 'requests');
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
    await organization.fill('Partner');
    await expect(page.getByRole('option', { name: /Zulu Partner/ })).toBeVisible();
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`${theme}-creation-search-mobile.png`), fullPage: true });
    await page.getByRole('option', { name: /Zulu Partner/ }).click();
    await expect(organization).toHaveValue(/Zulu Partner/);
    await organization.press('Tab'); await noError(page);
    // Close the directory layer explicitly before addressing the dialog footer.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('option')).toHaveCount(0);
    // The native zoom suite owns browser zoom; this checks the equally narrow CSS viewport.
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  });
}

test('requester creation is explicit and organization locked, then selects the returned stable ID', async ({ page, request }) => {
  await grid(page, 'requests'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const ticket = page.getByRole('dialog').first();
  await ticket.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Customer organization');
  await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
  const customer = ticket.getByRole('combobox', { name: /^User the ticket is for\*?$/ });
  await customer.fill('new-person@example.test');
  await expect(page.getByText('No matching customers.')).toBeVisible();
  expect((await (await request.get(`${fixture}/state`)).json()).customerCreates).toHaveLength(0);
  await customer.press('Escape');
  await ticket.getByRole('button', { name: 'Create user', exact: true }).click();
  const createUser = page.getByRole('dialog').filter({ has: page.getByRole('textbox', { name: /^Email\*?$/ }) });
  await expect(createUser.getByRole('combobox', { name: /^Organization\*?$/ })).toBeDisabled();
  await createUser.getByRole('textbox', { name: /^Name\*?$/ }).fill('New synthetic person');
  await createUser.getByRole('textbox', { name: /^Email\*?$/ }).fill('new-person@example.test');
  await createUser.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(createUser).not.toBeVisible();
  await expect(customer).toHaveValue('New synthetic person (new-person@example.test)');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.customerCreates).toHaveLength(1); expect(state.customerCreates[0].organizationId).toBe('org-1');
  expect(state.saves).toHaveLength(0);
  await ticket.getByRole('button', { name: 'Cancel', exact: true }).click(); await noError(page);
});

test('organization switch cancels a delayed customer search and resets its text and candidate identities', async ({ page, request }) => {
  await grid(page, 'requests'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
  await organization.fill('Customer organization'); await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
  const customer = dialog.getByRole('combobox', { name: /^User the ticket is for\*?$/ });
  await customer.fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).creationSearches.some(x => x.q === 'slow' && x.organizationId === 'org-1')).toBe(true);
  await organization.fill('Second organization'); await page.getByRole('option', { name: 'Second organization', exact: true }).click();
  await expect(customer).toHaveValue('');
  await customer.click();
  await expect(page.getByRole('option')).toHaveCount(20);
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).completedCreationQueries.includes('slow')).toBe(true);
  await expect(page.getByRole('option', { name: /^Alpha customer/ })).toHaveCount(0);
  await customer.fill('remote-customer@');
  await page.getByRole('option', { name: /Second Zulu Customer/ }).click();
  await expect(customer).toHaveValue(/second-remote-customer@example.test/);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click(); await noError(page);
});

test.describe('touch creation selections', () => {
  test.use({ hasTouch: true, viewport: { width: 390, height: 844 } });
  for (const module of ['incidents', 'requests', 'changes']) {
    test(`${module} organization and participant choices support touch without saving`, async ({ page, request }) => {
      await grid(page, module); await page.getByRole('button', { name: 'Create', exact: true }).tap();
      const dialog = page.getByRole('dialog').first();
      const organization = dialog.getByRole('combobox', { name: /^Organization\*?$/ });
      await organization.tap(); await organization.fill('Partner');
      await page.getByRole('option', { name: /Zulu Partner/ }).tap();
      const participant = dialog.getByRole('combobox', { name: module === 'changes' ? /^Requested for\*?$/ : /^User the ticket is for\*?$/ });
      await participant.tap(); await participant.fill('remote-');
      await page.getByRole('option', { name: /Zulu (Participant|Customer)/ }).tap();
      await expect(participant).toHaveValue(/remote-/);
      expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).tap(); await noError(page);
    });
  }
});

for (const type of ['Standard', 'Normal', 'Emergency']) test(`guided ${type} change draft preserves hidden data, saves once and reopens without submitting`, async ({ page, request }) => {
  await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByRole('textbox', { name: /^Title\*?$/ }).fill(`Synthetic ${type} draft`);
  await dialog.getByRole('textbox', { name: /^Description\*?$/ }).fill('Synthetic incomplete plan retained for later review');
  await dialog.getByRole('combobox', { name: /^Organization\*?$/ }).fill('Customer organization');
  await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
  await dialog.getByRole('combobox', { name: /^Requested for\*?$/ }).fill('remote-participant@');
  await page.getByRole('option', { name: /Zulu Participant/ }).click();
  await dialog.getByRole('combobox', { name: 'Change type', exact: true }).click();
  await page.getByRole('option', { name: type, exact: true }).click();
  await dialog.getByRole('button', { name: /^2\. Plan/ }).click();
  await dialog.getByRole('textbox', { name: 'Scope of Change', exact: true }).fill('Retained across all steps');
  await dialog.getByRole('textbox', { name: 'Rollback Reference', exact: true }).fill('Synthetic-runbook');
  await dialog.getByRole('button', { name: /^3\. Schedule/ }).click();
  const implementor = dialog.getByRole('button', { name: /^Choose implementor:/ });
  await implementor.click(); await search(page).fill('late@');
  await page.getByRole('option', { name: /Zulu/ }).click();
  await dialog.getByRole('textbox', { name: 'Start date (UTC)', exact: true }).fill('2030-11-01');
  await dialog.getByRole('textbox', { name: 'Start time (UTC)', exact: true }).fill('10:00');
  await dialog.getByRole('textbox', { name: 'End date (UTC)', exact: true }).fill('2030-11-01');
  await dialog.getByRole('textbox', { name: 'End time (UTC)', exact: true }).fill('11:00');
  await dialog.getByRole('button', { name: /^4\. Review/ }).click();
  await expect(dialog).toContainText('Incomplete plan · optional for saving this draft');
  await dialog.getByRole('button', { name: /^2\. Plan/ }).click();
  await expect(dialog.getByRole('textbox', { name: 'Scope of Change', exact: true })).toHaveValue('Retained across all steps');
  await dialog.getByRole('button', { name: /^4\. Review/ }).click();
  await request.get(`${fixture}/fault?mode=slow-save`);
  await dialog.getByRole('button', { name: 'Create draft', exact: true }).evaluate((element: HTMLElement) => { element.click(); element.click(); });
  await expect(dialog).toBeHidden();
  const state = await (await request.get(`${fixture}/state`)).json();
  const saves = state.saves.filter((x: any) => x.kind === 'create'); expect(saves).toHaveLength(1);
  expect(saves[0]).toMatchObject({ saveAsDraft: true, changeType: type, requestedForUserId: 'late-participant', implementorUserId: 'late',
    implementationStartAt: '2030-11-01T10:00:00Z', implementationEndAt: '2030-11-01T11:00:00Z', changeTemplate: { scopeOfChange: 'Retained across all steps', rollbackReference: 'Synthetic-runbook' } });
  await page.goto('/changes/changes-1'); await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('button', { name: 'Edit plan', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit plan', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Scope of Change', exact: true })).toHaveValue('Retained across all steps');
  await noError(page);
});

test('guided change validates hidden steps, focuses errors and confirms discard without mutations', async ({ page, request }) => {
  await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByRole('button', { name: /^4\. Review/ }).click();
  await dialog.getByRole('button', { name: 'Create draft', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: /^Title\*?$/ })).toBeFocused();
  await dialog.getByRole('textbox', { name: /^Title\*?$/ }).fill('Unsaved draft');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  const discard = page.getByRole('dialog').filter({ has: page.getByText('Discard change draft?', { exact: true }) });
  await discard.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: /^Title\*?$/ })).toHaveValue('Unsaved draft');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Discard draft', exact: true }).click();
  await expect(dialog).toBeHidden(); expect((await (await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
});

for (const theme of ['light', 'dark']) for (const [width, height] of [[1440, 900], [1366, 768], [390, 844]]) {
  test(`guided change layout ${theme} ${width}x${height}`, async ({ page, request }, info) => {
    await page.setViewportSize({ width, height }); await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
    await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    for (const [index, name] of ['Overview', 'Plan', 'Schedule', 'Review'].entries()) {
      await dialog.getByRole('button', { name: new RegExp(`^${index + 1}\\. ${name}`) }).click();
      await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport();
      const rect = await dialog.boundingBox(); expect(rect!.y + rect!.height).toBeLessThanOrEqual(height + 1);
      await assertNoHorizontalOverflow(page);
      await page.screenshot({ path: info.outputPath(`change-${theme}-${width}-step-${index}.png`) });
    }
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
  });
}

test('change type switches retain distinct hidden plan fields in the same draft', async ({ page, request }) => {
  await grid(page, 'changes'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog').first();
  const chooseType = async (type: string) => {
    await dialog.getByRole('button', { name: /^1\. Overview/ }).click();
    await dialog.getByRole('combobox', { name: 'Change type', exact: true }).click();
    await page.getByRole('option', { name: type, exact: true }).click();
    await dialog.getByRole('button', { name: /^2\. Plan/ }).click();
  };
  await chooseType('Normal');
  await dialog.getByRole('textbox', { name: 'Business Justification', exact: true }).fill('Retain the normal business case');
  await chooseType('Emergency');
  await dialog.getByRole('textbox', { name: 'Reason for Emergency', exact: true }).fill('Retain the emergency justification');
  await chooseType('Normal');
  await expect(dialog.getByRole('textbox', { name: 'Business Justification', exact: true })).toHaveValue('Retain the normal business case');
  await chooseType('Emergency');
  await expect(dialog.getByRole('textbox', { name: 'Reason for Emergency', exact: true })).toHaveValue('Retain the emergency justification');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Discard draft', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
});

import { expect, test, Page } from '@playwright/test';

const fixture = 'http://127.0.0.1:18620/fixture';
const categorySearch = (page: Page) => page.getByRole('combobox', { name: 'Add service category', exact: true });
const selected = (page: Page) => page.locator('.selected-categories');
const save = (page: Page) => page.getByRole('button', { name: 'Save Changes', exact: true });
const revert = (page: Page) => page.getByRole('button', { name: 'Revert', exact: true });

async function ready(page: Page) {
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 30_000 });
  await expect(selected(page)).toContainText('Historical selection');
}

async function open(page: Page, module: string, role: 'contributor' | 'manager') {
  const origin = `https://127.0.0.1:${role === 'contributor' ? 5194 : 5195}`;
  await page.goto(`${origin}/auth/development`);
  await page.goto(`${origin}/${module}/${module}-1`);
  await ready(page);
}

async function managementUnavailable(page: Page) {
  await expect(page.getByRole('combobox', { name: 'Priority', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Choose (assignee|implementor):/i })).toHaveCount(0);
  await expect(categorySearch(page)).toBeDisabled();
  await expect(selected(page).getByRole('button', { name: /^Remove category/ })).toHaveCount(0);
  await expect(save(page)).toBeDisabled();
  await expect(revert(page)).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Pause SLA', exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'AI Assistant', exact: true })).toHaveCount(0);
}

test.beforeEach(async ({ request }) => {
  await request.get(`${fixture}/reset`);
  await request.post(`${fixture}/ticket-scope`, { data: { organizationId: 'org-1', customerId: 'customer-owner' } });
});

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} contributor can note own ticket without management rights and loses contribution outside ownership or scope`, async ({ page, request }, testInfo) => {
    await open(page, module, 'contributor');
    await managementUnavailable(page);
    await page.getByRole('tab', { name: /Timeline/ }).click();
    await expect(page.getByRole('button', { name: 'Add Note', exact: true })).toBeEnabled();
    await page.screenshot({ path: testInfo.outputPath(`${module}-contributor-owned.png`), fullPage: true });
    await page.getByRole('button', { name: 'Add Note', exact: true }).click();
    const note = `Synthetic ${module} owner contribution`;
    await page.getByPlaceholder('Enter your note details here...').fill(note);
    await page.getByPlaceholder('Enter your note details here...').press('Tab');
    await page.getByRole('button', { name: 'Submit Note', exact: true }).click();
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).worklogs).toEqual([expect.objectContaining({ module, notes: note })]);
    await page.getByRole('tab', { name: /Attachments/ }).click();
    await expect(page.getByRole('button', { name: 'Upload Files', exact: true })).toBeEnabled();
    await managementUnavailable(page);

    // Read remains granted, while another customer or organization cannot borrow
    // the contributor's ownership permission from the previously opened ticket.
    for (const scope of [{ organizationId: 'org-1', customerId: 'customer-other' }, { organizationId: 'org-2', customerId: 'customer-owner' }]) {
      await request.post(`${fixture}/ticket-scope`, { data: scope });
      await page.reload(); await ready(page);
      await managementUnavailable(page);
      await page.getByRole('tab', { name: /Timeline/ }).click();
      await expect(page.getByRole('button', { name: 'Add Note', exact: true })).toBeDisabled();
      await page.getByRole('tab', { name: /Attachments/ }).click();
      await expect(page.getByRole('button', { name: 'Upload Files', exact: true })).toHaveCount(0);
    }
    const state = await (await request.get(`${fixture}/state`)).json();
    expect(state.saves).toEqual([]); expect(state.slaActions).toEqual([]);
    expect(state.worklogs).toHaveLength(1);
    expect(state.tickets[module].categoryIds).toHaveLength(2);
  });

  test(`${module} manager saves and reverts another customer's categories only inside management scope`, async ({ page, request }, testInfo) => {
    await request.post(`${fixture}/ticket-scope`, { data: { organizationId: 'org-1', customerId: 'customer-other' } });
    await open(page, module, 'manager');
    await expect(categorySearch(page)).toBeEnabled();
    await expect(page.getByRole('combobox', { name: 'Priority', exact: true })).toBeEnabled();
    const assignment = page.getByRole('button', { name: /^Choose (assignee|implementor):/i });
    if (module === 'requests') await expect(assignment).toHaveCount(0);
    else await expect(assignment).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Pause SLA', exact: true })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'AI Assistant', exact: true })).toBeVisible();
    await categorySearch(page).fill('A manager cannot create this catalogue entry');
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).categorySearches).toContain('A manager cannot create this catalogue entry');
    await expect(page.getByRole('button', { name: /^Add “/ })).toHaveCount(0);
    await page.keyboard.press('Escape');

    const remove = selected(page).getByRole('button', { name: 'Remove category Network Operations', exact: true });
    await remove.click(); await expect(save(page)).toBeEnabled();
    await page.screenshot({ path: testInfo.outputPath(`${module}-manager-draft.png`), fullPage: true });
    await revert(page).click(); await expect(remove).toBeVisible();
    await expect(save(page)).toBeDisabled();
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
    await remove.click(); await save(page).click();
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).saves.length).toBe(1);
    await expect(save(page)).toBeDisabled();
    await page.reload(); await ready(page);
    await expect(selected(page)).not.toContainText('Network Operations');
    expect((await (await request.get(`${fixture}/state`)).json()).tickets[module].categoryIds).toEqual(['79000000-0000-0000-0000-000000000002']);

    await request.post(`${fixture}/ticket-scope`, { data: { organizationId: 'org-2', customerId: 'customer-other' } });
    await page.reload(); await ready(page);
    await managementUnavailable(page);
    await page.getByRole('tab', { name: /Timeline/ }).click();
    await expect(page.getByRole('button', { name: 'Add Note', exact: true })).toBeDisabled();
    await page.screenshot({ path: testInfo.outputPath(`${module}-manager-outside-scope.png`), fullPage: true });
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(1);
  });
}

test('change manager retains lifecycle assignment lock after submission', async ({ page, request }) => {
  await request.get(`${fixture}/fault?mode=locked`);
  await open(page, 'changes', 'manager');
  await expect(categorySearch(page)).toBeDisabled();
  await expect(page.getByRole('combobox', { name: 'Priority', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Choose implementor:/i })).toHaveCount(0);
  await expect(selected(page).getByRole('button', { name: /^Remove category/ })).toHaveCount(0);
  await expect(save(page)).toBeDisabled(); await expect(revert(page)).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Pause SLA', exact: true })).toBeEnabled();
  await page.getByRole('tab', { name: /Timeline/ }).click();
  await expect(page.getByRole('button', { name: 'Add Note', exact: true })).toBeEnabled();
  expect((await (await request.get(`${fixture}/state`)).json()).saves).toEqual([]);
});

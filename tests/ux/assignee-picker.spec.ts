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
    await dialog.getByRole('combobox', { name: 'Organization', exact: true }).click();
    await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
    const chooser = dialog.getByRole('button', { name: /Choose technician assignment/ });
    await chooser.click(); await search(page).fill('late@');
    await page.getByRole('option', { name: /Zulu/ }).click();
    await expect(chooser).toHaveAccessibleName(/Zulu/);
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
    await dialog.getByRole('combobox', { name: 'Organization', exact: true }).click();
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
  await dialog.getByRole('combobox', { name: 'Organization', exact: true }).click();
  await page.getByRole('option', { name: 'Customer organization', exact: true }).click();
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

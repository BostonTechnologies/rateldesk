import { expect, test, Page } from '@playwright/test';

const fixture = 'http://127.0.0.1:18600/fixture';
const selected = (page: Page) => page.locator('.selected-categories');
const categorySearch = (page: Page) => page.getByRole('combobox', { name: 'Add service category' });
async function open(page: Page, module = 'incidents', origin = 'https://127.0.0.1:5190') {
  await page.goto(`${origin}/auth/development`);
  await page.goto(`${origin}/${module}/${module}-1`);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('region', { name: 'Ticket details' })).toBeVisible();
  await expect(selected(page)).toContainText('Network Operations');
  await expect(selected(page)).toContainText('Historical selection (inactive)');
}
async function addLiteral(page: Page) {
  await categorySearch(page).fill('R&D + 100%_\\');
  await page.getByRole('option', { name: /R&D/ }).click();
  await expect(selected(page)).toContainText('R&D');
}
async function noOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}

test.beforeEach(async ({ request }) => { await request.get(`${fixture}/reset`); });

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} saved multiple IDs survive add remove save reload and revert`, async ({ page, request }) => {
    await open(page, module);
    await addLiteral(page);
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
    await selected(page).locator('.mud-chip').filter({ hasText: 'Network Operations' }).locator('.mud-chip-close-button').click();
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeDisabled();
    let state = await (await request.get(`${fixture}/state`)).json();
    expect(state.tickets[module].categoryIds).toEqual(expect.arrayContaining(['79000000-0000-0000-0000-000000000002', '79000000-0000-0000-0000-000000000003']));
    expect(state.tickets[module].categoryIds).toHaveLength(2);
    expect(state.categorySearches).toContain('R&D + 100%_\\');
    await page.reload();
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    await expect(selected(page)).toContainText('R&D');
    await selected(page).locator('.mud-chip').filter({ hasText: 'Historical selection' }).locator('.mud-chip-close-button').click();
    await page.getByRole('button', { name: 'Revert', exact: true }).click();
    await expect(selected(page)).toContainText('Historical selection');
    await expect(selected(page)).toContainText('R&D');
    await noOverflow(page);
  });
}

test('explicit catalogue creation is confirmed and remains a draft assignment', async ({ page, request }) => {
  await open(page);
  await categorySearch(page).fill('New service name');
  await page.getByRole('button', { name: 'Add “New service name”', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('applied when you save the ticket');
  await page.getByRole('button', { name: 'Create category', exact: true }).click();
  await expect(selected(page)).toContainText('New service name');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.categories.some(x => x.name === 'New service name')).toBe(true);
  expect(state.saves).toHaveLength(0);
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
  await expect(selected(page)).not.toContainText('New service name');
});

test('save failure retains edits and dirty navigation can be cancelled', async ({ page, request }) => {
  await open(page);
  await addLiteral(page);
  await request.get(`${fixture}/fault?mode=save`);
  await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
  await expect(page.getByText(/Error saving incident/)).toBeVisible();
  await expect(selected(page)).toContainText('R&D');
  await page.getByRole('link', { name: 'Home', exact: true }).click();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page).toHaveURL(/incidents\/incidents-1/);
  await expect(selected(page)).toContainText('R&D');
  await page.getByRole('button', { name: 'Revert', exact: true }).click();
});

test('semantic selection ignores legacy indices and retains a visited panel while switching', async ({ page }) => {
  await page.goto('/auth/development');
  await page.evaluate(() => localStorage.setItem('ticket-tabs-index:incidents:incidents-1', '5'));
  await open(page);
  await expect(page.getByRole('tab', { name: 'Messages', exact: true })).toHaveAttribute('aria-selected', 'true');
  const attachments = page.getByRole('tab', { name: /Attachments/ });
  await expect(attachments).toContainText('999+');
  await expect(attachments.locator('.activity-count')).toHaveAttribute('aria-label', '1200 items');
  await attachments.click();
  await expect(attachments).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Messages', exact: true }).click();
  expect(await page.locator('[role=tabpanel][hidden]').count()).toBe(1);
  await page.getByRole('tab', { name: /Listeners/ }).click();
  expect(await page.evaluate(() => localStorage.getItem('ticket-tabs-selection:v2:incidents:incidents-1'))).toBe('listeners');
  await page.reload();
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('tab', { name: /Listeners/ })).toHaveAttribute('aria-selected', 'true');
});

test('unavailable count does not become zero and tab keyboard operation reaches relations', async ({ page, request }) => {
  await request.get(`${fixture}/fault?mode=counts`);
  await open(page);
  await expect(page.getByRole('tab', { name: /Listeners/ }).locator('.activity-count')).toHaveAttribute('aria-label', 'Count unavailable');
  const messages = page.getByRole('tab', { name: 'Messages', exact: true });
  await messages.focus(); await page.keyboard.press('End');
  await expect(page.getByRole('tab', { name: /Relations/ })).toBeFocused();
  await expect(page.getByRole('tab', { name: /Relations/ })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Home');
  await expect(messages).toBeFocused();
});

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} reader sees hydrated categories and no mutation or AI controls`, async ({ page }) => {
    await open(page, module, 'https://127.0.0.1:5191');
    await expect(categorySearch(page)).toBeDisabled();
    await expect(selected(page).locator('.mud-chip-close-button')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeDisabled();
    await expect(page.getByRole('tab', { name: 'AI Assistant' })).toHaveCount(0);
    await noOverflow(page);
  });
}

for (const width of [320, 768, 1920]) {
  test(`detail controls and inline count bounds at ${width}px in dark theme and zoom`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.addInitScript(() => localStorage.setItem('helpdesk.theme.preference', 'dark'));
    await open(page);
    await noOverflow(page);
    for (const pill of await page.locator('.activity-count').all()) {
      const bounds = await pill.evaluate(el => { const p = el.getBoundingClientRect(); const t = el.closest('[role=tab]')!.getBoundingClientRect(); return { left: p.left >= t.left, right: p.right <= t.right, top: p.top >= t.top, bottom: p.bottom <= t.bottom }; });
      expect(Object.values(bounds).every(Boolean)).toBe(true);
    }
    await page.screenshot({ path: testInfo.outputPath(`details-${width}-dark.png`), fullPage: true });
    // Keep the effective CSS viewport at least 320px while exercising 200% reflow.
    await page.setViewportSize({ width: Math.max(width, 640), height: 1000 });
    await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
    await noOverflow(page);
    await page.getByRole('tab', { name: /Relations/ }).scrollIntoViewIfNeeded();
    await page.getByRole('tab', { name: /Relations/ }).click();
    await expect(page.getByRole('tab', { name: /Relations/ })).toHaveAttribute('aria-selected', 'true');
  });
}

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} authorized SLA pause and resume remain available`, async ({ page }) => {
    await open(page, module);
    await addLiteral(page);
    await page.getByRole('button', { name: 'Pause SLA', exact: true }).click();
    await page.getByRole('textbox', { name: /^Reason/ }).fill('Synthetic pause reason');
    await page.getByRole('button', { name: 'Pause', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Resume SLA', exact: true })).toBeVisible();
    await expect(page.getByText('Pause reason: Synthetic pause reason')).toBeVisible();
    await page.getByRole('button', { name: 'Resume SLA', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Pause SLA', exact: true })).toBeVisible();
    await expect(selected(page)).toContainText('R&D');
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeEnabled();
  });
}

for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} changing ticket during an outstanding search hydrates the new selection`, async ({ page, request }) => {
    await open(page, module);
    await categorySearch(page).fill('slow');
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).categorySearches.includes('slow')).toBe(true);
    // Use a real intercepted anchor to exercise same-component parameter navigation.
    const link = page.getByRole('link', { name: 'Home', exact: true });
    await link.evaluate((element, href) => element.setAttribute('href', href), `/${module}/${module}-2`);
    await link.click();
    await expect(page).toHaveURL(new RegExp(`${module}/${module}-2$`));
    await expect(selected(page)).toContainText('R&D');
    await expect(selected(page)).not.toContainText('Network Operations');
    await expect(selected(page)).not.toContainText('Historical selection');
    await expect(selected(page)).not.toContainText('Stale result');
  });
}

test('submitted changes retain lifecycle assignment restrictions', async ({ page, request }) => {
  await request.get(`${fixture}/fault?mode=locked`);
  await open(page, 'changes');
  await expect(categorySearch(page)).toBeDisabled();
  await expect(selected(page).locator('.mud-chip-close-button')).toHaveCount(0);
  await expect(page.locator('.ticket-header-category-chip .mud-chip-close-button')).toHaveCount(0);
});


test('change template draft survives activity tab switches', async ({ page }) => {
  await open(page, 'changes');
  const draft = page.getByRole('textbox', { name: 'Scope of Change', exact: true });
  await draft.fill('Synthetic draft retained while reading activity');
  await page.getByRole('tab', { name: /Timeline/ }).click();
  await expect(draft).toBeHidden();
  await page.getByRole('tab', { name: 'Change Template', exact: true }).click();
  await expect(draft).toHaveValue('Synthetic draft retained while reading activity');
  await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeEnabled();
});


async function routeWithoutReload(page: Page, path: string) {
  await page.getByRole('link', { name: 'Home', exact: true }).evaluate((link, path) => {
    link.setAttribute('href', path); (link as HTMLAnchorElement).click();
  }, path);
}
for (const module of ['incidents', 'requests', 'changes']) {
  test(`${module} pending SLA dialog cannot apply to a newly opened ticket`, async ({ page, request }) => {
    await open(page, module);
    await page.getByRole('button', { name: 'Pause SLA', exact: true }).click();
    await routeWithoutReload(page, `/${module}/${module}-2`);
    await expect(page).toHaveURL(new RegExp(`${module}-2$`));
    await page.getByRole('textbox', { name: /^Reason/ }).fill('Old modal must not target new ticket');
    await page.getByRole('button', { name: 'Pause', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(selected(page)).toContainText('R&D');
    expect((await (await request.get(`${fixture}/state`)).json()).slaActions).toEqual([]);
  });

  test(`${module} late background refresh cannot replace another ticket`, async ({ page, request }) => {
    await open(page, module);
    await request.get(`${fixture}/fault?mode=hold-refresh`);
    await page.getByRole('button', { name: 'Pause SLA', exact: true }).click();
    await page.getByRole('textbox', { name: /^Reason/ }).fill('Synthetic pause reason');
    await page.getByRole('button', { name: 'Pause', exact: true }).click();
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).held).toContain('refresh');
    await routeWithoutReload(page, `/${module}/${module}-2`);
    await expect(selected(page)).toContainText('R&D');
    await request.get(`${fixture}/release`);
    await page.getByRole('tab', { name: /Attachments/ }).click();
    await expect(page.getByRole('tab', { name: /Attachments/ })).toHaveAttribute('aria-selected', 'true');
    await expect(selected(page)).not.toContainText('Historical selection');
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeDisabled();
  });

  test(`${module} old save response cannot clear a new draft after A B A navigation`, async ({ page, request }) => {
    await open(page, module);
    await addLiteral(page);
    await request.get(`${fixture}/fault?mode=hold-save`);
    await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
    await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).held).toContain('save');
    await routeWithoutReload(page, `/${module}/${module}-2`);
    await page.getByRole('button', { name: 'Discard changes', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`${module}-2$`));
    await expect(selected(page)).toContainText('R&D');
    await routeWithoutReload(page, `/${module}/${module}-1`);
    await expect(selected(page)).toContainText('Historical selection');
    await selected(page).locator('.mud-chip').filter({ hasText: 'Historical selection' }).locator('.mud-chip-close-button').click();
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeEnabled();
    await request.get(`${fixture}/release`);
    await page.getByRole('tab', { name: /Attachments/ }).click();
    await expect(page.getByRole('tab', { name: /Attachments/ })).toHaveAttribute('aria-selected', 'true');
    await expect(selected(page)).not.toContainText('Historical selection');
    await expect(page.getByRole('button', { name: 'Save Changes', exact: true })).toBeEnabled();
  });
}


test('request workflow ignores a late response from the previous event group', async ({ page, request }) => {
  await open(page, 'requests');
  await page.getByRole('tab', { name: 'Workflow', exact: true }).click();
  await request.get(`${fixture}/fault?mode=hold-workflow`);
  await page.getByRole('combobox', { name: 'Event Group', exact: true }).click();
  await page.getByRole('option', { name: 'Tasks', exact: true }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json()).held).toContain('workflow');
  await page.getByRole('combobox', { name: 'Event Group', exact: true }).click();
  await page.getByRole('option', { name: 'SLA', exact: true }).click();
  await expect(page.getByText('SLA workflow event', { exact: true })).toBeVisible();
  await request.get(`${fixture}/release`);
  await page.getByRole('textbox', { name: 'Search', exact: true }).fill('workflow');
  await page.getByRole('textbox', { name: 'Search', exact: true }).press('Tab');
  await expect(page.getByText('Tasks workflow event', { exact: true })).toHaveCount(0);
  await expect(page.getByText('SLA workflow event', { exact: true })).toBeVisible();
});

import { captureTables } from './table-evidence';
import { expect, test, type Page } from '@playwright/test';

const fixture = 'http://127.0.0.1:18440/fixture';
async function form(page: Page, organization?: string) {
  await page.goto('/self-service/request/form-synthetic');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 30_000 });
  await expect(page.getByRole('heading', { name: 'Synthetic access request', exact: true })).toBeVisible();
  await expect(page.getByTestId('submission-organization')).toContainText('Synthetic Owner');
  if (organization) await expect(page.getByTestId('submission-organization')).toContainText(organization);
  await page.getByRole('textbox', { name: 'Request reason' }).fill('Harmless rendered submission test');
  await page.getByRole('textbox', { name: 'Additional note (optional)' }).fill('Retain this note while correcting the request.');
}
test.beforeEach(async ({ page, request }) => {
  await request.get(`${fixture}/reset`);
  await page.goto('/auth/development');
});

for (const mode of ['empty-401', 'empty-403', 'empty-400', 'empty-500', 'malformed-error', 'invalid-success']) {
  test(`rendered form retains values and a safe retry receipt after ${mode}`, async ({ page, request }) => {
    await request.get(`${fixture}/fault?mode=${mode}`);
    await form(page, 'Example Organization');
    await page.getByRole('button', { name: 'Submit', exact: true }).click();
    const alert = page.locator('.self-service-form-page .mud-alert');
    await expect(alert).toBeVisible();
    await expect(alert).toContainText(mode === 'invalid-success' ? 'could not confirm' : 'HTTP');
    await expect(alert).not.toContainText('Private stack trace');
    await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Request reason' })).toHaveValue('Harmless rendered submission test');
    await expect(page.getByRole('textbox', { name: 'Additional note (optional)' })).toHaveValue('Retain this note while correcting the request.');
    const retryLabel = ['empty-500', 'malformed-error', 'invalid-success'].includes(mode) ? 'Retry previous submission' : 'Submit';
    const retry = page.getByRole('button', { name: retryLabel, exact: true });
    await expect(retry).toBeEnabled();
    const first = await (await request.get(`${fixture}/state`)).json();
    expect(first.submissions).toHaveLength(1);
    expect(first.submissions[0].submissionId).toMatch(/^[a-f0-9-]{36}$/);
    await request.get(`${fixture}/fault`);
    await retry.click();
    await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
    const final = await (await request.get(`${fixture}/state`)).json();
    expect(final.submissions).toHaveLength(2);
    expect(final.submissions[1]).toEqual(first.submissions[0]);
    expect(final.submissions[1].targetOrganizationId).toBe('org-synthetic');
    expect(final.receiptCount).toBe(1);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}

for (const mode of ['missing-requester', 'disabled-requester', 'wrong-organization']) {
  test(`rendered requester context explains ${mode} and supports correction without losing input`, async ({ page, request }) => {
    await request.get(`${fixture}/fault?mode=${mode}`);
    await form(page);
    await expect(page.locator('.self-service-form-page .mud-alert')).toContainText(mode === 'wrong-organization' ? 'different organization' : 'requester profile is unavailable');
    const submit = page.getByRole('button', { name: 'Submit', exact: true });
    await expect(submit).toBeDisabled();
    expect((await (await request.get(`${fixture}/state`)).json()).submissions).toEqual([]);
    await request.get(`${fixture}/fault`);
    await page.getByRole('button', { name: 'Refresh organization choices' }).click();
    await expect(page.getByRole('textbox', { name: 'Request reason' })).toHaveValue('Harmless rendered submission test');
    await submit.click();
    await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
    expect((await (await request.get(`${fixture}/state`)).json()).receiptCount).toBe(1);
  });
}

test('multiple choices require a deliberate organization and an uncertain send keeps its original selection', async ({ page, request }, info) => {
  await request.get(`${fixture}/multiple-organizations`);
  await request.get(`${fixture}/fault?mode=invalid-success`);
  await form(page);
  const submit = page.getByRole('button', { name: 'Submit', exact: true });
  await expect(submit).toBeDisabled();
  const organization = page.getByRole('combobox', { name: 'Organization' });
  await organization.fill('Second');
  await page.getByRole('option', { name: 'Second Organization' }).click();
  await expect(page.getByTestId('submission-organization')).toContainText('Second Organization');
  await page.screenshot({ path: info.outputPath('selected-organization.png'), fullPage: true });
  await submit.click();
  await expect(page.getByText('We could not confirm the submission.', { exact: false })).toBeVisible();
  await expect(organization).toBeDisabled();
  await request.get(`${fixture}/fault`);
  await page.getByRole('button', { name: 'Retry previous submission' }).click();
  await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
  await expect(page.getByText('Organization: Second Organization', { exact: true })).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.submissions).toHaveLength(2);
  expect(state.submissions[0]).toEqual(state.submissions[1]);
  expect(state.submissions[0].targetOrganizationId).toBe('org-secondary');
});

test('self submit rejects duplicate button events and on-behalf retains explicit recipient confirmation', async ({ page, request }) => {
  await form(page, 'Example Organization');
  await page.getByRole('button', { name: 'Submit', exact: true }).evaluate((button: HTMLButtonElement) => { button.click(); button.click(); });
  await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
  let state = await (await request.get(`${fixture}/state`)).json();
  expect(state.submissions).toHaveLength(1);
  expect(state.submissions[0].requestedForPersonId).toBeNull();
  await form(page, 'Example Organization');
  await page.getByRole('button', { name: 'Select', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('row', { name: /Synthetic Recipient/ }).getByRole('button').click();
  await expect(page.getByText('Synthetic Recipient (recipient@example.test) - Example Organization')).toBeVisible();
  await page.getByRole('button', { name: 'Submit', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('on behalf of Synthetic Recipient');
  await page.getByRole('button', { name: 'No', exact: true }).click();
  state = await (await request.get(`${fixture}/state`)).json();
  expect(state.submissions).toHaveLength(1);
  await page.getByRole('button', { name: 'Submit', exact: true }).click();
  await page.getByRole('button', { name: 'Yes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
  state = await (await request.get(`${fixture}/state`)).json();
  expect(state.submissions).toHaveLength(2);
  expect(state.submissions[1].requestedForPersonId).toBe('synthetic-recipient');
  expect(state.submissions[1].requestedForPersonSource).toBe(0);
  expect(state.receiptCount).toBe(2);
});

test('form cancel and successful return retain the catalog folder and search context', async ({ page, request }) => {
  await request.get(`${fixture}/form-card`);
  await page.goto('/self-service/service-0?search=access');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: /^Synthetic access request\./ }).click();
  await expect(page).toHaveURL(/\/self-service\/request\/form-synthetic$/);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page).toHaveURL(/\/self-service\/service-0\?search=access$/);
  await expect(page.getByRole('textbox', { name: 'Search services and requests' })).toHaveValue('access');
  await page.getByRole('button', { name: /^Synthetic access request\./ }).click();
  await page.getByRole('textbox', { name: 'Request reason' }).fill('Harmless return-context test');
  await page.getByRole('button', { name: 'Submit', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Back to catalog', exact: true }).click();
  await expect(page).toHaveURL(/\/self-service\/service-0\?search=access$/);
  await expect(page.getByRole('textbox', { name: 'Search services and requests' })).toHaveValue('access');
});

// Opt-in geometry and synthetic screenshots for the owner table inventory.
test.afterEach(async ({ page }) => { await captureTables(page); });

for (const [width, height] of [[1440, 900], [390, 844]]) {
  test(`multi-select retains choices across searches and pages at ${width}px`, async ({ page, request }, info) => {
    await page.setViewportSize({ width, height });
    await page.addInitScript(theme => localStorage.setItem('helpdesk.theme.preference', theme), width < 600 ? 'dark' : 'light');
    await request.get(`${fixture}/multi-select`);
    await form(page, 'Example Organization');

    const groups = page.getByRole('region', { name: 'Groups', exact: true });
    const reviewers = page.getByRole('region', { name: 'Reviewers', exact: true });
    const chooseGroups = groups.getByRole('button', { name: 'Groups *', exact: true });
    const search = groups.getByRole('textbox', { name: 'Search Groups' });
    const submit = page.getByRole('button', { name: 'Submit', exact: true });
    await submit.click();
    await expect(groups.getByRole('alert')).toContainText('Required');
    await chooseGroups.focus();
    await page.keyboard.press('Enter');
    await expect(search).toBeFocused();
    await search.fill('f');
    await expect(groups.getByText('Enter at least 2 characters')).toBeVisible();
    await search.fill('fleet');
    await groups.getByRole('checkbox', { name: /Fleet.Mobile.Admin/ }).focus();
    await page.keyboard.press('Space');
    await groups.getByRole('checkbox', { name: /Fleet.Web.Reporting/ }).check();
    await expect(groups.getByText('2 selected', { exact: true })).toBeVisible();
    await groups.getByRole('button', { name: 'Next results' }).click();
    await groups.getByRole('checkbox', { name: /Fleet Team 18/ }).check();
    await search.fill('warehouse');
    await groups.getByRole('checkbox', { name: /Warehouse.Readers/ }).check();
    await expect(groups.getByText('4 selected', { exact: true })).toBeVisible();
    await groups.getByRole('button', { name: /Remove Fleet.Mobile.Admin/ }).click();
    await expect(groups.getByText('3 selected', { exact: true })).toBeVisible();
    await expect(groups.getByRole('checkbox', { name: /Warehouse.Readers/ })).toBeChecked();
    await groups.screenshot({ path: info.outputPath(`dataset-multi-select-${width}.png`) });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width + 1);

    await search.press('Escape');
    await expect(chooseGroups).toBeFocused();
    await chooseGroups.click();
    await expect(groups.getByText('3 selected', { exact: true })).toBeVisible();
    await search.fill('no matches');
    await expect(groups.getByText('No matching options.')).toBeVisible();
    await request.get(`${fixture}/fault?mode=dataset-options`);
    await search.fill('fleet');
    await expect(groups.getByRole('alert')).toContainText('Could not load options');
    await request.get(`${fixture}/fault`);
    await groups.getByRole('button', { name: 'Retry search' }).click();
    await expect(groups.getByRole('checkbox', { name: /Fleet.Web.Reporting/ })).toBeChecked();
    await groups.getByRole('button', { name: 'Clear all selections for Groups' }).click();
    await expect(groups.getByText('0 selected', { exact: true })).toBeVisible();
    await groups.getByRole('checkbox', { name: /Fleet.Web.Reporting/ }).check();

    await reviewers.getByRole('button', { name: 'Reviewers', exact: true }).click();
    await reviewers.getByRole('textbox', { name: 'Search Reviewers' }).fill('reviewer');
    await reviewers.getByRole('checkbox', { name: /Synthetic reviewer/ }).check();
    await expect(groups.getByText('1 selected', { exact: true })).toBeVisible();
    await expect(reviewers.getByText('1 selected', { exact: true })).toBeVisible();

    const regions = page.getByRole('combobox', { name: /Regions/ });
    const north = page.getByText('North', { exact: true });
    await regions.click();
    await north.click();
    await page.getByText('South', { exact: true }).click();
    await regions.press('Escape');
    await expect(north).toBeHidden();
    await expect(regions).toHaveValue('2 selected');
    await regions.press('ArrowDown');
    await north.click();
    await regions.press('Escape');
    await expect(north).toBeHidden();
    await expect(regions).toHaveValue('1 selected');
    await regions.press('ArrowDown');
    await north.click();
    await regions.press('Escape');
    await expect(north).toBeHidden();
    await page.getByRole('combobox', { name: 'Urgency', exact: true }).click();
    await page.getByText('Routine', { exact: true }).click();

    await submit.click();
    await expect(page.getByRole('heading', { name: 'Request submitted' })).toBeVisible();
    const state = await (await request.get(`${fixture}/state`)).json();
    expect(JSON.parse(state.submissions[0].payloadJson)).toMatchObject({
      groups: ['CN=Fleet.Web.Reporting,DC=example,DC=org'], reviewers: ['reviewer-1'], regions: ['South', 'North'], urgency: 'Routine'
    });
    expect(state.receiptCount).toBe(1);
    expect(state.requests.filter((entry: any) => entry.page).every((entry: any) => entry.pageSize === 20)).toBeTruthy();
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}

test('changing the organization clears dataset choices and fences a slow response', async ({ page, request }) => {
  await request.get(`${fixture}/multi-select`);
  await request.get(`${fixture}/multiple-organizations`);
  await form(page);
  const organization = page.getByRole('combobox', { name: 'Organization' });
  await organization.fill('Example');
  await page.getByRole('option', { name: 'Example Organization' }).click();

  const groups = page.getByRole('region', { name: 'Groups', exact: true });
  await groups.getByRole('button', { name: 'Groups *' }).click();
  const search = groups.getByRole('textbox', { name: 'Search Groups' });
  await search.fill('fleet');
  await groups.getByRole('checkbox', { name: /Fleet.Web.Reporting/ }).check();
  await search.fill('slow');
  await expect.poll(async () => (await (await request.get(`${fixture}/state`)).json())
    .requests.some((entry: any) => entry.query === 'slow')).toBeTruthy();

  await organization.fill('Second');
  await page.getByRole('option', { name: 'Second Organization' }).click();
  await expect(groups.getByText('0 selected', { exact: true })).toBeVisible();
  await groups.getByRole('button', { name: 'Groups *' }).click();
  await search.fill('fleet');
  await expect(groups.getByRole('checkbox', { name: /Second tenant fleet/ })).toBeVisible();
  await expect(groups.getByText('Slow result')).toHaveCount(0);
  await expect(groups.getByText('Fleet.Web.Reporting')).toHaveCount(0);
});

test('disabled binding gives the on-behalf requester an actionable blocker and retains the form', async ({ page, request }, info) => {
  await request.get(`${fixture}/fault?mode=automation-disabled`);
  await form(page, 'Example Organization');
  await page.getByRole('button', { name: 'Select', exact: true }).click();
  await page.getByRole('dialog').getByRole('row', { name: /Synthetic Recipient/ }).getByRole('button').click();
  await page.getByRole('button', { name: 'Submit', exact: true }).click();
  await page.getByRole('button', { name: 'Yes', exact: true }).click();
  await expect(page.locator('.self-service-form-page .mud-alert')).toContainText('disabled NetRatel binding');
  await expect(page.locator('.self-service-form-page .mud-alert')).toContainText('synthetic-automation-reference');
  await expect(page.getByRole('textbox', { name: 'Request reason' })).toHaveValue('Harmless rendered submission test');
  expect((await (await request.get(`${fixture}/state`)).json()).receiptCount).toBe(0);
  await page.screenshot({ path: info.outputPath('disabled-binding.png'), animations: 'disabled' });
});

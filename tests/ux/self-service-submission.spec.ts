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

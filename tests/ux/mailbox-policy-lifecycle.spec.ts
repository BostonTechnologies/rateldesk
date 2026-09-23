import { execFileSync } from 'node:child_process';
import { expect, test } from '@playwright/test';

type Mailbox = { id: string; mailboxAddress: string; organizationId: string | null };
type Worker = { deploymentPermitsIngestion: boolean; instanceRunning: boolean; state: string; blockedBy: string | null };
type Diagnostics = { state: { initialized: boolean } | null; effectiveStatus: { state: string } | null };
type Incident = { subject: string };

test.setTimeout(180_000);
test.describe.configure({ retries: 0 });

test('mailbox deployment policy: explicit false blocks worker and explicit true preserves run intent', async ({ page }) => {
  const expected = process.env.HELPDESK_MAILBOX_POLICY_EXPECT;
  expect(['blocked', 'running']).toContain(expected);
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText(expected === 'blocked' ? 'Disabled by deployment' : 'Instance enabled',
    { exact: true })).toBeVisible();
  if (expected === 'blocked') {
    await expect(page.getByText('Blocked by EmailIngestion:Enabled=false')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Start worker' })).toBeDisabled();
  }

  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).fill('Tenant A');
  await page.getByRole('option', { name: /Tenant A/ }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: 'IMAP', exact: true }).click();
  await page.getByLabel('Mailbox display name').fill('Policy fixture IMAP');
  await page.getByRole('textbox', { name: 'Mail host' }).fill('localhost');
  await page.getByLabel('Port', { exact: true }).fill(process.env.MAILBOX_FIXTURE_IMAPS_PORT!);
  await page.getByRole('textbox', { name: 'Mailbox address' }).fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol username').fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol password').fill('synthetic-mail-password');
  await page.getByRole('tab', { name: /Processing/ }).click();
  await page.getByLabel('Mailbox enabled').click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  const mailbox = ((await (await page.request.get('/api/v1/email-settings')).json()) as Mailbox[])
    .find(item => item.mailboxAddress === 'support@tenant-a.example.test');
  expect(mailbox).toBeDefined();
  const testResponse = await page.request.post(`/api/v1/email-settings/${mailbox!.id}/test`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' }
  });
  expect(testResponse.ok()).toBe(true);
  expect((await testResponse.json() as { success: boolean }).success).toBe(true);
  const worker = await (await page.request.get('/api/v1/email-settings/worker')).json() as Worker;
  const diagnostics = async (): Promise<Diagnostics> =>
    await (await page.request.get(`/api/v1/email-settings/${mailbox!.id}/diagnostics`)).json() as Diagnostics;

  if (expected === 'blocked') {
    expect(worker).toMatchObject({ deploymentPermitsIngestion: false, instanceRunning: false,
      state: 'Disabled by deployment', blockedBy: 'EmailIngestion:Enabled=false' });
    expect((await diagnostics()).effectiveStatus?.state).toBe('Disabled by deployment');
    await expect(page.getByRole('button', { name: 'Sync now' })).toBeDisabled();
    const sync = await page.request.post(`/api/v1/email-settings/${mailbox!.id}/sync`, {
      headers: { 'X-Requested-With': 'XMLHttpRequest' }
    });
    expect(sync.status()).toBe(409);
    expect((await sync.json() as { status: string }).status).toBe('Disabled by deployment');
    return;
  }

  expect(worker).toMatchObject({ deploymentPermitsIngestion: true, instanceRunning: true });
  await expect.poll(async () => (await diagnostics()).state?.initialized,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  execFileSync('python3', ['tools/ci/mailbox-lifecycle-fixture.py', 'send', '--sender', 'requester',
    '--recipient', 'support', '--subject', 'Fixture explicit true policy', '--body', 'Worker ran by explicit policy'],
  { encoding: 'utf8', timeout: 30_000, env: process.env });
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${mailbox!.organizationId}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items.filter(item =>
      item.subject === 'Fixture explicit true policy').length;
  }, { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
});

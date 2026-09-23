import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string; organizationId: string | null; mailboxAddress: string };
type Incident = { id: string; subject: string };
type Diagnostics = {
  state: { initialized: boolean; hasCheckpoint: boolean; syncCompletedVersion: number } | null;
  receipts: { reason: string | null; ticketId: string | null }[];
  effectiveStatus: { state: string; skippedInitialCount: number } | null;
};

function send(subject: string): void {
  execFileSync('python3', ['tools/ci/mailbox-lifecycle-fixture.py', 'send',
    '--sender', 'requester', '--recipient', 'support', '--subject', subject,
    '--body', `Activation-boundary fixture: ${subject}`],
  { encoding: 'utf8', timeout: 30_000, env: process.env });
}

test.setTimeout(300_000);
test.describe.configure({ retries: 0 });

test('mailbox activation boundary: published worker keeps arrivals after the first IMAP UID snapshot', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();

  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText('Instance paused', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).fill('Tenant A');
  await page.getByRole('option', { name: /Tenant A/ }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: 'IMAP', exact: true }).click();
  await page.getByLabel('Mailbox display name').fill('Activation boundary support');
  await page.getByRole('textbox', { name: 'Mail host' }).fill('localhost');
  await page.getByLabel('Port', { exact: true }).fill(process.env.MAILBOX_FIXTURE_IMAPS_PORT!);
  await page.getByRole('textbox', { name: 'Mailbox address' }).fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol username').fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol password').fill('synthetic-mail-password');
  await page.getByRole('tab', { name: /Processing/ }).click();
  await page.getByLabel('Poll interval (seconds)').fill('300');
  await page.getByLabel('Messages per poll').fill('1');
  await page.getByLabel('Mailbox enabled').click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();

  const mailbox = ((await (await page.request.get('/api/v1/email-settings')).json()) as Mailbox[])
    .find(item => item.mailboxAddress === 'support@tenant-a.example.test');
  expect(mailbox?.organizationId).toBeTruthy();
  const mailboxId = mailbox!.id;
  const diagnostics = async (): Promise<Diagnostics> =>
    await (await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`)).json() as Diagnostics;
  const incidents = async (): Promise<Incident[]> =>
    ((await (await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${mailbox!.organizationId}`))
      .json()) as { items: Incident[] }).items;
  const syncAndWait = async (): Promise<void> => {
    const response = await page.request.post(`/api/v1/email-settings/${mailboxId}/sync`, {
      headers: { 'X-Requested-With': 'XMLHttpRequest' }
    });
    expect(response.ok(), await response.text()).toBe(true);
    const request = await response.json() as { requestVersion: number };
    await expect.poll(async () => (await diagnostics()).state?.syncCompletedVersion,
      { timeout: 60_000, intervals: [1_000, 2_000, 3_000] })
      .toBeGreaterThanOrEqual(request.requestVersion);
  };

  const oldSubjects = ['Fixture before activation one', 'Fixture before activation two'];
  for (const subject of oldSubjects) send(subject);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Start worker' }).click();
  await expect(page.getByText('Instance enabled', { exact: true })).toBeVisible();
  await expect.poll(async () => {
    const snapshot = await diagnostics();
    return snapshot.state?.hasCheckpoint === true && snapshot.state.initialized === false &&
      snapshot.receipts.filter(receipt => receipt.reason === 'InitialBaselineSkipped').length === 1;
  }, { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(true);
  expect(['Initializing baseline', 'Waiting for baseline']).toContain(
    (await diagnostics()).effectiveStatus?.state);

  const duringSubject = 'Fixture arrived during activation';
  send(duringSubject);
  await syncAndWait();
  let snapshot = await diagnostics();
  expect(snapshot.state?.initialized).toBe(false);
  expect(snapshot.receipts.filter(receipt => receipt.reason === 'InitialBaselineSkipped')).toHaveLength(2);
  expect((await incidents()).filter(incident => oldSubjects.includes(incident.subject))).toHaveLength(0);

  await syncAndWait();
  snapshot = await diagnostics();
  expect(snapshot.state?.initialized).toBe(false);
  expect((await incidents()).filter(incident => incident.subject === duringSubject)).toHaveLength(1);
  await syncAndWait();
  expect((await diagnostics()).state?.initialized).toBe(true);

  const afterSubject = 'Fixture arrived after baseline';
  send(afterSubject);
  await syncAndWait();
  await syncAndWait();
  const created = await incidents();
  expect(created.filter(incident => incident.subject === duringSubject)).toHaveLength(1);
  expect(created.filter(incident => incident.subject === afterSubject)).toHaveLength(1);
  expect(created.filter(incident => oldSubjects.includes(incident.subject))).toHaveLength(0);
  snapshot = await diagnostics();
  expect(snapshot.effectiveStatus?.skippedInitialCount).toBe(2);
  expect(snapshot.receipts.filter(receipt => receipt.ticketId !== null)).toHaveLength(2);

  await page.goto(`/admin/email-settings/${mailboxId}/ingestion`);
  await expect(page.getByTestId('mailbox-ingestion')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText(/Initial baseline skipped: 2/)).toBeVisible();
});

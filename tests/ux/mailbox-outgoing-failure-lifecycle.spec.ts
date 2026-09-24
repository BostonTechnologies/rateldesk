import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string };
type Incident = { id: string; trackingId: string; subject: string };
type MailMessage = { subject: string; from: string };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number } | null;
  receipts: { ticketId: string | null }[] };

function fixture(command: 'send' | 'messages', args: string[]): unknown {
  return JSON.parse(execFileSync('python3', ['tools/ci/mailbox-lifecycle-fixture.py', command, ...args], {
    encoding: 'utf8', timeout: 30_000, env: process.env
  }));
}

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true', { timeout: 15_000 });
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
}

test.setTimeout(300_000);
test.describe.configure({ retries: 0 });

test('published dedicated SMTP authentication failure never falls back and repairs one delivery', async ({ page }) => {
  await login(page);
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  const tenant = ((await (await page.request.get('/api/v1/email-settings/effective')).json()) as
    { id: string; name: string }[]).find(organization => organization.name === 'Tenant A');
  expect(tenant).toBeDefined();
  const create = async (displayName: string, address: string, password: string, port: string,
    scope: number, organizationId: string | null, poll: boolean): Promise<Mailbox> => {
    const response = await page.request.post('/api/v1/email-settings/', { headers, data: {
      displayName, provider: 1, authentication: 1, scope, organizationId,
      mailHost: 'localhost', port: Number(port), tlsMode: 0,
      mailboxAddress: address, username: address, password, mailboxFolder: 'inbox',
      enabled: true, backgroundSyncEnabled: poll, initialImport: 0, markReadAfterSuccess: true
    } });
    expect(response.status(), await response.text()).toBe(201);
    return await response.json() as Mailbox;
  };
  const saveOutgoing = async (mailbox: Mailbox, address: string, password: string, port: string,
    version: number): Promise<{ version: number }> => {
    const response = await page.request.put(`/api/v1/email-settings/${mailbox.id}/outgoing`, {
      headers, data: { version, enabled: true, transport: 0, displayName: 'Fixture support',
        smtpHost: 'localhost', smtpPort: Number(port), smtpTlsMode: 0,
        smtpUsername: address, smtpPassword: password, clearSmtpPassword: false }
    });
    expect(response.ok(), await response.text()).toBe(true);
    return await response.json() as { version: number };
  };
  const global = await create('Fixture global sender', 'global@tenant-b.example.test',
    'synthetic-global-password', process.env.MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT!, 0, null, false);
  await saveOutgoing(global, 'global@tenant-b.example.test', 'synthetic-global-password',
    process.env.MAILBOX_FIXTURE_GLOBAL_SMTPS_PORT!, 0);
  const dedicated = await create('Fixture dedicated failed sender', 'support@tenant-a.example.test',
    'synthetic-mail-password', process.env.MAILBOX_FIXTURE_IMAPS_PORT!, 1, tenant!.id, true);
  await saveOutgoing(dedicated, 'support@tenant-a.example.test', 'synthetic-wrong-password',
    process.env.MAILBOX_FIXTURE_SMTPS_PORT!, 0);
  const start = await page.request.post('/api/v1/email-settings/worker', {
    headers, data: { running: true, confirmed: true }
  });
  expect(start.ok(), await start.text()).toBe(true);
  const diagnostics = async (): Promise<Diagnostics> =>
    await (await page.request.get(`/api/v1/email-settings/${dedicated.id}/diagnostics`)).json() as Diagnostics;
  await expect.poll(async () => (await diagnostics()).state?.initialized,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);

  const connection = await page.request.post(`/api/v1/email-settings/${dedicated.id}/outgoing/test`, { headers });
  expect(connection.ok(), await connection.text()).toBe(true);
  expect((await connection.json() as { success: boolean }).success).toBe(false);
  const outgoing = await (await page.request.get(`/api/v1/email-settings/${dedicated.id}/outgoing`)).json() as
    { version: number; testedVersion: number; lastTestCode: string };
  expect(outgoing.testedVersion).toBe(outgoing.version);
  expect(outgoing.lastTestCode).toBe('SmtpAuthenticationFailed');
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('combobox', { name: 'Selected mailbox' }).click();
  await page.getByRole('option', { name: /Fixture dedicated failed sender/ }).click();
  await expect(page.getByText('Outgoing: Connection failed (SmtpAuthenticationFailed)')).toBeVisible();

  const subject = 'Fixture dedicated SMTP authentication failure';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', subject,
    '--body', 'Incoming must commit while the dedicated outgoing authentication fails.']);
  const sync = await page.request.post(`/api/v1/email-settings/${dedicated.id}/sync`, { headers });
  expect(sync.ok(), await sync.text()).toBe(true);
  const command = await sync.json() as { requestVersion: number };
  await expect.poll(async () => (await diagnostics()).state?.syncCompletedVersion,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(command.requestVersion);
  const incidents = async (): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${tenant!.id}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  await expect.poll(async () => (await incidents()).filter(incident => incident.subject === subject).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const incident = (await incidents()).find(item => item.subject === subject)!;
  const failed = async (): Promise<{ id: string; ticketId: string; deliveryErrorCode: string }[]> =>
    await (await page.request.get('/api/v1/timeline/failed')).json() as
      { id: string; ticketId: string; deliveryErrorCode: string }[];
  await expect.poll(async () => (await failed()).filter(item => item.ticketId === incident.id).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const failure = (await failed()).find(item => item.ticketId === incident.id)!;
  expect(failure.deliveryErrorCode).toBe('SmtpAuthenticationFailed');
  expect((await diagnostics()).receipts.filter(receipt => receipt.ticketId === incident.id)).toHaveLength(1);
  const requesterMail = (): MailMessage[] => fixture('messages', ['--account', 'requester']) as MailMessage[];
  expect(requesterMail().filter(message => message.subject.includes(incident.trackingId))).toHaveLength(0);

  const repaired = await saveOutgoing(dedicated, 'support@tenant-a.example.test',
    'synthetic-mail-password', process.env.MAILBOX_FIXTURE_SMTPS_PORT!, outgoing.version);
  const previewResponse = await page.request.get(`/api/v1/timeline/${failure.id}/outgoing-retry-preview`);
  expect(previewResponse.ok()).toBe(true);
  const preview = await previewResponse.json() as
    { canRetry: boolean; mailboxAddress: string; currentOutgoingVersion: number };
  expect(preview.canRetry).toBe(true);
  expect(preview.mailboxAddress).toBe('support@tenant-a.example.test');
  expect(preview.currentOutgoingVersion).toBe(repaired.version);
  const retry = await page.request.post(`/api/v1/timeline/${failure.id}/retry-current-outgoing`, {
    headers, data: { confirmed: true, expectedOutgoingVersion: repaired.version }
  });
  expect(retry.status(), await retry.text()).toBe(202);
  await expect.poll(() => requesterMail().filter(message => message.subject.includes(incident.trackingId) &&
    message.from.includes('support@tenant-a.example.test')).length,
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(1);
  expect(requesterMail().filter(message => message.subject.includes(incident.trackingId) &&
    message.from.includes('global@tenant-b.example.test'))).toHaveLength(0);
  expect((await incidents()).filter(item => item.subject === subject)).toHaveLength(1);
  expect((await diagnostics()).receipts.filter(receipt => receipt.ticketId === incident.id)).toHaveLength(1);
});

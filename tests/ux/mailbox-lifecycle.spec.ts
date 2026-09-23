import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type MailMessage = { subject: string; from: string; to: string; replyTo: string; messageId: string; body: string };
type Mailbox = { id: string; organizationId: string | null; mailboxAddress: string };
type Incident = { id: string; trackingId: string; subject: string; requesterEmail: string; organizationId: string };
type Diagnostics = { state: { initialized: boolean } | null; receipts: { outcome: number | string; ticketId: string | null }[] };

function fixture(command: 'send' | 'messages', args: string[]): unknown {
  return JSON.parse(execFileSync('python3', ['tools/ci/mailbox-lifecycle-fixture.py', command, ...args], {
    encoding: 'utf8', timeout: 30_000, env: process.env
  }));
}

function inbox(account: 'requester' | 'recipient'): MailMessage[] {
  return fixture('messages', ['--account', account]) as MailMessage[];
}

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
}

test.setTimeout(240_000);

test('mailbox lifecycle acceptance: published Web/API receives, sends and threads through isolated IMAP plus SMTP', async ({ page }) => {
  await login(page);
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText('Instance paused', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).click();
  await page.getByRole('option', { name: /Tenant A/ }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: 'IMAP', exact: true }).click();
  await page.getByLabel('Mailbox display name').fill('Tenant A support');
  await page.getByRole('textbox', { name: 'Mail host' }).fill('localhost');
  await page.getByLabel('Port', { exact: true }).fill(process.env.MAILBOX_FIXTURE_IMAPS_PORT!);
  await page.getByRole('textbox', { name: 'Mailbox address' }).fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol username').fill('support@tenant-a.example.test');
  await page.getByLabel('Protocol password').fill('synthetic-mail-password');
  await page.getByLabel('Mailbox enabled').click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();

  const mailboxes = await (await page.request.get('/api/v1/email-settings')).json() as Mailbox[];
  const selected = mailboxes.find(mailbox => mailbox.mailboxAddress === 'support@tenant-a.example.test');
  expect(selected).toBeDefined();
  expect(selected!.organizationId).toBeTruthy();
  const mailboxId = selected!.id;

  await page.getByRole('tab', { name: /Outgoing/ }).click();
  await expect(page.getByTestId('mailbox-outgoing-editor')).toBeVisible();
  await page.getByRole('textbox', { name: 'SMTP submission host' }).fill('localhost');
  await page.getByLabel('SMTP port').fill(process.env.MAILBOX_FIXTURE_SMTPS_PORT!);
  await page.getByRole('combobox', { name: 'Required SMTP TLS' }).click();
  await page.getByRole('option', { name: 'TLS on connect' }).click();
  await page.getByRole('textbox', { name: 'SMTP username' }).fill('support@tenant-a.example.test');
  await page.getByLabel('SMTP password').fill('synthetic-mail-password');
  await page.getByLabel('Enable outgoing mail').click();
  await page.getByRole('button', { name: 'Save outgoing' }).click();
  await expect(page.getByTestId('mailbox-outgoing-editor').getByText('Outgoing settings saved.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Test outgoing connection' }).click();
  await expect(page.getByText('SMTP TLS and authentication succeeded; no message was sent.')).toBeVisible();

  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Start worker' }).click();
  await expect(page.getByText('Instance enabled', { exact: true })).toBeVisible();
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as Diagnostics).state?.initialized;
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);

  const subjects = ['Fixture printer issue', 'Fixture access issue', 'Fixture network issue'];
  for (const subject of subjects)
    fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', subject, '--body', `Body for ${subject}`]);

  const listIncidents = async (): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${encodeURIComponent(selected!.organizationId!)}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  await expect.poll(async () => (await listIncidents()).filter(incident => subjects.includes(incident.subject)).length,
    { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(3);
  const created = (await listIncidents()).filter(incident => subjects.includes(incident.subject));
  expect(new Set(created.map(incident => incident.id)).size).toBe(3);
  expect(created.every(incident => incident.requesterEmail === 'requester@tenant-a.example.test' &&
    incident.organizationId === selected!.organizationId)).toBe(true);
  await page.goto('/incidents');
  await expect(page.getByText(subjects[0], { exact: true })).toBeVisible();

  await expect.poll(() => inbox('requester').filter(message => created.some(incident =>
    message.subject.includes(incident.trackingId))).length,
  { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(3);
  const confirmations = inbox('requester').filter(message => created.some(incident =>
    message.subject.includes(incident.trackingId)));
  expect(confirmations.every(message => message.from.includes('support@tenant-a.example.test') &&
    message.replyTo.includes('support@tenant-a.example.test'))).toBe(true);

  const replyTarget = confirmations[0];
  fixture('send', ['--sender', 'requester', '--recipient', 'support',
    '--subject', `Re: ${replyTarget.subject}`, '--body', 'Fixture reply text',
    '--in-reply-to', replyTarget.messageId]);
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`);
    return ((await response.json()) as Diagnostics).receipts.filter(receipt => receipt.ticketId !== null).length;
  }, { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(4);
  expect((await listIncidents()).filter(incident => subjects.includes(incident.subject))).toHaveLength(3);
  const replyIncident = created.find(incident => replyTarget.subject.includes(incident.trackingId))!;
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/incidents/${replyIncident.id}/timeline`);
    return JSON.stringify(await response.json()).includes('Fixture reply text');
  }, { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(true);
});

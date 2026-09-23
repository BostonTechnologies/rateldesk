import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string; organizationId: string | null; mailboxAddress: string };
type Incident = { id: string; subject: string; trackingId: string; organizationId: string };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number } | null;
  receipts: { id: string; ticketId: string | null; acknowledged: boolean }[];
  effectiveStatus: { state: string } | null };
type MailMessage = { subject: string; from: string; to: string; replyTo: string };

function fixture(command: 'send' | 'messages', args: string[]): unknown {
  return JSON.parse(execFileSync('python3', ['tools/ci/mailbox-lifecycle-fixture.py', command, ...args], {
    encoding: 'utf8', timeout: 30_000, env: process.env
  }));
}

async function addDedicated(page: Page, organization: string, provider: 'IMAP' | 'POP3',
  address: string, password: string, incomingPort: string, outgoingEnabled = true): Promise<Mailbox> {
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).fill(organization);
  await page.getByRole('option', { name: new RegExp(organization) }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: provider, exact: true }).click();
  await page.getByLabel('Mailbox display name').fill(`${organization} ${provider} support`);
  await page.getByRole('textbox', { name: 'Mail host' }).fill('localhost');
  await page.getByLabel('Port', { exact: true }).fill(incomingPort);
  await page.getByRole('textbox', { name: 'Mailbox address' }).fill(address);
  await page.getByLabel('Protocol username').fill(address);
  await page.getByLabel('Protocol password').fill(password);
  await page.getByRole('tab', { name: /Processing/ }).click();
  await page.getByLabel('Mailbox enabled').click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  const mailbox = ((await (await page.request.get('/api/v1/email-settings')).json()) as Mailbox[])
    .find(item => item.mailboxAddress === address);
  expect(mailbox).toBeDefined();
  if (outgoingEnabled)
    await configureOutgoing(page, address, password);
  return mailbox!;
}

async function configureOutgoing(page: Page, address: string, password: string): Promise<void> {
  await page.getByRole('tab', { name: /Outgoing/ }).click();
  await page.getByRole('textbox', { name: 'SMTP submission host' }).fill('localhost');
  await page.getByLabel('SMTP port').fill(process.env.MAILBOX_FIXTURE_SMTPS_PORT!);
  await page.getByRole('combobox', { name: 'Required SMTP TLS' }).click();
  await page.getByRole('option', { name: 'TLS on connect' }).click();
  await page.getByRole('textbox', { name: 'SMTP username' }).fill(address);
  await page.getByLabel('SMTP password').fill(password);
  await page.getByLabel('Enable outgoing mail').click();
  await page.getByRole('button', { name: 'Save outgoing' }).click();
  await expect(page.getByTestId('mailbox-outgoing-editor').getByText('Outgoing settings saved.', { exact: true })).toBeVisible();
}

test.setTimeout(300_000);
test.describe.configure({ retries: 0 });

test('mailbox POP3 lifecycle: dedicated IMAP and POP3 work without any global mailbox', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();

  const organizationResponse = await page.request.post('/api/v1/organizations/', {
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    data: { name: 'Tenant C', dnsName: 'tenant-c.example.test' }
  });
  expect(organizationResponse.status()).toBe(201);
  const organizationC = await organizationResponse.json() as { id: string };

  const imap = await addDedicated(page, 'Tenant A', 'IMAP', 'support@tenant-a.example.test',
    'synthetic-mail-password', process.env.MAILBOX_FIXTURE_IMAPS_PORT!);
  const pop = await addDedicated(page, 'Tenant C', 'POP3', 'pop@tenant-c.example.test',
    'synthetic-pop-password', process.env.MAILBOX_FIXTURE_POP3S_PORT!, false);
  await expect(page.getByText('Outgoing: Not configured', { exact: true })).toBeVisible();
  expect(pop.organizationId).toBe(organizationC.id);
  const mailboxes = await (await page.request.get('/api/v1/email-settings')).json() as Mailbox[];
  expect(mailboxes).toHaveLength(2);
  expect(mailboxes.every(mailbox => mailbox.organizationId !== null)).toBe(true);

  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Start worker' }).click();
  await expect(page.getByText('Instance enabled', { exact: true })).toBeVisible();
  const diagnostics = async (id: string): Promise<Diagnostics> =>
    await (await page.request.get(`/api/v1/email-settings/${id}/diagnostics`)).json() as Diagnostics;
  for (const mailbox of [imap, pop]) {
    await expect.poll(async () => (await diagnostics(mailbox.id)).state?.initialized,
      { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  }

  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', 'Fixture no-global IMAP',
    '--body', 'Dedicated IMAP remains independent of a global mailbox']);
  fixture('send', ['--sender', 'requester-c', '--recipient', 'pop', '--subject', 'Fixture no-global POP3',
    '--body', 'Dedicated POP3 uses a retained UIDL message']);
  const incidents = async (organizationId: string): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${organizationId}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  await expect.poll(async () => (await incidents(imap.organizationId!)).filter(item =>
    item.subject === 'Fixture no-global IMAP').length,
  { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  await expect.poll(async () => (await incidents(organizationC.id)).filter(item =>
    item.subject === 'Fixture no-global POP3').length,
  { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  const imapIncident = (await incidents(imap.organizationId!)).find(item => item.subject === 'Fixture no-global IMAP')!;
  const popIncident = (await incidents(organizationC.id)).find(item => item.subject === 'Fixture no-global POP3')!;
  await expect.poll(() => (fixture('messages', ['--account', 'requester']) as MailMessage[]).some(message =>
    message.subject.includes(imapIncident.trackingId) && message.from.includes('support@tenant-a.example.test') &&
    message.replyTo.includes('support@tenant-a.example.test')),
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(true);
  const failedDeliveries = async (): Promise<{ id: string; ticketId: string }[]> =>
    await (await page.request.get('/api/v1/timeline/failed')).json() as { id: string; ticketId: string }[];
  await expect.poll(async () => (await failedDeliveries()).filter(item => item.ticketId === popIncident.id).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  expect((fixture('messages', ['--account', 'requester-c']) as MailMessage[])
    .filter(message => message.subject.includes(popIncident.trackingId))).toHaveLength(0);
  await configureOutgoing(page, 'pop@tenant-c.example.test', 'synthetic-pop-password');
  const failed = (await failedDeliveries()).find(item => item.ticketId === popIncident.id)!;
  const retry = await page.request.post(`/api/v1/timeline/${failed.id}/retry`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' }
  });
  expect(retry.ok()).toBe(true);
  await expect.poll(() => (fixture('messages', ['--account', 'requester-c']) as MailMessage[]).filter(message =>
    message.subject.includes(popIncident.trackingId) && message.from.includes('pop@tenant-c.example.test') &&
    message.replyTo.includes('pop@tenant-c.example.test')).length,
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(1);
  await expect.poll(async () => (await failedDeliveries()).filter(item => item.ticketId === popIncident.id).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(0);

  expect((fixture('messages', ['--account', 'pop']) as MailMessage[])
    .filter(message => message.subject === 'Fixture no-global POP3')).toHaveLength(1);
  await expect.poll(async () => (await diagnostics(pop.id)).receipts.filter(receipt =>
    receipt.ticketId === popIncident.id && receipt.acknowledged).length,
  { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const syncResponse = await page.request.post(`/api/v1/email-settings/${pop.id}/sync`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' }
  });
  expect(syncResponse.ok()).toBe(true);
  const requested = await syncResponse.json() as { requestVersion: number };
  await expect.poll(async () => (await diagnostics(pop.id)).state?.syncCompletedVersion,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(requested.requestVersion);
  expect((await incidents(organizationC.id)).filter(item => item.subject === 'Fixture no-global POP3')).toHaveLength(1);
  expect((await diagnostics(pop.id)).receipts.filter(receipt => receipt.ticketId === popIncident.id)).toHaveLength(1);
  expect((fixture('messages', ['--account', 'pop']) as MailMessage[])
    .filter(message => message.subject === 'Fixture no-global POP3')).toHaveLength(1);

  await page.getByRole('tab', { name: /Processing/ }).click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  await expect.poll(async () => (await diagnostics(pop.id)).effectiveStatus?.state).toBe('Incoming paused');
  const sample = await page.request.post(`/api/v1/email-settings/${pop.id}/outgoing/send-test`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    data: { recipient: 'requester@tenant-c.example.test', confirmed: true }
  });
  expect(sample.ok(), await sample.text()).toBe(true);
  expect((fixture('messages', ['--account', 'requester-c']) as MailMessage[]).some(message =>
    message.subject.startsWith('RatelDesk mailbox send test') &&
    message.from.includes('pop@tenant-c.example.test'))).toBe(true);

  const heartbeat = async (): Promise<number> => {
    const worker = await (await page.request.get('/api/v1/email-settings/worker')).json() as
      { lastHeartbeatUnixMilliseconds: number | null };
    return worker.lastHeartbeatUnixMilliseconds ?? 0;
  };
  const beforePauseCycle = await heartbeat();
  fixture('send', ['--sender', 'requester-c', '--recipient', 'pop', '--subject', 'Fixture paused POP3',
    '--body', 'This message waits while only incoming is paused']);
  await expect.poll(heartbeat, { timeout: 30_000, intervals: [1_000, 2_000] })
    .toBeGreaterThan(beforePauseCycle);
  expect((await incidents(organizationC.id)).filter(item => item.subject === 'Fixture paused POP3')).toHaveLength(0);
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  await expect.poll(async () => (await incidents(organizationC.id)).filter(item =>
    item.subject === 'Fixture paused POP3').length,
  { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  expect((await incidents(organizationC.id)).filter(item => item.subject === 'Fixture no-global POP3')).toHaveLength(1);

  const beforeRotation = ((await (await page.request.get(`/api/v1/email-settings/${pop.id}`)).json()) as
    { version: number }).version;
  await page.getByRole('tab', { name: /Incoming/ }).click();
  await page.getByLabel('Protocol password').fill('synthetic-pop-password');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  const afterRotation = ((await (await page.request.get(`/api/v1/email-settings/${pop.id}`)).json()) as
    { version: number }).version;
  expect(afterRotation).toBeGreaterThan(beforeRotation);
  expect((await diagnostics(pop.id)).state?.initialized).toBe(true);
  fixture('send', ['--sender', 'requester-c', '--recipient', 'pop', '--subject', 'Fixture rotated POP3',
    '--body', 'A credential update retains the existing UIDL boundary']);
  await expect.poll(async () => (await incidents(organizationC.id)).filter(item =>
    item.subject === 'Fixture rotated POP3').length,
  { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);

  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Pause worker' }).click();
  await expect(page.getByText('Instance paused', { exact: true })).toBeVisible();
  expect((await diagnostics(pop.id)).effectiveStatus?.state).toBe('Instance paused');
  fixture('send', ['--sender', 'requester-c', '--recipient', 'pop', '--subject', 'Fixture instance-paused POP3',
    '--body', 'The instance is paused without clearing its source boundary']);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Start worker' }).click();
  await expect(page.getByText('Instance enabled', { exact: true })).toBeVisible();
  await expect.poll(async () => (await incidents(organizationC.id)).filter(item =>
    item.subject === 'Fixture instance-paused POP3').length,
  { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  expect((await incidents(organizationC.id)).filter(item => item.subject === 'Fixture no-global POP3')).toHaveLength(1);
});

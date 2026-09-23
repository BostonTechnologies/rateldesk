import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type MailMessage = { subject: string; from: string; to: string; replyTo: string; messageId: string; body: string };
type Mailbox = { id: string; organizationId: string | null; mailboxAddress: string };
type Incident = { id: string; trackingId: string; subject: string; requesterEmail: string; organizationId: string; customerId: string };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number } | null;
  receipts: { id: string; outcome: number | string; reason: string | null; ticketId: string | null }[] };

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
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true', { timeout: 15_000 });
  await page.getByRole('textbox', { name: 'Email' }).fill(process.env.HELPDESK_E2E_LOCAL_EMAIL!);
  await page.getByLabel('Password').fill(process.env.HELPDESK_E2E_LOCAL_PASSWORD!);
  await page.getByRole('button', { name: /^Sign in to/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
}

test.setTimeout(300_000);
// This scenario mutates one fresh installation; a retry would reuse its mailbox
// assignment and cannot represent a fresh lifecycle run.
test.describe.configure({ retries: 0 });

test('mailbox lifecycle acceptance: published Web/API receives, sends and threads through isolated IMAP plus SMTP', async ({ page }) => {
  await login(page);
  await page.goto('/admin/email-settings');
  await expect(page.getByTestId('mailbox-settings')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText('Instance paused', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Tenant override' }).click();
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).fill('Tenant A');
  await page.getByRole('option', { name: /Tenant A/ }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: 'IMAP', exact: true }).click();
  await page.getByLabel('Mailbox display name').fill('Tenant A support');
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

  const oldSubject = 'Fixture pre-activation message';
  const unselectedOldSubject = 'Fixture unselected pre-activation message';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', oldSubject,
    '--body', 'Only the selected historical import should create this incident']);
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', unselectedOldSubject,
    '--body', 'This baseline message must remain skipped']);

  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Start worker' }).click();
  await expect(page.getByText('Instance enabled', { exact: true })).toBeVisible();
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as Diagnostics).state?.initialized;
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`);
    return ((await response.json()) as Diagnostics).receipts.filter(receipt =>
      receipt.reason === 'InitialBaselineSkipped').length;
  }, { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(2);
  const beforeImport = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${encodeURIComponent(selected!.organizationId!)}`);
  expect(((await beforeImport.json()) as { items: Incident[] }).items.some(incident =>
    incident.subject === oldSubject || incident.subject === unselectedOldSubject)).toBe(false);

  await page.goto(`/admin/email-settings/${mailboxId}/ingestion`);
  await expect(page.getByTestId('mailbox-ingestion')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Import existing mail…' }).click();
  await page.getByRole('button', { name: 'Preview', exact: true }).click();
  await expect(page.getByText(oldSubject, { exact: true })).toBeVisible();
  await expect(page.getByText(unselectedOldSubject, { exact: true })).toBeVisible();
  await page.getByRole('row').filter({ hasText: oldSubject })
    .getByRole('checkbox', { name: 'Select message for import' }).check();
  await page.getByRole('checkbox', { name: /I confirm these messages/ }).check();
  await page.getByRole('button', { name: 'Import selected' }).click();
  await expect(page.getByText(/Queued 1 selected messages/)).toBeVisible();
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${encodeURIComponent(selected!.organizationId!)}`);
    return ((await response.json()) as { items: Incident[] }).items.filter(incident => incident.subject === oldSubject).length;
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  const afterImport = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${encodeURIComponent(selected!.organizationId!)}`);
  expect(((await afterImport.json()) as { items: Incident[] }).items.some(incident =>
    incident.subject === unselectedOldSubject)).toBe(false);
  await page.getByRole('button', { name: 'Preview', exact: true }).click();
  await expect(page.getByText(unselectedOldSubject, { exact: true })).toBeVisible();
  await expect(page.getByText(oldSubject, { exact: true })).toHaveCount(0);
  const sync = await page.request.post(`/api/v1/email-settings/${mailboxId}/sync`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' }
  });
  expect(sync.ok(), await sync.text()).toBe(true);
  const command = await sync.json() as { requestVersion: number };
  await expect.poll(async () => ((await (await page.request.get(
    `/api/v1/email-settings/${mailboxId}/diagnostics`)).json()) as Diagnostics).state?.syncCompletedVersion,
  { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(command.requestVersion);
  const afterSync = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${encodeURIComponent(selected!.organizationId!)}`);
  expect(((await afterSync.json()) as { items: Incident[] }).items.filter(incident =>
    incident.subject === oldSubject)).toHaveLength(1);
  expect(((await (await page.request.get(`/api/v1/email-settings/${mailboxId}/diagnostics`)).json()) as Diagnostics)
    .receipts.filter(receipt => receipt.reason === 'InitialBaselineSkipped')).toHaveLength(1);

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

  const manualResponse = await page.request.post('/api/v1/incidents', { headers: { 'X-Requested-With': 'XMLHttpRequest' }, data: {
    title: 'Fixture manual incident', description: '<p>Created by an administrator</p>', priority: 0,
    customerId: created[0].customerId, organizationId: selected!.organizationId
  } });
  expect(manualResponse.status()).toBe(201);
  const manual = await manualResponse.json() as Incident;
  await expect.poll(() => inbox('requester').some(message => message.subject.includes(manual.trackingId) &&
    message.from.includes('support@tenant-a.example.test') && message.replyTo.includes('support@tenant-a.example.test')),
  { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(true);

  const organizationResponse = await page.request.post('/api/v1/organizations/', {
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    data: { name: 'Tenant B', dnsName: 'tenant-b.example.test' }
  });
  expect(organizationResponse.status()).toBe(201);
  const organizationB = await organizationResponse.json() as { id: string };
  const postAdmin = async (path: string, data: object): Promise<{ id: string }> => {
    const response = await page.request.post(path, {
      headers: { 'X-Requested-With': 'XMLHttpRequest' }, data
    });
    expect(response.status(), `${path}: ${await response.text()}`).toBe(201);
    return await response.json() as { id: string };
  };
  const outsideRecipient = await postAdmin('/api/v1/users/', {
    name: 'Fixture outside support recipient', email: 'recipient@tenant-a.example.test',
    role: 'Technician', organizationId: selected!.organizationId
  });
  const supportGroup = await postAdmin('/api/v1/support/groups/', {
    owningOrganizationId: selected!.organizationId, name: 'Fixture cross-tenant coverage', isEnabled: true
  });
  await postAdmin(`/api/v1/support/groups/${supportGroup.id}/members/`, {
    userId: outsideRecipient.id, isEnabled: true
  });
  await postAdmin('/api/v1/support/coverage/', {
    customerOrganizationId: organizationB.id, providerOrganizationId: selected!.organizationId,
    supportGroupId: supportGroup.id, isEnabled: true
  });
  await postAdmin('/api/v1/support/notification-subscriptions/', {
    customerOrganizationId: organizationB.id, eventType: 0, recipientType: 0,
    recipientId: supportGroup.id, channel: 0, isEnabled: true
  });
  const recipientPreview = await page.request.get(
    `/api/v1/support/organizations/${organizationB.id}/recipient-preview?eventType=0`);
  expect(recipientPreview.ok()).toBe(true);
  expect(JSON.stringify(await recipientPreview.json())).toContain('recipient@tenant-a.example.test');
  await page.goto('/admin/email-settings');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Global mailbox' }).click();
  await page.getByRole('combobox', { name: 'Inbound provider' }).click();
  await page.getByRole('option', { name: 'IMAP', exact: true }).click();
  await page.getByLabel('Mailbox display name').fill('Instance global support');
  await page.getByRole('textbox', { name: 'Mail host' }).fill('localhost');
  await page.getByLabel('Port', { exact: true }).fill(process.env.MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT!);
  await page.getByRole('textbox', { name: 'Mailbox address' }).fill('global@tenant-b.example.test');
  await page.getByLabel('Protocol username').fill('global@tenant-b.example.test');
  await page.getByLabel('Protocol password').fill('synthetic-global-password');
  await page.getByRole('tab', { name: /Processing/ }).click();
  await page.getByLabel('Mailbox enabled').click();
  await page.getByLabel('Background ingestion enabled').click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Mailbox settings saved.', { exact: true })).toBeVisible();
  const globalMailbox = ((await (await page.request.get('/api/v1/email-settings')).json()) as Mailbox[])
    .find(mailbox => mailbox.mailboxAddress === 'global@tenant-b.example.test');
  expect(globalMailbox).toBeDefined();
  await page.getByRole('tab', { name: /Outgoing/ }).click();
  await page.getByRole('textbox', { name: 'SMTP submission host' }).fill('localhost');
  await page.getByLabel('SMTP port').fill(process.env.MAILBOX_FIXTURE_GLOBAL_SMTPS_PORT!);
  await page.getByRole('combobox', { name: 'Required SMTP TLS' }).click();
  await page.getByRole('option', { name: 'TLS on connect' }).click();
  await page.getByRole('textbox', { name: 'SMTP username' }).fill('global@tenant-b.example.test');
  await page.getByLabel('SMTP password').fill('synthetic-global-password');
  await page.getByLabel('Enable outgoing mail').click();
  await page.getByRole('button', { name: 'Save outgoing' }).click();
  await expect(page.getByTestId('mailbox-outgoing-editor').getByText('Outgoing settings saved.', { exact: true })).toBeVisible();
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${globalMailbox!.id}/diagnostics`);
    return ((await response.json()) as Diagnostics).state?.initialized;
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);

  const globalSubject = 'Fixture global tenant B issue';
  fixture('send', ['--sender', 'requester-b', '--recipient', 'global', '--subject', globalSubject,
    '--body', 'Global mailbox creates a tenant B incident']);
  const tenantBIncidents = async (): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${organizationB.id}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  await expect.poll(async () => (await tenantBIncidents()).filter(incident => incident.subject === globalSubject).length,
    { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  const tenantBIncident = (await tenantBIncidents()).find(incident => incident.subject === globalSubject)!;
  await expect.poll(() => inbox('requester-b').some(message =>
    message.subject.includes(tenantBIncident.trackingId) &&
    message.from.includes('global@tenant-b.example.test') &&
    message.replyTo.includes('global@tenant-b.example.test')),
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(true);
  await expect.poll(() => inbox('recipient').some(message =>
    message.subject.includes(tenantBIncident.trackingId) &&
    message.from.includes('global@tenant-b.example.test') &&
    message.replyTo.includes('global@tenant-b.example.test') &&
    message.to.includes('recipient@tenant-a.example.test')),
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(true);

  const dedicatedSubject = 'Fixture dedicated route after global activation';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', dedicatedSubject,
    '--body', 'Tenant A must keep its dedicated sender']);
  await expect.poll(async () => (await listIncidents()).filter(incident => incident.subject === dedicatedSubject).length,
    { timeout: 90_000, intervals: [1_000, 2_000, 3_000] }).toBe(1);
  const dedicatedIncident = (await listIncidents()).find(incident => incident.subject === dedicatedSubject)!;
  await expect.poll(() => inbox('requester').some(message =>
    message.subject.includes(dedicatedIncident.trackingId) &&
    message.from.includes('support@tenant-a.example.test') &&
    message.replyTo.includes('support@tenant-a.example.test')),
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(true);
});

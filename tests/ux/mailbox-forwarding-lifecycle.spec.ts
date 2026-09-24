import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string };
type Incident = { id: string; trackingId: string; subject: string; requesterEmail: string;
  organizationId: string; state: number; assignedToId: string | null };
type Receipt = { id: string; reason: string | null; ticketId: string | null; outcome: number | string };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number } | null; receipts: Receipt[] };
type MailMessage = { subject: string };

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

test('published forwarding enforces route, rule, permission and foreign reference boundaries', async ({ page }) => {
  await login(page);
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  const organizationA = ((await (await page.request.get('/api/v1/email-settings/effective')).json()) as
    { id: string; name: string }[]).find(organization => organization.name === 'Tenant A');
  expect(organizationA).toBeDefined();
  const createB = await page.request.post('/api/v1/organizations/', {
    headers, data: { name: 'Tenant B', dnsName: 'tenant-b.example.test' }
  });
  expect(createB.status(), await createB.text()).toBe(201);
  const organizationB = await createB.json() as { id: string };

  const account = await page.request.post('/api/v1/local-auth/users', {
    headers, data: { displayName: 'Fixture forwarded support user', email: 'requester@tenant-a.example.test',
      role: 'User', organizationId: organizationA!.id }
  });
  expect(account.status(), await account.text()).toBe(201);
  const forwarder = await account.json() as { userId: string };
  const access = await page.request.put(`/api/v1/local-auth/users/${forwarder.userId}/assignments`, {
    headers, data: { assignments: [
      { roleKey: 'SelfServiceUser', organizationId: organizationA!.id },
      { roleKey: 'Technician', organizationId: organizationB.id }
    ] }
  });
  expect(access.status(), await access.text()).toBe(204);

  const createMailbox = async (address: string, username: string, password: string, port: string,
    scope: number, organizationId: string | null): Promise<Mailbox> => {
    const response = await page.request.post('/api/v1/email-settings/', {
      headers, data: { displayName: `Fixture ${address}`, provider: 1, authentication: 1,
        scope, organizationId, mailHost: 'localhost', port: Number(port), tlsMode: 0,
        mailboxAddress: address, username, password, mailboxFolder: 'inbox',
        enabled: true, backgroundSyncEnabled: true, initialImport: 0, markReadAfterSuccess: true }
    });
    expect(response.status(), `${address}: ${await response.text()}`).toBe(201);
    return await response.json() as Mailbox;
  };
  const saveOutgoing = async (mailbox: Mailbox, address: string, password: string, port: string): Promise<void> => {
    const response = await page.request.put(`/api/v1/email-settings/${mailbox.id}/outgoing`, {
      headers, data: { version: 0, enabled: true, transport: 0, displayName: 'Fixture support',
        smtpHost: 'localhost', smtpPort: Number(port), smtpTlsMode: 0,
        smtpUsername: address, smtpPassword: password, clearSmtpPassword: false }
    });
    expect(response.ok(), `${address}: ${await response.text()}`).toBe(true);
  };
  const diagnostics = async (mailbox: Mailbox): Promise<Diagnostics> => {
    const response = await page.request.get(`/api/v1/email-settings/${mailbox.id}/diagnostics`);
    expect(response.ok()).toBe(true);
    return await response.json() as Diagnostics;
  };
  const sync = async (mailbox: Mailbox): Promise<void> => {
    const response = await page.request.post(`/api/v1/email-settings/${mailbox.id}/sync`, { headers });
    expect(response.ok(), await response.text()).toBe(true);
    const command = await response.json() as { requestVersion: number };
    await expect.poll(async () => (await diagnostics(mailbox)).state?.syncCompletedVersion,
      { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(command.requestVersion);
  };
  const incidents = async (organizationId = organizationB.id): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${organizationId}`);
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  const rule = async (mailbox: Mailbox): Promise<{ id: string }> => {
    const response = await page.request.post('/api/v1/inbound-email-rules/', {
      headers, data: { scopeType: 1, tenantId: organizationB.id, mailboxId: mailbox.id,
        name: `Fixture forwarding ${mailbox.id}`, enabled: true, priority: 0, stopProcessing: true,
        conditions: [{ type: 0 }, { type: 2 }, { type: 3 }], actions: [{ type: 0 }] }
    });
    expect(response.status(), await response.text()).toBe(201);
    return await response.json() as { id: string };
  };
  const sendForward = (recipient: 'global' | 'dedicated-b', subject: string): void => {
    fixture('send', ['--sender', 'requester', '--recipient', recipient, '--subject', `Fwd: ${subject}`,
      '--body', `Please handle this for Tenant B.\n\n-----Original Message-----\nFrom: Requester B <requester@tenant-b.example.test>\nTo: ${recipient === 'global' ? 'global' : 'dedicated-b'}@tenant-b.example.test\nSubject: ${subject}\n\nForwarded request body`]);
  };
  const expectForwardedIncident = async (mailbox: Mailbox, subject: string): Promise<Incident> => {
    await sync(mailbox);
    await expect.poll(async () => (await incidents()).filter(incident => incident.subject === subject).length,
      { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
    const incident = (await incidents()).find(candidate => candidate.subject === subject)!;
    expect(incident.requesterEmail).toBe('requester@tenant-b.example.test');
    expect(incident.organizationId).toBe(organizationB.id);
    expect(incident.state).toBe(0);
    expect(incident.assignedToId).toBeNull();
    expect((await diagnostics(mailbox)).receipts.some(receipt => receipt.ticketId === incident.id)).toBe(true);
    return incident;
  };
  const expectHeldWithoutIncident = async (mailbox: Mailbox, subject: string,
    expectedReason: string): Promise<void> => {
    const previous = new Set((await diagnostics(mailbox)).receipts.map(receipt => receipt.id));
    sendForward(mailbox.id === globalMailbox.id ? 'global' : 'dedicated-b', subject);
    await sync(mailbox);
    const newReceipts = (await diagnostics(mailbox)).receipts.filter(receipt => !previous.has(receipt.id));
    expect(newReceipts).toHaveLength(1);
    expect(newReceipts[0].ticketId).toBeNull();
    expect(newReceipts[0].reason).toBe(expectedReason);
    expect((await incidents()).some(incident => incident.subject === subject)).toBe(false);
  };

  const dedicatedA = await createMailbox('support@tenant-a.example.test', 'support@tenant-a.example.test',
    'synthetic-mail-password', process.env.MAILBOX_FIXTURE_IMAPS_PORT!, 1, organizationA!.id);
  const globalMailbox = await createMailbox('global@tenant-b.example.test', 'global@tenant-b.example.test',
    'synthetic-global-password', process.env.MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT!, 0, null);
  await saveOutgoing(globalMailbox, 'global@tenant-b.example.test', 'synthetic-global-password',
    process.env.MAILBOX_FIXTURE_GLOBAL_SMTPS_PORT!);
  const start = await page.request.post('/api/v1/email-settings/worker', {
    headers, data: { running: true, confirmed: true }
  });
  expect(start.ok(), await start.text()).toBe(true);
  for (const mailbox of [dedicatedA, globalMailbox])
    await expect.poll(async () => (await diagnostics(mailbox)).state?.initialized,
      { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  await rule(globalMailbox);
  const globalSubject = 'Fixture authorized global forward';
  sendForward('global', globalSubject);
  const globalIncident = await expectForwardedIncident(globalMailbox, globalSubject);

  const dedicatedB = await createMailbox('dedicated-b@tenant-b.example.test',
    'dedicated-b@tenant-b.example.test', 'synthetic-dedicated-b-password',
    process.env.MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT!, 1, organizationB.id);
  await saveOutgoing(dedicatedB, 'dedicated-b@tenant-b.example.test', 'synthetic-dedicated-b-password',
    process.env.MAILBOX_FIXTURE_GLOBAL_SMTPS_PORT!);
  await expect.poll(async () => (await diagnostics(dedicatedB)).state?.initialized,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  const dedicatedRule = await rule(dedicatedB);
  const dedicatedSubject = 'Fixture authorized dedicated forward';
  sendForward('dedicated-b', dedicatedSubject);
  const dedicatedIncident = await expectForwardedIncident(dedicatedB, dedicatedSubject);

  await expectHeldWithoutIncident(globalMailbox, 'Fixture wrong source forward', 'TenantUsesDedicatedMailbox');
  const tenantASubject = 'Fixture Tenant A foreign reference seed';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', tenantASubject,
    '--body', 'Create a Tenant A incident for the foreign reference check.']);
  await sync(dedicatedA);
  await expect.poll(async () => (await incidents(organizationA!.id)).filter(incident =>
    incident.subject === tenantASubject).length,
  { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const tenantAIncident = (await incidents(organizationA!.id)).find(incident =>
    incident.subject === tenantASubject)!;
  await expectHeldWithoutIncident(dedicatedB,
    `Fixture foreign reference ${tenantAIncident.trackingId}`, 'CrossTenantReference');

  const disable = await page.request.post(`/api/v1/inbound-email-rules/${dedicatedRule.id}/disable`, { headers });
  expect(disable.ok(), await disable.text()).toBe(true);
  await expectHeldWithoutIncident(dedicatedB, 'Fixture disabled rule forward', 'RequesterOwnershipConflict');
  const enable = await page.request.post(`/api/v1/inbound-email-rules/${dedicatedRule.id}/enable`, { headers });
  expect(enable.ok(), await enable.text()).toBe(true);
  const revoke = await page.request.put(`/api/v1/local-auth/users/${forwarder.userId}/assignments`, {
    headers, data: { assignments: [{ roleKey: 'SelfServiceUser', organizationId: organizationA!.id }] }
  });
  expect(revoke.status(), await revoke.text()).toBe(204);
  await expectHeldWithoutIncident(dedicatedB, 'Fixture denied forwarder', 'RequesterOwnershipConflict');

  // Both sending configurations were enabled. Forwarding must still suppress
  // the ordinary requester confirmation for either created incident.
  const requesterMail = fixture('messages', ['--account', 'requester-b']) as MailMessage[];
  expect(requesterMail.some(message => [globalIncident, dedicatedIncident].some(incident =>
    message.subject.includes(incident.trackingId)))).toBe(false);
});

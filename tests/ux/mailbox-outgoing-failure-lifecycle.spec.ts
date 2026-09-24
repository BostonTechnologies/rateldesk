import { execFileSync } from 'node:child_process';
import { createConnection, createServer, type Socket } from 'node:net';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string };
type Incident = { id: string; trackingId: string; subject: string; customerId: string };
type MailMessage = { subject: string; from: string };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number; errorCode: string | null } | null;
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

let stopGlobalImapProxy: (() => Promise<void>) | undefined;
test.afterEach(async () => {
  await stopGlobalImapProxy?.();
  stopGlobalImapProxy = undefined;
});

async function globalImapProxy(): Promise<number> {
  const sockets = new Set<Socket>();
  const server = createServer(client => {
    const upstream = createConnection(Number(process.env.MAILBOX_FIXTURE_GLOBAL_IMAPS_PORT), '127.0.0.1');
    sockets.add(client);
    sockets.add(upstream);
    client.on('close', () => sockets.delete(client));
    upstream.on('close', () => sockets.delete(upstream));
    client.on('error', () => upstream.destroy());
    upstream.on('error', () => client.destroy());
    client.pipe(upstream);
    upstream.pipe(client);
  });
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  stopGlobalImapProxy = async () => {
    if (!server.listening) return;
    for (const socket of sockets) socket.destroy();
    await new Promise<void>(resolve => server.close(() => resolve()));
  };
  const address = server.address();
  if (address === null || typeof address === 'string') throw new Error('Global IMAP proxy did not bind a port.');
  return address.port;
}

test('published incoming and outgoing outages isolate mailboxes and repair one delivery', async ({ page }) => {
  await login(page);
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  const tenant = ((await (await page.request.get('/api/v1/email-settings/effective')).json()) as
    { id: string; name: string }[]).find(organization => organization.name === 'Tenant A');
  expect(tenant).toBeDefined();
  const tenantBResponse = await page.request.post('/api/v1/organizations/', {
    headers, data: { name: 'Tenant B', dnsName: 'tenant-b.example.test' }
  });
  expect(tenantBResponse.status(), await tenantBResponse.text()).toBe(201);
  const tenantB = await tenantBResponse.json() as { id: string };
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
  const globalImapPort = await globalImapProxy();
  const global = await create('Fixture global sender', 'global@tenant-b.example.test',
    'synthetic-global-password', String(globalImapPort), 0, null, true);
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
  const globalDiagnostics = async (): Promise<Diagnostics> =>
    await (await page.request.get(`/api/v1/email-settings/${global.id}/diagnostics`)).json() as Diagnostics;
  await expect.poll(async () => (await diagnostics()).state?.initialized,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  await expect.poll(async () => (await globalDiagnostics()).state?.initialized,
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

  const globalSubject = 'Fixture healthy global sender during dedicated failure';
  fixture('send', ['--sender', 'requester-b', '--recipient', 'global', '--subject', globalSubject,
    '--body', 'The global mailbox must keep receiving and sending while dedicated SMTP authentication fails.']);
  const globalSync = await page.request.post(`/api/v1/email-settings/${global.id}/sync`, { headers });
  expect(globalSync.ok(), await globalSync.text()).toBe(true);
  const globalCommand = await globalSync.json() as { requestVersion: number };
  await expect.poll(async () => (await globalDiagnostics()).state?.syncCompletedVersion,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(globalCommand.requestVersion);
  const globalIncidents = async (): Promise<Incident[]> => {
    const response = await page.request.get(`/api/v1/incidents?pageSize=50&organizationId=${tenantB.id}`);
    expect(response.ok(), await response.text()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  await expect.poll(async () => (await globalIncidents()).filter(item => item.subject === globalSubject).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const globalIncident = (await globalIncidents()).find(item => item.subject === globalSubject)!;
  await expect.poll(() => (fixture('messages', ['--account', 'requester-b']) as MailMessage[])
    .filter(message => message.subject.includes(globalIncident.trackingId) &&
      message.from.includes('global@tenant-b.example.test')).length,
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(1);
  expect((await globalDiagnostics()).receipts.filter(receipt => receipt.ticketId === globalIncident.id)).toHaveLength(1);
  expect((await failed()).filter(item => item.ticketId === incident.id)).toHaveLength(1);

  await stopGlobalImapProxy!();
  const unavailable = await page.request.post(`/api/v1/email-settings/${global.id}/sync`, { headers });
  expect(unavailable.ok(), await unavailable.text()).toBe(true);
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v1/email-settings/${global.id}/diagnostics`);
    return (await response.json() as Diagnostics).state?.errorCode;
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeTruthy();

  const afterOutageSubject = 'Fixture dedicated progress during global IMAP outage';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', afterOutageSubject,
    '--body', 'Dedicated incoming must progress while global IMAP cannot be reached.']);
  const dedicatedSync = await page.request.post(`/api/v1/email-settings/${dedicated.id}/sync`, { headers });
  expect(dedicatedSync.ok(), await dedicatedSync.text()).toBe(true);
  const dedicatedCommand = await dedicatedSync.json() as { requestVersion: number };
  await expect.poll(async () => (await diagnostics()).state?.syncCompletedVersion,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(dedicatedCommand.requestVersion);
  await expect.poll(async () => (await incidents()).filter(item => item.subject === afterOutageSubject).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const afterOutageIncident = (await incidents()).find(item => item.subject === afterOutageSubject)!;
  expect((await diagnostics()).receipts.filter(receipt => receipt.ticketId === afterOutageIncident.id)).toHaveLength(1);
  expect(requesterMail().filter(message => message.subject.includes(afterOutageIncident.trackingId))).toHaveLength(0);

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

  await saveOutgoing(dedicated, 'support@tenant-a.example.test',
    'synthetic-wrong-password', process.env.MAILBOX_FIXTURE_SMTPS_PORT!, repaired.version);
  const beforeRevertSubject = 'Fixture dedicated delivery held across revert';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', beforeRevertSubject,
    '--body', 'A failed dedicated delivery must not silently move to the global sender.']);
  const beforeRevertSync = await page.request.post(`/api/v1/email-settings/${dedicated.id}/sync`, { headers });
  expect(beforeRevertSync.ok(), await beforeRevertSync.text()).toBe(true);
  const beforeRevertCommand = await beforeRevertSync.json() as { requestVersion: number };
  await expect.poll(async () => (await diagnostics()).state?.syncCompletedVersion,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(beforeRevertCommand.requestVersion);
  const beforeRevertIncident = (await incidents()).find(item => item.subject === beforeRevertSubject)!;
  expect(beforeRevertIncident).toBeDefined();
  await expect.poll(async () => (await failed()).filter(item => item.ticketId === beforeRevertIncident.id).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(1);
  const heldDelivery = (await failed()).find(item => item.ticketId === beforeRevertIncident.id)!;
  expect(heldDelivery.deliveryErrorCode).toBe('SmtpAuthenticationFailed');

  const savedMailbox = await (await page.request.get(`/api/v1/email-settings/${dedicated.id}`)).json() as
    { version: number };
  const archive = await page.request.post(`/api/v1/email-settings/${dedicated.id}/archive`, {
    headers, data: { version: savedMailbox.version, confirmed: true }
  });
  expect(archive.ok(), await archive.text()).toBe(true);
  const heldPreview = await page.request.get(`/api/v1/timeline/${heldDelivery.id}/outgoing-retry-preview`);
  expect(heldPreview.ok()).toBe(true);
  expect(await heldPreview.json() as { canRetry: boolean; status: string })
    .toMatchObject({ canRetry: false, status: 'SenderRouteChanged' });
  const blockedRetry = await page.request.post(`/api/v1/timeline/${heldDelivery.id}/retry-current-outgoing`, {
    headers, data: { confirmed: true, expectedOutgoingVersion: repaired.version }
  });
  expect(blockedRetry.status(), await blockedRetry.text()).toBe(409);
  expect(requesterMail().filter(message => message.subject.includes(beforeRevertIncident.trackingId))).toHaveLength(0);

  const routeResponse = await page.request.get(`/api/v1/timeline/${heldDelivery.id}/changed-route-preview`);
  expect(routeResponse.ok(), await routeResponse.text()).toBe(true);
  const route = await routeResponse.json() as {
    canRetry: boolean; originalMailboxId: string; currentMailboxId: string;
    originalMailboxAddress: string; currentMailboxAddress: string;
    fence: number; currentMailboxVersion: number; currentOutgoingVersion: number;
  };
  expect(route.canRetry, JSON.stringify(route)).toBe(true);
  expect(route.originalMailboxId).toBe(dedicated.id);
  expect(route.currentMailboxId).toBe(global.id);
  expect(route.originalMailboxAddress).toBe('support@tenant-a.example.test');
  expect(route.currentMailboxAddress).toBe('global@tenant-b.example.test');
  await page.goto('/admin/pending-emails');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('row').filter({ hasText: beforeRevertIncident.id })
    .getByRole('button', { name: 'Review sender and retry' }).click();
  await expect(page.getByText('Delivery queued with the confirmed current sender.')).toBeVisible();
  await expect.poll(() => requesterMail().filter(message => message.subject.includes(beforeRevertIncident.trackingId) &&
    message.from.includes('global@tenant-b.example.test')).length,
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(1);
  expect((await failed()).filter(item => item.ticketId === beforeRevertIncident.id)).toHaveLength(0);

  const manual = await page.request.post('/api/v1/incidents', { headers, data: {
    title: 'Fixture new incident after explicit revert', description: '<p>Global route after revert</p>',
    priority: 0, customerId: incident.customerId, organizationId: tenant!.id
  } });
  expect(manual.status(), await manual.text()).toBe(201);
  const afterRevertIncident = await manual.json() as Incident;
  await expect.poll(() => requesterMail().filter(message => message.subject.includes(afterRevertIncident.trackingId) &&
    message.from.includes('global@tenant-b.example.test')).length,
  { timeout: 60_000, intervals: [1_000, 2_000] }).toBe(1);
  expect(requesterMail().filter(message => message.subject.includes(beforeRevertIncident.trackingId) &&
    message.from.includes('global@tenant-b.example.test'))).toHaveLength(1);
});

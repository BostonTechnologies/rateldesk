import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

type Mailbox = { id: string };
type Incident = { id: string; trackingId: string; subject: string };
type Receipt = { ticketId: string | null };
type Diagnostics = { state: { initialized: boolean; syncCompletedVersion: number } | null; receipts: Receipt[] };
type Message = { subject: string; from: string; replyTo: string };

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

test('published replicas preserve one mailbox owner and send after primary API stops', async ({ page }) => {
  const replicaWeb = process.env.MAILBOX_FIXTURE_REPLICA_WEB_URL;
  const primaryApiUrl = process.env.MAILBOX_FIXTURE_PRIMARY_API_URL;
  const primaryApiPid = Number(process.env.MAILBOX_FIXTURE_PRIMARY_API_PID);
  expect(replicaWeb).toMatch(/^https:\/\/127\.0\.0\.1:\d+$/);
  expect(primaryApiUrl).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);
  expect(Number.isSafeInteger(primaryApiPid) && primaryApiPid > 0).toBe(true);
  await login(page);
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  const tenant = ((await (await page.request.get('/api/v1/email-settings/effective')).json()) as
    { id: string; name: string }[]).find(organization => organization.name === 'Tenant A');
  expect(tenant).toBeDefined();

  const create = await page.request.post('/api/v1/email-settings/', { headers, data: {
    displayName: 'Replica fixture mailbox', provider: 1, authentication: 1,
    scope: 1, organizationId: tenant!.id, mailHost: 'localhost',
    port: Number(process.env.MAILBOX_FIXTURE_IMAPS_PORT), tlsMode: 0,
    mailboxAddress: 'support@tenant-a.example.test', username: 'support@tenant-a.example.test',
    password: 'synthetic-mail-password', mailboxFolder: 'inbox', enabled: true,
    backgroundSyncEnabled: true, initialImport: 0, markReadAfterSuccess: true
  } });
  expect(create.status(), await create.text()).toBe(201);
  const mailbox = await create.json() as Mailbox;
  const outgoing = await page.request.put(`/api/v1/email-settings/${mailbox.id}/outgoing`, { headers, data: {
    version: 0, enabled: true, transport: 0, displayName: 'Replica fixture support',
    smtpHost: 'localhost', smtpPort: Number(process.env.MAILBOX_FIXTURE_SMTPS_PORT),
    smtpTlsMode: 0, smtpUsername: 'support@tenant-a.example.test',
    smtpPassword: 'synthetic-mail-password', clearSmtpPassword: false
  } });
  expect(outgoing.ok(), await outgoing.text()).toBe(true);
  const worker = await page.request.post('/api/v1/email-settings/worker', {
    headers, data: { running: true, confirmed: true }
  });
  expect(worker.ok(), await worker.text()).toBe(true);

  const diagnostics = async (base = ''): Promise<Diagnostics> => {
    const response = await page.request.get(`${base}/api/v1/email-settings/${mailbox.id}/diagnostics`);
    expect(response.ok(), await response.text()).toBe(true);
    return await response.json() as Diagnostics;
  };
  await expect.poll(async () => (await diagnostics(replicaWeb)).state?.initialized,
    { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true);
  const incidents = async (): Promise<Incident[]> => {
    const response = await page.request.get(`${replicaWeb}/api/v1/incidents?pageSize=50&organizationId=${tenant!.id}`);
    expect(response.ok(), await response.text()).toBe(true);
    return ((await response.json()) as { items: Incident[] }).items;
  };
  const sync = async (base = ''): Promise<number> => {
    const response = await page.request.post(`${base}/api/v1/email-settings/${mailbox.id}/sync`, { headers });
    expect(response.ok(), await response.text()).toBe(true);
    return (await response.json() as { requestVersion: number }).requestVersion;
  };
  const verify = async (subjects: string[], version: number): Promise<void> => {
    await expect.poll(async () => (await diagnostics(replicaWeb)).state?.syncCompletedVersion,
      { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBeGreaterThanOrEqual(version);
    await expect.poll(async () => (await incidents()).filter(incident =>
      subjects.includes(incident.subject)).length,
    { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(subjects.length);
    const created = (await incidents()).filter(incident => subjects.includes(incident.subject));
    for (const subject of subjects) expect(created.filter(incident => incident.subject === subject)).toHaveLength(1);
    const receipts = (await diagnostics(replicaWeb)).receipts;
    for (const incident of created)
      expect(receipts.filter(receipt => receipt.ticketId === incident.id)).toHaveLength(1);
    await expect.poll(() => {
      const messages = fixture('messages', ['--account', 'requester']) as Message[];
      return created.every(incident => messages.filter(message => message.subject.includes(incident.trackingId) &&
        message.from.includes('support@tenant-a.example.test') &&
        message.replyTo.includes('support@tenant-a.example.test')).length === 1);
    }, { timeout: 30_000, intervals: [1_000, 2_000] }).toBe(true);
  };

  const first = ['Fixture replica concurrent one', 'Fixture replica concurrent two'];
  for (const subject of first)
    fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', subject,
      '--body', 'Both published API workers share one durable source owner.']);
  const requests = await Promise.all([sync(), sync(replicaWeb)]);
  await verify(first, Math.max(...requests));

  process.kill(primaryApiPid, 'SIGTERM');
  await expect.poll(async () => {
    try { return (await page.request.get(`${primaryApiUrl}/health/ready`, { timeout: 1_000 })).ok(); }
    catch { return false; }
  }, { timeout: 20_000, intervals: [500, 1_000] }).toBe(false);
  const failoverSubject = 'Fixture replica failover message';
  fixture('send', ['--sender', 'requester', '--recipient', 'support', '--subject', failoverSubject,
    '--body', 'The remaining published API worker processes this after primary shutdown.']);
  await verify([...first, failoverSubject], await sync(replicaWeb));
});

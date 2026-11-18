import { randomBytes } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name} for disposable native acceptance`);
  return value;
}

type ChatEvent = { type: string; sequence: number; text: string; clientMessageId?: string; metadataJson?: string; createdUtc: string };
type ChatSnapshot = { conversationId: string; state: number; events: ChatEvent[]; lastSequence: number };
type Completion = { OperatorMessageId: string; TurnNumber: number; SourceReminderId?: string; ScheduledReports?: { Id: string }[] };
const metadata = (event: ChatEvent): Completion => JSON.parse(event.metadataJson!);
const login = async (page: Page) => {
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Email' }).fill(required('HELPDESK_E2E_LOCAL_EMAIL'));
  await page.getByLabel('Password').fill(required('HELPDESK_E2E_LOCAL_PASSWORD'));
  await page.getByRole('button', { name: /^Sign in$/ }).click();
  await expect(page.getByRole('heading', { name: 'Operations Overview' })).toBeVisible();
};

async function nativeAudit(logs: string, sessionId: string) {
  const id = sessionId.split('/').at(-1)!;
  const paths = await readdir(logs, { recursive: true });
  const match = paths.find(path => basename(path) === 'session.log' && path.includes(id));
  return match ? readFile(join(logs, match), 'utf8') : '';
}

for (const [scope, mode] of [['organization', 'post-parent'], ['ticket', 'buffered']] as const) {
  test(`${scope} chat saves the real NetClaw ${mode} report once and reloads Ready`, async ({ page, request }, testInfo) => {
    const marker = randomBytes(6).toString('hex');
    const finalMarker = `RD-SCHEDULED-FINAL-${marker}`;
    const reminderId = `rd-smoke-${marker}`;
    const daemonLog = required('NETCLAW_SMOKE_DAEMON_LOG');
    const nativeLogs = required('NETCLAW_SMOKE_SESSION_LOGS');
    const model = new URL(required('NETCLAW_SMOKE_MODEL_URL'));
    expect(model.host).toMatch(/^127\.0\.0\.1:\d+$/);
    expect(model.protocol).toBe('http:');
    const appVersion = required('RATELDESK_SMOKE_VERSION');
    const nativeVersion = required('NETCLAW_SMOKE_VERSION');
    const readJson = async (url: string) => {
      const response = await page.request.get(url);
      expect(response.status(), url).toBe(200);
      return response.json();
    };
    await login(page);
    const organizations = await readJson('/api/v1/ai/chat/organizations') as { id: string; name: string }[];
    const organizationId = organizations.find(row => row.name === 'Fixture Organization')?.id;
    expect(organizationId).toBeTruthy();
    const ticketId = scope === 'ticket' ? required('HELPDESK_E2E_SCHEDULED_TICKET') : '';
    const webPath = scope === 'organization' ? `/ai/chat?organizationId=${organizationId}` : `/incidents/${ticketId}`;
    const apiPath = scope === 'organization'
      ? `/api/v1/ai/chat/organizations/${organizationId}` : `/api/v1/incidents/${ticketId}/ai-assistant/chat`;
    const openTicketTab = () => page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
    await page.goto(webPath);
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    if (scope === 'ticket') await openTicketTab();
    const input = page.getByRole('textbox', { name: 'Message Netclaw' });
    await expect(input).toBeEnabled();
    await input.fill(`RD-SCHEDULE-SMOKE ${mode} ${marker}. Load scheduling if needed and register one current_session one-shot reminder after 30 seconds. Its only action is to return a harmless marker. Do not access records or perform other actions.`);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    if (scope === 'organization') await expect(page).toHaveURL(/conversationId=[a-f0-9-]+/i);
    const savedUrl = page.url();
    const id = new URL(savedUrl).searchParams.get('conversationId') ?? (await readJson(apiPath)).conversationId;
    const snapshot = async () => await readJson(`${apiPath}?conversationId=${id}`) as ChatSnapshot;
    const dispatchAcknowledgements = async () => (await readFile(daemonLog, 'utf8')).split('\n')
      .filter(line => line.includes('reminder_current_session_dispatch_acked') && line.includes(reminderId));
    if (mode === 'buffered') {
      await expect.poll(async () => (await dispatchAcknowledgements()).length, { timeout: 47_000 }).toBe(1);
      expect((await snapshot()).events.filter(event => event.type === 'turn_completed')).toHaveLength(0);
      await expect(await request.post(new URL(`/fixture/release-parent/${marker}`, model).href)).toBeOK();
    }

    const surface = page.getByRole('region', { name: 'Conversation', exact: true });
    await expect(page.getByTestId('chat-observation-status')).toContainText('Waiting for scheduled reports');
    await expect(surface).toHaveAttribute('data-pending-scheduled-reports', '1');
    await expect(input).toBeDisabled();
    const pending = await snapshot();
    expect(pending.state).toBe(6);
    const operator = pending.events.filter(event => event.type === 'operator');
    expect(operator).toHaveLength(1);
    expect(operator[0].clientMessageId).toMatch(/^[a-f0-9-]+$/i);
    const parent = pending.events.filter(event => event.type === 'turn_completed');
    expect(parent).toHaveLength(1);
    expect(metadata(parent[0]).ScheduledReports?.map(report => report.Id)).toEqual([reminderId]);
    if (mode === 'post-parent') expect(await dispatchAcknowledgements()).toHaveLength(0);
    const observed = await readJson(`${apiPath}/connection?conversationId=${id}`);
    const sessionId = observed.sessions.find((session: { conversationId: string }) => session.conversationId === id)?.sessionId;
    expect(sessionId).toMatch(/^signalr\/[a-f0-9]{32}$/);
    if (scope === 'ticket') {
      await page.goto('/home');
      await page.goto(savedUrl);
      await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
      await openTicketTab();
      await expect(surface).toHaveAttribute('data-pending-scheduled-reports', '1');
    }
    await page.reload();
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    if (scope === 'ticket') await openTicketTab();
    await expect(surface).toHaveAttribute('data-pending-scheduled-reports', '1');
    await page.screenshot({ path: testInfo.outputPath('01-pending.png') });
    await expect.poll(async () => (await dispatchAcknowledgements()).length, { timeout: 42_000 }).toBe(1);
    const [dispatch] = await dispatchAcknowledgements();
    expect(dispatch).toContain(sessionId);
    await expect(await request.post(new URL(`/fixture/release-report/${marker}`, model).href)).toBeOK();

    const reply = page.getByTestId('ai-assistant-response').filter({ hasText: finalMarker });
    await expect(reply).toHaveCount(1, { timeout: 20_000 });
    await expect(surface).toHaveAttribute('data-pending-scheduled-reports', '0');
    await expect(page.getByTestId('chat-observation-status')).toHaveCount(0);
    await expect(surface.locator('.conversation-identity small')).toContainText('Ready');
    await expect(input).toBeEnabled();
    const ready = await snapshot();
    expect(ready.state).toBe(0);
    const reports = ready.events.filter(event => event.type === 'assistant' && event.text === finalMarker);
    expect(reports).toHaveLength(1);
    const terminals = ready.events.filter(event => event.type === 'turn_completed');
    expect(terminals).toHaveLength(2);
    const final = metadata(terminals[1]);
    expect(final.SourceReminderId).toMatch(new RegExp(`^${reminderId}:\\d+$`));
    expect(final.TurnNumber).toBeGreaterThan(metadata(parent[0]).TurnNumber);
    expect(final.ScheduledReports).toEqual([]);
    expect(final.OperatorMessageId).toBe(operator[0].clientMessageId);
    const replyBinding = JSON.parse(reports[0].metadataJson!);
    expect([replyBinding.OperatorMessageId, replyBinding.NativeTurnNumber, replyBinding.SourceReminderId])
      .toEqual([final.OperatorMessageId, final.TurnNumber, final.SourceReminderId]);
    expect(reports[0].sequence + 1).toBe(terminals[1].sequence);
    await expect.poll(async () => await nativeAudit(nativeLogs, sessionId)).toContain(`Assistant: ${finalMarker}`);
    const native = (await nativeAudit(nativeLogs, sessionId)).split('\n');
    const nativeFinal = native.filter(line => line.endsWith(`Assistant: ${finalMarker}`));
    const nativeTerminal = native.filter(line => line.endsWith(`Turn ${final.TurnNumber} completed`));
    const nativeInput = native.filter(line => line.includes(`User: [helpdesk-chat:${id}:${final.OperatorMessageId}]`));
    expect(nativeFinal).toHaveLength(1);
    expect(nativeTerminal).toHaveLength(1);
    expect(nativeInput).toHaveLength(1);
    expect(native.some(line => line.includes('turn_received channel=signalr sender=reminder-system'))).toBe(true);

    await page.reload();
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
    if (scope === 'ticket') await openTicketTab();
    await expect(reply).toHaveCount(1);
    await expect(input).toBeEnabled();
    await expect(surface.locator('.conversation-identity small')).toContainText('Ready');
    await expect(surface).toHaveAttribute('data-pending-scheduled-reports', '0');
    expect((await snapshot()).events.filter(event => event.type === 'operator')).toEqual(operator);
    await page.screenshot({ path: testInfo.outputPath('02-ready-reload.png') });
    const receipt = {
      scope, mode, appVersion, nativeVersion, organizationId, conversationId: id, sessionId, marker, reminderId,
      operator: { id: final.OperatorMessageId, sequence: operator[0].sequence, acceptedUtc: operator[0].createdUtc },
      parent: { nativeTurn: metadata(parent[0]).TurnNumber, sequence: parent[0].sequence, savedUtc: parent[0].createdUtc, pending: 1 },
      dispatch, nativeFinal: nativeFinal[0], nativeTerminal: nativeTerminal[0],
      final: { fireKey: final.SourceReminderId, replySequence: reports[0].sequence, replyUtc: reports[0].createdUtc,
        terminalSequence: terminals[1].sequence, terminalUtc: terminals[1].createdUtc, pending: 0, state: 'Ready' },
      readyAfterReload: true, composerEnabledAfterReload: true, operatorAdmissions: 1, nativeAdmissions: 1
    };
    await testInfo.attach('receipt', { body: JSON.stringify(receipt, null, 2), contentType: 'application/json' });
    console.log(JSON.stringify(receipt));
  });
}

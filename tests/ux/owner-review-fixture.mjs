// Opt-in synthetic records for table contexts not exercised by the ordinary admin suite.
// This module is served only by the loopback fixture, never by a product host.
let enabled = false;
export function resetTableReview() { enabled = false; }
const mailboxId = '79000000-0000-4000-8000-000000000001';
const guid = index => `79000000-0000-4000-8000-${String(index + 2).padStart(12, '0')}`;
const rows = factory => Array.from({ length: 65 }, (_, index) => factory(index));
export async function handleTableReview(req, res, url) {
  const path = url.pathname.replace(/\/$/, '');
  const reply = body => { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); return true; };
  if (path === '/fixture/table-review') { enabled = true; return reply({ synthetic: true }); }
  if (!enabled) return false;
  if (/^\/api\/v1\/admin\/role-definitions\/[^/]+\/assignments$/.test(path)) {
    const assignments = Array.from({ length: 237 }, (_, i) => ({ userId: `member-${i}`, name: `Synthetic Operator ${String(i).padStart(3, '0')}`, email: `member-${i}@example.test`, organizationId: 'org-1' }));
    const pageSize = Number(url.searchParams.get('pageSize') ?? 25), page = Number(url.searchParams.get('page') ?? 1);
    return reply(url.searchParams.get('paged') === 'true' ? { items: assignments.slice((page - 1) * pageSize, page * pageSize), totalCount: assignments.length, page, pageSize } : assignments.slice(0, 200));
  }
  if (path === '/api/v1/assets') return reply(rows(i => ({ id: `asset-${i}`, name: `Synthetic asset ${String(i).padStart(3, '0')} with a deliberately long descriptive name`, type: 'Workstation', manufacturer: 'Example manufacturer', model: 'Example model', organizationId: 'org-1' })));
  if (path === '/api/v1/email-layouts') return reply(rows(i => ({ id: i + 1, name: `Synthetic layout ${String(i).padStart(3, '0')}`, htmlContent: '<p>{{Content}}</p>', isSystem: i === 0 })));
  if (path === '/api/v1/email-templates') return reply(['Change', 'Incident', 'Request', 'Other'].flatMap((category, index) => rows(i => ({ id: index * 65 + i + 1, name: `${category} synthetic template ${String(i).padStart(3, '0')}`, subject: 'A long synthetic subject for contained table rendering', htmlContent: '<p>Synthetic authored content</p>', isSystem: i === 0 }))));
  if (path === '/api/v1/email-templates/preview') {
    let text = ''; for await (const chunk of req) text += chunk;
    const draft = JSON.parse(text);
    return reply({ subject: draft.draftSubject ?? 'Synthetic preview', html: draft.draftHtmlContent });
  }
  if (path === '/api/v1/automation/rules') return reply(rows(i => ({ id: `rule-${i}`, name: `Synthetic rule ${String(i).padStart(3, '0')}`, trigger: 'TicketCreated', actions: '[]', conditions: '[]' })));
  if (path === '/api/v1/admin/ai-assistant-webhooks') return reply(rows(i => ({ id: guid(i), organizationId: 'org-1', name: `Synthetic webhook ${String(i).padStart(3, '0')}`, endpoint: 'https://automation.example.test/inbound', routeName: 'synthetic', promptTemplate: 'Synthetic preview only', ticketAreas: [0], enabled: false, archived: false })));
  if (path === '/api/v1/tenant-admin/organizations/org-1/users') return reply(rows(i => ({ userId: `member-${i}`, name: `Synthetic member ${String(i).padStart(3, '0')}`, email: `member-${i}@example.test`, roleKeys: ['SelfServiceUser'] })));
  if (path === '/api/v1/tenant-admin/organizations/org-1/users/roles') return reply([{ key: 'SelfServiceUser', name: 'Self-service user' }]);
  if (path === '/api/v1/support/groups/group-1/members') return reply(rows(i => ({ id: `member-${i}`, supportGroupId: 'group-1', userId: `member-${i}`, userName: `Synthetic member ${String(i).padStart(3, '0')}`, userEmail: `member-${i}@example.test`, role: 0, source: 0, isEnabled: true, createdUtc: '2026-11-05T07:00:00Z' })));
  if (path.endsWith('/recipient-preview')) return reply({ customerOrganizationId: 'org-1', eventType: 0, channel: 0, recipients: rows(i => ({ userId: `member-${i}`, userName: `Synthetic recipient ${String(i).padStart(3, '0')}`, email: `member-${i}@example.test`, supportGroupId: 'group-1', supportGroupName: 'Primary support' })) });
  if (path === '/api/v1/email-settings/worker') return reply({ deploymentPermitsIngestion: false, instanceRunning: false, state: 'Stopped', blockedBy: 'Isolated evidence fixture' });
  if (path === `/api/v1/email-settings/${mailboxId}`) return reply({ id: mailboxId, organizationId: 'org-1', name: 'Synthetic archived mailbox', incomingProtocol: 0, enabled: false, archived: true });
  if (path === `/api/v1/email-settings/${mailboxId}/diagnostics`) return reply({ state: { mailboxId, initialized: true, currentStage: 'Completed', hasCheckpoint: true }, effectiveStatus: { mailboxId, state: 'Archived', blockedBy: 'Synthetic source is never processed', baselineComplete: true, capturedCount: 65, skippedInitialCount: 65, heldCount: 1 }, receipts: rows(i => ({ id: guid(i), outcome: i === 0 ? 3 : 1, reason: 'Synthetic retained receipt; no effects execute', organizationId: 'org-1', createdUnixMilliseconds: 1793862000000, attempts: 1, acknowledged: true })) });
  if (path === `/api/v1/email-settings/${mailboxId}/historical/preview`) return reply({ items: rows(i => ({ receiptId: guid(i), sender: `sender-${i}@example.test`, subject: `Synthetic baseline-skipped message ${String(i).padStart(3, '0')} with long subject content`, receivedUnixMilliseconds: 1793862000000, available: i !== 0, errorCode: i === 0 ? 'Source unavailable' : null })).slice(0, 25), hasMore: true, skip: 0 });
  return false;
}

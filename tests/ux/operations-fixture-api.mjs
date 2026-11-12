// Synthetic browser-only fixture. Real API authorization is tested in the owning API suites.
import http from 'node:http';
let requests = [], mutations = [], fault = '', enabled = true, premium = false, manyDiagnostics = false, receipts = new Set();
const streams = new Set();
const uid = n => `56000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const originalTasks = Array.from({ length: 76 }, (_, i) => {
  const type = i % 2 + 1, status = i % 5 + 1;
  const healthyRunning = type === 2 && status === 2 && i % 7 !== 0;
  return { id: `task-${i}`, requestId: `request-${i}`, requestTrackingId: `REQ-${String(i).padStart(4, '0')}`, requestTitle: 'Long synthetic request title for regional workstation and access readiness', name: `Task ${i}: verify preparation with a realistic long operational name`, organizationId: 'org-synthetic', type, status, assignedToId: i % 2 ? 'other' : 'synthetic-operator', assignedToDisplayName: i % 2 ? 'Another operator' : 'Synthetic operator', createdAt: '2026-11-01T12:00:00Z', automationReadyToStart: i % 3 !== 0, automationBindingSyncState: i % 5, automationBlockReason: i % 3 === 0 ? 'Binding needs review' : null, lastAutomationStatus: i % 7 === 0 ? 'ExecutionUnknownManualReconcile' : healthyRunning ? 'running' : null, orchestrationExternalRunId: i % 7 === 0 ? `run-${i}` : null,
    ...(healthyRunning ? { startedAt: new Date(Date.now() - 120_000).toISOString(), lastAutomationUpdatedAt: new Date(Date.now() - 60_000).toISOString() } : {}) };
});
let tasks = structuredClone(originalTasks);
let events = [];
const originalRules = Array.from({ length: 64 }, (_, i) => ({ id: `rule-${i}`, name: `Rule ${i}: synthetic inbound support routing`, description: 'A long synthetic description retained in details', scopeType: i >= 60 ? 1 : 0, tenantId: i >= 60 ? 'org-synthetic' : null, mailboxId: null, priority: i, enabled: i % 2 === 0, stopProcessing: i % 2 === 0, conditions: [], actions: [], createdAtUtc: '2026-11-01T12:00:00Z', updatedAtUtc: '2026-11-01T12:00:00Z' }));
let rules = structuredClone(originalRules);
const audit = Array.from({ length: 305 }, (_, i) => ({ id: `audit-${i}`, messageId: `message-${i}@example.test`, mailboxId: null, tenantId: i % 2 ? 'org-synthetic' : null, ruleId: i % 4 ? `rule-${i % 64}` : 'deleted-rule', actionKey: 'CreateIncidentFromForwardedSupportEmail', matched: true, status: i === 0 ? 2 : 1, ticketId: i % 3 ? `incident-${i}` : null, error: i === 0 ? 'Older synthetic failure outside newest 200' : null, createdAtUtc: new Date(Date.UTC(2026, 10, 1) + i * 60000).toISOString() }));
function newEvent(i) { return { id: uid(i), title: i % 2 ? 'TaskDashboardViewed' : 'UnknownVendorEventWithVeryLongKey', message: JSON.stringify({ eventType: 'TaskDashboardViewed', summary: 'Synthetic operational event with diagnostics retained', exactKey: `raw-${i}` }), source: 'Helpdesk.Application.Tasks, Version=0.1.3, Environment=Testing', category: 'DomainEvent', severity: i % 5, reference: uid(i + 1000), correlationId: uid(i + 2000), createdUtc: new Date(Date.now() - i * 60000).toISOString(), isRead: receipts.has(uid(i)), link: i % 2 ? `/requests/request-${i}` : null }; }
const rolesFor = secret => secret?.endsWith('-sla') ? ['Incident.Manager'] : secret?.endsWith('-workflow') ? ['Request.Read'] : secret?.endsWith('-notifications') ? [] : ['HelpdeskAdmin'];
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { requests = []; mutations = []; rules = structuredClone(originalRules); tasks = structuredClone(originalTasks); fault = ''; enabled = true; premium = false; manyDiagnostics = false; receipts.clear(); return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ requests, mutations, rules, enabled, receipts: [...receipts] });
  if (url.pathname === '/fixture/fault') { fault = url.searchParams.get('category') ?? ''; return reply({ ok: true }); }
  if (url.pathname === '/fixture/many-ops') {
    manyDiagnostics = true;
    rules[0].conditions = [{ type: 1, permissions: ['Incident.Read'] }, { type: 5, permissions: [] }];
    rules[0].actions = [{ type: 0, actionKey: 'synthetic-retained-action-identity-with-long-readable-context' }];
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/premium') { premium = true; events = Array.from({ length: 80 }, (_, i) => newEvent(i + 1)); return reply({ ok: true }); }
  if (url.pathname === '/fixture/arrival') { const event = newEvent(events.length + 1); event.createdUtc = new Date().toISOString(); events.unshift(event); for (const stream of streams) { stream.write(`event: notification\ndata: ${JSON.stringify(event)}\n\n`); if (url.searchParams.has('duplicate')) stream.write(`event: notification\ndata: ${JSON.stringify(event)}\n\n`); } return reply(event); }
  if (url.pathname === '/fixture/disconnect') { for (const stream of streams) stream.end(); return reply({ ok: true }); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', preferred_username: 'Synthetic Operations User With An Exceptionally Long Display Name', roles: rolesFor(req.headers['x-system-secret']), organization_id: 'org-synthetic', allowed_organization_id: 'org-synthetic', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  const token = (req.headers.authorization ?? '').split(' ')[1];
  const roles = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles : [];
  if (url.pathname === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Operations User With An Exceptionally Long Display Name', email: 'Synthetic Operations User With An Exceptionally Long Display Name', customerId: null, userId: 'synthetic-operator', isHelpdeskAdmin: roles.includes('HelpdeskAdmin'), roleBundles: roles, permissions: [], primaryOrganizationId: 'org-synthetic', allowedOrganizationIds: ['org-synthetic'], managedOrganizationIds: [] });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
  if (url.pathname.startsWith('/api/v1/service-items')) return reply([
    { id: 'hardware', name: 'Hardware', description: 'Synthetic device requests', itemType: 0, availableRequestCount: 2 },
    { id: 'access', name: 'Access request', description: 'Synthetic permissions request', itemType: 1 }
  ]);
  if (url.pathname.endsWith('/breadcrumb')) return reply([{ id: 'hardware', name: 'Hardware' }]);
  if (url.pathname === '/api/v1/admin/tenants/lookup') return reply([{ id: 'org-synthetic', name: 'Synthetic tenant' }]);
  if (url.pathname.startsWith('/api/v1/reports/sla')) {
    requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams), roles });
    if (fault === 'sla') return reply({}, 503);
    if (url.pathname.endsWith('/compliance')) return reply({ resolutionCompliancePercent: 75, responseCompliancePercent: 87.5, completedWithinResolutionSla: 6, completedWithinResponseSla: 7, completedTotal: 8 });
    const sparse = { items: [{ ticketId: 'synthetic-ticket', ticketNumber: 'SYN-001', title: 'Synthetic SLA example', ticketType: 0, tenantId: 'org-synthetic', responsePercentUsed: 85, resolutionPercentUsed: 92, responseRemainingSeconds: 1800, resolutionRemainingSeconds: 3600, responseDueAt: '2026-10-15T12:00:00Z', resolutionDueAt: '2026-10-15T13:00:00Z' }], total: url.pathname.endsWith('/breached') ? 3 : 5 };
    if (manyDiagnostics) {
      const rows=Array.from({length:137},(_,index)=>({...sparse.items[0],ticketId:`synthetic-ticket-${index}`,ticketNumber:`SYN-${String(index).padStart(3,'0')}`}));
      const page=Number(url.searchParams.get('page')??1),size=Number(url.searchParams.get('pageSize')??25);
      return reply({items:rows.slice((page-1)*size,page*size),total:rows.length});
    }
    return reply(sparse);
  }
  if (url.pathname.startsWith('/api/v1/ops/tasks')) {
    requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams), roles });
    if (fault && url.pathname.endsWith(fault)) return reply({}, 503);
    const row = url.pathname.endsWith('callback-rejections') ? { createdUtc: '2026-10-15T06:00:00Z', reasonCode: 'Synthetic rejection', callerClientId: 'fixture', correlationId: 'synthetic correlation' }
      : url.pathname.endsWith('binding-issues') ? { serviceName: 'Synthetic service', requestFormTitle: 'Synthetic form', taskName: 'Synthetic binding', enabled: false, syncState: 0, orchestrationRequestDefinitionName: 'Synthetic target' }
      : { taskId: 'synthetic-task', requestId: 'synthetic-request', requestTrackingId: 'SYN-002', taskName: 'Synthetic task', assignedToDisplayName: 'Synthetic assignee', dueAt: '2026-10-15T06:00:00Z', nextRetryAt: '2026-10-15T08:00:00Z', retryCount: 2, failurePolicy: 'Retry', failureReason: 'Synthetic failure', status: 1 };
    if (manyDiagnostics) {
      const rows = Array.from({length:137},(_,index)=>({...row, taskId:`synthetic-task-${index}`, taskName:`Synthetic task ${String(index).padStart(3,'0')}`, correlationId:`synthetic-correlation-${index}`}));
      const page=Number(url.searchParams.get('page')??1), size=Number(url.searchParams.get('pageSize')??25);
      return reply({items:rows.slice((page-1)*size,page*size),totalCount:rows.length,page,pageSize:size});
    }
    return reply({ items: [row], totalCount: 4 });
  }
  if (url.pathname === '/api/v1/ops/hangfire/settings') {
    requests.push({ path: url.pathname, method: req.method, roles });
    if (req.method === 'PUT') { let body = ''; for await (const part of req) body += part; enabled = JSON.parse(body).enabled; }
    return reply({ enabled, queueName: 'default', dashboardPath: '/hangfire', runtimeSource: 'Synthetic fixture', storageProvider: 'Synthetic storage', recurringJobCount: 3, graphDatasetSyncJobCount: 2, updatedAtUtc: '2026-10-15T06:00:00Z', updatedBy: 'Synthetic operator' });
  }
  if (url.pathname.startsWith('/hangfire')) { requests.push({ path: url.pathname, method: req.method }); res.writeHead(200, { 'Content-Type': 'text/html' }); return res.end('<title>Synthetic dashboard</title><p>Background dashboard fixture</p>'); }
  if (premium) {
    if (url.pathname === '/api/v1/ticketing/filter-options') {
      const term = (url.searchParams.get('q') ?? '').toLowerCase();
      const options = [{ id: 'org-synthetic', label: 'Synthetic tenant' }, { id: 'org-other', label: 'Other synthetic tenant' }];
      return reply({ items: options.filter(x => x.label.toLowerCase().includes(term)), hasMore: false });
    }
    const body = async () => { let text = ''; for await (const part of req) text += part; return text ? JSON.parse(text) : {}; };
    if (url.pathname === '/api/v1/request-tasks') {
      requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams), roles });
      if (fault === 'tasks') return reply({}, 503);
      let rows = tasks.filter(x => (url.searchParams.get('assignedToMe') !== 'true' || x.assignedToId === 'synthetic-operator') && (!url.searchParams.has('customerOrgId') || url.searchParams.getAll('customerOrgId').includes(x.organizationId)));
      const totalCount = rows.length, size = Number(url.searchParams.get('pageSize') ?? 25), page = Number(url.searchParams.get('page') ?? 1);
      return reply({ items: rows.slice((page - 1) * size, page * size), totalCount });
    }
    if (url.pathname === '/api/v1/email-settings') return reply([]);
    if (url.pathname === '/api/v1/inbound-email-rules') {
      if (req.method === 'POST') {
        const input = await body(); mutations.push({ path: url.pathname, method: req.method, body: input });
        const copy = { ...input, id: 'rule-copy', createdAtUtc: '2026-11-01T12:00:00Z', updatedAtUtc: '2026-11-01T12:00:00Z' }; rules.push(copy); return reply(copy);
      }
      return reply(rules);
    }
    if (url.pathname === '/api/v1/inbound-email-rules/reorder') {
      const input = await body(); mutations.push({ path: url.pathname, method: req.method, body: input });
      for (const priority of input.rules) rules.find(x => x.id === priority.id).priority = priority.priority;
      return reply(rules);
    }
    const ruleAction = url.pathname.match(/^\/api\/v1\/inbound-email-rules\/([^/]+)(?:\/(enable|disable))?$/);
    if (ruleAction && !['audit'].includes(ruleAction[1])) {
      const rule = rules.find(x => x.id === ruleAction[1]); if (!rule) return reply({}, 404);
      if (req.method !== 'GET') {
        const input = await body(); mutations.push({ path: url.pathname, method: req.method, body: input });
        if (ruleAction[2]) rule.enabled = ruleAction[2] === 'enable'; else Object.assign(rule, input);
      }
      return reply(rule);
    }
    const taskAction = url.pathname.match(/^\/api\/v1\/request-tasks\/([^/]+)\/(start|complete|retry)$/);
    if (taskAction) {
      const input = await body(); mutations.push({ path: url.pathname, method: req.method, body: input });
      // Hold the synthetic response so a second click arrives while the first is pending.
      return setTimeout(() => {
        const ids = taskAction[1] === 'bulk' ? input.ids : [taskAction[1]];
        for (const task of tasks.filter(x => ids.includes(x.id))) task.status = taskAction[2] === 'complete' ? 3 : taskAction[2] === 'start' ? 2 : 1;
        reply({ updated: ids.length });
      }, 300);
    }
    if (url.pathname === '/api/v1/inbound-email-rules/audit') return reply(audit.toReversed().slice(0, 200));
    if (url.pathname === '/api/v1/inbound-email-rules/audit/page') {
      requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams) });
      if (fault === 'audit') return reply({}, 503);
      let rows = audit.filter(x => !url.searchParams.get('status') || String(x.status) === url.searchParams.get('status') || (x.status === 2 ? 'Failed' : 'Succeeded') === url.searchParams.get('status'));
      const text = (url.searchParams.get('search') ?? '').toLowerCase(); rows = rows.filter(x => JSON.stringify(x).toLowerCase().includes(text));
      if (url.searchParams.get('descending')?.toLowerCase() !== 'false') rows = rows.toReversed();
      const total = rows.length, page = Number(url.searchParams.get('page') ?? 0), pageSize = Number(url.searchParams.get('pageSize') ?? 25);
      return reply({ items: rows.slice(page * pageSize, (page + 1) * pageSize).map(event => ({ event, ruleName: event.ruleId === 'deleted-rule' ? 'Unknown rule' : rules.find(x => x.id === event.ruleId)?.name ?? 'Unknown rule', organizationName: event.tenantId ? 'Synthetic tenant' : 'Instance', mailboxName: 'Default mailbox', ticketTrackingId: event.ticketId ? `INC-${event.id}` : null, ticketRoute: event.ticketId ? `/incidents/${event.ticketId}` : null })), total, page, pageSize });
    }
    if (url.pathname === '/api/v1/notifications/stream') { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); res.write(': connected\n\n'); streams.add(res); req.on('close', () => streams.delete(res)); return; }
    if (url.pathname === '/api/v1/notifications/summary') return reply({ totalCount: events.length, unreadCount: events.filter(x => !receipts.has(x.id)).length });
    if (url.pathname === '/api/v1/notifications') {
      requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams) });
      if (fault === 'notifications') return reply({}, 503);
      let rows = events.filter(x => !url.searchParams.get('search') || JSON.stringify(x).toLowerCase().includes(url.searchParams.get('search').toLowerCase()));
      const size = Number(url.searchParams.get('pageSize') ?? 25), page = Number(url.searchParams.get('page') ?? 1);
      return reply({ items: rows.slice((page - 1) * size, page * size).map(x => ({ ...x, isRead: receipts.has(x.id) })), totalCount: rows.length });
    }
    if (url.pathname === '/api/v1/notifications/mark-read' || /\/notifications\/[^/]+\/read$/.test(url.pathname)) {
      if (fault === 'receipts') return reply({}, 503);
      const data = await body(), ids = data.ids ?? [data.notificationId]; let updated = 0;
      for (const id of ids) { if (!receipts.has(id)) updated++; receipts.add(id); }
      return reply({ updated });
    }
    if (url.pathname === '/api/v1/notifications/timeline') return reply(events.filter(x => x.reference === url.searchParams.get('reference')));
  }
  if (url.pathname === '/api/v1/notifications') return reply({ items: [], totalCount: 0 });
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(Number(process.env.HELPDESK_OPERATIONS_FIXTURE_PORT ?? 18400), '127.0.0.1', () => console.log('Operations fixture listening on loopback'));

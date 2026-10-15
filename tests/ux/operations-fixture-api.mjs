// Synthetic browser-only fixture. Real API authorization is tested in the owning API suites.
import http from 'node:http';
let requests = [], fault = '', enabled = true;
const rolesFor = secret => secret?.endsWith('-sla') ? ['Incident.Manager'] : secret?.endsWith('-workflow') ? ['Request.Read'] : secret?.endsWith('-notifications') ? [] : ['HelpdeskAdmin'];
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { requests = []; fault = ''; enabled = true; return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ requests, enabled });
  if (url.pathname === '/fixture/fault') { fault = url.searchParams.get('category') ?? ''; return reply({ ok: true }); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', preferred_username: 'Synthetic Operations User With An Exceptionally Long Display Name', roles: rolesFor(req.headers['x-system-secret']), organization_id: 'org-synthetic', allowed_organization_id: 'org-synthetic', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  const token = (req.headers.authorization ?? '').split(' ')[1];
  const roles = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles : [];
  if (url.pathname === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Operations User With An Exceptionally Long Display Name', userId: 'synthetic-operator', isHelpdeskAdmin: roles.includes('HelpdeskAdmin'), roleBundles: roles, permissions: [], primaryOrganizationId: 'org-synthetic', allowedOrganizationIds: ['org-synthetic'], managedOrganizationIds: [] });
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
    return reply({ items: [{ ticketId: 'synthetic-ticket', ticketNumber: 'SYN-001', title: 'Synthetic SLA example', ticketType: 0, tenantId: 'org-synthetic', responsePercentUsed: 85, resolutionPercentUsed: 92, responseRemainingSeconds: 1800, resolutionRemainingSeconds: 3600, responseDueAt: '2026-10-15T12:00:00Z', resolutionDueAt: '2026-10-15T13:00:00Z' }], total: url.pathname.endsWith('/breached') ? 3 : 5 });
  }
  if (url.pathname.startsWith('/api/v1/ops/tasks')) {
    requests.push({ path: url.pathname, query: Object.fromEntries(url.searchParams), roles });
    if (fault && url.pathname.endsWith(fault)) return reply({}, 503);
    const row = url.pathname.endsWith('callback-rejections') ? { createdUtc: '2026-10-15T06:00:00Z', reasonCode: 'Synthetic rejection', callerClientId: 'fixture', correlationId: 'synthetic correlation' }
      : url.pathname.endsWith('binding-issues') ? { serviceName: 'Synthetic service', requestFormTitle: 'Synthetic form', taskName: 'Synthetic binding', enabled: false, syncState: 0, orchestrationRequestDefinitionName: 'Synthetic target' }
      : { taskId: 'synthetic-task', requestId: 'synthetic-request', requestTrackingId: 'SYN-002', taskName: 'Synthetic task', assignedToDisplayName: 'Synthetic assignee', dueAt: '2026-10-15T06:00:00Z', nextRetryAt: '2026-10-15T08:00:00Z', retryCount: 2, failurePolicy: 'Retry', failureReason: 'Synthetic failure', status: 1 };
    return reply({ items: [row], totalCount: 4 });
  }
  if (url.pathname === '/api/v1/ops/hangfire/settings') {
    requests.push({ path: url.pathname, method: req.method, roles });
    if (req.method === 'PUT') { let body = ''; for await (const part of req) body += part; enabled = JSON.parse(body).enabled; }
    return reply({ enabled, queueName: 'default', dashboardPath: '/hangfire', runtimeSource: 'Synthetic fixture', storageProvider: 'Synthetic storage', recurringJobCount: 3, graphDatasetSyncJobCount: 2, updatedAtUtc: '2026-10-15T06:00:00Z', updatedBy: 'Synthetic operator' });
  }
  if (url.pathname.startsWith('/hangfire')) { requests.push({ path: url.pathname, method: req.method }); res.writeHead(200, { 'Content-Type': 'text/html' }); return res.end('<title>Synthetic dashboard</title><p>Background dashboard fixture</p>'); }
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18400, '127.0.0.1', () => console.log('Operations fixture listening on loopback :18400'));

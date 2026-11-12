// Loopback synthetic persistence and fault control for real Blazor picker interaction.
// Authorization/database evidence belongs to AssigneeSearchEndpointsTests.
import http from 'node:http';
const actor = 'assignee-operator';
const port = Number(process.env.HELPDESK_DETAILS_FIXTURE_PORT ?? 18600);
const users = Array.from({ length: 25 }, (_, i) => ({ id: `user-${i}`, name: `Alpha ${String(i).padStart(2, '0')}`, email: `alpha${i}@example.test`, role: 'Technician', organizationId: 'org-1' }));
users.push({ id: 'late', name: 'Zulu Surname', email: 'late@example.test', role: 'Technician', organizationId: 'provider' },
  { id: 'duplicate-1', name: 'Duplicate Name', email: 'one@example.test', organizationId: 'org-1' },
  { id: 'duplicate-2', name: 'Duplicate Name', email: 'two@example.test', organizationId: 'org-1' });
const categoryOne = '79000000-0000-0000-0000-000000000001';
const categoryTwo = '79000000-0000-0000-0000-000000000002';
const categoryThree = '79000000-0000-0000-0000-000000000003';
let categories, categorySearches, tickets, searches, completedQueries, saves, fault, reader, slaActions, worklogs, requestTasks, tableReview = false;
let held = [];
const hold = kind => new Promise(resolve => held.push({ kind, resolve }));
function release() { const pending = held; held = []; pending.forEach(item => item.resolve()); }
function reset() {
  tableReview = false;
  release(); slaActions = []; worklogs = []; requestTasks = [];
  categories = [{ id: categoryOne, name: 'Network Operations', isActive: true, type: 0 }, { id: categoryTwo, name: 'Historical selection', isActive: false, type: 0 }, { id: categoryThree, name: 'R&D + 100%_\\', isActive: true, type: 0 }]; categorySearches = []; searches = []; completedQueries = []; saves = []; fault = ''; reader = false;
  tickets = Object.fromEntries(['incidents', 'requests', 'changes'].map(module => [module, {
    id: `${module}-1`, subject: 'Picker incident', title: `Picker ${module}`, description: 'Fixture ticket',
    trackingId: 'UX-146', organizationId: 'org-1', state: 0, priority: 1, lifecycleState: 0,
    categoryIds: [categoryOne, categoryTwo], ccRecipients: ['observer@example.test'], createdAt: "2026-10-01T10:00:00Z", updatedAt: "2026-10-02T12:00:00Z", sla: { status: 1, responseRemainingSeconds: 600, resolutionRemainingSeconds: 3600, responsePercentUsed: 30, resolutionPercentUsed: 40 }, assignedToId: null, implementorUserId: null, requestedForUserId: 'user-0',
    requestedForUserName: 'Alpha 00', requestedForUserEmail: 'alpha0@example.test', approverUserIds: [],
    approvers: [], approvals: [], changeTemplate: {}, templateValidationErrors: [], isTemplateComplete: false
  }]));
}
reset();
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  const body = async () => { let value = ''; for await (const part of req) value += part; return value ? JSON.parse(value) : {}; };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { reset(); return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ searches, completedQueries, saves, tickets, categories, categorySearches, slaActions, worklogs, held: held.map(item => item.kind) });
  if (url.pathname === '/fixture/table-review') { tableReview = true; return reply({ synthetic: true }); }
  // Presentation fixtures only. Real persisted gateway SSE acceptance is owned by
  // RequestLiveProgressTests; these rows do not establish provider/live outcomes.
  if (url.pathname === '/fixture/request-tasks' && req.method === 'POST') {
    const { count = 3, state = 'running' } = await body();
    requestTasks = Array.from({ length: count }, (_, i) => ({
      id: `presentation-task-${i}`, order: i + 1, name: i === 0 ? 'Long harmless automation task with a readable complete disclosure name' : `Harmless automation ${i + 1}`,
      description: i === 0 ? 'A deliberately long synthetic description to verify bounded primary text and accessible full task details in narrow and zoomed layouts.' : `Harmless automation ${i + 1}`,
      type: 2, status: state === 'unknown' ? 4 : 2, lastAutomationStatus: state === 'unknown' ? 'ExecutionUnknownManualReconcile' : 'running',
      orchestrationExternalRunId: `synthetic-provider-run-${i}`, orchestrationExternalRequestId: `synthetic-provider-request-${i}`,
      automationBindingId: `synthetic-binding-${i}`, automationBindingSyncState: 1, assignedToId: 'user-0',
      startedAt: '2026-11-11T10:00:00Z', lastAutomationUpdatedAt: '2026-11-11T10:01:00Z', expectedRuntimeSeconds: 300, graceSeconds: 30, hardTimeoutSeconds: 600
    }));
    tickets.requests.workflowStatus = state === 'unknown' ? 'NeedsReconciliation' : 'InProgress';
    tickets.requests.workflowBlockReason = state === 'unknown' ? 'The execution deadline elapsed. The remote outcome is unknown; support must reconcile this attempt.' : null;
    return reply({ count: requestTasks.length });
  }
  if (url.pathname === '/fixture/ticket-scope' && req.method === 'POST') {
    const scope = await body();
    for (const ticket of Object.values(tickets)) {
      ticket.organizationId = scope.organizationId ?? 'org-1';
      ticket.customerId = scope.customerId ?? 'customer-owner';
      ticket.customerEmail = ticket.requesterEmail = 'owner@example.test';
    }
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/release') { release(); return reply({ ok: true }); }
  if (url.pathname === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; reader = fault === 'reader'; if (fault === 'locked') tickets.changes.lifecycleState = 1; if (fault === 'dense') { const extra = Array.from({ length: 24 }, (_, i) => ({ id: `79000000-0000-0000-0000-${String(i + 100).padStart(12, '0')}`, name: `Long service category ${i} with an intentionally lengthy operational label for constrained layouts`, isActive: true, type: 0 })); categories.push(...extra); tickets.incidents.categoryIds.push(...extra.map(x => x.id)); tickets.incidents.assignedToId = 'dense-user'; } return reply({ ok: true }); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    const secret = req.headers['x-system-secret'];
    const role = secret === 'local-details-contributor-fixture' ? 'User' : secret === 'local-details-manager-fixture' ? 'Manager' : null;
    const roles = role ? ['Incident', 'Request', 'Change'].flatMap(module => [`${module}.Read`, `${module}.${role}`])
      : secret === 'local-details-reader-fixture' ? ['Incident.Read', 'Request.Read', 'Change.Read'] : ['HelpdeskAdmin'];
    const scopedClaims = role ? {
      customer_id: 'customer-owner', permission_scope_mode: 'scoped',
      scoped_permission: [...roles.map(permission => `${permission}|org-1`), ...['Incident', 'Request', 'Change'].map(module => `${module}.Read|org-2`)]
    } : {};
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: actor, auth_mode: 'development', preferred_username: 'Picker Operator', roles, allowed_organization_id: 'org-1', ...scopedClaims, exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') {
    const token = (req.headers.authorization ?? '').split(' ')[1];
    const claims = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) : {};
    reader = claims.roles?.includes('Incident.Read') ?? false;
    return reply({ isAuthenticated: true, name: 'Picker Operator', email: 'Picker Operator', primaryOrganizationId: null, primaryOrganizationName: null, customerId: claims.customer_id ?? null, userId: actor, isHelpdeskAdmin: claims.roles?.includes('HelpdeskAdmin') ?? false,
    roleBundles: claims.roles ?? [], permissions: claims.roles ?? [], allowedOrganizationIds: claims.permission_scope_mode ? ['org-1', 'org-2'] : ['org-1'], managedOrganizationIds: [],
    usesScopedPermissions: claims.permission_scope_mode === 'scoped', scopedPermissionGrants: (claims.scoped_permission ?? []).map(value => { const [permission, organizationId] = value.split('|'); return { permission, organizationId }; }) });
  }
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
  if (url.pathname === '/api/v1/ticketing/assignees/search') {
    const q = (url.searchParams.get('q') ?? '').trim().toLowerCase();
    searches.push({ q, module: url.searchParams.get('module'), organizationId: url.searchParams.get('organizationId') });
    if (fault === 'lookup') { fault = ''; return reply({ error: 'Fixture lookup failure' }, 400); }
    if (fault === 'empty') return reply({ items: [], hasMore: false });
    const matches = users.filter(user => `${user.name} ${user.email}`.toLowerCase().includes(q));
    // Deliberately complete an older lookup after newer requests have returned.
    if (q === 'slow') return setTimeout(() => { completedQueries.push(q); reply({ items: [users[0]], hasMore: false }); }, 1000);
    return reply({ items: matches.slice(0, 20), hasMore: matches.length > 20 });
  }
  if (url.pathname === '/api/v1/ticketing/assignees/current') {
    const ticket = Object.values(tickets).find(ticket => ticket.id === url.searchParams.get('ticketId'));
    return reply(ticket?.assignedToId === 'dense-user' ? { id: 'dense-user', name: 'Long Assigned Operator Name With Additional Identification Details', email: 'long-assigned-operator@example.test' } : users.find(user => user.id === ticket?.assignedToId) ?? null);
  }
  if (url.pathname === '/api/v1/ticketing/assignees') return reply(users);
  if (url.pathname === '/api/v1/ticketing/organizations') return reply([{ id: 'org-1', name: 'Customer organization', state: 0 }, { id: 'org-2', name: 'Second organization', state: 0 }]);
  if (url.pathname === '/api/v1/ticketing/customers') return reply([{ id: 'customer', name: 'Customer Person', email: 'customer@example.test', organizationId: url.searchParams.get('organizationId'), state: 0 }]);
  if (url.pathname.endsWith('/change-participants')) return reply({ organizationId: 'org-1', requestedForUsers: users.slice(0, 2), approverUsers: users.slice(0, 2), implementorUsers: users });
  if (url.pathname === '/api/v1/categories') {
    if (req.method === 'POST') { const dto = await body(); const existing = categories.find(x => x.name.toLowerCase() === dto.name.toLowerCase()); if (existing) return reply(existing); const item = { ...dto, id: '79000000-0000-0000-0000-000000000004', isActive: true }; categories.push(item); return reply(item, 201); }
    const ids = url.searchParams.get('ids');
    if (ids) return reply(categories.filter(x => ids.split(',').includes(x.id)));
    const q = url.searchParams.get('search') ?? '';
    categorySearches.push(q);
    if (fault === 'hold-category-search') { fault = ''; await hold('category-search'); }
    if (q === 'slow') { await new Promise(resolve => setTimeout(resolve, 1000)); return reply([{ id: '79000000-0000-0000-0000-000000000009', name: 'Stale result', isActive: true, type: 0 }]); }
    return reply(categories.filter(x => x.isActive && x.name.toLowerCase().includes(q.toLowerCase())).slice(0, 30));
  }
  if (/\/api\/v1\/tickets\/[^/]+\/sla\/(pause|resume)$/.test(url.pathname)) { slaActions.push(url.pathname); const pause = url.pathname.endsWith('/pause'); Object.values(tickets).forEach(ticket => { ticket.sla.status = pause ? 2 : 1; ticket.sla.pauseReason = pause ? 'Synthetic pause reason' : null; }); return reply({}); }
  if (/\/api\/v1\/tickets\/[^/]+\/attachments$/.test(url.pathname)) { if (fault === 'hold-attachments') { fault = ''; await hold('attachments'); return reply([{ id: '79000000-0000-0000-0000-000000000099', fileName: 'Stale count.txt', sizeBytes: 1 }]); } return reply([]); }
  const match = url.pathname.match(/^\/api\/v1\/(incidents|requests|changes)(?:\/(.*))?$/);
  if (match) {
    const [, module, action] = match;
    const ticket = tickets[module];
    if (action?.endsWith('/worklogs') && req.method === 'POST') { worklogs.push({ module, ...await body() }); return reply({}); }
    if (!action && req.method === 'GET') {
      const all = tableReview && module === 'incidents' ? Array.from({ length: 65 }, (_, i) => ({ ...ticket, id: `target-${i}`, trackingId: `INC-SYN-${String(i).padStart(3, '0')}`, subject: `Synthetic relation target ${i} with a long incident subject` })) : [ticket];
      const size = Number(url.searchParams.get('pageSize') ?? 25), page = Number(url.searchParams.get('page') ?? 1);
      return reply({ items: all.slice((page - 1) * size, page * size), totalCount: all.length });
    }
    if (tableReview && module === 'incidents' && action?.endsWith('/relations')) return reply(Array.from({ length: 35 }, (_, i) => ({ id: `79000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`, sourceTicketId: ticket.id, sourceTrackingId: ticket.trackingId, sourceSubject: ticket.subject, sourceState: 0, targetTicketId: `target-${i}`, targetTrackingId: `INC-SYN-${i}`, targetSubject: `Synthetic related incident ${i} with long retained context`, targetState: 0, relationType: 1, createdUtc: '2026-11-05T07:00:00Z', createdByUserName: 'Synthetic Operator', note: 'Synthetic relation only; no live incident linked', isOutgoing: true })));
    if (req.method === 'PUT' || (req.method === 'POST' && (action === 'bulk/assign' || action?.endsWith('/assignee')))) {
      const update = await body();
      if (fault === 'save') { fault = ''; return reply({ error: 'Fixture save rejected' }, 409); }
      if (fault === 'slow-save') { fault = ''; await new Promise(resolve => setTimeout(resolve, 1000)); }
      saves.push({ module, ...update }); Object.assign(ticket, Object.fromEntries(Object.entries(update).filter(([, value]) => value !== null)));
      const value = update.implementorUserId ?? update.assignedToId;
      if ('assignedToId' in update || 'implementorUserId' in update) {
        ticket.assignedToId = value || null;
        if (module === 'changes') {
          ticket.implementorUserId = value;
          const user = users.find(user => user.id === value);
          ticket.implementorUserName = user?.name; ticket.implementorUserEmail = user?.email;
        }
      }
      if (action?.endsWith('/assignee')) return reply(users.find(user => user.id === value));
      const snapshot = structuredClone(ticket);
      if (fault === 'hold-save') { fault = ''; await hold('save'); }
      return reply(action === 'bulk/assign' ? { updated: 1 } : snapshot);
    }
    if (action === ticket.id) { const snapshot = structuredClone(ticket); if (fault === 'hold-refresh') { fault = ''; await hold('refresh'); } return reply(snapshot); }
    if (action === `${module}-2`) return reply({ ...ticket, id: `${module}-2`, categoryIds: [categoryThree] });
    if (module === 'requests' && action?.endsWith('/tasks')) return reply(requestTasks);
    if (action?.endsWith('/workflow-timeline')) { const group = url.searchParams.get('categories')?.includes('Sla') ? 'SLA' : 'Tasks'; const snapshot = { items: [{ id: group, title: `${group} workflow event`, category: group, severity: 'Info', createdAt: '2026-10-01T10:00:00Z' }], totalCount: 1 }; if (fault === 'hold-workflow') { fault = ''; await hold('workflow'); } return reply(snapshot); }
    if (action?.endsWith('/peek')) return reply({ incidentId: ticket.id, text: 'Fixture message', html: '<p>Fixture message</p>' });
    if (action?.endsWith('/timeline/count')) return reply(0);
    if (action?.endsWith('/attachments/count')) return reply(1200);
    if (action?.endsWith('/listeners/count')) return fault === 'counts' ? reply({}, 503) : reply(12);
    if (action === 'count') return reply(1);
    if (action?.endsWith('/chat-capabilities')) return reply({ enabled: false, reason: 'Fixture' });
    if (action?.includes('summary')) return reply({});
    return reply([]);
  }
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(port, '127.0.0.1', () => console.log(`Details fixture API on loopback :${port}`));

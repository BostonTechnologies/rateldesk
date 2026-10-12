// Loopback synthetic persistence and fault control for real Blazor picker interaction.
// Authorization/database evidence belongs to AssigneeSearchEndpointsTests.
import http from 'node:http';
const actor = 'assignee-operator';
const users = Array.from({ length: 25 }, (_, i) => ({ id: `user-${i}`, name: `Alpha ${String(i).padStart(2, '0')}`, email: `alpha${i}@example.test`, role: 'Technician', organizationId: 'org-1' }));
users.push({ id: 'late', name: 'Zulu Surname', email: 'late@example.test', role: 'Technician', organizationId: 'provider' },
  { id: 'duplicate-1', name: 'Duplicate Name', email: 'one@example.test', organizationId: 'org-1' },
  { id: 'duplicate-2', name: 'Duplicate Name', email: 'two@example.test', organizationId: 'org-1' });
let tickets, searches, completedQueries, saves, fault, reader;
function reset() {
  searches = []; completedQueries = []; saves = []; fault = ''; reader = false;
  tickets = Object.fromEntries(['incidents', 'requests', 'changes'].map(module => [module, {
    id: `${module}-1`, subject: 'Picker incident', title: `Picker ${module}`, description: 'Fixture ticket',
    trackingId: 'UX-146', organizationId: 'org-1', state: 0, priority: 1, lifecycleState: 0,
    categoryIds: [], assignedToId: null, implementorUserId: null, requestedForUserId: 'user-0',
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
  if (url.pathname === '/fixture/state') return reply({ searches, completedQueries, saves, tickets });
  if (url.pathname === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; reader = fault === 'reader'; return reply({ ok: true }); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    const roles = req.headers['x-system-secret'] === 'local-ux-reader-fixture' ? ['Incident.Read', 'Request.Read', 'Change.Read'] : ['HelpdeskAdmin'];
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: actor, auth_mode: 'development', preferred_username: 'Picker Operator', roles, allowed_organization_id: 'org-1', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') {
    const token = (req.headers.authorization ?? '').split(' ')[1];
    reader = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles.includes('Incident.Read') : false;
    return reply({ isAuthenticated: true, name: 'Picker Operator', email: 'Picker Operator', primaryOrganizationId: null, primaryOrganizationName: null, customerId: null, userId: actor, isHelpdeskAdmin: !reader,
    roleBundles: reader ? ['Incident.Read', 'Request.Read', 'Change.Read'] : ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: ['org-1'], managedOrganizationIds: [] });
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
    return reply(users.find(user => user.id === ticket?.assignedToId) ?? null);
  }
  if (url.pathname === '/api/v1/ticketing/assignees') return reply(users);
  if (url.pathname === '/api/v1/ticketing/organizations') return reply([{ id: 'org-1', name: 'Customer organization', state: 0 }, { id: 'org-2', name: 'Second organization', state: 0 }]);
  if (url.pathname === '/api/v1/ticketing/customers') return reply([{ id: 'customer', name: 'Customer Person', email: 'customer@example.test', organizationId: url.searchParams.get('organizationId'), state: 0 }]);
  if (url.pathname.endsWith('/change-participants')) return reply({ organizationId: 'org-1', requestedForUsers: users.slice(0, 2), approverUsers: users.slice(0, 2), implementorUsers: users });
  const match = url.pathname.match(/^\/api\/v1\/(incidents|requests|changes)(?:\/(.*))?$/);
  if (match) {
    const [, module, action] = match;
    const ticket = tickets[module];
    if (!action && req.method === 'GET') return reply({ items: [ticket], totalCount: 1 });
    if (req.method === 'PUT' || (req.method === 'POST' && (action === 'bulk/assign' || action?.endsWith('/assignee')))) {
      const update = await body();
      if (fault === 'save') { fault = ''; return reply({ error: 'Fixture save rejected' }, 409); }
      if (fault === 'slow-save') { fault = ''; await new Promise(resolve => setTimeout(resolve, 1000)); }
      saves.push({ module, ...update });
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
      return reply(action === 'bulk/assign' ? { updated: 1 } : ticket);
    }
    if (action === ticket.id) return reply(ticket);
    if (action === 'count') return reply(1);
    if (action?.endsWith('/chat-capabilities')) return reply({ enabled: false, reason: 'Fixture' });
    if (action?.includes('summary')) return reply({});
    return reply([]);
  }
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18300, '127.0.0.1', () => console.log('Assignee fixture API on loopback :18300'));

// Synthetic loopback fixture. Real authorization and provider persistence are tested separately.
import http from 'node:http';
const organizations = [{ id: 'org-1', name: 'Atlas Tenant', itSupportOrganizationId: 'provider' }, { id: 'org-2', name: 'Birch Tenant', itSupportOrganizationId: 'provider' }, { id: 'provider', name: 'Support Provider' }];
const users = [{ id: 'user-1', name: 'Synthetic Operator', email: 'operator@example.test', organizationId: 'org-1' }];
let roles, datasets, calendars, subscriptions, settings, graph, coverage, saves, reads, fault;
const clone = value => JSON.parse(JSON.stringify(value));
function reset() {
 saves = []; reads = []; fault = '';
 roles = [{ id: 'built', key: 'builtin-reader', name: 'Identical display name', scope: 1, ownerOrganizationId: null, isBuiltIn: true, isProtected: true, permissions: ['Incident.Read'], assignmentCount: 1250 },
 { id: 'custom', key: 'synthetic-custom', name: 'Identical display name', scope: 1, ownerOrganizationId: 'org-1', isBuiltIn: false, isProtected: false, permissions: ['Incident.Read', 'Request.Read'], assignmentCount: 1 }];
 datasets = ['org-1', 'org-2'].map((organizationId, i) => ({ id: `dataset-${i + 1}`, organizationId, name: i ? 'Birch inventory' : 'Atlas inventory', slug: 'inventory', sourceType: 0, keyColumn: 'id', displayColumn: 'name', searchColumns: ['name'], isActive: true, columns: [{ id: 'column-id', name: 'id', dataType: 0, isKey: true, isDisplay: false, isSearchable: false, sortOrder: 0 }, { id: 'column-name', name: 'name', dataType: 0, isKey: false, isDisplay: true, isSearchable: true, sortOrder: 1 }] }));
 calendars = [{ id: 'calendar-1', name: 'Atlas working week', tenantId: 'org-1', scopeType: 1, timeZoneId: 'Africa/Johannesburg', isActive: true, weeklyRules: [], exceptions: [] }, { id: 'system-calendar', name: 'System working week', tenantId: null, scopeType: 0, timeZoneId: 'UTC', isActive: true, weeklyRules: [], exceptions: [] }];
 settings = Object.fromEntries(organizations.map(x => [x.id, { tenantId: x.id, useBusinessHours: x.id !== 'org-2', calendarId: null }]));
 graph = Object.fromEntries(organizations.map(x => [x.id, { organizationId: x.id, tenantId: '', clientId: '', clientSecret: '', enableUsers: false, enableDevices: false, enableGroups: false, enableSharePointSites: false, backgroundSyncEnabled: false, backgroundSyncCronExpression: '0 */6 * * *', hangfireEnabled: false, lastSyncStatus: 'Not run yet' }]));
 coverage = organizations.map(x => ({ id: `coverage-${x.id}`, customerOrganizationId: x.id, providerOrganizationId: 'provider', supportGroupId: 'group-1', role: 0, isEnabled: true }));
 subscriptions = [{ id: 'report-1', tenantId: 'org-1', frequency: 1, weeklyDay: 1, sendTimeLocal: '08:00:00', timeZoneId: 'Africa/Johannesburg', lookbackDays: 7, isActive: true, includeCsvAttachment: true, targets: [{ type: 0, value: 'operator@example.test' }] }];
}
reset();
http.createServer(async (req, res) => {
 const url = new URL(req.url, 'http://127.0.0.1'); const path = url.pathname.replace(/\/$/, '');
 const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
 const body = async () => { let text = ''; for await (const chunk of req) text += chunk; return text ? JSON.parse(text) : {}; };
 if (path === '/fixture/health') return reply({ ok: true });
 if (path === '/fixture/reset') { reset(); return reply({ ok: true }); }
 if (path === '/fixture/state') return reply({ saves, reads, roles, datasets, settings, subscriptions });
 if (path === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; if (fault === 'no-custom') roles = roles.filter(x => x.isBuiltIn); if (fault === 'many-roles') roles.push(...Array.from({ length: 60 }, (_, i) => ({ ...roles.find(x => !x.isBuiltIn), id: `many-${i}`, key: `many-${i}`, name: `Custom role ${String(i).padStart(2, '0')}`, assignmentCount: 0 }))); return reply({ ok: true }); }
 if (path.endsWith('/system/token')) {
  const mode = req.headers['x-system-secret'];
  const permissions = mode === 'admin-fixture-reader' ? ['Incident.Read'] : mode === 'admin-fixture-delegate' ? ['Tenant.Roles.Assign', 'DataManagement.Admin'] : ['HelpdeskAdmin'];
  const encode = x => Buffer.from(JSON.stringify(x)).toString('base64url');
  return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-admin', auth_mode: 'development', preferred_username: 'Synthetic Administrator', email: 'admin@example.test', organization_id: 'org-1', roles: permissions, allowed_organization_id: 'org-1', managed_organization_id: 'org-1', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
 }
 const token = req.headers.authorization?.split(' ')[1];
 const permissions = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles : [];
 const admin = permissions.includes('HelpdeskAdmin');
 const scopedOrganizations = admin ? organizations : organizations.slice(0, 1);
 if (path === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Administrator', email: 'admin@example.test', customerId: null, userId: 'synthetic-admin', primaryOrganizationId: 'org-1', primaryOrganizationName: 'Atlas Tenant', isHelpdeskAdmin: admin, roleBundles: permissions, permissions, allowedOrganizationIds: ['org-1'], managedOrganizationIds: admin ? [] : ['org-1'] });
 if (path === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
 if (req.method === 'GET') reads.push({ path, query: url.search });
 if (req.method !== 'GET' && path.startsWith('/api/')) {
  const payload = await body();
  if (fault === 'save') { fault = ''; return reply({ error: 'Synthetic rejected save' }, 409); }
  if (fault === 'slow-save') { fault = ''; await new Promise(resolve => setTimeout(resolve, 700)); }
  saves.push({ path, method: req.method, payload });
  if (path.startsWith('/api/v1/admin/role-definitions')) {
   if (req.method === 'POST') { const role = { ...payload, id: 'new-role', key: 'new-role', isBuiltIn: false, isProtected: false, scope: 1, assignmentCount: 0 }; roles.push(role); return reply(role); }
   roles = req.method === 'DELETE' ? roles.filter(x => !path.endsWith(x.id)) : roles.map(x => path.endsWith(x.id) ? { ...x, ...payload } : x); return reply({});
  }
  if (path.startsWith('/api/v1/tenant-sla-settings/')) { settings[payload.tenantId] = payload; return reply(payload); }
  if (path.startsWith('/api/v1/sla-report-subscriptions')) {
   if (req.method === 'POST') { payload.id = 'new-report'; subscriptions.push(payload); }
   else subscriptions = req.method === 'DELETE' ? subscriptions.filter(x => !path.endsWith(x.id)) : subscriptions.map(x => path.endsWith(x.id) ? payload : x);
   return reply(payload);
  }
  if (path.startsWith('/api/v1/support/coverage/')) { const row = coverage.find(x => path.endsWith(x.id)); if (row) Object.assign(row, payload); return reply(row ?? {}); }
  if (path.endsWith('/graph-settings/org-1/sync')) { const builtIn = { ...clone(datasets[0]), id: 'graph-users', name: 'Graph Users', sourceType: 1 }; datasets.push(builtIn); return reply([builtIn]); }
  if (path === '/api/v1/resources/datasets/graph-settings') { graph[payload.organizationId] = payload; return reply(payload); }
  if (/\/datasets\/[^/]+$/.test(path)) {
   const dataset = datasets.find(x => path.endsWith(x.id)); if (dataset) Object.assign(dataset, payload);
   if (req.method === 'DELETE') datasets = datasets.filter(x => !path.endsWith(x.id)); return reply(dataset ?? payload);
  }
  if (path === '/api/v1/resources/datasets') { payload.id = 'new-dataset'; payload.slug = 'synthetic'; datasets.push(payload); return reply(payload); }
  return reply({});
 }
 if (['/api/v1/organizations', '/api/v1/ticketing/organizations', '/api/v1/tenant-admin/organizations', '/api/v1/admin/tenants/lookup'].includes(path)) return reply(scopedOrganizations);
 if (path === '/api/v1/users') return reply(users);
 if (path === '/api/v1/customers') return reply([]);
 if (path === '/api/v1/admin/role-definitions') return reply(roles);
 if (path.endsWith('/assignments')) return reply([{ id: 'assignment-1', userId: 'user-1', name: 'Synthetic Operator', email: 'operator@example.test', organizationId: 'org-1', assignedAtUtc: '2026-10-01T00:00:00Z' }]);
 if (path === '/api/v1/sla-calendars') return reply(calendars);
 if (path === '/api/v1/slas') return reply([{ id: 'policy-1', name: 'Atlas priority response', tenantId: 'org-1', appliesTo: 0, responseTimeHours: 2, resolutionTimeHours: 8, escalations: [] }]);
 if (path === '/api/v1/sla-report-subscriptions') return reply(subscriptions);
 if (path.startsWith('/api/v1/tenant-sla-settings/')) {
  const tenant = path.split('/').pop(); if (fault === 'slow-tenant' && tenant === 'org-1') await new Promise(resolve => setTimeout(resolve, 1000)); return reply(settings[tenant]);
 }
 if (path === '/api/v1/resources/datasets/graph-settings') return reply(graph[url.searchParams.get('organizationId')]);
 if (path === '/api/v1/resources/datasets') return reply(datasets.filter(x => x.organizationId === url.searchParams.get('organizationId')));
 if (path.endsWith('/rows')) {
  const query = url.searchParams.get('query') ?? ''; const page = +(url.searchParams.get('page') ?? 1); const size = +(url.searchParams.get('pageSize') ?? 50);
  if (query === 'slow') await new Promise(resolve => setTimeout(resolve, 1000));
  const rows = Array.from({ length: 65 }, (_, i) => ({ id: `row-${i + 1}`, externalKey: `SYN-${i + 1}`, data: { id: `SYN-${i + 1}`, name: `Inventory item ${i + 1}` }, lastIngestedAtUtc: '2026-10-01T10:00:00Z' })).filter(x => !query || x.data.name.toLowerCase().includes(query.toLowerCase()));
  return reply({ items: rows.slice((page - 1) * size, page * size), total: rows.length });
 }
 if (path.endsWith('/credentials')) return reply([{ id: 'credential-1', name: 'Inventory integration', isActive: true, createdAtUtc: '2026-10-01T10:00:00Z' }]);
 if (/\/datasets\/[^/]+$/.test(path)) return reply(datasets.find(x => path.endsWith(x.id)));
 if (path === '/api/v1/support/groups') return reply([{ id: 'group-1', name: 'Primary support', owningOrganizationId: 'provider', description: 'Synthetic support group', isEnabled: true }]);
 if (path === '/api/v1/support/coverage') return reply(coverage.filter(x => x.customerOrganizationId === url.searchParams.get('customerOrganizationId')));
 if (path === '/api/v1/support/notification-subscriptions') return reply([{ id: 'rule-1', customerOrganizationId: url.searchParams.get('customerOrganizationId'), eventType: 0, recipientType: 0, recipientId: 'group-1', channel: 0, isEnabled: true }]);
 if (path.endsWith('/recipient-preview')) {
  if (fault === 'slow-preview') { fault = ''; await new Promise(resolve => setTimeout(resolve, 900)); }
  return reply({ organizationId: path.split('/')[5], eventType: url.searchParams.get('eventType') === 'TicketAssigned' ? 1 : 0, recipients: [], warnings: [] });
 }
 if (path.endsWith('/count')) return reply(0);
 if (path.includes('summary') || path.includes('status')) return reply({});
 if (path.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
 return reply([]);
}).listen(18500, '127.0.0.1', () => console.log('Admin fixture listening on loopback :18500'));

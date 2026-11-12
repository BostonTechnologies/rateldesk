// Loopback-only synthetic presentation fixture. This never invokes a provider.
import http from 'node:http';
const organizations = Array.from({ length: 100 }, (_, index) => ({ id: `org-${index + 1}`, name: `Example organization ${String(index + 1).padStart(3, '0')}` }));
const pairId = 'a'.repeat(64);
const clone = value => JSON.parse(JSON.stringify(value));
let mode, role, brand, reads, mutations, connections, services;
function reset(options = {}) {
  mode = options.mode ?? ''; role = options.role ?? 'HelpdeskAdmin'; brand = options.brand ?? 'RatelDesk'; reads = []; mutations = [];
  const now = Date.now();
  connections = Array.from({ length: 6 }, (_, index) => {
    const id = `11111111-1111-1111-1111-${String(index + 1).padStart(12, '0')}`;
    return { id, pairId, peer: { contract: 'bostec.pairing.v1', product: 'netratel', installationId: 'synthetic-peer', name: 'Example automation platform', webOrigin: 'https://automation.example.test', apiOrigin: 'https://api.automation.example.test' },
      mapping: index === 5 ? null : { id, pairId, name: ['Example incident intake', 'Example automation execution with a deliberately long readable connection name', 'Example stale test', 'Example failed test', 'Example pending configuration'][index], netRatelTenantId: '17', ratelDeskOrganizationId: 'org-1', ratelDeskCustomerId: 'customer-1', createIncidents: index % 2 === 0, runAutomation: index !== 0 },
      status: index >= 4 ? 'paired' : 'connected', lastTest: index === 0 || index >= 4 ? null : { success: index !== 3, message: index === 3 ? 'The peer is unavailable. Retry this saved connection.' : 'Current mapping and permissions verified', testedAtUtc: new Date(now - (index === 2 ? 2 * 86400_000 : 60_000)).toISOString() } };
  });
  services = [0, 2, 20, 100].map((count, index) => ({ id: `service-${index + 1}`, name: index === 1 ? 'A service folder with a deliberately long title that remains accessible' : `Example service ${index + 1}`, description: index % 2 ? 'A longer description that explains the example service. '.repeat(10) : 'Harmless synthetic service.', itemType: 0, availableRequestCount: index + 1, allowedOrganizationIds: organizations.slice(0, count).map(item => item.id) }));
  if (mode === 'empty') { connections = []; services = []; }
  if (options.connectionCount !== undefined) connections = connections.slice(0, options.connectionCount);
  if (options.serviceCount !== undefined) services = services.slice(0, options.serviceCount);
}
reset();
http.createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1'); const path = url.pathname.replace(/\/$/, '');
  const reply = (value, status = 200) => { response.writeHead(status, { 'Content-Type': 'application/json' }); response.end(status === 204 ? undefined : JSON.stringify(value)); };
  const body = async () => { let text = ''; for await (const chunk of request) text += chunk; return text ? JSON.parse(text) : {}; };
  if (path === '/fixture/health') return reply({ ok: true });
  if (path === '/fixture/reset') { reset(Object.fromEntries(url.searchParams)); return reply({ ok: true }); }
  if (path === '/fixture/options') { const options = await body(); if ('mode' in options) mode = options.mode; if ('brand' in options) brand = options.brand; if ('role' in options) role = options.role; return reply({ ok: true }); }
  if (path === '/fixture/state') return reply({ reads, mutations, connections, services });
  if (path.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', preferred_username: 'Example Operator', email: 'operator@example.test', organization_id: 'org-1', roles: [role], allowed_organization_id: 'org-1', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  const token = request.headers.authorization?.split(' ')[1];
  const roles = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles : [role];
  if (path === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Example Operator', email: 'operator@example.test', userId: 'synthetic-operator', customerId: null, primaryOrganizationId: 'org-1', primaryOrganizationName: organizations[0].name, isHelpdeskAdmin: role === 'HelpdeskAdmin', roles: [role], roleBundles: [role], permissions: [role], allowedOrganizationIds: ['org-1'], managedOrganizationIds: ['org-1'] });
  if (path === '/api/v1/branding') return mode === 'branding-unavailable' ? reply({}, 503) : reply({ applicationName: brand, faviconUrl: '/favicon.ico' });
  reads.push({ path, query: Object.fromEntries(url.searchParams), method: request.method, roles });
  if (path === '/api/v1/dashboard/admin-summary') {
    if (mode === 'summary-error') return reply({}, 503);
    return reply(mode === 'empty' ? {} : { incidentCountsByState: { New: 12, InProgress: 7, WaitingReply: 3, Resolved: 21 }, requestCountsByState: { New: 5, InProgress: 9, OnHold: 2, Resolved: 18 }, changeCountsByState: { Draft: 3, PendingApproval: 2, ImplementationInProgress: 1 }, pendingChanges: 2 });
  }
  const feeds = ['/api/v1/incidents', '/api/v1/requests', '/api/v1/changes'];
  if (feeds.includes(path)) {
    if (mode === path.split('/').at(-1) + '-error') return reply({}, 503);
    const prefix = path.endsWith('incidents') ? 'INC' : path.endsWith('requests') ? 'REQ' : 'CHG';
    const items = mode === 'empty' ? [] : Array.from({ length: 5 }, (_, index) => ({ id: `example-${prefix}-${index + 1}`, trackingId: `${prefix}-EXAMPLE-${index + 1}`, title: `Example ${prefix} work item ${index + 1} with a long title that remains available to keyboard and assistive technology`, subject: `Example incident ${index + 1} with a readable long subject`, state: index % 2 ? 3 : 0, lifecycleState: index % 2 ? 2 : 0, updatedAt: '2026-11-11T09:00:00Z' }));
    return reply({ items, total: items.length, totalCount: items.length });
  }
  if (path === '/api/v1/organizations') return reply(organizations);
  if (path === '/api/v1/service-items' || path === '/api/v1/service-items/service-1') return mode === 'services-error' ? reply({}, 503) : reply(services);
  if (path === '/api/v1/services/service-1') return reply({ id: 'service-1', name: 'Example service 1', depth: 1, allowedOrganizationIds: [] });
  if (path.endsWith('/breadcrumb')) return reply([{ id: 'service-1', name: 'Example service 1' }]);
  if (/^\/api\/v1\/services\/service-\d+$/.test(path) && request.method === 'GET') return reply(services.find(item => path.endsWith(item.id)) ?? {});
  if (/^\/api\/v1\/services\/service-\d+$/.test(path) && request.method === 'PUT') { const payload = await body(); mutations.push({ path, payload }); Object.assign(services.find(item => path.endsWith(item.id)), payload); return reply({}); }
  if (path === '/api/v1/admin/system-connections') return reply(connections);
  if (path.startsWith('/api/v1/admin/system-connections/')) {
    const parts = path.slice('/api/v1/admin/system-connections/'.length).split('/');
    if (parts[1] === 'directory') return mode === 'directory-error' ? reply({}, 503) : reply({ netRatelTenants: [{ id: '17', name: 'Example automation tenant' }], ratelDeskOrganizations: organizations.slice(0, 2), ratelDeskCustomers: [{ id: 'customer-1', name: 'Example incident account', parentId: 'org-1' }] });
    const connection = connections.find(item => item.mapping?.id === parts[2]);
    if (parts[3] === 'test') {
      mutations.push({ path, method: request.method });
      return setTimeout(() => { const result = { success: true, message: 'Current mapping and permissions verified', testedAtUtc: new Date().toISOString() }; if (connection) connection.lastTest = result; reply(result); }, 250);
    }
    if (request.method === 'DELETE') { mutations.push({ path, method: request.method }); connections = connections.filter(item => item !== connection); return reply({}, 204); }
    if (request.method === 'PUT') { const mapping = await body(); mutations.push({ path, mapping }); return reply({ ...connections[0], id: mapping.id, mapping, status: 'connected', lastTest: null }); }
  }
  if (path === '/api/v1/admin/orchestration/bindings') return reply([]);
  if (path.includes('/global-search/')) return reply({ items: [], totalCount: 0 });
  if (path.includes('/stream')) { response.writeHead(200, { 'Content-Type': 'text/event-stream' }); return response.end(); }
  if (path.endsWith('/count')) return reply(0);
  if (path.includes('summary') || path.includes('status')) return reply({});
  return reply([]);
}).listen(18720, '127.0.0.1', () => console.log('Synthetic premium cards fixture :18720'));

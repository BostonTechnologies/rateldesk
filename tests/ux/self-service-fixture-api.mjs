// Loopback synthetic browser fixture. API ownership and SQL behavior have owning provider tests.
import http from 'node:http';
const fixturePort = Number(process.env.SELF_SERVICE_FIXTURE_PORT ?? 18440);

const originalRequests = Array.from({ length: 137 }, (_, i) => ({
  id: `request-${i}`, trackingId: `REQ-SYN-${String(i).padStart(3, '0')}`,
  title: i === 136 ? 'Needle beyond the first hundred requests' : `Request ${String(i).padStart(3, '0')} with a long synthetic title for accessible wrapping`,
  state: i % 7, releaseStatus: i % 2,
  createdAt: '2026-11-01T12:00:00Z', updatedAt: new Date(Date.UTC(2026, 10, 2, 12, i)).toISOString()
}));
let count = 7, fault = '', requests = [], rows = structuredClone(originalRequests), submissions = [], receipts = new Map();
const catalogue = () => Array.from({ length: count }, (_, i) => ({ id: `service-${i}`, name: `Service ${i + 1} for synthetic access and device support`,
  description: i % 2 ? 'A short description.' : 'A longer description that remains accessible in full while the common card geometry reserves two lines and anchors all footers consistently.',
  itemType: 0, availableRequestCount: i + 1 }));

http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const path = url.pathname.replace(/\/$/, '');
  const reply = (value, status = 200) => { if (!res.destroyed) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); } };
  if (path === '/fixture/health') return reply({ ok: true });
  if (path === '/fixture/reset') { count = 7; fault = ''; requests = []; rows = structuredClone(originalRequests); submissions = []; receipts = new Map(); return reply({ ok: true }); }
  if (path === '/fixture/state') return reply({ requests, count, submissions, receiptCount: receipts.size });
  if (path === '/fixture/catalogue') { count = Math.max(0, Math.min(100, Number(url.searchParams.get('count') ?? 7))); return reply({ ok: true }); }
  if (path === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; return reply({ ok: true }); }
  if (path === '/fixture/remove-last-page') { rows.splice(100); return reply({ ok: true }); }
  if (path.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-owner', auth_mode: 'development', preferred_username: 'Synthetic Owner', email: 'owner@example.test', organization_id: 'org-synthetic', customer_id: 'customer-synthetic', roles: ['HelpdeskAdmin'], allowed_organization_id: 'org-synthetic', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  if (path === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Owner', email: 'owner@example.test', userId: 'synthetic-owner', customerId: 'customer-synthetic', primaryOrganizationId: 'org-synthetic', isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: ['org-synthetic'], managedOrganizationIds: [], scopedPermissionGrants: [] });
  if (path === '/api/v1/setup/status') return reply({ state: 'Ready' });
  if (path === '/api/v1/branding') return reply({ applicationName: 'Example Support', faviconUrl: '/favicon.ico' });
  if (path.startsWith('/api/v1/service-items')) {
    requests.push({ path, query: Object.fromEntries(url.searchParams) });
    if (fault === 'catalogue') return reply({}, 503);
    return reply(catalogue());
  }
  if (path.endsWith('/breadcrumb')) return reply([{ id: 'service-0', name: 'Synthetic hardware folder' }]);
  if (path === '/api/v1/request-forms/form-synthetic') return reply({ id: 'form-synthetic', serviceId: 'service-0', title: 'Synthetic access request', description: 'Harmless rendered submission fixture', organizationId: 'org-synthetic', allowedOrganizationIds: ['org-synthetic'], releaseStatus: 1,
    jsonSchema: JSON.stringify({ title: 'Synthetic access request', description: 'Harmless rendered submission fixture', fields: [{ key: 'reason', label: 'Request reason', type: 'text', required: true }], tasks: [] }) });
  if (path === '/api/v1/self-service/submission-context') {
    const denied = ['missing-requester', 'disabled-requester', 'wrong-organization'].includes(fault);
    return reply({ organizationId: 'org-synthetic', organizationName: 'Example Organization', requestedForName: 'Synthetic Owner', canSubmit: !denied,
      errorCode: fault === 'wrong-organization' ? 'request_organization_mismatch' : denied ? 'requester_unavailable' : null,
      message: fault === 'wrong-organization' ? 'This form uses a different organization. Choose a form available to Example Organization.' : denied ? 'Your requester profile is unavailable. Ask an administrator to review your enabled account and organization membership.' : null });
  }
  if (path === '/api/v1/self-service/request-users') return reply([{ id: 'synthetic-recipient', name: 'Synthetic Recipient', email: 'recipient@example.test', source: 0, organizationId: 'org-synthetic', organizationName: 'Example Organization' }]);
  if (path === '/api/v1/self-service/requests' && req.method === 'POST') {
    let text = ''; for await (const part of req) text += part;
    const payload = JSON.parse(text); submissions.push(payload);
    const empty = fault.match(/^empty-(401|403|400|500)$/);
    if (empty) { res.writeHead(Number(empty[1])); return res.end(); }
    if (fault === 'malformed-error') { res.writeHead(500, { 'Content-Type': 'text/html' }); return res.end('<html>Private stack trace and synthetic credential must never appear</html>'); }
    if (fault === 'missing-requester' || fault === 'disabled-requester') return reply({ code: 'requester_unavailable', traceId: 'synthetic-requester-reference' }, 403);
    if (fault === 'wrong-organization') return reply({ code: 'request_organization_mismatch', traceId: 'synthetic-org-reference' }, 403);
    if (fault === 'invalid-success') return reply({});
    if (!receipts.has(payload.submissionId)) receipts.set(payload.submissionId, { requestId: `new-request-${receipts.size + 1}`, trackingId: `REQ-NEW-${receipts.size + 1}` });
    const receipt = receipts.get(payload.submissionId);
    return setTimeout(() => reply(receipt), 250);
  }
  if (path === '/api/v1/self-service/requests') {
    const receipt = { path, query: Object.fromEntries(url.searchParams), completed: false };
    requests.push(receipt);
    if (fault === 'requests') return reply({}, 503);
    const q = (url.searchParams.get('q') ?? '').toLowerCase();
    let result = rows.filter(x => `${x.trackingId} ${x.title}`.toLowerCase().includes(q));
    for (const [key, property] of [['state', 'state'], ['releaseStage', 'releaseStatus']])
      if (url.searchParams.has(key)) result = result.filter(x => String(x[property]) === url.searchParams.get(key));
    for (const [key, property, before] of [['createdFromUtc', 'createdAt', false], ['createdBeforeUtc', 'createdAt', true], ['updatedFromUtc', 'updatedAt', false], ['updatedBeforeUtc', 'updatedAt', true]])
      if (url.searchParams.has(key)) result = result.filter(x => before ? Date.parse(x[property]) < Date.parse(url.searchParams.get(key)) : Date.parse(x[property]) >= Date.parse(url.searchParams.get(key)));
    const property = { created: 'createdAt', updated: 'updatedAt', trackingId: 'trackingId', title: 'title', state: 'state' }[url.searchParams.get('sortBy') ?? 'created'];
    const direction = url.searchParams.get('descending') === 'false' ? 1 : -1;
    result.sort((a, b) => (a[property] < b[property] ? -1 : a[property] > b[property] ? 1 : 0) * direction || a.id.localeCompare(b.id));
    const pageSize = Number(url.searchParams.get('pageSize') ?? 25), page = Math.min(Number(url.searchParams.get('page') ?? 1), Math.max(1, Math.ceil(result.length / pageSize)));
    const value = { items: result.slice((page - 1) * pageSize, page * pageSize), totalCount: result.length, page, pageSize };
    if (q === 'request') return setTimeout(() => { receipt.completed = true; reply(value); }, 1200);
    receipt.completed = true;
    return reply(value);
  }
  if (/^\/api\/v1\/self-service\/requests\/[^/]+$/.test(path)) return reply({ ...rows[0], description: 'Synthetic request detail', payloadJson: '{}' });
  if (path.endsWith('/tasks')) return reply([]);
  if (path === '/api/v1/notifications') return reply({ items: [], totalCount: 0 });
  if (path.endsWith('/count')) return reply(0);
  if (path.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  if (path.includes('summary') || path.includes('status')) return reply({});
  return reply([]);
}).listen(fixturePort, '127.0.0.1', () => console.log(`Self Service fixture on loopback :${fixturePort}`));

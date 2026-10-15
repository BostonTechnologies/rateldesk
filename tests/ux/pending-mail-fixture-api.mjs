// Synthetic presentation fixture only. The owning .NET tests exercise real migrated SQLite/PostgreSQL.
import http from 'node:http';
let requests = [], actions = [], removed = new Set(), cancelled = new Set(), fail = false;
const data = Array.from({ length: 1205 }, (_, i) => ({
  id: `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`, ticketId: `synthetic-${i + 1}`,
  trackingId: `INC-SYN-${String(i + 1).padStart(4, '0')}`, ticketType: 'incidents', subject: `Synthetic delivery ${i + 1}`,
  emailRecipient: `recipient${i + 1}@example.test`, sender: 'support@example.test', organizationName: 'Synthetic organization', organizationId: 'org-synthetic',
  createdUtc: new Date(Date.UTC(2026, 9, 1) + i * 60000).toISOString(), emailStatus: 3, retryCount: 2,
  isRetryable: i % 3 !== 0, canCancel: i % 3 !== 0, canRemove: true, queueState: 'Exhausted',
  deliveryErrorCode: i % 3 === 0 ? 'SubmissionOutcomeUnknown' : 'SyntheticRejected', messageText: 'Synthetic delivery needs review.'
}));
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { requests = []; actions = []; removed.clear(); cancelled.clear(); fail = false; return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ requests, actions });
  if (url.pathname === '/fixture/fail') { fail = url.searchParams.get('enabled') === 'true'; return reply({ ok: true }); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', roles: ['HelpdeskAdmin'], exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic operator', userId: 'synthetic-operator', isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: [], managedOrganizationIds: [] });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
  if (url.pathname === '/api/v1/timeline/failed') return reply(data.slice(0, 100));
  if (url.pathname === '/api/v1/timeline/pending') {
    requests.push(Object.fromEntries(url.searchParams));
    if (fail) return reply({}, 503);
    const search = (url.searchParams.get('search') ?? '').toLowerCase();
    let items = data.filter(x => !removed.has(x.id) && !cancelled.has(x.id) && `${x.trackingId} ${x.subject} ${x.emailRecipient}`.toLowerCase().includes(search));
    const total = items.length, size = Number(url.searchParams.get('pageSize') ?? 25), page = Math.min(Number(url.searchParams.get('page') ?? 0), Math.max(0, Math.ceil(total / size) - 1));
    if (url.searchParams.get('descending')?.toLowerCase() !== 'false') items = items.toReversed();
    return reply({ items: items.slice(page * size, (page + 1) * size), total, page, pageSize: size });
  }
  if (url.pathname === '/api/v1/timeline/pending/actions') {
    let body = ''; for await (const part of req) body += part; const action = JSON.parse(body); actions.push(action);
    return reply({ results: action.ids.map(id => {
      const row = data.find(x => x.id === id);
      if (action.action !== 'remove' && !row?.isRetryable) return { id, outcome: 'ReviewRequired', message: 'Individual review required.' };
      if (action.action === 'remove') removed.add(id); else cancelled.add(id);
      return { id, outcome: action.action === 'remove' ? 'Removed' : action.action === 'cancel' ? 'Cancelled' : 'Queued', message: 'Synthetic action persisted.' };
    }) });
  }
  if (url.pathname.endsWith('/pending-count') || url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18700, '127.0.0.1');

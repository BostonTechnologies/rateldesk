// Synthetic presentation fixture only. The owning .NET tests exercise real migrated SQLite/PostgreSQL.
import http from 'node:http';
const id = n => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const mailboxId = id(9001), currentMailboxId = id(9002);
const makeRow = i => ({
  id: id(i + 1), ticketId: `synthetic-${i + 1}`,
  trackingId: `INC-SYN-${String(i + 1).padStart(4, '0')}`, ticketType: 'incidents', subject: `Synthetic delivery ${i + 1}`,
  emailRecipient: `recipient${i + 1}@example.test`, sender: 'support@example.test', organizationName: 'Synthetic organization', organizationId: 'org-synthetic',
  createdUtc: new Date(Date.UTC(2026, 9, 1) + i * 60000).toISOString(), emailStatus: 3, retryCount: 2,
  isRetryable: i % 3 !== 0, canCancel: i % 3 !== 0, canRemove: true, queueState: 'Exhausted',
  deliveryErrorCode: i % 3 === 0 ? 'SubmissionOutcomeUnknown' : 'SyntheticRejected', messageText: 'Synthetic delivery needs review.'
});
let requests, actions, retries, removed, fail, failActions, actionDelay, data, history, operations;
function reset(scenario) {
  operations = new Map(); requests = []; actions = []; retries = []; removed = new Set(); fail = false; failActions = false; actionDelay = 0;
  data = Array.from({ length: scenario === 'bulk' ? 1113 : scenario === 'campaign' ? 619 : scenario === 'retries' ? 5 : 1205 }, (_, i) => makeRow(i));
  if (scenario === 'bulk') data.forEach((row, i) => { row.subject = i < 1106 ? 'Disposable bulk delivery' : 'Unrelated control'; row.canCancel = true; row.deliveryErrorCode = 'SyntheticRejected'; });
  if (scenario === 'campaign') data.forEach((row, i) => {
    row.subject = i < 18 ? 'Disposable campaign delivery' : 'Unrelated baseline delivery';
    row.queueState = 'NeedsReview'; row.deliveryErrorCode = 'NoEffectiveMailbox'; row.sender = null;
    row.isRetryable = false; row.canCancel = true;
  });
  if (scenario === 'retries') data.forEach((row, i) => {
    row.subject = ['Legacy retry', 'Current outgoing retry', 'Uncertain retry', 'Changed route retry', 'Auto-detected route retry'][i];
    row.queueState = i === 0 ? 'Legacy' : 'NeedsReview'; row.isRetryable = true;
    row.deliveryErrorCode = ['Legacy', 'OutgoingRevisionChanged', 'SubmissionOutcomeUnknown', 'SenderRouteChanged', 'OutgoingRevisionChanged'][i];
  });
  history = data.map(row => ({ id: row.id, ticketId: row.ticketId, messageText: row.messageText, recipient: row.emailRecipient }));
}
reset();
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { reset(url.searchParams.get('scenario')); return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ requests, actions, retries, data, history, submissions: [], operations: [...operations.values()] });
  if (url.pathname === '/fixture/fail') { fail = url.searchParams.get('enabled') === 'true'; return reply({ ok: true }); }
  if (url.pathname === '/fixture/action-options') {
    failActions = url.searchParams.get('fail') === 'true'; actionDelay = Number(url.searchParams.get('delay') ?? 0); return reply({ ok: true });
  }
  if (url.pathname === '/fixture/arrival') { const row = makeRow(data.length); row.subject = 'Disposable bulk delivery'; row.canCancel = true; data.push(row); return reply(row); }
  if (url.pathname === '/fixture/change') { const row = data.find(x => x.id === url.searchParams.get('id')); if (row) { row.emailStatus = 1; row.canCancel = false; } return reply(row); }
  const matchRows = query => data.filter(x => !removed.has(x.id) &&
      (query.status == null ? x.emailStatus !== 5 : x.emailStatus === Number(query.status === 'Cancelled' ? 5 : query.status)) &&
      `${x.trackingId} ${x.subject} ${x.emailRecipient}`.toLowerCase().includes((query.search ?? '').toLowerCase()));
  const summary = op => ({ id: op.id, action: op.action, scope: JSON.stringify(op.query), total: op.targets.length,
      eligible: op.targets.filter(x => x.eligible).length, processed: op.results.length,
      completed: op.results.filter(x => ['Cancelled', 'Removed'].includes(x.outcome)).length,
      skipped: op.results.filter(x => x.outcome === 'ReviewRequired').length, conflicted: op.results.filter(x => x.outcome === 'Conflict').length,
      confirmed: Boolean(op.reason), results: op.results.slice(-100) });
  if (url.pathname === '/api/v1/timeline/pending/operations' && req.method === 'POST') {
    let text = ''; for await (const chunk of req) text += chunk; const input = JSON.parse(text);
    const targets = matchRows(input.query).filter(x => (input.allMatching || input.includedIds.includes(x.id)) && !input.excludedIds.includes(x.id))
      .map(row => ({ id: row.id, snapshot: JSON.stringify(row), eligible: input.action === 'cancel' ? row.canCancel : row.canRemove }));
    const op = { id: id(50000 + operations.size), action: input.action, query: input.query, targets, results: [], reason: null };
    operations.set(op.id, op); return reply(summary(op));
  }
  const opMatch = url.pathname.match(/^\/api\/v1\/timeline\/pending\/operations\/([^/]+)(?:\/(confirm|process|results))?$/);
  if (opMatch) {
    const op = operations.get(opMatch[1]); if (!op) return reply({}, 404);
    if (opMatch[2] === 'confirm') {
      let text = ''; for await (const chunk of req) text += chunk; const input = JSON.parse(text);
      if (!input.confirmed || !input.reason.trim() || input.reason.length > 200) return reply({}, 400);
      if (!op.reason) { op.reason = input.reason; actions.push({ ids: op.targets.map(x => x.id), action: op.action, reason: op.reason, confirmed: true }); }
      return reply({});
    }
    if (opMatch[2] === 'process') {
      if (actionDelay) await new Promise(resolve => setTimeout(resolve, actionDelay));
      if (failActions) return reply({}, 503); if (!op.reason) return reply({}, 400);
      for (const target of op.targets.slice(op.results.length, op.results.length + 50)) {
        const row = data.find(x => x.id === target.id);
        const outcome = JSON.stringify(row) !== target.snapshot ? 'Conflict' : !target.eligible ? 'ReviewRequired' : op.action === 'cancel' ? 'Cancelled' : 'Removed';
        if (outcome === 'Cancelled') { row.emailStatus = 5; row.canCancel = false; row.queueState = 'Cancelled'; }
        if (outcome === 'Removed') removed.add(row.id);
        op.results.push({ id: target.id, outcome, message: 'Synthetic action persisted' });
      }
    }
    return reply(opMatch[2] === 'results' ? op.results.slice(Number(url.searchParams.get('page') ?? 0) * 100, (Number(url.searchParams.get('page') ?? 0) + 1) * 100) : summary(op));
  }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', roles: ['HelpdeskAdmin'], exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') return reply({ isAuthenticated: true, email: null, primaryOrganizationId: null, customerId: null, name: 'Synthetic operator', userId: 'synthetic-operator', isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: [], managedOrganizationIds: [] });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
  if (url.pathname === '/api/v1/timeline/failed') return reply(data.slice(0, 100));
  if (url.pathname === '/api/v1/timeline/pending/organizations') return reply([{ id: 'org-synthetic', label: 'Synthetic organization' }]);
  if (url.pathname === '/api/v1/timeline/pending') {
    requests.push(Object.fromEntries(url.searchParams));
    if (fail) return reply({}, 503);
    const search = (url.searchParams.get('search') ?? '').toLowerCase();
    let items = matchRows(Object.fromEntries(url.searchParams));
    const total = items.length, size = Number(url.searchParams.get('pageSize') ?? 25), page = Math.min(Number(url.searchParams.get('page') ?? 0), Math.max(0, Math.ceil(total / size) - 1));
    if (url.searchParams.get('descending')?.toLowerCase() !== 'false') items = items.toReversed();
    return reply({ items: items.slice(page * size, (page + 1) * size), total, page, pageSize: size });
  }
  if (url.pathname === '/api/v1/timeline/pending/actions' && req.method === 'POST') {
    let body = ''; for await (const part of req) body += part; const action = JSON.parse(body); actions.push(action);
    if (actionDelay) await new Promise(resolve => setTimeout(resolve, actionDelay));
    if (failActions) return reply({ error: 'Synthetic action failure' }, 409);
    return reply({ results: action.ids.map(id => {
      const row = data.find(x => x.id === id);
      if (!row) return { id, outcome: 'NotFound', message: 'Delivery unavailable.' };
      if ((action.action === 'retry' && !row.isRetryable) || (action.action === 'cancel' && !row.canCancel))
        return { id, outcome: 'ReviewRequired', message: 'Individual review required.' };
      if (action.action === 'remove') removed.add(id); else if (action.action === 'cancel') row.emailStatus = 5;
      return { id, outcome: action.action === 'remove' ? 'Removed' : action.action === 'cancel' ? 'Cancelled' : 'Queued', message: 'Synthetic action persisted.' };
    }) });
  }
  const retryRoute = url.pathname.match(/^\/api\/v1\/timeline\/([\da-f-]{36})\/(changed-route-preview|outgoing-retry-preview|uncertain-retry-preview|retry|retry-current-outgoing|retry-current-route|retry-confirmed-undelivered)$/);
  if (retryRoute) {
    const [, deliveryId, operation] = retryRoute, row = data.find(x => x.id === deliveryId);
    if (!row) return reply({}, 404);
    if (operation.endsWith('-preview') && req.method === 'GET') {
      if (fail) return reply({}, 503);
      const base = { timelineId: deliveryId, canRetry: true, status: 'Ready', fence: 23, recipients: [row.emailRecipient],
        mailboxId, mailboxAddress: 'support@example.test', transport: 0, originalOutgoingVersion: 4, currentOutgoingVersion: 7,
        originalMailboxVersion: 3, currentMailboxVersion: 5 };
      if (operation === 'changed-route-preview') return reply({ ...base, canRetry: row.id === id(4) || row.id === id(5),
        originalMailboxId: mailboxId, originalMailboxAddress: 'old-support@example.test', currentMailboxId,
        currentMailboxAddress: 'current-support@example.test', currentTransport: 0 });
      return reply(base);
    }
    if (!operation.endsWith('-preview') && req.method === 'POST') {
      let body = ''; for await (const part of req) body += part;
      retries.push({ id: deliveryId, operation, request: body ? JSON.parse(body) : null });
      if (failActions) return reply({}, 409);
      return reply({ queued: true });
    }
    return reply({}, 405);
  }
  // Do not let a misspelled pending-mail endpoint masquerade as successful acceptance.
  if (url.pathname === '/api/v1/timeline/pending-count') return reply(0);
  if (url.pathname.startsWith('/api/v1/timeline/')) return reply({ error: 'Unknown fixture timeline route' }, 404);
  if (url.pathname.endsWith('/pending-count') || url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18700, '127.0.0.1');

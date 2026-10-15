// Loopback-only synthetic data. Real endpoint authorization is covered by .NET API tests.
import http from 'node:http';
let mode = 'light', calls = [];
const light = `<div style="background-color:#ffffff;color:#202020;padding:16px"><h2>Synthetic service notice</h2><p>Readable original foreground</p><table style="background-color:#fff8dc;color:#302010"><tr><td>Priority</td><td>Normal</td></tr></table><p><a href="https://example.test/help">Help documentation</a></p><img src="/api/incidents/incidents-1/images/inline.png?token=synthetic" alt="Inline diagram"><img src="cid:missing" alt="Missing diagram"><img src="https://tracker.example.test/pixel" alt="Remote logo"><p style="color:#303030">Synthetic Support · example.test</p><p>${'Long content '.repeat(120)}</p><script>window.emailExploit=true;parent.emailExploit=true</script><img src=x onerror="window.emailExploit=true;parent.emailExploit=true"><style>body{background:url(https://tracker.example.test/css)}</style><iframe src="https://tracker.example.test/embed"></iframe></div>`;
const defaults = `<div style="background-color:#161616"><p>Background-only default</p></div><div style="color:#eeeeee"><p>Foreground-only default</p></div><p>Unstyled readable default</p>`;
const dark = `<body style="background-color:#161616;color:#eeeeee"><h2>Synthetic dark notice</h2><p>Readable dark foreground</p></body>`;
const ticket = module => ({ id: `${module}-1`, subject: 'Synthetic service notice', title: 'Synthetic service notice', description: '<b>Literal description</b>', trackingId: 'UX-164', organizationId: 'org-1', state: 0, priority: 1, lifecycleState: 0, categoryIds: [], assignedToId: null, implementorUserId: null, requestedForUserId: 'user-1', requestedForUserName: 'Synthetic User', approverUserIds: [], approvers: [], approvals: [], changeTemplate: {}, templateValidationErrors: [], isTemplateComplete: false });
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/health') return reply({ ok: true });
  if (url.pathname === '/fixture/reset') { mode = 'light'; calls = []; return reply({ ok: true }); }
  if (url.pathname === '/fixture/mode') { mode = url.searchParams.get('value'); return reply({ ok: true }); }
  if (url.pathname === '/fixture/state') return reply({ mode, calls });
  calls.push(url.pathname);
  if (url.pathname === '/api/incidents/incidents-1/images/inline.png' && url.searchParams.get('token') === 'synthetic') { res.writeHead(200, { 'Content-Type': 'image/png' }); return res.end(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')); }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-operator', auth_mode: 'development', preferred_username: 'Synthetic Operator', roles: ['HelpdeskAdmin'], allowed_organization_id: 'org-1', exp: Math.floor(Date.now()/1000)+3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Operator', email: 'Synthetic Operator', primaryOrganizationId: null, primaryOrganizationName: null, customerId: null, userId: 'synthetic-operator', isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: ['org-1'], managedOrganizationIds: [] });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk' });
  if (url.pathname === '/api/v1/ticketing/assignees/current') return reply(null);
  if (url.pathname === '/api/v1/ticketing/assignees/search') return reply({ items: [], hasMore: false });
  if (url.pathname === '/api/v1/ticketing/organizations') return reply([{ id: 'org-1', name: 'Synthetic organization', state: 0 }]);
  if (url.pathname.endsWith('/change-participants')) return reply({ organizationId: 'org-1', requestedForUsers: [], approverUsers: [], implementorUsers: [] });
  const match = url.pathname.match(/^\/api\/v1\/(incidents|requests|changes)(?:\/(.*))?$/);
  if (match) {
    const [, module, action] = match;
    if (!action) return reply({ items: [ticket(module)], totalCount: 1 });
    if (action?.endsWith('/peek')) {
      if (mode === 'failure') return reply({ error: 'Synthetic unavailable' }, 503);
      const payload = { html: mode === 'empty' || mode === 'plain' ? '' : mode === 'dark' ? dark : mode === 'defaults' ? defaults : light, text: mode === 'empty' ? '' : 'Synthetic complete plain text. ' + 'Full message '.repeat(50), from: 'sender@example.test', receivedUtc: '2026-10-15T08:00:00Z' };
      if (mode === 'slow') return setTimeout(() => reply(payload), 1500);
      return reply(payload);
    }
    if (action === `${module}-1`) return reply(ticket(module));
    if (action === 'count') return reply(1);
    if (action?.endsWith('/chat-capabilities')) return reply({ enabled: false, reason: 'Synthetic fixture' });
    return reply([]);
  }
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary') || url.pathname.includes('status')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18304, '127.0.0.1', () => console.log('Email fixture on loopback :18304'));

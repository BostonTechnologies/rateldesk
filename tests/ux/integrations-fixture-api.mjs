// Synthetic UI-only loopback fixture. Real lifecycle and authorization run in owning API/provider suites.
import http from 'node:http';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../../src/Helpdesk.Shared/Auth/HelpdeskPermissions.cs', import.meta.url), 'utf8');
const constants = Object.fromEntries([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)].map(match => [match[1], match[2]]));
const permissions = source.match(/AssignablePermissions =\s*\[([\s\S]*?)\]/)[1].split(',').map(value => value.trim()).filter(Boolean).map(name => constants[name]);
if (permissions.some(value => !value)) throw new Error('Fixture must use the complete actual assignable permission catalog');
const organizations = [{ id: 'org-a', name: 'Synthetic Alpha', isEnabled: true }, { id: 'org-b', name: 'Synthetic Beta', isEnabled: true }];
let credentials, calls, fault, systemConnections, aiPrompts, aiInvestigations, policies, policyPreview;
const identifier = i => `00000000-0000-0000-0000-${String(i + 1).padStart(12, '0')}`;
const systemRoot = '/api/v1/admin/system-connections';
const systemPeer = name => ({ contract: 'bostec.pairing.v1', product: 'netratel', installationId: 'synthetic-netratel', name,
 webOrigin: 'https://netratel.example.test', apiOrigin: 'https://api.netratel.example.test', producerInstanceId: 'synthetic-producer' });
const systemDirectory = { netRatelTenants: [{ id: 'tenant-a', name: 'Synthetic monitoring' }, { id: 'tenant-b', name: 'Synthetic second tenant' }],
 ratelDeskOrganizations: organizations, ratelDeskCustomers: organizations.map(o => ({ id: `customer-${o.id}`, name: `${o.name} customer`, parentId: o.id })) };
function reset() {
 credentials = Array.from({ length: 25 }, (_, i) => ({ id: identifier(i), name: `Synthetic credential ${String(i).padStart(2, '0')}`, prefix: `rdk_synthetic${i}`, purpose: i % 2 ? 'mcp' : 'api', organizationId: 'org-a', permissions: ['Incident.Read'], mcpResourceUri: i % 2 ? 'https://127.0.0.1:5190/mcp' : null, createdAtUtc: '2026-10-01T00:00:00Z', expiresAtUtc: i % 3 === 2 ? '2026-01-01T00:00:00Z' : '2030-01-01T00:00:00Z', lastUsedAtUtc: null, revokedAtUtc: i % 3 === 1 ? '2026-10-01T10:00:00Z' : null }));
 systemConnections = Array.from({ length: 6 }, (_, i) => ({ id: `mapping-${i + 1}`, pairId: `pair-${i + 1}`,
  peer: systemPeer(`Synthetic NetRatel ${i + 1}`), status: 'connected', lastTest: null,
  mapping: { id: `mapping-${i + 1}`, pairId: `pair-${i + 1}`, name: `Synthetic system connection ${i + 1}`,
   netRatelTenantId: 'tenant-a', ratelDeskOrganizationId: i % 2 ? 'org-b' : 'org-a', ratelDeskCustomerId: i % 2 ? null : 'customer-org-a', createIncidents: i % 2 === 0, runAutomation: true } }));
 systemConnections.push({ id: 'pair-awaiting', pairId: 'pair-awaiting', mapping: null, peer: systemPeer('Synthetic unfinished NetRatel'), status: 'paired', lastTest: null });
 calls = []; fault = ''; aiPrompts = []; aiInvestigations = []; policies = []; policyPreview = null;
}
reset();
http.createServer(async (req, res) => {
 const url = new URL(req.url, 'http://127.0.0.1'); const path = url.pathname.replace(/\/$/, '');
 const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(status === 204 ? undefined : JSON.stringify(value)); };
 const body = async () => { let text = ''; for await (const chunk of req) { text += chunk; if (text.length > 16384) throw new Error('bounded fixture request'); } return text ? JSON.parse(text) : {}; };
 if (path === '/fixture/health') return reply({ ok: true });
 if (path === '/fixture/reset') { reset(); return reply({ ok: true }); }
 if (path === '/fixture/state') return reply({ calls, credentials, permissions, systemConnections });
 if (path === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; return reply({ ok: true }); }
 if (path.endsWith('/system/token')) {
  const reader = req.headers['x-system-secret'] === 'integrations-fixture-reader';
  const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: reader ? 'synthetic-reader' : 'synthetic-admin', auth_mode: 'development', preferred_username: 'Synthetic Operator', email: 'operator@example.test', organization_id: 'org-a', roles: reader ? ['Incident.Read'] : ['HelpdeskAdmin'], allowed_organization_id: 'org-a', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
 }
 const token = req.headers.authorization?.split(' ')[1];
 const claims = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) : {};
 const admin = claims.roles?.includes('HelpdeskAdmin') ?? false;
 if (path === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Operator', email: 'operator@example.test', userId: claims.sub, primaryOrganizationId: 'org-a', customerId: null, isHelpdeskAdmin: admin, roleBundles: claims.roles ?? [], permissions: claims.roles ?? [], allowedOrganizationIds: ['org-a'], managedOrganizationIds: [] });
 if (path === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
 if (path === '/api/v1/organizations') return reply(admin ? organizations : organizations.slice(0, 1));
 if (path === '/api/v1/integration-credentials' && req.method === 'GET') return reply(credentials);
 if (path.startsWith('/api/v1/ai/ops')) {
  if (!admin) return reply({}, 403);
  if (path.endsWith('/tickets')) return reply([{ id: 'ticket-incident', type: 'incidents', trackingId: 'INC-1', title: 'Synthetic incident' }, { id: 'ticket-task', type: 'tasks', trackingId: 'TASK-1', title: 'Synthetic task' }]);
  if (path.endsWith('/prompts') && req.method === 'GET') return reply(aiPrompts);
  if (req.method === 'PUT' && path.includes('/prompts/')) {
   const payload = await body(); calls.push({ path, method: req.method, payload });
   const prompt = { ...payload, id: path.split('/').at(-1), revision: payload.expectedRevision + 1 };
   aiPrompts = aiPrompts.filter(p => p.id !== prompt.id).concat(prompt); return reply(prompt);
  }
  if (path.endsWith('/investigations') && req.method === 'GET') return reply(aiInvestigations);
  if (path.endsWith('/investigations') && req.method === 'POST') {
   const payload = await body(); calls.push({ path, method: req.method, payload });
   const investigation = { ...payload, ticketId: payload.ticketIds[0], ticketType: 'incidents', state: 0, createdUtc: new Date().toISOString(), contextSnapshot: JSON.stringify({ guidance: aiPrompts, tickets: payload.ticketIds }) };
   aiInvestigations.push(investigation); return reply(investigation);
  }
  if (req.method === 'GET') return reply(aiInvestigations.find(i => path.endsWith(i.id)) ?? {}, aiInvestigations.some(i => path.endsWith(i.id)) ? 200 : 404);
 }
 if (path.endsWith('/ai-assistant/chat-capabilities')) return reply({ enabled: false, reason: 'Synthetic UI fixture: no external transport' });
 if (path.startsWith('/api/v1/admin/mcp-policies')) {
  if (!admin) return reply({}, 403);
  if (path.endsWith('/credentials')) return reply([{ id: identifier(1), name: 'Separate synthetic operator', owner: 'other-owner', organizationId: 'org-a', expiresAtUtc: '2030-01-01T00:00:00Z' }]);
  if (req.method === 'GET') return reply(path.endsWith('/audit') ? [] : policies);
  const payload = await body(); calls.push({ path, method: req.method, payload });
  if (path.endsWith('/preview')) { policyPreview = payload; return reply({ confirmation: 'synthetic-confirmation', policy: payload, effect: 'Narrow the separate operator credential' }); }
  if (path.endsWith('/confirm')) { const policy = { ...policyPreview, revision: policyPreview.expectedRevision + 1 }; policies = [policy]; return reply(policy); }
 }

 if (path.startsWith(systemRoot)) {
  if (!admin) return reply({}, 403);
  if (req.method === 'GET' && path === systemRoot) return reply(systemConnections);
  if (req.method === 'GET' && path.endsWith('/directory')) return reply(systemDirectory);
  const payload = await body(); calls.push({ path, method: req.method, payload });
  if (path === `${systemRoot}/code`) return reply({ code: 'FAKE-CODE', expiresAtUtc: new Date(Date.now() + 300_000).toISOString() });
  if (path === `${systemRoot}/pair`) {
   const connection = { id: 'pair-new', pairId: 'pair-new', mapping: null, peer: systemPeer('Synthetic paired NetRatel'), status: 'paired', lastTest: null };
   systemConnections.push(connection); return reply(connection);
  }
  const parts = path.slice(systemRoot.length + 1).split('/');
  const connection = systemConnections.find(c => c.pairId === parts[0] && (!parts[2] || c.mapping?.id === parts[2]));
  if (req.method === 'PUT' && parts[1] === 'mappings') {
   if (fault === 'mapping-save') { fault = ''; return reply({ message: 'Synthetic mapping save unavailable; retry.', reference: 'synthetic-save' }, 503); }
   const saved = { id: payload.id, pairId: payload.pairId, peer: connection?.peer ?? systemPeer('Synthetic unfinished NetRatel'), status: 'connected', mapping: payload, lastTest: null };
   systemConnections = systemConnections.filter(c => c.id !== payload.id && !(c.pairId === payload.pairId && c.mapping === null));
   systemConnections.push(saved); return reply(saved);
  }
  if (req.method === 'POST' && parts[3] === 'test') {
   await new Promise(resolve => setTimeout(resolve, 250));
   if (fault === 'system-test') { fault = ''; return reply({ message: 'Synthetic connection test unavailable; retry.', reference: 'synthetic-test' }, 503); }
   const result = { success: true, message: 'Synthetic authorized connection test passed', testedAtUtc: new Date().toISOString() };
   if (connection) connection.lastTest = result;
   return reply(result);
  }
  if (req.method === 'DELETE') {
   systemConnections = systemConnections.filter(c => parts[2] ? c.mapping?.id !== parts[2] : c.pairId !== parts[0]);
   return reply({}, 204);
  }
  return reply({}, 404);
 }
 if (req.method !== 'GET' && path.startsWith('/api/')) {
  const payload = await body(); calls.push({ path, method: req.method, payload });
  if (path.endsWith('/review')) {
   const code = payload.humanCode.replaceAll('-', '').replaceAll(' ', '').toUpperCase();
   if (path.includes('host-proposals')) {
    if (!admin) return reply({}, 403);
    if (code !== 'HOSTTEST') return reply({}, 404);
    return reply({ humanCode: 'HOST-TEST', expiresAtUtc: '2030-01-01T00:00:00Z', proposal: { name: 'Synthetic HTTP MCP host', proposalHash: 'a'.repeat(64), sourceHash: 'b'.repeat(64), publicOrigin: 'https://127.0.0.1:5190', resourceUri: 'https://127.0.0.1:5190/mcp', instance: 'office-private', authenticationMode: 'gateway', apiBaseUrl: 'http://api:8080', mcpDestination: 'http://mcp-http:8080', reviewSettings: [{ key: 'AllowedOrigins', value: 'https://127.0.0.1:5190', source: 'Compose environment' }, { key: 'RequestTimeoutSeconds', value: '45', source: 'appsettings.json' }], changes: ['Use request-scoped gateway execution', 'Keep existing private API destination', 'Preserve selected durable volumes'] } });
   }
   if (!['ABCDTEST', 'TEMPTEST'].includes(code)) return reply({}, 404);
   const verifier = code === 'TEMPTEST';
   return reply({ humanCode: verifier ? 'TEMP-TEST' : 'ABCD-TEST', name: verifier ? 'Synthetic status verification' : 'Synthetic ordinary client', purpose: 'mcp', mcpResourceUri: 'https://127.0.0.1:5190/mcp', grantKind: verifier ? 'setup-verification' : 'ordinary', expiresAtUtc: '2030-01-01T00:00:00Z', organizations: organizations.map((o, i) => ({ ...o, permissions: verifier ? [] : i ? ['Incident.Read'] : permissions })) });
  }
  if (path.endsWith('/approve') || path.endsWith('/deny')) {
   if (fault === 'consent') { fault = ''; return reply({}, 503); }
   if (!payload.consent && path.endsWith('/approve')) return reply({}, 400);
   return reply({}, 204);
  }
  if (req.method === 'DELETE') {
   const id = path.split('/')[4];
   if (fault === 'partial' && id.replaceAll('-', '') === identifier(2).replaceAll('-', '')) return reply({}, 503);
   const row = credentials.find(c => c.id.replaceAll('-', '') === id.replaceAll('-', ''));
   if (!row) return reply({}, 404);
   if (path.endsWith('/record')) credentials = credentials.filter(c => c !== row); else row.revokedAtUtc = new Date().toISOString();
   return reply({}, 204);
  }
  return reply({});
 }
 // Keep the real notification badge present alongside consent acknowledgement.
 if (path === '/api/v1/notifications/error-summary') return reply({ unreadErrorCount: 1, unreadCount: 1, totalCount: 1 });
 if (path.endsWith('/count')) return reply(0);
 if (path.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
 if (path.includes('summary') || path.includes('status')) return reply({});
 return reply([]);
}).listen(18505, '127.0.0.1', () => console.log('Synthetic integrations fixture listening on loopback :18505'));

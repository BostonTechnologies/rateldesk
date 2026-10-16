// Synthetic UI-only loopback fixture. Real lifecycle and authorization run in owning API/provider suites.
import http from 'node:http';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../../src/Helpdesk.Shared/Auth/HelpdeskPermissions.cs', import.meta.url), 'utf8');
const constants = Object.fromEntries([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)].map(match => [match[1], match[2]]));
const permissions = source.match(/AssignablePermissions =\s*\[([\s\S]*?)\]/)[1].split(',').map(value => value.trim()).filter(Boolean).map(name => constants[name]);
if (permissions.some(value => !value)) throw new Error('Fixture must use the complete actual assignable permission catalog');
const organizations = [{ id: 'org-a', name: 'Synthetic Alpha', isEnabled: true }, { id: 'org-b', name: 'Synthetic Beta', isEnabled: true }];
let credentials, calls, fault;
const identifier = i => `00000000-0000-0000-0000-${String(i + 1).padStart(12, '0')}`;
function reset() {
 credentials = Array.from({ length: 25 }, (_, i) => ({ id: identifier(i), name: `Synthetic credential ${String(i).padStart(2, '0')}`, prefix: `rdk_synthetic${i}`, purpose: i % 2 ? 'mcp' : 'api', organizationId: 'org-a', permissions: ['Incident.Read'], mcpResourceUri: i % 2 ? 'https://127.0.0.1:5190/mcp' : null, createdAtUtc: '2026-10-01T00:00:00Z', expiresAtUtc: i % 3 === 2 ? '2026-01-01T00:00:00Z' : '2030-01-01T00:00:00Z', lastUsedAtUtc: null, revokedAtUtc: i % 3 === 1 ? '2026-10-01T10:00:00Z' : null }));
 calls = []; fault = '';
}
reset();
http.createServer(async (req, res) => {
 const url = new URL(req.url, 'http://127.0.0.1'); const path = url.pathname.replace(/\/$/, '');
 const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(status === 204 ? undefined : JSON.stringify(value)); };
 const body = async () => { let text = ''; for await (const chunk of req) { text += chunk; if (text.length > 16384) throw new Error('bounded fixture request'); } return text ? JSON.parse(text) : {}; };
 if (path === '/fixture/health') return reply({ ok: true });
 if (path === '/fixture/reset') { reset(); return reply({ ok: true }); }
 if (path === '/fixture/state') return reply({ calls, credentials, permissions });
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
 if (path === '/api/v1/admin/system-connections' || path === '/api/v1/admin/system-connections/directory') return reply([]);
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
 if (path.endsWith('/count')) return reply(0);
 if (path.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
 if (path.includes('summary') || path.includes('status')) return reply({});
 return reply([]);
}).listen(18505, '127.0.0.1', () => console.log('Synthetic integrations fixture listening on loopback :18505'));

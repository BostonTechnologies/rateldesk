// Synthetic HTTPS OIDC authority against the published Authentik ingress mode.
// This tests the mode's protocol, not a live third-party Authentik deployment.
import http from 'node:http';
import https from 'node:https';
import { createPrivateKey, sign, randomUUID } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, chmod, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';

const [image, expectedDigest, output, localAssembly] = process.argv.slice(2);
if (!image || !expectedDigest || !output) throw new Error('Expected measured image, config digest and output directory');
await mkdir(output, { recursive: true });
const temporary = await mkdtemp(path.join(tmpdir(), 'rd-oidc-fixture-'));
await chmod(temporary, 0o755);
const name = 'rd-authentik-' + randomUUID();
const report = { status: 'FAIL', authenticationMode: 'authentik', scope: 'Published mode with synthetic trusted HTTPS OIDC issuer and separate downstream API identity; no live Authentik tenant', checks: [], backendRequests: [] };
let issuer, backend, child;
const b64 = value => Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64url');
const secret = 'rdk_campaign_separate_api_fixture';
const issued = [];
const backendState = {};
function within(promise, message) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(message)), 10000);
    promise.then(value => { clearTimeout(timer); resolve(value); }, error => { clearTimeout(timer); reject(error); });
  });
}
try {
  const keyPath = path.join(temporary, 'key.pem'), certPath = path.join(temporary, 'ca.pem');
  const generated = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-keyout', keyPath, '-out', certPath,
    '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1', '-addext', 'basicConstraints=critical,CA:TRUE'], { stdio: 'ignore' });
  assert.equal(generated.status, 0);
  await chmod(certPath, 0o644);
  const key = createPrivateKey(await readFile(keyPath));
  const jwk = key.export({ format: 'jwk' });
  let authority;
  issuer = https.createServer({ key: await readFile(keyPath), cert: await readFile(certPath) }, (request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/.well-known/openid-configuration') response.end(JSON.stringify({ issuer: authority, jwks_uri: authority + '/jwks', authorization_endpoint: authority + '/authorize', token_endpoint: authority + '/token', response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'] }));
    else if (request.url === '/jwks') response.end(JSON.stringify({ keys: [{ kty: 'RSA', kid: 'campaign-key', use: 'sig', alg: 'RS256', n: jwk.n, e: jwk.e }] }));
    else { response.statusCode = 404; response.end('{}'); }
  });
  await new Promise(resolve => issuer.listen(0, '127.0.0.1', resolve));
  authority = 'https://localhost:' + issuer.address().port;
  backend = http.createServer((request, response) => {
    const authenticated = request.headers.authorization === 'Bearer ' + secret;
    report.backendRequests.push({ method: request.method, path: request.url, authenticated });
    response.setHeader('Content-Type', 'application/json');
    if (!authenticated) { response.statusCode = 401; response.end('{}'); return; }
    if (request.method !== 'GET') { response.statusCode = 405; response.end('{}'); return; }
    if (request.url.startsWith('/api/v1/system/version') && backendState.hold) {
      response.on('close', () => { if (!response.writableFinished) backendState.closed?.(); });
      backendState.entered?.();
    }
    else if (request.url.startsWith('/api/v1/system/version')) response.end(JSON.stringify({ version: '0.0.0-fixture', product: 'RatelDesk' }));
    else if (request.url.startsWith('/api/v1/auth/ai-agent/status')) response.end(JSON.stringify({ enabled: true, authenticated: true, permissions: ['Incident.Read'] }));
    else { response.statusCode = 404; response.end('{}'); }
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));
  const resource = 'https://mcp.fixture.test/mcp';
  const settings = {
    ASPNETCORE_URLS: 'http://127.0.0.1:8223', SSL_CERT_FILE: localAssembly ? certPath : '/acceptance-ca.pem',
    Helpdesk__Mcp__AuthenticationMode: 'authentik', Helpdesk__Mcp__PublicResourceUri: resource,
    Helpdesk__Mcp__Instance: 'authentik-fixture', Helpdesk__Mcp__ExpectedApiBaseUrl: 'http://127.0.0.1:' + backend.address().port,
    Helpdesk__Mcp__AllowedOrigins__0: 'https://mcp.fixture.test',
    Helpdesk__Mcp__Outbound__ApiBaseUrl: 'http://127.0.0.1:' + backend.address().port,
    Helpdesk__Mcp__Outbound__CredentialMode: 'integration', Helpdesk__Mcp__Outbound__IntegrationCredential: secret,
    Authentication__AuthentikMcp__Authority: authority, Authentication__AuthentikMcp__Audience: resource,
    Authentication__AuthentikMcp__RequiredScopes__0: 'helpdesk_mcp', Authentication__AuthentikMcp__RequiredGroups__0: 'helpdesk-users'
  };
  const environment = { ...process.env, ...settings };
  const log = [];
  const starting = performance.now();
  if (localAssembly) child = spawn('dotnet', [localAssembly], { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  else {
    const args = ['run', '--rm', '--name', name, '--network', 'host', '--mount', `type=bind,src=${certPath},dst=/acceptance-ca.pem,readonly`];
    for (const key of Object.keys(settings)) args.push('--env', key);
    args.push(image);
    child = spawn('docker', args, { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  }
  child.stdout.on('data', data => log.push(data.toString())); child.stderr.on('data', data => log.push(data.toString()));
  let ready = false;
  for (let attempt = 0; attempt < 120 && child.exitCode === null; attempt++) {
    try { if ((await fetch('http://127.0.0.1:8223/health/live')).ok) { ready = true; break; } } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  if (!ready) throw new Error('Authentik mode did not become ready: ' + log.join('').slice(-1500));
  report.firstHealthReadyMilliseconds = Math.round((performance.now() - starting) * 1000) / 1000;
  if (!localAssembly) {
    const observed = spawnSync('docker', ['inspect', '--format', '{{.Image}}', name], { encoding: 'utf8' });
    assert.equal(observed.stdout.trim(), expectedDigest);
    assert.equal(spawnSync('docker', ['exec', name, 'id', '-u'], { encoding: 'utf8' }).stdout.trim(), '10001');
    report.executedConfigDigest = observed.stdout.trim();
  } else report.scope = 'Local assembly prototype; not image acceptance';
  function token(changes = {}) {
    const now = Math.floor(Date.now() / 1000);
    const payload = { iss: authority, aud: resource, sub: 'synthetic-user', iat: now - 10, nbf: now - 10, exp: now + 600, scope: 'helpdesk_mcp', groups: ['helpdesk-users'], ...changes };
    const input = b64({ alg: 'RS256', typ: 'JWT', kid: 'campaign-key' }) + '.' + b64(payload);
    const value = input + '.' + sign('RSA-SHA256', Buffer.from(input), key).toString('base64url'); issued.push(value); return value;
  }
  let identifier = 0;
  async function call(method, params, bearer, { origin, raw, signal } = {}) {
    const id = ++identifier;
    const response = await fetch('http://127.0.0.1:8223/mcp', { method: 'POST', signal, headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18', ...(bearer ? { Authorization: 'Bearer ' + bearer } : {}), ...(origin ? { Origin: origin } : {}) }, body: raw ?? JSON.stringify({ jsonrpc: '2.0', id, method, params }) });
    const text = await response.text();
    let value;
    if (response.ok) {
      const candidates = text.startsWith('data:') || text.startsWith('event:') ? text.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => JSON.parse(line.slice(5))) : [JSON.parse(text)];
      value = candidates.find(item => item.id === id) ?? candidates.at(-1);
    }
    return { status: response.status, value };
  }
  const parameters = { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'campaign-oidc-fixture', version: '0.0.0' } };
  const invalid = token().split('.'); invalid[2] = (invalid[2][0] === 'A' ? 'B' : 'A') + invalid[2].slice(1);
  for (const [label, bearer, status] of [['missing', null, 401], ['invalid signature', invalid.join('.'), 401], ['wrong audience', token({ aud: 'https://other.fixture.test/mcp' }), 401], ['wrong issuer', token({ iss: 'https://other.fixture.test' }), 401], ['expired', token({ nbf: 1, exp: 2 }), 401], ['missing scope', token({ scope: 'unrelated' }), 403], ['missing group', token({ groups: ['unrelated'] }), 403]]) {
    assert.equal((await call('initialize', parameters, bearer)).status, status, label);
    report.checks.push({ kind: label, expectedStatus: status, status: 'PASS' });
  }
  assert.equal(report.backendRequests.length, 0, 'Rejected ingress reached the downstream identity');
  const valid = token();
  assert.equal((await call('initialize', parameters, valid)).status, 200);
  report.checks.push({ kind: 'Valid signed token initialize over trusted HTTPS metadata/JWKS', status: 'PASS' });
  for (const [method, key, expected] of [['tools/list', 'tools', 37], ['resources/list', 'resources', 4], ['prompts/list', 'prompts', 4], ['resources/templates/list', 'resourceTemplates', 0]]) {
    const values = []; let cursor;
    do { const response = await call(method, cursor ? { cursor } : {}, valid); assert.equal(response.status, 200); assert.ok(response.value.result); values.push(...response.value.result[key]); cursor = response.value.result.nextCursor; } while (cursor);
    assert.equal(values.length, expected);
    (report.catalog ??= {})[key] = values;
    report.checks.push({ kind: method, count: values.length, status: 'PASS' });
  }
  const result = await call('tools/call', { name: 'helpdesk_system', arguments: { operation: 'version' } }, valid);
  assert.equal(result.status, 200); assert.equal(result.value.result.structuredContent.success, true);
  assert.ok(report.backendRequests.length > 0); assert.ok(report.backendRequests.every(item => item.method === 'GET' && item.authenticated));
  report.checks.push({ kind: 'Separate API credential used downstream; ingress JWT never forwarded', status: 'PASS' });
  assert.equal((await call('initialize', parameters, valid, { origin: 'https://unapproved.fixture.test' })).status, 403);
  report.checks.push({ kind: 'Unapproved browser origin rejected', status: 'PASS' });
  const metadata = await fetch('http://127.0.0.1:8223/.well-known/oauth-protected-resource/mcp');
  assert.equal(metadata.status, 200); assert.equal((await metadata.json()).resource, resource);
  report.checks.push({ kind: 'Protected resource metadata preserves canonical audience', status: 'PASS' });
  const beforeDuplicate = report.backendRequests.length;
  const duplicate = await call('tools/call', {}, valid, { raw: '{"jsonrpc":"2.0","id":999,"method":"tools/call","params":{"name":"helpdesk_system","arguments":{"operation":"version","operation":"version"}}}' });
  assert.equal(duplicate.status, 200);
  assert.equal(duplicate.value.result.isError, true);
  assert.equal(duplicate.value.result.structuredContent.success, false);
  assert.equal(duplicate.value.result.structuredContent.status, 'validation_failed');
  assert.equal(duplicate.value.result.structuredContent.failure.code, 'validation_error');
  assert.equal(report.backendRequests.length, beforeDuplicate);
  report.checks.push({ kind: 'Duplicate argument rejected before downstream access', status: 'PASS' });
  const controller = new AbortController();
  backendState.hold = true;
  const entered = new Promise(resolve => { backendState.entered = resolve; });
  const closed = new Promise(resolve => { backendState.closed = resolve; });
  const pending = call('tools/call', { name: 'helpdesk_system', arguments: { operation: 'version' } }, valid, { signal: controller.signal });
  pending.catch(() => {});
  await within(entered, 'Cancellation fixture did not reach the backend');
  controller.abort();
  await pending.then(() => assert.fail('Canceled MCP request completed normally'), error => assert.equal(error.name, 'AbortError'));
  await within(closed, 'HTTP MCP cancellation did not close the actual backend request');
  backendState.hold = false;
  assert.equal((await call('tools/call', { name: 'helpdesk_system', arguments: { operation: 'version' } }, valid)).value.result.structuredContent.success, true);
  report.checks.push({ kind: 'HTTP cancellation closes backend request and next call succeeds', status: 'PASS' });
  assert.ok(!log.join('').includes(secret) && issued.every(value => !log.join('').includes(value)), 'Diagnostics exposed a fixture credential');
  report.status = 'PASS';
} catch (error) { report.error = String(error); }
finally {
  if (localAssembly) child?.kill('SIGTERM');
  else spawnSync('docker', ['rm', '--force', name], { stdio: 'ignore', timeout: 15000 });
  await Promise.all([new Promise(resolve => issuer ? issuer.close(resolve) : resolve()), new Promise(resolve => backend ? backend.close(resolve) : resolve())]);
  await rm(temporary, { recursive: true, force: true });
  await writeFile(path.join(output, 'authentik.json'), JSON.stringify(report, null, 2) + '\n');
}
console.log(JSON.stringify({ status: report.status, checks: report.checks.length, error: report.error }));
process.exitCode = report.status === 'PASS' ? 0 : 1;

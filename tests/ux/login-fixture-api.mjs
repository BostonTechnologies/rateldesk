// Loopback presentation fixture. Authentication correctness is tested through the real API in .NET.
import http from 'node:http';
import { createHash, generateKeyPairSync, randomBytes, randomUUID, sign } from 'node:crypto';

const issuer = 'http://127.0.0.1:18499/id';
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const keyId = 'synthetic-provider-key';
const codes = new Map();
const releases = new Map();
const scopedGrants = new Map();
const base64 = value => Buffer.from(JSON.stringify(value)).toString('base64url');
const jwt = payload => {
  const data = base64({ alg: 'RS256', kid: keyId, typ: 'JWT' }) + '.' + base64(payload);
  return data + '.' + sign('RSA-SHA256', Buffer.from(data), privateKey).toString('base64url');
};
const access = {
  isAuthenticated: true, userId: 'synthetic-oidc-operator', name: 'Synthetic Operator', email: 'operator@example.test',
  primaryOrganizationId: null, customerId: null, usesScopedPermissions: false,
  isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin'], permissions: [], allowedOrganizationIds: [],
  managedOrganizationIds: [], scopedPermissionGrants: []
};
const parseForm = async req => new URLSearchParams(await body(req));
async function body(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}
const localWeb = url => {
  const parsed = URL.parse(url ?? '');
  return parsed && parsed.origin.match(/^http:\/\/127\.0\.0\.1:1850[0-2]$/) ? parsed : null;
};
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGNgYPgPAAEDAQAIicLsAAAAAElFTkSuQmCC', 'base64');
let logo = '/branding/rateldesk-mark.webp';
let applicationName = 'RatelDesk';
let publicViews = 0, publicTimelines = 0;
let captcha = null;
const origin = 'http://127.0.0.1:18499';
http.createServer(async (req, res) => {
  const url = new URL(req.url, origin);
  const reply = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
  const fail = status => { res.statusCode = status; return reply({ rejected: true }); };
  if (url.pathname === '/fixture/public-stats') {
    if (url.searchParams.has('reset')) { publicViews = 0; publicTimelines = 0; releases.clear(); scopedGrants.clear(); }
    return reply({ publicViews, publicTimelines, exchanges: releases.size });
  }
  if (url.pathname === '/fixture/email') {
    const target = localWeb(url.searchParams.get('to'));
    if (!target) return fail(400);
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    return res.end('<a href="' + target.origin + '/public/link#' + 'a'.repeat(43) + '">Open ticket from email</a>');
  }
  if (url.pathname === '/fixture/referrer') return reply({ referrer: req.headers.referer ?? null });
  if (process.env.HELPDESK_E2E_ASSET_API_BASE && /^\/api\/(incidents|worklogs)\/browser-fixture\/images\//.test(url.pathname)) {
    const asset = await fetch(new URL(url.pathname + url.search, process.env.HELPDESK_E2E_ASSET_API_BASE));
    res.statusCode = asset.status;
    for (const header of ['content-type', 'content-security-policy', 'x-content-type-options', 'cache-control', 'referrer-policy']) {
      if (asset.headers.has(header)) res.setHeader(header, asset.headers.get(header));
    }
    return res.end(Buffer.from(await asset.arrayBuffer()));
  }
  if (url.pathname === '/api/v1/captcha') {
    const relay = req.headers['x-rateldesk-client-proof'];
    if (!relay) return fail(400);
    captcha = { id: randomUUID(), relay };
    return reply({ id: captcha.id, imageBase64: 'data:image/png;base64,' + png.toString('base64') });
  }
  if (url.pathname === '/api/v1/public/tickets/submit' && req.method === 'POST') {
    const challenge = captcha;
    captcha = null;
    const input = JSON.parse(await body(req));
    const relay = req.headers['x-rateldesk-client-proof'];
    if (!challenge || challenge.id !== input.captchaId || input.captchaAnswer !== '123456' || !relay || relay === challenge.relay)
      return fail(400);
    return reply({ id: 'synthetic-ticket', trackingId: 'INC-BROWSER-FIXTURE' });
  }
  if (url.pathname === '/api/v1/public/access/exchange') {
    if (req.method !== 'POST' || req.headers.authorization || req.headers.cookie || url.search) return fail(400);
    const input = JSON.parse(await body(req));
    const legacy = input.legacy?.token === 'signed-public-fixture' && input.legacy.email === 'customer@example.test';
    const launch = legacy ? 'a'.repeat(43) : input.launch;
    if (![ 'a'.repeat(43), 'b'.repeat(43), 'c'.repeat(43) ].includes(launch)) return fail(401);
    if (releases.has(launch)) {
      const existing = releases.get(launch);
      return existing.browserKey === input.browserKey ? reply(existing.dto) : fail(401);
    }
    const dto = { accessId: randomUUID(), grant: randomBytes(32).toString('base64url'),
      purpose: launch === 'c'.repeat(43) ? 3 : 0, expiresAt: new Date(Date.now() + 600_000).toISOString() };
    const scope = { dto, browserKey: input.browserKey, launch, reviewed: false };
    releases.set(launch, scope);
    scopedGrants.set(dto.accessId, scope);
    return reply(dto);
  }
  if (url.pathname.startsWith('/api/v1/public/access/')) {
    const [, , , , , id, resource, detail] = url.pathname.split('/');
    const scope = scopedGrants.get(id);
    if (req.headers.authorization || req.headers.cookie || url.search || !scope ||
        req.headers['x-rateldesk-public-grant'] !== scope.dto.grant) return fail(401);
    const second = scope.launch.startsWith('b');
    if (resource === 'ticket') {
      publicViews++;
      return reply({ trackingId: second ? 'UX-SECOND' : 'UX-PUBLIC',
        title: second ? 'Second synthetic ticket' : 'Signed public fixture ticket', state: 0 });
    }
    if (resource === 'timeline') {
      publicTimelines++;
      return reply([{ eventType: 0, isCustomer: false, hours: 0.25, messageText: 'Public fixture worklog',
        messageHtml: '<p>Public fixture worklog</p><img alt="Public fixture screenshot" src="/public/access/' + id + '/asset/fixture">',
        createdUtc: '2026-10-29T10:00:00Z' }]);
    }
    if (resource === 'asset') {
      res.setHeader('Content-Type', 'image/png');
      return res.end(png);
    }
    if (resource === 'request' && detail === 'decision' && req.method === 'POST') {
      const input = JSON.parse(await body(req));
      if (!input.approve) return fail(400);
      scope.reviewed = true;
      res.statusCode = 204;
      return res.end();
    }
    if (resource === 'request') return reply({ trackingId: 'REQ-EXAMPLE', requestTitle: 'Synthetic approval', requestDescription: 'A deliberate decision',
      taskName: 'Review request', approvalStatus: scope.reviewed ? 2 : 0, canReview: !scope.reviewed, payloadFields: [] });
    return fail(404);
  }
  if (url.pathname === '/fixture/branding') {
    logo = url.searchParams.get('logo') || '/branding/rateldesk-mark.webp';
    applicationName = url.searchParams.get('name') || 'RatelDesk';
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/opaque.svg') {
    res.setHeader('Content-Type', 'image/svg+xml');
    return res.end('<svg xmlns="http://www.w3.org/2000/svg" width="180" height="60"><rect width="180" height="60" fill="white"/><text x="12" y="38" fill="black" font-size="24">Custom Logo</text></svg>');
  }
  if (url.pathname === '/fixture/transparent.svg') {
    res.setHeader('Content-Type', 'image/svg+xml');
    return res.end('<svg xmlns="http://www.w3.org/2000/svg" width="180" height="60"><rect x="4" y="4" width="172" height="52" rx="12" fill="#c43df0"/></svg>');
  }
  if (url.pathname === '/api/v1/setup/status') return reply({ state: 'Ready' });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName, compactLogoUrl: logo, faviconUrl: '/favicon.ico' });
  if (url.pathname === '/api/v1/notifications/unread-errors' || url.pathname === '/api/v1/notifications/unread') return reply([]);
  if (url.pathname === '/id/.well-known/openid-configuration')
    return reply({ issuer, authorization_endpoint: origin + '/authorize', token_endpoint: origin + '/token',
      end_session_endpoint: origin + '/end-session', jwks_uri: origin + '/jwks', response_types_supported: ['code'],
      subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'] });
  if (url.pathname === '/jwks') return reply({ keys: [{ ...publicKey.export({ format: 'jwk' }), kid: keyId, use: 'sig', alg: 'RS256' }] });
  if (url.pathname === '/authorize') {
    const target = localWeb(url.searchParams.get('redirect_uri'));
    if (!target || url.searchParams.get('client_id') !== 'presentation-client') return fail(400);
    const code = randomUUID();
    codes.set(code, { nonce: url.searchParams.get('nonce'), challenge: url.searchParams.get('code_challenge') });
    target.searchParams.set('code', code);
    target.searchParams.set('state', url.searchParams.get('state') ?? '');
    res.writeHead(302, { Location: target.toString() });
    return res.end();
  }
  if (url.pathname === '/token' && req.method === 'POST') {
    const form = await parseForm(req);
    const code = form.get('code');
    const issued = codes.get(code);
    codes.delete(code);
    if (!issued || !issued.challenge || createHash('sha256').update(form.get('code_verifier') ?? '').digest('base64url') !== issued.challenge)
      return fail(400);
    const issuedAt = Math.floor(Date.now() / 1000);
    const token = jwt({ iss: issuer, aud: 'presentation-client', sub: access.userId, email: access.email,
      preferred_username: access.name, nonce: issued.nonce, iat: issuedAt, exp: issuedAt + 3600 });
    return reply({ id_token: token, access_token: token, token_type: 'Bearer', expires_in: 3600 });
  }
  if (url.pathname === '/end-session') {
    const target = localWeb(url.searchParams.get('post_logout_redirect_uri'));
    if (!target) return fail(400);
    if (url.searchParams.has('state')) target.searchParams.set('state', url.searchParams.get('state'));
    res.writeHead(302, { Location: target.toString() });
    return res.end();
  }
  if (url.pathname === '/api/v1/auth/me' || url.pathname === '/api/v1/users/provision') return reply(access);
  return reply({ ok: true });
}).listen(18499, '127.0.0.1');

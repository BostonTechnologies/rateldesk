// Loopback presentation fixture. Authentication correctness is tested through the real API in .NET.
import http from 'node:http';
let logo = '/branding/rateldesk-mark.webp';
const origin = 'http://127.0.0.1:18499';
http.createServer((req, res) => {
  const url = new URL(req.url, origin);
  const reply = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/branding') {
    logo = url.searchParams.get('logo') || '/branding/rateldesk-mark.webp';
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
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', compactLogoUrl: logo, faviconUrl: '/favicon.ico' });
  if (url.pathname === '/id/.well-known/openid-configuration') return reply({ issuer: origin + '/id', authorization_endpoint: origin + '/authorize', token_endpoint: origin + '/token', jwks_uri: origin + '/jwks', response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'] });
  if (url.pathname === '/jwks') return reply({ keys: [] });
  return reply({ ok: true });
}).listen(18499, '127.0.0.1');

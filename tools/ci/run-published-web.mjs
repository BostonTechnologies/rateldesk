// Playwright-owned disposable container for the existing synthetic API fixtures.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, rm, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

if (process.platform !== 'linux' || !process.env.HELPDESK_PUBLISHED_WEB_IMAGE) throw new Error('Published Web fixtures require native Linux and an explicit local image');
const settings = JSON.parse(process.env.HELPDESK_PUBLISHED_WEB_ENV ?? '{}');
if (!settings.ASPNETCORE_URLS || !settings.ApiBaseUrl) throw new Error('The owning fixture must supply its listener and synthetic API');
const name = 'rd-published-web-' + randomUUID();
const directory = await mkdtemp(path.join(tmpdir(), 'rd-browser-certificate-'));
const args = ['run', '--rm', '--name', name, '--label', 'rateldesk.acceptance.published-web=true', '--network', 'host'];
let stopping = false;
try {
  if (settings.ASPNETCORE_URLS.includes('https:')) {
    const key = path.join(directory, 'key.pem'), cert = path.join(directory, 'certificate.pem'), pfx = path.join(directory, 'fixture.pfx');
    let result = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-keyout', key, '-out', cert,
      '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'], { stdio: 'ignore' });
    if (result.status !== 0) throw new Error('Cannot generate disposable browser certificate');
    const password = randomUUID();
    result = spawnSync('openssl', ['pkcs12', '-export', '-inkey', key, '-in', cert, '-out', pfx, '-passout', 'fd:0'], { input: password + '\n', stdio: ['pipe', 'ignore', 'ignore'] });
    if (result.status !== 0) throw new Error('Cannot package disposable browser certificate');
    args.push('--mount', `type=bind,src=${directory},dst=/acceptance-certificate,readonly`);
    settings.ASPNETCORE_Kestrel__Certificates__Default__Path = '/acceptance-certificate/fixture.pfx';
    settings.ASPNETCORE_Kestrel__Certificates__Default__Password = password;
  }
  // Values come exclusively from this suite's explicit configuration, never the
  // ambient integration identity. Only variable names appear in Docker argv.
  for (const key of Object.keys(settings)) args.push('--env', key);
  args.push(process.env.HELPDESK_PUBLISHED_WEB_IMAGE);
  const child = spawn('docker', args, { stdio: 'inherit', env: { ...process.env, ...settings } });
  const exited = new Promise((resolve, reject) => { child.on('error', reject); child.on('exit', code => resolve(code)); });
  for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => {
    stopping = true;
    spawnSync('docker', ['stop', '--time', '5', name], { stdio: 'ignore', timeout: 15000 });
  });
  let observed;
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline && child.exitCode === null) {
    const inspected = spawnSync('docker', ['inspect', '--format', '{{.Image}}', name], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 10000 });
    if (inspected.status === 0) { observed = inspected.stdout.trim(); break; }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!observed || observed !== process.env.HELPDESK_PUBLISHED_WEB_CONFIG_DIGEST) throw new Error('Owning browser fixture did not start the measured image');
  const evidence = process.env.HELPDESK_PUBLISHED_WEB_PROVENANCE;
  if (!evidence) throw new Error('Owning browser fixture must retain executed-image provenance');
  await mkdir(evidence, { recursive: true });
  await writeFile(path.join(evidence, name + '.json'), JSON.stringify({ observedConfigDigest: observed, suite: process.env.HELPDESK_PUBLISHED_SUITE,
    authenticationMode: settings.Authentication__Mode ?? 'owning development fixture' }, null, 2) + '\n');
  const code = await exited;
  process.exitCode = stopping ? 0 : (code ?? 1);
} finally {
  spawnSync('docker', ['rm', '--force', name], { stdio: 'ignore', timeout: 15000 });
  await rm(directory, { recursive: true, force: true });
}

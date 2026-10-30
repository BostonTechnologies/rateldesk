import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:18505';
const directory = '/api/v1/ai/ops/organizations';

test('integration fixture serves the current investigation directory contract', async t => {
 const child = spawn(process.execPath, [fileURLToPath(new URL('./integrations-fixture-api.mjs', import.meta.url))], {
  windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
 });
 let ready = false, diagnostics = '';
 child.stdout.on('data', chunk => { ready ||= chunk.toString().includes('listening on loopback'); });
 child.stderr.on('data', chunk => { diagnostics += chunk; });
 try {
  for (let attempts = 0; !ready && attempts < 100; attempts++) {
   assert.equal(child.exitCode, null, `Fixture exited before listening: ${diagnostics}`);
   await delay(25);
  }
  assert.ok(ready, 'The owned fixture must start before any contract checks.');
  await t.test('anonymous requests remain denied', async () => {
   const response = await fetch(origin + directory);
   assert.equal(response.status, 403);
  });
  for (const [identity, names] of [
   ['admin', [{ id: 'org-a', name: 'Synthetic Alpha' }, { id: 'org-b', name: 'Synthetic Beta' }]],
   ['reader', [{ id: 'org-a', name: 'Synthetic Alpha' }]]
  ]) {
   await t.test(`${identity} receives only its directory choices`, async () => {
    const tokenResponse = await fetch(origin + '/api/v1/system/token', {
     headers: { 'x-system-secret': `integrations-fixture-${identity}` }
    });
    assert.equal(tokenResponse.status, 200);
    const { token } = await tokenResponse.json();
    const response = await fetch(origin + directory, { headers: { authorization: `Bearer ${token}` } });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), names);
   });
  }
 } finally {
  if (child.exitCode === null) {
   const exited = once(child, 'exit');
   child.kill();
   await exited;
  }
 }
});

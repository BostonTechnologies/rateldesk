import { expect, test, type Page, type Route } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const profile = {
  isAuthenticated: true, isHelpdeskAdmin: false, email: 'reader@example.test',
  primaryOrganizationId: 'tenant-a', customerId: null, usesScopedPermissions: true,
  roleBundles: ['Role.User'], permissions: ['Incident.Read'], allowedOrganizationIds: ['tenant-a'],
  scopedPermissionGrants: [{ permission: 'Incident.Read', organizationId: 'tenant-a' }]
};
const rendered = { ...profile, roleBundles: ['Role.User', 'Incident.Read'], permissions: ['Role.User', 'Incident.Read'] };

async function serve(page: Page, api: (route: Route) => Promise<void>) {
  await page.route('**/*', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith('/js/')) {
      const source = readFileSync(resolve('src/HelpDesk.NewWeb/wwwroot', path.slice(1)), 'utf8');
      return route.fulfill({ contentType: 'text/javascript', body: source });
    }
    if (path === '/session/access' || path.startsWith('/api/')) return api(route);
    return route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><body>Session fixture</body></html>' });
  });
  // All requests, including this document, are fulfilled by the local fixture.
  await page.goto('http://127.0.0.1:9/');
}

test('session monitor compares the rendered grants on the first response without bundle-shape reload loops', async ({ page }) => {
  let navigations = 0;
  let calls = 0;
  let firstPoll: Route | undefined;
  let grantsChanged = false;
  await serve(page, async route => {
    calls++;
    if (calls === 1) { firstPoll = route; return; }
    return route.fulfill({ json: grantsChanged ? { ...profile, roleBundles: [], permissions: [], scopedPermissionGrants: [] } : profile });
  });
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.evaluate(async initial => {
    const session = await import('/js/session-access.js');
    session.start(initial);
  }, rendered);
  await expect.poll(() => calls).toBe(1);
  expect(navigations).toBe(0);
  // A focus event while the first response is pending is intentionally coalesced.
  // Seeing the request arrive does not mean its grants have been compared yet.
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  expect(calls).toBe(1);
  await firstPoll!.fulfill({ json: profile });
  await expect.poll(async () => {
    if (calls < 2) await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    return calls;
  }).toBe(2);
  expect(navigations).toBe(0);
  grantsChanged = true;
  await expect.poll(async () => {
    if (calls < 3) await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    return calls;
  }).toBe(3);
  await expect.poll(() => navigations).toBe(1);
});

test('a permission change before the first poll reloads the stale rendered principal', async ({ page }) => {
  let navigations = 0;
  await serve(page, route => route.fulfill({ json: profile }));
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.evaluate(async initial => {
    const session = await import('/js/session-access.js');
    session.start(initial);
  }, { ...rendered, permissions: [...rendered.permissions, 'Incident.Write'] });
  await expect.poll(() => navigations).toBe(1);
});

test('account mutation waits for an old poll and suppresses its unauthorized redirect before replacing the cookie', async ({ page, context }) => {
  let oldPoll: Route | undefined;
  let polls = 0;
  let mutations = 0;
  let navigations = 0;
  await serve(page, async route => {
    if (new URL(route.request().url()).pathname === '/session/access') {
      polls++;
      if (polls === 1) { oldPoll = route; return; }
      return route.fulfill({ json: profile });
    }
    mutations++;
    return route.fulfill({ json: { sharedKey: 'test-only-key' }, headers: { 'Set-Cookie': 'session=new; Path=/' } });
  });
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.evaluate(async initial => {
    const session = await import('/js/session-access.js');
    session.start(initial);
  }, rendered);
  await expect.poll(() => polls).toBe(1);
  await page.evaluate(async () => {
    const account = await import('/js/local-account.js');
    (window as any).mutation = account.post('/api/v1/local-auth/two-factor/setup', { currentPassword: 'synthetic-test-value' });
  });
  expect(mutations).toBe(0);
  await oldPoll!.fulfill({ status: 401, headers: { 'Set-Cookie': 'session=old; Path=/' } });
  const result = await page.evaluate(() => (window as any).mutation);
  expect(result.ok).toBe(true);
  expect(mutations).toBe(1);
  expect(navigations).toBe(0);
  expect((await context.cookies()).find(cookie => cookie.name === 'session')?.value).toBe('new');
});

import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { assertNoHorizontalOverflow, authenticate, selectTheme } from './auth';

// The manual journey reveals a synthetic credential. Keep automatic artifacts off for this suite.
test.use({ trace: 'off', screenshot: 'off' });

test.beforeEach(async ({ page }, testInfo) => {
  if (testInfo.title.startsWith('signed-out peer')) return;
  if (testInfo.title.startsWith('manual UI')) {
    const email = process.env.HELPDESK_E2E_LOCAL_EMAIL;
    const password = process.env.HELPDESK_E2E_LOCAL_PASSWORD;
    if (!email || !password) throw new Error('The service-client journey requires the synthetic local administrator fixture. Run tools/ci/run-ux-local.sh.');
    await page.goto('/login');
    const login = page.getByTestId('local-login-form');
    await expect(login).toHaveAttribute('data-interactive', 'true');
    await login.getByLabel('Email', { exact: true }).fill(email);
    await login.getByLabel('Password', { exact: true }).fill(password);
    await login.getByRole('button', { name: /^Sign in to / }).click();
    await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
  } else {
    await authenticate(page);
  }
});

test.afterEach(async ({ page }) => {
  if (!page.isClosed()) await page.evaluate(() => document.documentElement.replaceChildren()).catch(() => {});
});

test('NetRatel service mode requires explicit grants and fits mobile light/dark layouts', async ({ page }, testInfo) => {
  await page.goto('/account/integration-credentials');
  await expect(page.getByTestId('integration-credentials-page')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('tab', { name: 'System connections', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'API & MCP credentials', exact: true }).click();
  await expect(page.getByTestId('integration-credential-create')).toBeVisible();
  await expect(page.getByTestId('netratel-service-link-list')).not.toBeVisible();
  await page.getByRole('tab', { name: 'System connections', exact: true }).click();
  await expect(page.getByTestId('integration-credential-create')).not.toBeVisible();
  await page.getByRole('button', { name: 'Connect NetRatel', exact: true }).click();
  await expect(page.getByTestId('netratel-m2m-form')).toBeVisible();
  await expect(page.getByTestId('netratel-service-link-list')).toHaveCount(0);
  await expect(page.getByTestId('netratel-link-start').getByRole('button', { name: 'Connect NetRatel', exact: true })).toBeDisabled();
  await expect(page.getByTestId('netratel-link-start').locator('input[name="requestedResponderTenantId"]')).toHaveValue('');
  await page.getByTestId('netratel-manual-mode').click();
  await expect(page.getByRole('combobox', { name: 'NetRatel → RatelDesk permissions', exact: true })).toHaveValue('3 selected permissions');
  await expect(page.getByLabel('Verified NetRatel instance ID', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create and reveal service client', exact: true })).toBeDisabled();
  await expect(page.getByLabel('New client secret', { exact: true })).toHaveCount(0);
  for (const theme of ['Light', 'Dark'] as const) {
    await selectTheme(page, theme);
    await page.setViewportSize({ width: 390, height: 844 });
    await assertNoHorizontalOverflow(page);
    await expect(page.getByTestId('netratel-m2m-form')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`netratel-manual-${theme.toLowerCase()}-mobile.png`), fullPage: true, animations: 'disabled' });
    await page.setViewportSize({ width: 1440, height: 900 });
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`netratel-manual-${theme.toLowerCase()}-desktop.png`), fullPage: true, animations: 'disabled' });
  }
  await page.getByTestId('netratel-guided-mode').click();
  await expect(page.getByTestId('netratel-guided-mode')).toBeEnabled();
  await expect(page.getByText('Create helpdesk incidents from monitoring alerts. Sign in at NetRatel and approve the connection; you return here automatically.', { exact: true })).toBeVisible();
  await expect(page.getByText('Connections enabled locally. Peer approval and verification are still required.', { exact: true })).toBeVisible();
  await expect(page.getByTestId('netratel-link-start').locator('input[name="__RequestVerificationToken"]')).toHaveCount(1);
  const stored = await page.evaluate(() => Object.keys(localStorage).concat(Object.keys(sessionStorage)));
  expect(stored.some(key => /service.?link|pairing|verifier|browser.?state/i.test(key))).toBe(false);
  await page.screenshot({ path: testInfo.outputPath('netratel-guided-dark-desktop.png'), fullPage: true, animations: 'disabled' });
});

test('a failed proof has a clean review page and a truthful manual compatibility path', async ({ page }) => {
  const response = await page.goto('/account/integration-credentials/link/result?status=upgrade-required&stage=start&correlationId=0123456789abcdef0123456789abcdef');
  expect(response?.headers()['cache-control']).toContain('no-store');
  expect(response?.headers()['referrer-policy']).toBe('no-referrer');
  await expect(page.getByText(/The peer does not support the required connection contract/)).toBeVisible();
  await expect(page.getByRole('link', { name: 'Correct setup and retry', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Resume/ })).toHaveCount(0);
  await page.getByText('Manual connection settings', { exact: true }).click();
  await expect(page.getByRole('link', { name: 'Manual outbound settings', exact: true })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.goto('/account/integration-credentials/link/callback?attempt_id=synthetic-attempt&pairing_code=synthetic-proof&browser_state=synthetic-state&responder_instance_id=synthetic-peer&oauth_issuer=https%3A%2F%2Fnetratel.example.invalid');
  expect(new URL(page.url()).pathname).toBe('/account/integration-credentials/link/result');
  expect(new URL(page.url()).searchParams.has('pairing_code')).toBe(false);
  expect(new URL(page.url()).searchParams.has('browser_state')).toBe(false);
  await expect(page.getByText(/The approval session is no longer valid/)).toBeVisible();
});

test('signed-out peer approval discards correlation before ordinary sign-in', async ({ page }) => {
  const approval = '/account/integration-credentials/link/approve?initiator_web_base_url=https%3A%2F%2Fnetratel.example.test&attempt_id=synthetic-attempt&browser_state=synthetic-private-correlation';
  const rejected = await page.request.get(approval, { maxRedirects: 0 });
  expect(rejected.status()).toBe(302);
  expect(rejected.headers().location).toBe('/login?ReturnUrl=%2Faccount%2Fintegration-credentials%2Flink%2Fresume-sign-in');
  expect(rejected.headers()['cache-control']).toContain('no-store');
  expect(rejected.headers()['referrer-policy']).toBe('no-referrer');
  await page.goto(approval);
  expect(new URL(page.url()).pathname).toBe('/login');
  expect(new URL(page.url()).searchParams.get('ReturnUrl')).toBe('/account/integration-credentials/link/resume-sign-in');
  const login = page.getByTestId('local-login-form');
  await expect(login).toHaveAttribute('data-interactive', 'true');
  expect((await login.innerHTML()).includes('synthetic-private-correlation')).toBe(false);
  const stored = await page.evaluate(() => Object.keys(localStorage).concat(Object.keys(sessionStorage)));
  expect(stored.some(key => /service.?link|pairing|verifier|browser.?state/i.test(key))).toBe(false);
  const email = process.env.HELPDESK_E2E_LOCAL_EMAIL;
  const password = process.env.HELPDESK_E2E_LOCAL_PASSWORD;
  if (!email || !password) throw new Error('The sign-in continuation journey requires the synthetic local administrator fixture. Run tools/ci/run-ux-local.sh.');
  await login.getByLabel('Email', { exact: true }).fill(email);
  await login.getByLabel('Password', { exact: true }).fill(password);
  await login.getByRole('button', { name: /^Sign in to / }).click();
  await expect(page.getByTestId('service-link-consent-page')).toBeVisible();
  expect(new URL(page.url()).pathname).toBe('/account/integration-credentials/link/result');
  expect(new URL(page.url()).searchParams.has('browser_state')).toBe(false);
});

test('manual UI creates a live scoped client, hides its secret and revokes cached authorization', async ({ page, request }) => {
  test.setTimeout(90_000);
  const sourceInstance = randomUUID();
  const serviceName = `Synthetic UI NetRatel ${randomUUID()}`;
  const customerName = `Synthetic receiver customer ${randomUUID()}`;
  const apiBase = process.env.HELPDESK_E2E_API_BASE_URL ?? 'http://127.0.0.1:5158';
  // The normal UX fixture has an organization but no customer. Provision this test's explicit target through its existing authorized UI.
  await page.goto('/admin/customers');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  const customerDialog = page.getByRole('dialog');
  await customerDialog.getByLabel('Name', { exact: true }).fill(customerName);
  await customerDialog.getByLabel('Email', { exact: true }).fill(`synthetic-${randomUUID()}@example.test`);
  await customerDialog.getByRole('combobox', { name: 'Organization', exact: true }).click();
  await page.getByRole('option').first().click();
  const organizationId = await customerDialog.locator('input[aria-label="Organization"]').inputValue();
  await customerDialog.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(customerDialog).toHaveCount(0);
  await page.goto('/account/integration-credentials?purpose=netratel-m2m');
  await expect(page.getByTestId('integration-credentials-page')).toHaveAttribute('data-interactive', 'true');
  await page.getByTestId('netratel-manual-mode').click();
  await page.getByRole('combobox', { name: 'Local organization', exact: true }).click();
  await page.getByRole('option').filter({ hasText: `(${organizationId})` }).click();
  await page.getByRole('combobox', { name: 'Approved incident customer', exact: true }).click();
  await page.getByRole('option').filter({ hasText: customerName }).click();
  await page.getByLabel('Service name', { exact: true }).fill(serviceName);
  await page.getByLabel('Verified NetRatel instance ID', { exact: true }).fill(`synthetic-netratel-${randomUUID()}`);
  await page.getByLabel('Approved NetRatel tenant ID', { exact: true }).fill('42');
  await page.getByLabel('NetRatel Flow source instance ID', { exact: true }).fill(sourceInstance);
  await page.getByRole('button', { name: 'Create and reveal service client', exact: true }).click();
  await expect(page.getByTestId('netratel-secret-reveal')).toBeVisible();
  const clientId = await page.getByLabel('Client ID', { exact: true }).inputValue();
  const clientSecret = await page.getByLabel('New client secret', { exact: true }).inputValue();
  const tokenEndpoint = await page.getByLabel('Token endpoint', { exact: true }).inputValue();
  const issuance = await request.post(tokenEndpoint, { form: {
    grant_type: 'client_credentials', client_id: clientId, client_secret: clientSecret,
    scope: 'rateldesk.incident-receipts.read'
  } });
  expect(issuance.status()).toBe(200);
  const accessToken = (await issuance.json()).access_token;
  const headers = { Authorization: `Bearer ${accessToken}`, 'X-NetRatel-Source-Instance': sourceInstance };
  const capabilitiesUrl = `${apiBase}/api/v1/integrations/netratel/capabilities`;
  const capabilities = await request.get(capabilitiesUrl, { headers });
  expect(capabilities.status()).toBe(200);
  const granted = await capabilities.json();
  expect(granted.sourceInstanceId).toBe(sourceInstance);
  expect(granted.authenticationModes).toContain('oauth_client_credentials');
  const forbidden = await request.get(`${apiBase}/api/v1/incidents/`, { headers });
  expect([401, 403]).toContain(forbidden.status());
  await page.getByRole('button', { name: 'I have saved it', exact: true }).click();
  await expect(page.getByLabel('New client secret', { exact: true })).toHaveCount(0);
  const containsSecret = await page.evaluate(secret =>
    document.body.innerText.includes(secret) || [...document.querySelectorAll('input')].some(input => input.value.includes(secret)), clientSecret);
  expect(containsSecret).toBe(false);
  await page.getByText('Service clients and rotation', { exact: true }).click();
  const row = page.getByRole('row').filter({ has: page.getByText(clientId, { exact: true }) });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Rotate', exact: true }).click();
  await row.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(page.getByTestId('netratel-secret-reveal')).toBeVisible();
  await expect(page.getByLabel('Client ID', { exact: true })).toHaveValue(clientId);
  const replacementSecret = await page.getByLabel('New client secret', { exact: true }).inputValue();
  expect(replacementSecret === clientSecret).toBe(false);
  const replacementIssuance = await request.post(tokenEndpoint, { form: {
    grant_type: 'client_credentials', client_id: clientId, client_secret: replacementSecret,
    scope: 'rateldesk.incident-receipts.read'
  } });
  expect(replacementIssuance.status()).toBe(200);
  const replacementToken = (await replacementIssuance.json()).access_token;
  const replacementHeaders = { Authorization: `Bearer ${replacementToken}`, 'X-NetRatel-Source-Instance': sourceInstance };
  const rotatedCapabilities = await request.get(capabilitiesUrl, { headers: replacementHeaders });
  expect(rotatedCapabilities.status()).toBe(200);
  expect((await rotatedCapabilities.json()).sourceNamespaceId).toBe(granted.sourceNamespaceId);
  await page.getByRole('button', { name: 'I have saved it', exact: true }).click();
  await row.getByRole('button', { name: 'Revoke', exact: true }).click();
  await row.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(row.getByText(/revoked/)).toBeVisible();
  const revoked = await request.get(capabilitiesUrl, { headers });
  expect([401, 403]).toContain(revoked.status());
  const revokedReplacement = await request.get(capabilitiesUrl, { headers: replacementHeaders });
  expect([401, 403]).toContain(revokedReplacement.status());
  await page.reload();
  await expect(page.getByLabel('New client secret', { exact: true })).toHaveCount(0);
});

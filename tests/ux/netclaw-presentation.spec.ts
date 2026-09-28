import { expect, test, type Page } from '@playwright/test';
import { assertNoHorizontalOverflow, selectTheme } from './auth';

// Pairing inputs are secrets even in this deterministic presentation fixture.
// Explicit screenshots are taken only after the code field is empty or hidden.
test.use({ trace: 'off', screenshot: 'off' });

test.afterEach(async ({ page }) => {
  if (page.isClosed()) return;
  // The one-time code is deliberately typed in the legacy and uncertainty paths.
  // Clear the live DOM before Playwright writes any failure context artifact.
  await page.evaluate(() => document.documentElement.replaceChildren());
});

async function setNetclawFixtureMode(page: Page, mode: 'first-run' | 'legacy' | 'connected' | 'uncertain'): Promise<void> {
  const response = await page.request.get(`http://127.0.0.1:18299/fixture/netclaw?mode=${mode}`);
  expect(response.ok()).toBe(true);
}

async function setStoredTheme(page: Page, mode: 'light' | 'dark'): Promise<void> {
  await page.evaluate(value => localStorage.setItem('helpdesk.theme.preference', value), mode);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', mode);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

async function authenticateFixture(page: Page): Promise<void> {
  await page.goto('/auth/development');
  await page.goto('/home');
  await expect(page.getByRole('heading', { name: 'Operations Dashboard' })).toBeVisible();
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

async function expectSyntheticFirstRun(page: Page): Promise<void> {
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByRole('heading', { name: 'Netclaw AI harness' })).toBeVisible();
  await expect(page.getByTestId('netclaw-connection-status')).toHaveText('Not connected');

  const address = page.getByLabel('Netclaw address', { exact: true });
  const code = page.getByLabel('Pairing code', { exact: true });
  await expect(address).toBeVisible();
  await expect(address).toHaveAttribute('placeholder', 'http://192.168.1.20:5199');
  await expect(code).toBeVisible();
  await expect(code).toHaveAttribute('type', 'password');
  await expect(code).toHaveValue('');
  await expect(page.getByTestId('pair-and-connect-netclaw')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save settings' })).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Test draft' })).not.toBeVisible();
  await expect(page.getByRole('checkbox')).toHaveCount(0);
  await expect(page.getByTestId('netclaw-legacy-ownership')).not.toBeVisible();
  await expect(page.locator('input[type="password"]:visible')).toHaveCount(1);
  await assertNoHorizontalOverflow(page);
}

async function expectFixtureSecretAbsent(page: Page, secret: string): Promise<void> {
  const found = await page.evaluate(value => {
    const html = document.documentElement.outerHTML;
    const text = document.body.innerText;
    const inputValues = Array.from(document.querySelectorAll('input'), input => (input as HTMLInputElement).value);
    return html.includes(value) || text.includes(value) || inputValues.includes(value);
  }, secret);
  expect(found).toBe(false);
}

test('Netclaw first run stays compact at desktop in light and dark themes', async ({ page }, testInfo) => {
  await setNetclawFixtureMode(page, 'first-run');
  await authenticateFixture(page);
  await page.goto('/admin/automation/integration/netclaw');
  await expectSyntheticFirstRun(page);
  const cardWidth = await page.getByTestId('netclaw-settings-page').locator('.netclaw-card').evaluate(element => element.getBoundingClientRect().width);
  expect(cardWidth).toBeLessThanOrEqual(850);
  expect(cardWidth).toBeGreaterThan(700);

  await selectTheme(page, 'Light');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'light');
  await expectSyntheticFirstRun(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-desktop-light.png'), fullPage: true });

  await selectTheme(page, 'Dark');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'dark');
  await expectSyntheticFirstRun(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-desktop-dark.png'), fullPage: true });

  const address = page.getByLabel('Netclaw address', { exact: true });
  const code = page.getByLabel('Pairing code', { exact: true });
  await address.focus();
  await expect(address).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(code).toBeFocused();
});

test('Netclaw first run fits a 390px viewport in light and dark themes', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'light',
    ignoreHTTPSErrors: true
  });
  const page = await context.newPage();
  await setNetclawFixtureMode(page, 'first-run');
  await authenticateFixture(page);
  await page.goto('/admin/automation/integration/netclaw');
  await expectSyntheticFirstRun(page);

  const action = page.locator('.netclaw-primary-action');
  const pairButton = page.getByTestId('pair-and-connect-netclaw');
  const [actionWidth, buttonWidth] = await Promise.all([
    action.evaluate(element => element.getBoundingClientRect().width),
    pairButton.evaluate(element => element.getBoundingClientRect().width)
  ]);
  expect(buttonWidth).toBeGreaterThanOrEqual(actionWidth - 2);

  await setStoredTheme(page, 'light');
  await expectSyntheticFirstRun(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-mobile-light.png'), fullPage: true });

  await setStoredTheme(page, 'dark');
  await expectSyntheticFirstRun(page);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-mobile-dark.png'), fullPage: true });
  await context.close();
});

test('Netclaw same-server review is compact and continues with the pending code', async ({ page }, testInfo) => {
  await setNetclawFixtureMode(page, 'legacy');
  await authenticateFixture(page);
  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Netclaw address', { exact: true }).fill('http://192.168.1.20:5199');
  await page.getByLabel('Pairing code', { exact: true }).fill('SYNTHETIC-CODE');
  await expect(page.getByTestId('pair-and-connect-netclaw')).toBeEnabled();
  await page.getByTestId('pair-and-connect-netclaw').click();

  await expect(page.getByTestId('netclaw-legacy-confirmation')).toBeVisible();
  await expect(page.getByText('2 previous AI Assistant conversations')).toBeVisible();
  await expect(page.getByText('Did those conversations use this same Netclaw server?')).toBeVisible();
  await expect(page.getByText('http://192.168.1.20:5199', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Yes — use this server and continue' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'No — review them separately' })).toBeVisible();
  await expect(page.getByTestId('netclaw-legacy-ownership')).not.toBeVisible();
  await expect(page.locator('input[type="password"]:visible')).toHaveCount(0);
  await expect(page.getByText('Fixture-only untrusted detail must not be rendered.')).not.toBeVisible();
  await expectFixtureSecretAbsent(page, 'SYNTHETIC-CODE');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-legacy-review.png'), fullPage: true });

  await page.getByTestId('confirm-same-netclaw-server').click();
  await expect(page.getByTestId('netclaw-connection-status')).toHaveText('Connected');
  await expect(page.getByTestId('netclaw-connected-summary').locator('p.netclaw-muted-copy')).toContainText('Authenticated SignalR verified');
  await expect(page.getByText('SYNTHETIC-CODE')).not.toBeVisible();
});

test('Netclaw connected state reports the authenticated check without exposing a token', async ({ page }, testInfo) => {
  await setNetclawFixtureMode(page, 'connected');
  await authenticateFixture(page);
  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');

  await expect(page.getByTestId('netclaw-connection-status')).toHaveText('Connected');
  await expect(page.getByTestId('netclaw-connected-summary')).toBeVisible();
  await expect(page.getByTestId('netclaw-connected-summary').locator('p.netclaw-muted-copy')).toContainText('Authenticated SignalR verified');
  await expect(page.getByRole('button', { name: 'Test connection' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit / Re-pair' })).toBeVisible();
  await expect(page.getByLabel('Pairing code', { exact: true })).not.toBeVisible();
  await expect(page.getByLabel('Paired-device token', { exact: true })).not.toBeVisible();
  await expectFixtureSecretAbsent(page, 'FIXTURE-DEVICE-TOKEN');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-connected.png'), fullPage: true });
});

test('Netclaw uncertain saved-token state offers inspection and test before another pairing', async ({ page }, testInfo) => {
  await setNetclawFixtureMode(page, 'uncertain');
  await authenticateFixture(page);
  await page.goto('/admin/automation/integration/netclaw');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Netclaw address', { exact: true }).fill('http://192.168.1.20:5199');
  await page.getByLabel('Pairing code', { exact: true }).fill('SYNTHETIC-CODE');
  await expect(page.getByTestId('pair-and-connect-netclaw')).toBeEnabled();
  await page.getByTestId('pair-and-connect-netclaw').click();
  await expect(page.getByTestId('netclaw-saved-state-review')).toBeVisible();
  const fixtureStateResponse = await page.request.get('http://127.0.0.1:18299/fixture/netclaw');
  expect(fixtureStateResponse.ok()).toBe(true);
  expect((await fixtureStateResponse.json()).pairResponseStatuses).toEqual([502]);
  const savedStateReview = page.getByTestId('netclaw-saved-state-review');
  await expect(savedStateReview.getByTestId('netclaw-pairing-status'))
    .toContainText('Pairing may have been saved, but authenticated SignalR verification is uncertain.');
  await expect(savedStateReview.getByText('Protected token', { exact: true })).toBeVisible();
  await expect(savedStateReview.getByText('Configured', { exact: true })).toBeVisible();
  await expect(page.getByTestId('test-netclaw-saved-state')).toBeVisible();
  await expect(page.getByTestId('continue-after-saved-state-review')).toBeVisible();
  await expect(page.getByLabel('Pairing code', { exact: true })).not.toBeVisible();
  await expect(page.locator('input[type="password"]:visible')).toHaveCount(0);
  await expectFixtureSecretAbsent(page, 'SYNTHETIC-CODE');
  await expect(page.getByText('Fixture-only untrusted detail must not be rendered.')).not.toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('netclaw-fixture-uncertain-saved-token.png'), fullPage: true });

  await page.getByTestId('test-netclaw-saved-state').click();
  await expect(page.getByTestId('netclaw-connection-status')).toHaveText('Connected');
  await expect(page.getByTestId('netclaw-connected-summary')).toBeVisible();
  await expect(page.getByTestId('netclaw-connected-summary').locator('p.netclaw-muted-copy')).toContainText('Authenticated SignalR verified');
  await expect(page.getByLabel('Pairing code', { exact: true })).not.toBeVisible();
});

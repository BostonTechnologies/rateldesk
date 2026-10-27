// Real disposable host/client consent. Secret input is stdin only; do not trace,
// screenshot, log requests, or include browser exception/DOM text in failures.
import { chromium, expect } from '@playwright/test';
import { expectConsentAcknowledged } from './mcp-host-browser-acknowledgement.mjs';

let browser;
let stage = 'bounded input';
let page, codeInput, reviewButton;
let request;
try {
  let input = '';
  for await (const data of process.stdin) {
    input += data;
    if (input.length > 16384) throw new Error('input bound');
  }
  request = JSON.parse(input);
  const origin = new URL(request.origin);
  if (origin.protocol !== 'https:' || !(origin.hostname === 'localhost' || origin.hostname.endsWith('.fixture.test'))) throw new Error('fixture origin required');
  browser = await chromium.launch({
    executablePath: process.env.RATELDESK_ACCEPTANCE_CHROMIUM || undefined,
    args: [`--host-resolver-rules=MAP ${origin.hostname} 127.0.0.1`, '--no-proxy-server'],
  });
  const context = await browser.newContext({ baseURL: origin.origin, ignoreHTTPSErrors: true });
  // Typography is unrelated to local consent. Keep this isolated browser from
  // waiting on a public font service while every application request stays real.
  await context.route(/^https:\/\/(?:fonts\.googleapis\.com|fonts\.gstatic\.com)\//, route => route.abort());
  page = await context.newPage();
  page.setDefaultTimeout(30000);
  stage = 'local sign-in page navigation';
  await page.goto('/login', { waitUntil: 'domcontentloaded' });
  stage = 'interactive local sign-in renderer';
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  stage = 'local account form input';
  await page.getByLabel('Email', { exact: false }).fill(request.email);
  await page.getByLabel('Password', { exact: false }).fill(request.password);
  stage = 'local account form submission';
  await page.getByRole('button', { name: /^Sign in$/ }).click();
  stage = 'local account signed-in navigation';
  await expect(page).toHaveURL(/\/home(?:[?#].*)?$/);
  stage = 'approval page navigation';
  await page.goto('/account/integration-credentials/approve', { waitUntil: 'domcontentloaded' });
  const host = request.kind === 'host';
  reviewButton = page.getByRole('button', { name: host ? 'Review host' : 'Review client', exact: true });
  stage = 'interactive approval renderer';
  // The SSR form is visible before Blazor attaches its input handlers. Filling
  // it before its explicit interactive gate can lose the code on hydration.
  await expect(reviewButton).toBeEnabled();
  codeInput = page.getByLabel(host ? 'Host code' : 'Client code', { exact: true });
  stage = 'approval code input';
  await codeInput.fill(request.code);
  stage = 'connection review request';
  await reviewButton.click();
  const dialog = page.getByRole('dialog', { name: host ? 'Host deployment consent' : 'Connection consent', exact: true });
  await expect(dialog).toBeVisible();
  stage = 'explicit scoped consent';
  if (host) {
    await expect(dialog).toContainText(origin.origin);
    for (const summary of request.summaries || []) await expect(dialog).toContainText(summary);
    if (request.sourceDiff) {
      const diff = dialog.locator('li').filter({ hasText: /Source diff:.*WebGateway__Mcp__Enabled/ }).first();
      await diff.scrollIntoViewIfNeeded();
      await expect(diff).toBeVisible();
    }
    // beta.3 native setup also reviews an unchanged beta.2 server. Both known
    // labels require the same explicit exact-generation host consent.
    await dialog.getByRole('checkbox', { name: /^(?:I approve this exact baseline or configuration proposal and its source generation\.|I authorize these exact deployment settings and source generation\.)$/ }).check();
  } else {
    stage = 'organization selection';
    await dialog.getByLabel('Organization', { exact: true }).selectOption(request.organizationId);
    if (request.kind === 'verifier') {
      stage = 'temporary verifier identity';
      await expect(dialog).toContainText('Temporary verification client');
      stage = 'temporary verifier permission controls';
      await expect(dialog.getByTestId('credential-permission-selector')).toHaveCount(0);
      stage = 'temporary verifier lifetime controls';
      await expect(dialog.locator('[name="lifetimeDays"]')).toHaveCount(0);
      stage = 'temporary verifier expiry disclosure';
      await expect(dialog).toContainText('five minutes');
      stage = 'temporary verifier consent';
      await dialog.getByRole('checkbox', { name: 'I approve only this temporary status verification.', exact: true }).check();
    } else {
      for (const permission of request.permissions) await dialog.getByRole('checkbox', { name: permission, exact: true }).check();
      await expect(dialog.getByTestId('credential-permission-selector').getByRole('checkbox', { checked: true })).toHaveCount(request.permissions.length);
      await dialog.getByLabel('Credential lifetime (days)', { exact: true }).fill('1');
      await dialog.getByRole('checkbox', { name: 'I approve this client connection with the permissions selected above.', exact: true }).check();
    }
  }
  stage = 'consent submission';
  await dialog.getByRole('button', { name: host ? 'Approve host' : 'Approve connection', exact: true }).click();
  stage = 'consent acknowledgement';
  await expectConsentAcknowledged(page);
  // No secrets, codes, names, URLs, source references or DOM fragments in output.
  process.stdout.write(JSON.stringify({ approved: true, kind: request.kind, browser: 'chromium', interactiveConsent: true }) + '\n');
} catch (error) {
  const observations = { stage, error: error?.name || 'Error' };
  if (page && !page.isClosed()) {
    observations.codeRetained = codeInput ? await codeInput.inputValue().then(value => value === request.code).catch(() => null) : null;
    observations.reviewEnabled = reviewButton ? await reviewButton.isEnabled().catch(() => null) : null;
    observations.dialogCount = await page.getByRole('dialog').count().catch(() => null);
    observations.alertCount = await page.getByRole('alert').count().catch(() => null);
    observations.atLogin = new URL(page.url()).pathname === '/login';
    observations.atHome = new URL(page.url()).pathname === '/home';
    observations.loginErrorVisible = await page.getByTestId('local-login-error').isVisible().catch(() => null);
    const loginForm = page.getByTestId('local-login-form');
    observations.loginInteractive = await loginForm.count().then(count => count ? loginForm.getAttribute('data-interactive') : null).catch(() => null);
  }
  process.stderr.write('Disposable browser consent did not complete: ' + JSON.stringify(observations) + '; no credential or page content was recorded.\n');
  process.exitCode = 1;
} finally {
  await browser?.close();
}

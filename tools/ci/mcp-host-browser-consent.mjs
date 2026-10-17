// Real disposable host/client consent. Secret input is stdin only; do not trace,
// screenshot, log requests, or include browser exception/DOM text in failures.
import { chromium, expect } from '@playwright/test';

let browser;
let stage = 'bounded input';
try {
  let input = '';
  for await (const data of process.stdin) {
    input += data;
    if (input.length > 16384) throw new Error('input bound');
  }
  const request = JSON.parse(input);
  const origin = new URL(request.origin);
  if (origin.protocol !== 'https:' || !(origin.hostname === 'localhost' || origin.hostname.endsWith('.fixture.test'))) throw new Error('fixture origin required');
  browser = await chromium.launch({
    executablePath: process.env.RATELDESK_ACCEPTANCE_CHROMIUM || undefined,
    args: [`--host-resolver-rules=MAP ${origin.hostname} 127.0.0.1`, '--no-proxy-server'],
  });
  const context = await browser.newContext({ baseURL: origin.origin, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  page.setDefaultTimeout(30000);
  stage = 'local account sign-in';
  await page.goto('/login');
  await expect(page.getByTestId('local-login-form')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Email', { exact: false }).fill(request.email);
  await page.getByLabel('Password', { exact: false }).fill(request.password);
  await page.getByRole('button', { name: /^Sign in to / }).click();
  await expect(page).toHaveURL(/\/home(?:[?#].*)?$/);
  stage = 'connection review';
  await page.goto('/account/integration-credentials/approve');
  const host = request.kind === 'host';
  await page.getByLabel(host ? 'Host code' : 'Client code', { exact: true }).fill(request.code);
  await page.getByRole('button', { name: host ? 'Review host' : 'Review client', exact: true }).click();
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
    await dialog.getByRole('checkbox', { name: 'I approve this exact baseline or configuration proposal and its source generation.', exact: true }).check();
  } else {
    await dialog.getByLabel('Organization', { exact: true }).selectOption(request.organizationId);
    if (request.kind === 'verifier') {
      await expect(dialog).toContainText('Temporary verification client');
      await expect(dialog.getByTestId('credential-permission-selector')).toHaveCount(0);
      await expect(dialog.locator('[name="lifetimeDays"]')).toHaveCount(0);
      await expect(dialog).toContainText('five minutes');
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
  await expect(page.getByRole('status')).toContainText('Connection approved');
  // No secrets, codes, names, URLs, source references or DOM fragments in output.
  process.stdout.write(JSON.stringify({ approved: true, kind: request.kind, browser: 'chromium', interactiveConsent: true }) + '\n');
} catch {
  process.stderr.write(`Disposable browser consent did not complete at ${stage}; no credential or page content was recorded.\n`);
  process.exitCode = 1;
} finally {
  await browser?.close();
}

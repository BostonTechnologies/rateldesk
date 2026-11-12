import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
for (const mode of reviewModes) test(`first-run semantic review ${modeName(mode)}`, async ({ browser, baseURL }, info) => {
  await reviewPage(browser, baseURL!, mode, async page => {
    await reviewOpen(page, baseURL!, '/setup');
    await expect(page.getByTestId('setup-wizard')).toHaveAttribute('data-interactive', 'true');
    await page.getByLabel('Setup code', { exact: true }).fill('synthetic-owner-review-setup-code');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.getByRole('button', { name: 'Prepare storage', exact: true }).click();
    await page.getByLabel('Initial organization', { exact: true }).fill('Synthetic first-run review organization');
    await page.getByLabel('Application name', { exact: true }).fill('Synthetic RatelDesk review');
    await page.getByLabel('Public application URL', { exact: true }).fill(baseURL!);
    await page.getByLabel('Time zone', { exact: true }).fill('Africa/Johannesburg');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.getByLabel('Display name', { exact: true }).fill('Synthetic first-run operator');
    await page.getByLabel('Email', { exact: true }).fill('setup@example.test');
    await page.getByRole('textbox', { name: 'Passphrase*', exact: true }).fill('synthetic review passphrase');
    await page.getByRole('textbox', { name: 'Confirm passphrase*', exact: true }).fill('synthetic review passphrase');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await reviewCapture(page, info, mode, 'setup-review', ['pages-setup-1']);
    await expect(page.getByRole('button', { name: 'Initialize instance', exact: true })).toBeEnabled();
    // The review ends before initialization; no administrator is created by this table audit.
  });
});

import { expect, test } from '@playwright/test';

test('OIDC-only Team keeps application Delete and hides unavailable local account routes', async ({ page, request }) => {
  const fixture = process.env.HELPDESK_TEAM_FIXTURE_BASE ?? 'http://127.0.0.1:18500/fixture/team';
  const origin = process.env.HELPDESK_TEAM_OIDC_BASE_URL ?? 'https://127.0.0.1:5183';
  await request.get(fixture);
  await page.goto(`${origin}/auth/development`);
  await page.goto(`${origin}/admin/users`);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByText('137 team members', { exact: true })).toBeVisible();
  await expect(page.locator('tbody tr')).toHaveCount(25);
  await expect(page.getByRole('button', { name: 'Manage local account', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^(Enable|Disable) sign-in$/ })).toHaveCount(0);
  const deletion = page.locator('tbody tr').first().getByRole('button', { name: 'Delete user', exact: true });
  await expect(deletion).toBeEnabled();
  await deletion.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('An external identity provider account is retained');
  await expect(dialog.getByRole('button', { name: 'Delete user', exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.some((entry: { path: string }) => entry.path.includes('/local-auth/'))).toBe(false);
});

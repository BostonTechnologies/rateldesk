import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
import { modeName, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';

const fixture = 'http://127.0.0.1:18299';
const origin = 'https://127.0.0.1:5158';
const conversation = '11111111-1111-4111-8111-111111111111';
const provider = 'Synthetic OpenAI';

const modes = reviewModes.filter(mode => mode.width === 390 || mode.custom || mode.zoom === 2);
for (const mode of modes) {
  // The shared helper uses Chromium's tab zoom extension for native 200%.
  test(`AI Providers and pinned OpenAI workspaces in ${modeName(mode)}`, async ({ browser, request }, info) => {
    test.setTimeout(90_000);
    await request.get(`${fixture}/fixture/reset?mode=tables&provider=openai`);
    await reviewPage(browser, origin, mode, async page => {
      const open = async (route: string) => {
        await reviewOpen(page, origin, route);
        if (mode.custom) {
          await page.addStyleTag({ content: ':root { --mud-palette-primary: #6868aa; --mud-palette-secondary: #547f80; --mud-palette-divider: #9381aa; }' });
        }
      };
      await open('/admin/integration?tab=ai-providers');
      await expect(page.getByRole('tab', { name: 'AI Providers' })).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: `Edit ${provider}` }).click();
      await page.getByText('Advanced settings', { exact: true }).click();
      await expect(page.getByRole('combobox', { name: 'Versioned skill bundle reference (optional)' })).toHaveText('fixture-bundle-v1');
      await expect(page.getByLabel('Server key reference')).toHaveValue('fixture');
      await expect(page.getByRole('button', { name: 'Use my ChatGPT plan' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Use an API project' })).toBeVisible();
      const refresh = page.getByRole('button', { name: 'Refresh project models' });
      await refresh.focus();
      await page.keyboard.press('Enter');
      await expect(page.getByText(/1 models · Checked/)).toBeVisible();
      await assertNoHorizontalOverflow(page);
      await page.screenshot({ path: info.outputPath('ai-providers.png'), fullPage: mode.zoom !== 2 });

      await open('/admin/integration?tab=netclaw');
      await expect(page.getByRole('tab', { name: 'AI Providers' })).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByText('Netclaw address', { exact: true })).toBeVisible();

      const routes = [
        ['chat', `/ai/chat?organizationId=org&conversationId=${conversation}`],
        ['ops', `/ai/ops?id=${conversation}`],
        ['ticket', '/incidents/ux-chat'],
      ] as const;
      for (const [name, route] of routes) {
        await open(route);
        if (name === 'ticket') {
          const tab = page.getByRole('tab', { name: 'AI Assistant', exact: true });
          await tab.click();
          await expect(tab).toHaveAttribute('aria-selected', 'true');
        }
        const input = page.getByRole('textbox', { name: `Message ${provider}` });
        await expect(input).toBeEnabled();
        await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', mode.theme);
        await expect(page.getByText('Capacity report', { exact: true })).toBeVisible();
        await input.fill('Unsent provider draft');
        const maximize = page.getByRole('button', { name: 'Maximise conversation' });
        await maximize.click();
        const surface = page.getByRole('dialog', { name: 'Conversation', exact: true });
        await expect(surface).toBeVisible();
        await expect(input).toHaveValue('Unsent provider draft');
        await assertNoHorizontalOverflow(page);
        if (mode.zoom === 2) await page.screenshot({ path: info.outputPath(`${name}-fullscreen.png`) });
        else await surface.screenshot({ path: info.outputPath(`${name}-fullscreen.png`) });
        await page.keyboard.press('Escape');
        await expect(maximize).toBeFocused();
        await expect(input).toHaveValue('Unsent provider draft');
      }
    });
    const { sends } = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
    expect(sends).toBe(0);
  });
}

test('Codex device setup resumes after reload, can be cancelled and reports linked identity', async ({ browser, request }) => {
  await request.get(`${fixture}/fixture/reset?mode=tables&provider=openai&codex=true`);
  const mode = reviewModes.find(item => item.width === 390 && item.theme === 'light')!;
  await reviewPage(browser, origin, mode, async page => {
    const openSetup = async () => {
      await reviewOpen(page, origin, '/admin/integration?tab=ai-providers');
      await page.getByRole('button', { name: 'Use my ChatGPT plan' }).click();
    };
    await openSetup();
    await page.getByRole('button', { name: 'Check runtime' }).click();
    await expect(page.getByText('Codex 0.170.0 available', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Continue with ChatGPT' }).click();
    await expect(page.getByText('Waiting for sign-in in ChatGPT.', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open ChatGPT sign-in' })).toHaveAttribute('href', 'https://auth.openai.com/codex/device');
    await openSetup();
    await expect(page.getByText('TEST-4410', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel sign-in' }).click();
    await expect(page.getByText('Sign-in cancelled.', { exact: true })).toBeVisible();
    await expect(page.getByText('TEST-4410', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Retry ChatGPT sign-in' }).click();
    await request.get(`${fixture}/fixture/codex-complete`);
    await page.getByRole('button', { name: 'Check sign-in status' }).click();
    await expect(page.getByText(/Linked to operator@example.test/)).toBeVisible();
    await page.getByRole('button', { name: 'Refresh Codex models' }).click();
    await expect(page.getByText(/Runtime catalog checked/)).toBeVisible();
    await page.getByLabel('Model', { exact: true }).fill('Fixture plan');
    await page.getByText('Fixture plan model', { exact: true }).click();
    await page.getByLabel('Reasoning effort', { exact: true }).click();
    await expect(page.getByText('medium', { exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Disconnect ChatGPT' }).click();
    await expect(page.getByText('ChatGPT disconnected.', { exact: true })).toBeVisible();
    await assertNoHorizontalOverflow(page);
  });
});

import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';

// Synthetic API + actual NewWeb. Does not claim daemon or production acceptance.
const id = '11111111-1111-4111-8111-111111111111';
const fixture = 'http://127.0.0.1:18299';
const routes = [`/ai/chat?organizationId=org&conversationId=${id}`, `/ai/ops?id=${id}`];

for (const route of routes) {
  for (const [theme, width] of [['light', 1440], ['dark', 1440], ['light', 390], ['dark', 390]] as const) {
    test(`${route.split('?')[0]} ${theme} ${width}px retains one mounted interactive conversation`, async ({ page, request }, info) => {
      await request.get(`${fixture}/fixture/reset?mode=completed`);
      await page.goto('/auth/development');
      await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
      await page.setViewportSize({ width, height: 950 });
      await page.goto(route);
      const input = page.getByRole('textbox', { name: 'Message AiAssistant' });
      await expect(input).toBeEnabled();
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      const tool = page.getByTestId('chat-tool').first();
      await expect(tool).toContainText('terminal_execute_0: result received');
      await tool.getByRole('button').click();
      await expect(tool).toContainText('Details were not recorded');
      await input.fill('Preserve this unsent draft');
      const before = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
      await page.getByRole('button', { name: 'Maximise conversation' }).click();
      const surface = page.getByRole('dialog', { name: 'Conversation', exact: true });
      await expect(surface).toBeVisible();
      expect(await surface.evaluate(el => el.getBoundingClientRect().top)).toBe(0);
      expect(await page.evaluate(() => document.elementFromPoint(10, 10)?.closest('[aria-label="Conversation"]') !== null)).toBe(true);
      await expect(input).toHaveValue('Preserve this unsent draft');
      await expect(tool).toContainText('Details were not recorded');
      await page.getByRole('button', { name: 'Chat display settings' }).click();
      await page.getByRole('checkbox', { name: 'Show call identifiers' }).check();
      await expect(tool).toContainText('call-0');
      await page.keyboard.press('Escape');
      await expect(page.getByRole('button', { name: 'Maximise conversation' })).toBeFocused();
      await expect(input).toHaveValue('Preserve this unsent draft');
      const after = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
      expect(after.streamOpens).toBe(before.streamOpens);
      expect(after.sends).toBe(0);
      await assertNoHorizontalOverflow(page);
      await page.getByRole('button', { name: 'Maximise conversation' }).click();
      await page.screenshot({ path: info.outputPath('fullscreen.png'), fullPage: true });
      await page.reload();
      await expect(input).toBeEnabled();
      await page.getByRole('button', { name: 'Chat display settings' }).click();
      await expect(page.getByRole('checkbox', { name: 'Show call identifiers' })).toBeChecked();
    });
  }

  test(`${route.split('?')[0]} fullscreen streaming approval recovery and archived access`, async ({ page, request }) => {
    await request.get(`${fixture}/fixture/reset?mode=processing`);
    await page.goto('/auth/development'); await page.goto(route);
    await expect(page.getByRole('button', { name: 'Stop waiting' })).toBeVisible();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await request.get(`${fixture}/fixture/draft`);
    await expect(page.getByText('Streaming response', { exact: true })).toBeVisible();
    await request.get(`${fixture}/fixture/complete`);
    await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeEnabled();
    await expect(page.getByText('Canonical response', { exact: true })).toBeVisible();

    await request.get(`${fixture}/fixture/reset?mode=approval`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await page.getByRole('button', { name: 'Allow once', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Allow once', exact: true })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeDisabled();

    await request.get(`${fixture}/fixture/reset?mode=recovery`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await page.getByRole('button', { name: 'Check saved session' }).click();
    await expect(page.getByText('Admission confirmed; outcome unknown.', { exact: false })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeDisabled();
    const counts = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
    expect(counts.sends).toBe(0); expect(counts.recoveries).toBe(1);

    await request.get(`${fixture}/fixture/reset?mode=archived`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await expect(page.getByRole('button', { name: 'Restore conversation' })).toBeEnabled();
    await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeDisabled();
  });
}

test('general chat saves without sending and changing organization clears the selected conversation', async ({ page, request }) => {
  await request.get(`${fixture}/fixture/reset?mode=empty`);
  await page.goto('/auth/development'); await page.goto('/ai/chat?organizationId=org');
  await page.getByRole('button', { name: 'New chat', exact: true }).click();
  await page.getByRole('textbox', { name: 'Conversation title', exact: true }).fill('Synthetic new chat');
  await page.getByRole('button', { name: 'Save chat', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Synthetic new chat', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/conversationId=/);
  await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeEnabled();
  await page.getByRole('textbox', { name: 'Message AiAssistant' }).fill('Private draft for the first organization');
  await page.getByRole('combobox', { name: 'Organization', exact: true }).selectOption('org-b');
  await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toHaveCount(0);
  await expect(page.getByText('Private draft for the first organization')).toHaveCount(0);
  const counts = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
  expect(counts.sends).toBe(0);
});

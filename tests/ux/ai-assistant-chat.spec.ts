import { expect, test } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';

// Requires the loopback fixture API and real feature NewWeb host. No live ticket mutations.
test.beforeEach(async ({ page, request }) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=completed');
  await page.goto('/auth/development', { waitUntil: 'domcontentloaded' });
});

async function openChat(page, theme: 'Light' | 'Dark' = 'Light') {
  await page.addInitScript((selectedTheme) => {
    localStorage.setItem('helpdesk.theme.preference', selectedTheme.toLowerCase());
  }, theme);
  await page.goto('/incidents/ux-chat', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  const aiTab = page.getByRole('tab', { name: 'AI Assistant', exact: true });
  await aiTab.click();
  const composer = page.getByTestId('ai-assistant-composer');
  await expect(aiTab).toHaveAttribute('aria-selected', 'true');
  await expect(composer).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.dataset.helpdeskTheme)).toBe(theme.toLowerCase());
}

for (const theme of ['Light', 'Dark'] as const) {
  for (const mobile of [false, true]) {
    test(`${theme} ${mobile ? 'mobile' : 'desktop'} completed, expand and composer boundary`, async ({ page }, testInfo) => {
      await openChat(page, theme);
      await page.setViewportSize(mobile ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
      const heading = page.getByTestId('ai-assistant-activity').getByRole('button');
      await expect(heading).toHaveAttribute('aria-expanded', 'false');
      await expect(page.getByTestId('ai-assistant-turn')).toHaveCount(1);
      await expect(page.getByTestId('ai-assistant-response')).toHaveCount(1);
      await expect(page.getByText('c0c48409', { exact: false })).toHaveCount(0);
      await heading.click();
      await expect(heading).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByText('terminal_execute_13', { exact: false })).toBeVisible();
      await assertNoHorizontalOverflow(page);
      await page.screenshot({ path: testInfo.outputPath('expanded.png'), fullPage: true });
      await heading.click();
      const input = page.getByRole('textbox', { name: 'Message AiAssistant' });
      await input.fill('Composer focus test');
      await input.focus();
      const styles = await input.evaluate(element => {
        const inputStyle = getComputedStyle(element);
        const boundary = getComputedStyle(element.closest('[data-testid="ai-assistant-composer"]')!);
        return { inputBorder: inputStyle.borderTopWidth, inputOutline: inputStyle.outlineStyle,
          composerBorder: boundary.borderTopWidth, composerShadow: boundary.boxShadow };
      });
      expect(styles.inputBorder).toBe('0px');
      expect(styles.inputOutline).toBe('none');
      expect(styles.composerBorder).toBe('1px');
      await testInfo.attach('computed-composer-styles', { body: JSON.stringify(styles), contentType: 'application/json' });
      await page.screenshot({ path: testInfo.outputPath('collapsed-composer.png'), fullPage: true });
    });
  }
}

for (const mode of ['empty', 'processing', 'approval', 'failed', 'archived']) {
  test(`${mode} presentation`, async ({ page, request }, testInfo) => {
    await request.get(`http://127.0.0.1:18299/fixture/reset?mode=${mode}`);
    await openChat(page);
    if (['processing', 'approval', 'failed'].includes(mode))
      await expect(page.getByTestId('ai-assistant-activity').getByRole('button').first()).toHaveAttribute('aria-expanded', 'true');
    if (mode === 'processing') {
      await expect(page.getByRole('button', { name: 'Stop waiting' })).toBeVisible();
      await expect(page.getByText('will not resend this request automatically', { exact: false })).toBeVisible();
    }
    if (mode === 'approval') {
      const activity = page.getByTestId('ai-assistant-activity');
      const approval = page.getByTestId('ai-assistant-approval');
      const allow = approval.getByRole('button', { name: 'Allow once' });
      const deny = approval.getByRole('button', { name: 'Deny', exact: true });
      await expect(activity.getByRole('button').first()).toContainText('Approval required');
      await expect(approval.getByRole('status')).toHaveText('Approval required');
      await expect(allow).toBeEnabled();
      await expect(deny).toBeEnabled();
      await expect(allow).toHaveClass(/mud-button-filled/);
      await expect(deny).toHaveClass(/mud-button-outlined/);
      await expect(allow).toHaveAttribute('data-approval-key', 'approve_once');
      await expect(deny).toHaveAttribute('data-approval-key', 'deny');
      const approvalStyles = await approval.evaluate(element => {
        const callout = getComputedStyle(element);
        const allow = element.querySelector('[data-approval-key="approve_once"]')!;
        const deny = element.querySelector('[data-approval-key="deny"]')!;
        const allowStyle = getComputedStyle(allow);
        const denyStyle = getComputedStyle(deny);
        return {
          background: callout.backgroundColor,
          borderLeftWidth: callout.borderLeftWidth,
          allowBackground: allowStyle.backgroundColor,
          denyBorderWidth: denyStyle.borderTopWidth,
          denyColor: denyStyle.color
        };
      });
      expect(approvalStyles.borderLeftWidth).toBe('4px');
      expect(approvalStyles.background).not.toBe('rgba(0, 0, 0, 0)');
      expect(approvalStyles.allowBackground).not.toBe('rgba(0, 0, 0, 0)');
      expect(approvalStyles.denyBorderWidth).not.toBe('0px');
      expect(approvalStyles.denyColor).not.toBe(approvalStyles.allowBackground);
      await testInfo.attach('approval-presentation-styles', { body: JSON.stringify(approvalStyles), contentType: 'application/json' });
    }
    if (mode === 'archived') {
      await expect(page.getByText('Archived transcript', { exact: false })).toBeVisible();
      await expect(page.getByRole('textbox', { name: 'Message AiAssistant' })).toBeDisabled();
    }
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`${mode}.png`), fullPage: true });
  });
}

test('dark desktop approval callout remains visually distinct', async ({ page, request }, testInfo) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=approval');
  await openChat(page, 'Dark');
  await page.setViewportSize({ width: 1440, height: 1000 });
  const approval = page.getByTestId('ai-assistant-approval');
  await expect(approval.getByRole('status')).toHaveText('Approval required');
  const styles = await approval.evaluate(element => {
    const style = getComputedStyle(element);
    return { background: style.backgroundColor, borderLeftWidth: style.borderLeftWidth };
  });
  expect(styles.borderLeftWidth).toBe('4px');
  expect(styles.background).not.toBe('rgba(0, 0, 0, 0)');
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('dark-desktop-approval.png'), fullPage: true });
});

test('stop waiting shows authoritative uncertain-delivery recovery controls', async ({ page, request }) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=processing');
  await openChat(page);
  await page.getByRole('checkbox', { name: 'The remote assistant may still be processing. RatelDesk will stop waiting locally and will not resend this request automatically.' }).check();
  const stopWaiting = page.getByRole('button', { name: 'Stop waiting' });
  await expect(stopWaiting).toBeEnabled();
  await stopWaiting.click();
  await expect(page.getByText('Delivery or completion could not be confirmed.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Check saved session' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Abandon unresolved turn and start blank' })).toBeVisible();
  await expect(page.getByText('Working…', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Stop waiting' })).toHaveCount(0);
});

test('desktop Enter admission lock, Shift+Enter and IME', async ({ page, request }) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=empty');
  await openChat(page);
  const input = page.getByRole('textbox', { name: 'Message AiAssistant' });
  await input.fill('First line');
  await input.press('Shift+Enter');
  await input.press('a');
  await expect(input).toHaveValue('First line\na');
  await expect(page.getByTestId('ai-assistant-turn')).toHaveCount(0);
  await input.dispatchEvent('compositionstart');
  await input.press('Enter');
  await expect(page.getByTestId('ai-assistant-turn')).toHaveCount(0);
  await input.dispatchEvent('compositionend');
  await input.press('Enter');
  await expect(page.getByTestId('ai-assistant-turn')).toHaveCount(1);
  await expect(input).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
});

test('live draft replacement, automatic completion collapse and duplicate replay', async ({ page, request }) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=processing');
  await openChat(page);
  const heading = page.getByTestId('ai-assistant-activity').getByRole('button');
  await expect(heading).toHaveAttribute('aria-expanded', 'true');
  await request.get('http://127.0.0.1:18299/fixture/draft');
  await expect(page.getByText('Streaming response', { exact: true })).toBeVisible();
  await request.get('http://127.0.0.1:18299/fixture/complete');
  await expect(heading).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByText('Streaming response', { exact: true })).toHaveCount(0);
  await expect(page.getByTestId('ai-assistant-response')).toHaveCount(1);
  await request.get('http://127.0.0.1:18299/fixture/replay');
  await heading.click();
  await expect(page.locator('.activity-row')).toHaveCount(14);
  await expect(page.getByTestId('ai-assistant-response')).toHaveCount(1);
  await openChat(page);
  await expect(page.getByTestId('ai-assistant-response')).toHaveCount(1);
  await expect(page.getByTestId('ai-assistant-activity').getByRole('button')).toHaveAttribute('aria-expanded', 'false');
});

test('mobile dark approval controls and bounded growing composer', async ({ page, request }, testInfo) => {
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=approval');
  await openChat(page, 'Dark');
  await page.setViewportSize({ width: 390, height: 844 });
  const approval = page.getByTestId('ai-assistant-approval');
  await expect(approval.getByRole('status')).toHaveText('Approval required');
  await expect(approval.getByRole('button', { name: 'Allow once' })).toBeVisible();
  await expect(approval.getByRole('button', { name: 'Deny', exact: true })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await page.screenshot({ path: testInfo.outputPath('mobile-dark-approval.png'), fullPage: true });
  await request.get('http://127.0.0.1:18299/fixture/reset?mode=empty');
  await openChat(page, 'Dark');
  const input = page.getByRole('textbox', { name: 'Message AiAssistant' });
  await input.fill(Array.from({ length: 30 }, (_, i) => `Line ${i}`).join('\n'));
  await expect.poll(() => input.evaluate(e => e.scrollHeight > e.clientHeight)).toBe(true);
  expect((await input.boundingBox())!.height).toBeLessThanOrEqual(240);
  await assertNoHorizontalOverflow(page);
});

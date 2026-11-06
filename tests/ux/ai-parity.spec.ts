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
      const input = page.getByRole('textbox', { name: 'Message Netclaw' });
      await expect(input).toBeEnabled();
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      const tool = page.getByTestId('chat-tool').first();
      await expect(tool).toContainText('terminal_execute_0: result received');
      await tool.getByRole('button').click();
      await expect(tool).toContainText('Details were not recorded');
      await input.fill('Preserve this unsent draft');
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: info.outputPath('workspace.png'), fullPage: true });
      const before = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
      await page.getByRole('button', { name: 'Maximise conversation' }).click();
      const surface = page.getByRole('dialog', { name: 'Conversation', exact: true });
      await expect(surface).toBeVisible();
      expect(await surface.evaluate(el => el.getBoundingClientRect().top)).toBe(0);
      expect(await page.evaluate(() => document.elementFromPoint(10, 10)?.closest('[aria-label="Conversation"]') !== null)).toBe(true);
      await expect(input).toHaveValue('Preserve this unsent draft');
      await expect(tool).toContainText('Details were not recorded');
      await page.getByRole('button', { name: 'Chat display settings' }).click();
      await page.getByRole('checkbox', { name: 'Call IDs on rows' }).check();
      await expect(tool).toContainText('call-0');
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog', { name: 'Chat display settings', exact: true })).toHaveCount(0);
      await expect(surface).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('button', { name: 'Maximise conversation' })).toBeFocused();
      await expect(input).toHaveValue('Preserve this unsent draft');
      const after = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
      expect(after.streamOpens).toBe(before.streamOpens);
      expect(after.sends).toBe(0);
      await assertNoHorizontalOverflow(page);
      await page.getByRole('button', { name: 'Maximise conversation' }).click();
      await surface.screenshot({ path: info.outputPath('fullscreen.png') });
      await page.reload();
      await expect(input).toBeEnabled();
      await page.getByRole('button', { name: 'Chat display settings' }).click();
      await expect(page.getByRole('checkbox', { name: 'Call IDs on rows' })).toBeChecked();
      await page.screenshot({ path: info.outputPath('settings.png') });
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
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeEnabled();
    await expect(page.getByText('Canonical response', { exact: true })).toBeVisible();

    await request.get(`${fixture}/fixture/reset?mode=approval`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await page.getByRole('button', { name: 'Allow once', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Allow once', exact: true })).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeDisabled();

    await request.get(`${fixture}/fixture/reset?mode=recovery`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await page.getByRole('button', { name: 'Check saved session' }).click();
    await expect(page.getByText('Admission confirmed; outcome unknown.', { exact: false })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeDisabled();
    const counts = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
    expect(counts.sends).toBe(0); expect(counts.recoveries).toBe(1);

    await request.get(`${fixture}/fixture/reset?mode=archived`); await page.reload();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await expect(page.getByRole('button', { name: 'Restore conversation' })).toBeEnabled();
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeDisabled();
  });
}

for (const [theme, width] of [['light', 1440], ['dark', 1440], ['light', 390], ['dark', 390]] as const) {
test(`general chat ${theme} ${width}px starts immediately and names itself on first send`, async ({ page, request }, info) => {
  await request.get(`${fixture}/fixture/reset?mode=empty`);
  await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  await page.setViewportSize({ width, height: 950 });
  await page.goto('/auth/development'); await page.goto('/ai/chat');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  const input = page.getByRole('textbox', { name: 'Message Netclaw' });
  await expect(input).toBeEnabled();
  await expect(page.getByRole('heading', { name: 'What would you like to work on?' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Conversation title', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Save chat', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Netclaw connection', exact: true })).toHaveAttribute('href', '/admin/automation/integration/netclaw');
  await page.getByRole('button', { name: 'Explore a question', exact: false }).click();
  await expect(input).toHaveValue('Help me work through an operational question. Ask me what I want to understand or achieve.');
  const before = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
  expect(before.starts).toBe(0); expect(before.sends).toBe(0); expect(before.streamOpens).toBe(0);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: info.outputPath('welcome.png'), fullPage: true });
  await input.fill('Help review the request queue');
  await input.press('Alt+Enter');
  await expect(input).toHaveValue('Help review the request queue\n');
  expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(0);
  await input.fill('Help review the request queue');
  await input.press('Enter');
  await expect(page.getByRole('heading', { name: 'Help review the request queue', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/conversationId=/);
  await expect(input).toBeEnabled();
  await expect(page.getByText('Synthetic first reply.')).toBeVisible();
  await input.fill('Private draft for the first organization');
  await page.getByRole('combobox', { name: 'Organization', exact: true }).selectOption('org-b');
  await expect(input).toBeEnabled(); await expect(input).toHaveValue('');
  await expect(page.getByText('Private draft for the first organization')).toHaveCount(0);
  const counts = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
  expect(counts.sends).toBe(1); expect(counts.starts).toBe(1);
  await assertNoHorizontalOverflow(page);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
}

for (const route of [...routes, '/incidents/ux-chat']) {
  for (const [theme, width] of [['light', 1440], ['dark', 1440], ['light', 390], ['dark', 390]] as const) {
    test(`${route.split('?')[0]} ${theme} ${width}px saved tables stay readable and copy independently`, async ({ page, context, request }, info) => {
      await request.get(`${fixture}/fixture/reset?mode=tables`);
      const payloads = await (await request.get(`${fixture}/fixture/table-payloads`)).json();
      await context.grantPermissions(['clipboard-read', 'clipboard-write']);
      await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
      await page.setViewportSize({ width, height: 950 });
      await page.goto('/auth/development'); await page.goto(route);
      if (route.startsWith('/incidents')) await page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
      const response = page.getByTestId('ai-assistant-response');
      await expect(response.getByRole('table')).toHaveCount(2);
      await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
      await expect(response.getByRole('table').first().getByRole('columnheader')).toHaveText(['Host', 'Total', 'Free', 'Sample time (UTC)']);
      await expect(response.locator('td strong')).toHaveText('17.8 GiB');
      await expect(response.locator('td code').last()).toHaveText('a|b');
      const color = await response.locator('.chat-markdown').evaluate(el => getComputedStyle(el).color);
      await expect(response.locator('th').first()).toHaveCSS('color', color);
      await expect(response.locator('td').first()).toHaveCSS('color', color);
      await expect(response.locator('img, script')).toHaveCount(0);
      await assertNoHorizontalOverflow(page);

      for (const fullscreen of [false, true]) {
        if (fullscreen) await page.getByRole('button', { name: 'Maximise conversation' }).click();
        const first = response.locator('.chat-table-block').first();
        const scroll = first.getByRole('region', { name: 'Markdown table', exact: true });
        await scroll.scrollIntoViewIfNeeded();
        await scroll.focus();
        await expect(scroll).toBeFocused();
        if (width < 760) {
          expect(await scroll.evaluate(el => el.scrollWidth > el.clientWidth)).toBe(true);
          await page.keyboard.press('ArrowRight');
          await expect.poll(() => scroll.evaluate(el => el.scrollLeft)).toBeGreaterThan(0);
          await scroll.evaluate(el => { el.scrollLeft = 0; });
          await expect.poll(() => scroll.evaluate(el => el.scrollLeft)).toBe(0);
        }
        const copy = first.getByRole('button', { name: 'Copy table', exact: true });
        await copy.focus(); await copy.press('Enter');
        await expect(first.getByRole('status')).toHaveText('Table copied as Markdown.');
        await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.diskTable);
        await first.screenshot({ path: info.outputPath(fullscreen ? 'table-fullscreen.png' : 'table-normal.png') });
        await page.screenshot({ path: info.outputPath(fullscreen ? 'table-fullscreen-workspace.png' : 'table-normal-workspace.png') });
        await response.getByRole('button', { name: 'Copy table', exact: true }).nth(1).click();
        await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.emptyTable);
        await assertNoHorizontalOverflow(page);
      }
      await response.getByRole('button', { name: 'Copy Code', exact: true }).click();
      await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe('| Example |\n| --- |\n| fenced table |\n');
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
      await page.reload();
      if (route.startsWith('/incidents')) await page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
      await expect(response.getByRole('table')).toHaveCount(2);
      expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(0);
    });
  }
}

for (const route of routes) {
  test(`${route.split('?')[0]} partial streamed tables become one saved answer without losing content`, async ({ page, context, request }) => {
    await request.get(`${fixture}/fixture/reset?mode=processing`);
    const payloads = await (await request.get(`${fixture}/fixture/table-payloads`)).json();
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.goto('/auth/development'); await page.goto(route);
    await expect.poll(async () => (await (await request.get(`${fixture}/fixture/chat-counts`)).json()).streamOpens).toBeGreaterThan(0);
    await request.get(`${fixture}/fixture/draft?table=partial`);
    const draft = page.getByTestId('ai-assistant-draft');
    await expect(draft).toContainText('| Host | Total | Free | Sample time (UTC) |');
    await expect(draft.getByRole('table')).toHaveCount(0);
    await request.get(`${fixture}/fixture/draft?table=complete`);
    await expect(draft.getByRole('table')).toHaveCount(2);
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await expect(draft.getByRole('table')).toHaveCount(2);
    await request.get(`${fixture}/fixture/complete?table=true`);
    await expect(draft).toHaveCount(0);
    const response = page.getByTestId('ai-assistant-response');
    await expect(response).toHaveCount(1);
    await expect(response.getByRole('table')).toHaveCount(2);
    await expect(response.locator('.chat-table-block').first()).toContainText('node-02-with-a-long-synthetic-host-name.example.test');
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeEnabled();
    await page.reload();
    await expect(response.getByRole('table')).toHaveCount(2);
    await expect(response.getByRole('heading', { name: 'Capacity report', exact: true })).toHaveCount(1);
    await response.getByRole('button', { name: 'Copy table', exact: true }).first().click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.diskTable);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });

  test(`${route.split('?')[0]} clipboard denial offers exact manual table Markdown`, async ({ page, request }) => {
    await request.get(`${fixture}/fixture/reset?mode=tables`);
    const payloads = await (await request.get(`${fixture}/fixture/table-payloads`)).json();
    await page.goto('/auth/development'); await page.goto(route);
    await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', {
      configurable: true, value: { writeText: () => Promise.reject(new DOMException('Synthetic clipboard denied', 'NotAllowedError')) }
    }));
    const first = page.getByTestId('ai-assistant-response').locator('.chat-table-block').first();
    await first.getByRole('button', { name: 'Copy table', exact: true }).click();
    await expect(first.getByRole('status')).toHaveText('Copy failed. Select the Markdown below and copy it manually.');
    await expect(first.getByRole('textbox', { name: 'Table Markdown for manual copying' })).toHaveValue(payloads.diskTable);
    await expect(first.getByRole('table')).toBeVisible();
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}

test('uncertain first-send response retries the same conversation and message', async ({ page, request }) => {
  await request.get(`${fixture}/fixture/reset?mode=empty&loseStartResponse=true`);
  await page.goto('/auth/development'); await page.goto('/ai/chat?organizationId=org');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('textbox', { name: 'Message Netclaw' }).fill('Preserve this first message');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Retry message', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry message', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Retry message', exact: true }).click();
  await expect(page.getByText('Synthetic first reply.')).toBeVisible();
  const counts = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
  expect(counts.starts).toBe(1); expect(counts.sends).toBe(1); expect(counts.startAttempts).toBe(2);
});

test('changing organization fences an in-flight first-send response and clears its draft', async ({ page, request }) => {
  await request.get(`${fixture}/fixture/reset?mode=empty&holdStartResponse=true`);
  await page.goto('/auth/development'); await page.goto('/ai/chat?organizationId=org');
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  const input = page.getByRole('textbox', { name: 'Message Netclaw' });
  await input.fill('First request for the previous organization');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect.poll(async () => (await (await request.get(`${fixture}/fixture/chat-counts`)).json()).starts).toBe(1);
  await page.getByRole('combobox', { name: 'Organization', exact: true }).selectOption('org-b');
  await expect(input).toBeEnabled(); await expect(input).toHaveValue('');
  await request.get(`${fixture}/fixture/release-start`);
  await expect(page).toHaveURL(/organizationId=org-b/);
  await expect(page.getByRole('heading', { name: 'First request for the previous organization' })).toHaveCount(0);
  await expect(page.getByText('Synthetic first reply.')).toHaveCount(0);
  expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(1);
});

for (const width of [1440, 390]) {
test(`connection popover ${width}px distinguishes saved verification, live native state and stale observations`, async ({ page, request }, info) => {
  await page.setViewportSize({ width, height: 950 });
  await request.get(`${fixture}/fixture/reset?mode=completed`);
  await page.goto('/auth/development'); await page.goto(routes[0]);
  const badge = page.getByTestId('netclaw-connection-status');
  await expect(badge).toContainText('Netclaw verified');
  await expect.poll(async () => (await (await request.get(`${fixture}/fixture/chat-counts`)).json()).streamOpens).toBeGreaterThan(0);
  await request.get(`${fixture}/fixture/connection?mode=connected`); await page.reload();
  await expect(badge).toContainText('Netclaw connected'); await badge.click();
  const details = page.getByRole('dialog', { name: 'Netclaw connection details' });
  await expect(details).toContainText('https://netclaw.example.test'); await expect(details).toContainText('SignalR: Connected');
  await assertNoHorizontalOverflow(page);
  await details.screenshot({ path: info.outputPath('connection-details.png') });
  await page.keyboard.press('Escape'); await expect(details).toHaveCount(0);
  await request.get(`${fixture}/fixture/connection?mode=stale`); await page.reload();
  await expect(badge).toContainText('Netclaw status unknown'); await badge.click();
  await expect(details).toContainText('This observation is stale');
});
}

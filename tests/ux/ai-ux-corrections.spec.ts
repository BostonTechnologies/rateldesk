import { chromium, expect, test, type Locator, type Page } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { assertNoHorizontalOverflow } from './auth';

// Actual NewWeb, synthetic API only. Native-daemon/owner acceptance is separate.
const fixture = 'http://127.0.0.1:18299';
const id = '11111111-1111-4111-8111-111111111111';
const routes = [`/ai/chat?organizationId=org&conversationId=${id}`, `/ai/ops?id=${id}`, '/incidents/ux-chat'];

async function open(page: Page, route: string) {
  await page.goto('/auth/development');
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
  if (route.startsWith('/incidents')) await page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeEnabled();
}

async function contained(panel: Locator) {
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.evaluate(el => {
    const box = el.getBoundingClientRect();
    const viewport = window.visualViewport!;
    const visible = document.elementFromPoint(box.x + box.width / 2, box.y + Math.min(box.height / 2, 30));
    return box.left >= viewport.offsetLeft && box.top >= viewport.offsetTop &&
      box.right <= viewport.offsetLeft + viewport.width + 1 && box.bottom <= viewport.offsetTop + viewport.height + 1 &&
      !!visible && el.contains(visible) && el.matches(':popover-open');
  })).toBe(true);
}

for (const route of routes) for (const width of [1440, 390]) {
  test(`${route.split('?')[0]} ${width}px right header badge, contained popups and left tool rows`, async ({ page, request }, info) => {
    await request.get(`${fixture}/fixture/reset?mode=completed`);
    await request.get(`${fixture}/fixture/connection?mode=connected`);
    await page.setViewportSize({ width, height: 844 });
    await open(page, route);
    const badge = page.getByRole('button', { name: 'Netclaw connection status' });
    await expect(badge).toContainText('Netclaw connected');
    await expect(badge).toHaveClass(/connected/);
    expect(await badge.evaluate(el => getComputedStyle(el).color)).toBe(await badge.evaluate(el => getComputedStyle(el).borderTopColor));
    const header = route.startsWith('/ai/chat') ? page.locator('.chat-page-heading') : route.startsWith('/ai/ops') ? page.locator('.ai-toolbar') : page.locator('.conversation-header');
    expect(await header.locator('[data-testid="netclaw-connection-status"]').count()).toBe(1);
    await badge.click();
    await contained(page.getByRole('dialog', { name: 'Netclaw connection details' }));
    await page.screenshot({ path: info.outputPath('header-popup.png'), fullPage: true });
    await page.keyboard.press('Escape');
    const rows = page.locator('.tool-summary');
    await expect(rows).toHaveCount(14);
    const positions = await rows.evaluateAll(elements => elements.map(el => ({ left: el.getBoundingClientRect().left, text: el.children[1].getBoundingClientRect().left, justify: getComputedStyle(el).justifyContent })));
    expect(Math.max(...positions.map(x => x.left)) - Math.min(...positions.map(x => x.left))).toBeLessThan(1);
    expect(Math.max(...positions.map(x => x.text)) - Math.min(...positions.map(x => x.text))).toBeLessThan(1);
    expect(positions.every(x => x.justify === 'flex-start')).toBe(true);
    await rows.first().click();
    await page.getByRole('textbox', { name: 'Message Netclaw' }).fill('Keep this unsent draft');
    const before = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
    await page.getByRole('button', { name: 'Maximise conversation' }).click();
    const surface = page.getByRole('dialog', { name: 'Conversation', exact: true });
    const fullBadge = surface.getByRole('button', { name: 'Netclaw connection status' });
    await fullBadge.click();
    const popup = surface.getByRole('dialog', { name: 'Netclaw connection details' });
    await contained(popup);
    await page.screenshot({ path: info.outputPath('fullscreen-popup.png') });
    await page.setViewportSize({ width, height: 568 });
    await contained(popup);
    await page.keyboard.press('Escape');
    await surface.getByRole('button', { name: 'Chat display settings' }).click();
    const settings = surface.getByRole('dialog', { name: 'Chat display settings', exact: true });
    await contained(settings);
    if (await settings.locator('summary').count()) {
      await settings.locator('summary').click();
      await contained(settings);
    }
    await settings.getByRole('checkbox', { name: 'Compact tool rows' }).uncheck();
    await contained(settings);
    await page.keyboard.press('Escape');
    await expect(surface.locator('.tool-details').first()).toContainText('Details were not recorded');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toHaveValue('Keep this unsent draft');
    const after = await (await request.get(`${fixture}/fixture/chat-counts`)).json();
    expect(after.sends).toBe(0); expect(after.streamOpens).toBe(before.streamOpens);
    await assertNoHorizontalOverflow(page);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}

for (const [width, height] of [[1912, 914], [1366, 640]] as const) {
  test(`investigation workspace ${width}x${height} fills remaining height with reachable composer`, async ({ page, request }, info) => {
    await request.get(`${fixture}/fixture/reset?mode=completed`);
    await page.setViewportSize({ width, height });
    await request.get(`${fixture}/fixture/connection?mode=connected`);
    await open(page, routes[1]);
    await expect(page.locator('.tool-summary')).toHaveCount(14);
    const geometry = await page.evaluate(() => {
      const box = (selector: string) => { const b = document.querySelector(selector)!.getBoundingClientRect(); return { top: b.top, bottom: b.bottom, height: b.height }; };
      return { workspace: box('.ai-workspace'), metrics: box('.ai-metrics'), composer: box('[data-testid="ai-assistant-composer"]'), scroll: document.documentElement.scrollHeight, viewport: innerHeight };
    });
    expect(geometry.workspace.top).toBeLessThan(190);
    expect(geometry.metrics.height).toBeLessThan(45);
    expect(geometry.composer.bottom).toBeLessThanOrEqual(height);
    expect(geometry.scroll).toBeLessThanOrEqual(geometry.viewport + 2);
    await page.screenshot({ path: info.outputPath('selected-workspace.png') });
    await page.goto('/ai/ops');
    await expect(page.getByRole('heading', { name: 'Bring the evidence together' })).toBeVisible();
    await page.screenshot({ path: info.outputPath('empty-workspace.png') });
  });
}

for (const route of routes.slice(0, 2)) {
  test(`${route.split('?')[0]} received skill and discovery payloads display and copy exactly after reload`, async ({ page, context, request }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await request.get(`${fixture}/fixture/reset?mode=payloads`);
    const payloads = await (await request.get(`${fixture}/fixture/trace-payloads`)).json();
    await open(page, route);
    for (let reload = 0; reload < 2; reload++) {
      if (reload) await page.reload();
      const skill = page.getByTestId('chat-tool').first();
      await skill.locator('.tool-summary').click();
      await expect(skill).toContainText('Received tool name: skill_load');
      expect(await skill.locator('pre code').nth(0).textContent()).toBe(payloads.skillArguments);
      expect(await skill.locator('pre code').nth(1).textContent()).toBe(payloads.skillResult);
      await skill.getByRole('button', { name: 'Copy Arguments', exact: true }).first().click();
      await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.skillArguments);
      await skill.getByRole('button', { name: 'Copy Result', exact: true }).first().click();
      await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.skillResult);
      const discovery = page.getByTestId('chat-tool').nth(1);
      await discovery.locator('.tool-summary').click();
      expect(await discovery.locator('pre code').nth(1).textContent()).toBe(payloads.discoveryResult);
      await discovery.getByRole('button', { name: 'Copy Result', exact: true }).first().click();
      await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(payloads.discoveryResult);
      expect(await page.evaluate(() => (window as any).traceExecuted)).toBeUndefined();
    }
    expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(0);
  });
}

test('investigation popups remain contained at native browser zoom 200 percent', async ({ request, baseURL }, info) => {
  await request.get(`${fixture}/fixture/reset?mode=completed`);
  await request.get(`${fixture}/fixture/connection?mode=connected`);
  const profile = await mkdtemp(path.resolve('work/ai-zoom-'));
  const extension = path.resolve('tests/ux/native-zoom-extension');
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium', headless: true, ignoreHTTPSErrors: true, viewport: { width: 1912, height: 914 },
    args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`]
  });
  try {
    await expect.poll(() => context.serviceWorkers().length).toBeGreaterThan(0);
    const worker = context.serviceWorkers()[0];
    const page = await context.newPage();
    await page.goto(`${baseURL}/auth/development`); await page.goto(`${baseURL}${routes[1]}`);
    const zoom = await worker.evaluate(async origin => {
      const api = (globalThis as any).chrome;
      const tabs = await api.tabs.query({}); const tab = tabs.find((t: any) => t.url?.startsWith(origin));
      await api.tabs.setZoom(tab.id, 2); return api.tabs.getZoom(tab.id);
    }, baseURL!);
    expect(zoom).toBe(2);
    await expect.poll(() => page.evaluate(() => innerWidth)).toBe(956);
    await expect(page.getByRole('textbox', { name: 'Message Netclaw' })).toBeEnabled();
    const composer = page.getByRole('textbox', { name: 'Message Netclaw' });
    await composer.scrollIntoViewIfNeeded();
    expect(await composer.evaluate(el => el.getBoundingClientRect().bottom <= innerHeight)).toBe(true);
    await page.getByRole('button', { name: 'Netclaw connection status' }).click();
    await contained(page.getByRole('dialog', { name: 'Netclaw connection details' }));
    await page.screenshot({ path: info.outputPath('native-zoom-200.png'), fullPage: true });
    await assertNoHorizontalOverflow(page);
  } finally { await context.close(); await rm(profile, { recursive: true, force: true }); }
});

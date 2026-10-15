import { expect, test, Page } from '@playwright/test';
const fixture = 'http://127.0.0.1:18304/fixture';
const preview = (page: Page) => page.getByRole('button', { name: 'Preview: Synthetic service notice' });
const frame = (page: Page) => page.frameLocator('iframe[title="Original email content"]').first();
const style = (element: HTMLElement) => ({ foreground: getComputedStyle(element).color, background: getComputedStyle(element).backgroundColor });
async function ready(page: Page, route: string) {
  await page.goto(route); await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
}

test('synthetic pre-fix computed styles reproduce pale foreground on original light canvas', async ({ page }, info) => {
  await page.setContent(`<style>:root{--mud-palette-text-primary:#edf1f5}.ticket-message-body :is(p,div,span,table,tbody,thead,tr,td,th,font,strong,b,em,i,u,li,ol,ul,blockquote,pre){color:var(--mud-palette-text-primary)!important}</style><div class="ticket-message-body"><div style="background-color:#ffffff;color:#202020"><p>Synthetic service notice before fix</p></div></div>`);
  const before = await page.locator('.ticket-message-body > div').evaluate(style);
  expect(before).toEqual({ foreground: 'rgb(237, 241, 245)', background: 'rgb(255, 255, 255)' });
  await info.attach('before-computed-style', { body: JSON.stringify(before), contentType: 'application/json' });
  await page.screenshot({ path: info.outputPath('before-dark-washed-out.png') });
});

test.describe('actual Blazor email boundary', () => {
  test.beforeEach(async ({ page, request }) => { await request.get(`${fixture}/reset`); await page.goto('/auth/development'); });
  for (const theme of ['light', 'dark']) {
    for (const width of [320, 768, 1920]) {
      test(`${theme} ${width}px original email preserves colours and blocks active content`, async ({ page }, info) => {
        await page.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
        await page.setViewportSize({ width, height: 900 });
        const tracking: string[] = []; page.on('request', request => { if (request.url().includes('tracker.example.test')) tracking.push(request.url()); });
        await ready(page, '/incidents');
        await preview(page).focus();
        await expect(page.getByRole('dialog', { name: 'Email preview' })).toBeVisible();
        const original = frame(page);
        await expect(original.getByText('Readable original foreground')).toBeVisible();
        const computed = await original.locator('body > div').evaluate(style);
        expect(computed).toEqual({ foreground: 'rgb(32, 32, 32)', background: 'rgb(255, 255, 255)' });
        await expect(original.locator('table')).toHaveCSS('background-color', 'rgb(255, 248, 220)');
        await expect(original.locator('iframe,form,object,[onerror],[href]')).toHaveCount(0);
        expect(await page.evaluate(() => (window as any).emailExploit)).toBeUndefined();
        expect(tracking).toEqual([]);
        const bounds = await page.getByRole('dialog', { name: 'Email preview' }).boundingBox();
        expect(bounds!.width).toBeLessThanOrEqual(width); expect(bounds!.x).toBeGreaterThanOrEqual(0);
        await info.attach('after-computed-style', { body: JSON.stringify({ theme, width, computed }), contentType: 'application/json' });
        await page.screenshot({ path: info.outputPath(`after-${theme}-${width}.png`), fullPage: true });
        await page.getByRole('button', { name: 'Plain text', exact: true }).click();
        await expect(page.locator('.email-plain-text')).toContainText('Synthetic complete plain text');
        expect((await page.locator('.email-plain-text').innerText()).length).toBeGreaterThan(400);
        await page.getByRole('button', { name: 'Plain text', exact: true }).press('Escape');
        await expect(page.getByRole('dialog', { name: 'Email preview' })).toHaveCount(0);
        await expect(preview(page)).toBeFocused();
      });
    }
  }
  test('hover remains interactive; keyboard Escape from focused iframe dismisses without navigation', async ({ page }) => {
    await ready(page, '/incidents'); await preview(page).hover();
    await expect(frame(page).getByText('Readable original foreground')).toBeVisible();
    await page.getByRole('dialog', { name: 'Email preview' }).hover(); await page.waitForTimeout(250);
    await expect(page.getByRole('dialog', { name: 'Email preview' })).toBeVisible();
    await frame(page).getByText('Readable original foreground').click();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: 'Email preview' })).toHaveCount(0);
    await expect(page).toHaveURL(/\/incidents$/);
  });
  test('dark email keeps its own colours; theme changes keep the open detail readable', async ({ page, request }) => {
    await request.get(`${fixture}/mode?value=dark`); await ready(page, '/incidents/incidents-1');
    await expect(frame(page).getByText('Readable dark foreground')).toBeVisible();
    for (const theme of ['dark', 'light']) {
      await page.getByTestId('theme-preference-menu').click(); await page.getByTestId(`theme-option-${theme}`).click();
      await expect(frame(page).locator('body > div')).toHaveCSS('background-color', 'rgb(22, 22, 22)');
      await expect(frame(page).locator('body > div')).toHaveCSS('color', 'rgb(238, 238, 238)');
    }
  });
  test('loading, retry and empty are distinct; request detail uses its own plain text API', async ({ page, request }) => {
    await request.get(`${fixture}/mode?value=slow`); await ready(page, '/incidents'); await preview(page).focus();
    await expect(page.getByRole('status').filter({ hasText: 'Loading content' })).toBeVisible();
    await preview(page).press('Escape'); await request.get(`${fixture}/mode?value=failure`); await preview(page).click();
    await expect(page.getByRole('alert').filter({ hasText: 'Unable to load this content' })).toBeVisible();
    await request.get(`${fixture}/mode?value=empty`); await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(page.getByText('No content available.', { exact: true })).toBeVisible();
    await ready(page, '/requests/requests-1');
    // The integration supplies TicketType through the activity tabs; no incident endpoint is valid here.
    await expect(page.locator('.email-plain-text')).toHaveText('<b>Literal description</b>');
    const calls = (await (await request.get(`${fixture}/state`)).json()).calls;
    expect(calls).not.toContain('/api/v1/incidents/requests-1/peek');
  });
});

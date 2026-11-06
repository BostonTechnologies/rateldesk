import { expect, test } from '@playwright/test';

const fixture = 'http://127.0.0.1:18299';
const id = '11111111-1111-4111-8111-111111111111';
const routes = ['/incidents/ux-chat', `/ai/chat?organizationId=org&conversationId=${id}`, `/ai/ops?id=${id}`];

for (const route of routes) for (const fullscreen of [false, true]) {
  test(`${route.split('?')[0]} ${fullscreen ? 'fullscreen' : 'normal'} Enter and Alt+Enter preserve exact text`, async ({ page, request }) => {
    await request.get(`${fixture}/fixture/reset?mode=completed`);
    await page.goto('/auth/development');
    await page.goto(route);
    if (route.startsWith('/incidents/')) await page.getByRole('tab', { name: 'AI Assistant', exact: true }).click();
    const input = page.getByRole('textbox', { name: 'Message Netclaw' });
    await expect(input).toBeEnabled();
    if (fullscreen) await page.getByRole('button', { name: 'Maximise conversation' }).click();
    await input.fill('alpha beta');
    await input.evaluate((element: HTMLTextAreaElement) => element.setSelectionRange(6, 10));
    await input.press('Alt+Enter');
    await expect(input).toHaveValue('alpha \n');
    expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(0);
    await input.pressSequentially('last character!');
    await input.press('Enter');
    await expect.poll(async () => (await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(1);
    const snapshot = await (await request.get(`${fixture}/api/v1/incidents/ux-chat/chat`)).json();
    expect(snapshot.events.filter((event: any) => event.Type === 'operator').at(-1).Text).toBe('alpha \nlast character!');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}

test('coarse pointer with physical keys, IME, repetition, and whitespace never cause duplicate sends', async ({ browser, request }) => {
  const context = await browser.newContext({ hasTouch: true, viewport: { width: 390, height: 900 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    await request.get(`${fixture}/fixture/reset?mode=completed`);
    await page.goto('https://127.0.0.1:5158/auth/development');
    await page.goto(`https://127.0.0.1:5158/ai/chat?organizationId=org&conversationId=${id}`);
    const input = page.getByRole('textbox', { name: 'Message Netclaw' });
    await expect(input).toBeEnabled();
    await input.fill('   '); await input.press('Enter');
    await input.fill('IME text');
    await input.dispatchEvent('compositionstart');
    await input.dispatchEvent('keydown', { key: 'Enter', code: 'Enter', isComposing: true });
    await input.dispatchEvent('compositionend');
    await input.dispatchEvent('keydown', { key: 'Enter', code: 'Enter', repeat: true });
    expect((await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(0);
    await input.fill('Physical keyboard on touch device');
    await input.press('Enter');
    await expect.poll(async () => (await (await request.get(`${fixture}/fixture/chat-counts`)).json()).sends).toBe(1);
  } finally { await context.close(); }
});

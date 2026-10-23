import { expect, test, Page } from '@playwright/test';
import { assertNoHorizontalOverflow } from './auth';
test.setTimeout(60_000);
const fixture = 'http://127.0.0.1:18700/fixture';
async function ready(page: Page, route = '/admin/pending-emails') {
  await page.goto(route);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 15_000 });
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
}
test.beforeEach(async ({ request, page }) => { await request.get(`${fixture}/reset`); await page.goto('/auth/development'); });
for (const theme of ['light', 'dark']) for (const width of [320, 768, 1440, 1920]) {
  test(`pending mail ${theme} ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(theme => localStorage.setItem('helpdesk.theme.preference', theme), theme);
    await ready(page);
    await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
    await expect(page.locator('.pending-mail tbody tr')).toHaveCount(25);
    await page.getByRole('button', { name: 'Details', exact: true }).first().click();
    await expect(page.getByRole('region', { name: 'Delivery details' })).toBeVisible();
    await assertNoHorizontalOverflow(page);
    await page.screenshot({ path: testInfo.outputPath(`pending-${theme}-${width}.png`), fullPage: true });
  });
}
test('server paging, filter URL, Back and explicit selection scope', async ({ page, request }) => {
  await ready(page);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByText('Page 2 of 49', { exact: true })).toBeVisible();
  await page.locator('.pending-mail tbody input[type="checkbox"]').first().check();
  await expect(page.getByText('1205 deliveries · 1 selected records on this page')).toBeVisible();
  await page.getByRole('textbox', { name: 'Search ticket or recipient' }).fill('INC-SYN-0001');
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
  await expect(page.getByText('1 deliveries · 0 selected records on this page')).toBeVisible();
  await expect(page.getByRole('link', { name: 'INC-SYN-0001' })).toHaveAttribute('href', '/incidents/synthetic-1');
  await page.goBack();
  await expect(page.getByText('Page 2 of 49', { exact: true })).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.requests.some((q: any) => q.page === '1')).toBe(true);
  expect(state.requests.some((q: any) => q.search === 'INC-SYN-0001' && q.page === '0')).toBe(true);
});
test('applied filter removal is reachable by Tab and resets URL, page and selection', async ({ page, request }) => {
  await ready(page, '/admin/pending-emails?search=INC-SYN-0&page=1');
  await expect(page.getByText('Page 2 of 40', { exact: true })).toBeVisible();
  await page.locator('.pending-mail tbody input[type="checkbox"]').first().check();
  await expect(page.getByText('999 deliveries · 1 selected records on this page')).toBeVisible();
  await page.getByRole('button', { name: 'Apply filters', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Clear filters', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Remove filter search: INC-SYN-0', exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
  await expect(page.getByText('Page 1 of 49', { exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('search')).toBeNull();
  expect(new URL(page.url()).searchParams.get('page')).toBe('0');
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.actions).toHaveLength(0);
  expect(state.requests.at(-1).page).toBe('0');
  expect(state.requests.at(-1).search ?? '').toBe('');
});
test('bounded selection, cancelled confirmation, mixed results, removal and refreshed totals', async ({ page, request }) => {
  await ready(page);
  await page.locator('.pending-mail thead input[type="checkbox"]').check();
  await expect(page.getByText('1205 deliveries · 25 selected records on this page')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('25 explicitly selected record(s)');
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await (await request.get(`${fixture}/state`)).json()).actions).toHaveLength(0);
  await page.getByRole('button', { name: 'Retry selected', exact: true }).click();
  await page.getByRole('dialog').getByRole('textbox', { name: 'Reason' }).fill('Synthetic operator review');
  await page.getByRole('dialog').getByRole('button', { name: 'Retry deliveries', exact: true }).click();
  await expect(page.getByText(/Action results \(25 selected records\)/)).toBeVisible();
  await expect(page.getByText(/ReviewRequired — Individual review required/).first()).toBeVisible();
  await page.getByRole('button', { name: 'Remove queue entry', exact: true }).first().click();
  await page.getByRole('dialog').getByRole('textbox', { name: 'Reason' }).fill('Synthetic operator review');
  await page.getByRole('dialog').getByRole('button', { name: 'Remove entries', exact: true }).click();
  await expect(page.getByText(/Action results \(1 selected records\)/)).toBeVisible();
  const state = await (await request.get(`${fixture}/state`)).json();
  expect(state.actions[0].ids).toHaveLength(25); expect(state.actions[0].confirmed).toBe(true);
  expect(state.actions[1].ids).toHaveLength(1);
  await expect(page.getByText(/0 selected records on this page/)).toBeVisible();
});
test('failure and zero states remain distinct and recover, keyboard at 200 percent zoom', async ({ page, request }) => {
  await ready(page);
  await request.get(`${fixture}/fail?enabled=true`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText('Delivery list unavailable. Refresh to try again.')).toBeVisible({ timeout: 35_000 });
  await request.get(`${fixture}/fail?enabled=false`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText('1205 deliveries · 0 selected records on this page')).toBeVisible();
  await page.getByRole('textbox', { name: 'Search ticket or recipient' }).fill('does-not-exist');
  await page.getByRole('button', { name: 'Apply filters', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByText('No deliveries match these filters.')).toBeVisible();
  await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
  await assertNoHorizontalOverflow(page);
});

for (const environment of ['normal browser', 'embedded browser']) {
  test.describe(environment, () => {
    test.beforeEach(async ({ page }) => {
      if (environment === 'embedded browser') await page.addInitScript(() => {
        (window as any).__nativeDialogCalls = 0;
        for (const method of ['prompt', 'confirm'] as const) window[method] = (() => {
          (window as any).__nativeDialogCalls++;
          throw new Error(`${method}() is not supported`);
        }) as any;
      });
      page.on('dialog', async dialog => { await dialog.dismiss(); throw new Error(`Unexpected native ${dialog.type()}`); });
    });
    test.afterEach(async ({ page }) => {
      expect(await page.evaluate(() => (window as any).__nativeDialogCalls ?? 0)).toBe(0);
    });

    test('18 exact cancellations retain history and leave 601 unrelated deliveries untouched after every dismissal', async ({ page, request }) => {
      await request.get(`${fixture}/reset?scenario=campaign`);
      const before = await (await request.get(`${fixture}/state`)).json();
      const selectedIds = before.data.slice(0, 18).map((x: any) => x.id);
      await ready(page, '/admin/pending-emails?search=Disposable%20campaign');
      await expect(page.getByText('18 deliveries · 0 selected records on this page')).toBeVisible();
      await page.locator('.pending-mail thead input[type="checkbox"]').check();
      for (const dismissal of ['cancel', 'escape', 'close', 'backdrop']) {
        await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
        const dialog = page.getByRole('dialog');
        await expect(dialog).toContainText('18 explicitly selected record(s)');
        await expect(dialog.getByRole('region', { name: 'Selected deliveries' }).locator('small')).toHaveCount(18);
        if (dismissal === 'cancel') await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        if (dismissal === 'escape') { await dialog.getByRole('button', { name: 'Cancel', exact: true }).focus(); await page.keyboard.press('Escape'); }
        if (dismissal === 'close') await dialog.getByRole('button', { name: 'Close', exact: true }).click();
        if (dismissal === 'backdrop') await page.locator('.mud-overlay-dialog').click({ position: { x: 5, y: 5 } });
        await expect(dialog).toBeHidden();
        await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
        await expect(page.getByText('18 deliveries · 18 selected records on this page')).toBeVisible();
        const state = await (await request.get(`${fixture}/state`)).json();
        expect(state.actions).toEqual([]); expect(state.retries).toEqual([]); expect(state.submissions).toEqual([]);
        expect(state.data).toEqual(before.data); expect(state.history).toEqual(before.history);
      }
      await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
      const dialog = page.getByRole('dialog'), reason = dialog.getByRole('textbox', { name: 'Reason' });
      const confirm = dialog.getByRole('button', { name: 'Cancel deliveries', exact: true });
      await expect(confirm).toBeDisabled();
      for (const invalid of [' ', 'x'.repeat(201)]) {
        await reason.fill('Synthetic valid reason'); await expect(confirm).toBeEnabled();
        await reason.fill(invalid); await expect(confirm).toBeDisabled();
      }
      await reason.fill('x'.repeat(200)); await expect(confirm).toBeEnabled();
      await reason.fill(''); await expect(confirm).toBeDisabled();
      await reason.fill('Synthetic cancellation regression'); await expect(confirm).toBeEnabled();
      await request.get(`${fixture}/action-options?delay=700`);
      // Deliver two click events before the server returns: the review and page both fence submission.
      await confirm.evaluate((element: HTMLElement) => { element.click(); element.click(); });
      await expect(dialog).toBeHidden();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeDisabled();
      await expect(page.getByText('Action results (18 selected records)')).toBeVisible();
      const after = await (await request.get(`${fixture}/state`)).json();
      expect(after.actions).toEqual([{ ids: expect.any(Array), action: 'cancel', reason: 'Synthetic cancellation regression', confirmed: true }]);
      expect([...after.actions[0].ids].sort()).toEqual([...selectedIds].sort());
      expect(after.data.slice(0, 18).every((x: any) => x.emailStatus === 5)).toBe(true);
      expect(after.data.slice(18)).toEqual(before.data.slice(18));
      expect(after.history).toEqual(before.history); expect(after.submissions).toEqual([]);
      await expect(page.getByRole('status').getByText(/Cancelled — Synthetic action persisted/)).toHaveCount(18);
      await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
      await expect(page.getByText('601 deliveries · 0 selected records on this page')).toBeVisible();
    });

    test('browser Back keeps action busy and invalidates a review opened before navigation', async ({ page, request }) => {
      await request.get(`${fixture}/reset?scenario=campaign`);
      await ready(page);
      await page.getByRole('textbox', { name: 'Search ticket or recipient' }).fill('Disposable campaign');
      await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
      await expect(page.getByText('18 deliveries · 0 selected records on this page')).toBeVisible();
      await page.locator('.pending-mail thead input[type="checkbox"]').check();
      await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText('18 explicitly selected record(s)');
      await page.goBack();
      await expect(page.getByText('619 deliveries · 0 selected records on this page')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeDisabled();
      await expect(page.getByRole('button', { name: 'Cancel delivery', exact: true }).first()).toBeDisabled();
      await dialog.getByRole('textbox', { name: 'Reason' }).fill('Stale navigation review');
      await dialog.getByRole('button', { name: 'Cancel deliveries', exact: true }).click();
      await expect(dialog).toBeHidden();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      const state = await (await request.get(`${fixture}/state`)).json();
      expect(state.actions).toEqual([]); expect(state.retries).toEqual([]); expect(state.submissions).toEqual([]);
      expect(state.data.every((row: any) => row.emailStatus === 3)).toBe(true);
    });

    test('action errors refresh actual state and allow another reviewed action', async ({ page, request }) => {
      await request.get(`${fixture}/reset?scenario=campaign`);
      await ready(page, '/admin/pending-emails?search=Disposable%20campaign');
      await request.get(`${fixture}/action-options?fail=true`);
      const submit = async () => {
        await page.locator('.pending-mail thead input[type="checkbox"]').check();
        await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
        await page.getByRole('dialog').getByRole('textbox', { name: 'Reason' }).fill('Synthetic recovery');
        await page.getByRole('dialog').getByRole('button', { name: 'Cancel deliveries', exact: true }).click();
      };
      await submit();
      await expect(page.getByText('Queue action failed. Refresh before trying again.')).toBeVisible();
      await expect(page.getByText('18 deliveries · 0 selected records on this page')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      await request.get(`${fixture}/action-options?fail=false`); await submit();
      await expect(page.getByText('Action results (18 selected records)')).toBeVisible();
      const state = await (await request.get(`${fixture}/state`)).json();
      expect(state.actions).toHaveLength(2); expect(state.actions[0].ids.sort()).toEqual(state.actions[1].ids.sort());
      expect(state.submissions).toEqual([]);
    });

    for (const [number, title, button, operation] of [
      [1, 'Retry legacy delivery', 'Queue legacy retry', 'retry'],
      [2, 'Review current outgoing settings', 'Queue retry', 'retry-current-outgoing'],
      [3, 'Confirm non-delivery before retry', 'Queue verified retry', 'retry-confirmed-undelivered'],
      [4, 'Review changed sender route', 'Queue with current sender', 'retry-current-route'],
      [5, 'Review changed sender route', 'Queue with current sender', 'retry-current-route']
    ] as const) test(`${title} (${number}): dismissal and reviewed positive preserve sender fences`, async ({ page, request }) => {
      await request.get(`${fixture}/reset?scenario=retries`);
      await ready(page, `/admin/pending-emails?search=INC-SYN-000${number}`);
      await page.getByRole('button', { name: 'Review / retry', exact: true }).click();
      let dialog = page.getByRole('dialog'); await expect(dialog).toContainText(title);
      await expect(dialog).toContainText(`recipient${number}@example.test`);
      if (operation === 'retry-current-outgoing') await expect(dialog).toContainText('outgoing revision 4 → 7');
      if (operation === 'retry-current-route') {
        await expect(dialog).toContainText('old-support@example.test');
        await expect(dialog).toContainText('current-support@example.test');
        await expect(dialog).toContainText('Current incoming revision 5; outgoing revision 7');
      }
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).focus(); await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      expect((await (await request.get(`${fixture}/state`)).json()).retries).toEqual([]);
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      await page.getByRole('button', { name: 'Review / retry', exact: true }).click(); dialog = page.getByRole('dialog');
      const confirm = dialog.getByRole('button', { name: button, exact: true });
      if (number === 3) {
        const evidence = dialog.getByRole('textbox', { name: 'Non-delivery evidence reference' });
        await expect(confirm).toBeDisabled();
        await dialog.getByRole('checkbox', { name: 'I verified non-delivery for every listed recipient.' }).check();
        for (const invalid of ['short', 'invalid reference', '_bad-first', 'x'.repeat(129)]) {
          await evidence.fill('Provider:valid-reference'); await expect(confirm).toBeEnabled();
          await evidence.fill(invalid); await expect(evidence).toHaveAttribute('aria-invalid', 'true'); await expect(confirm).toBeDisabled();
        }
        await evidence.fill('x'.repeat(128)); await expect(confirm).toBeEnabled();
        await evidence.fill('case-12'); await expect(confirm).toBeDisabled();
        await evidence.fill('case-123'); await expect(confirm).toBeEnabled();
        // Await the server validation transition before submitting a new valid reference.
        await evidence.fill(''); await expect(confirm).toBeDisabled();
        await evidence.fill('Provider:case-123/verified'); await expect(confirm).toBeEnabled();
      }
      await confirm.click(); await expect(dialog).toBeHidden();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      const state = await (await request.get(`${fixture}/state`)).json();
      expect(state.retries).toHaveLength(1); expect(state.retries[0].operation).toBe(operation);
      expect(state.retries[0].id).toBe(`00000000-0000-4000-8000-${String(number).padStart(12, '0')}`);
      if (number === 2) expect(state.retries[0].request).toEqual({ expectedOutgoingVersion: 7, confirmed: true,
        expectedMailboxId: '00000000-0000-4000-8000-000000009001', expectedMailboxVersion: 5, expectedFence: 23 });
      if (number === 3) expect(state.retries[0].request).toEqual({ expectedFence: 23, expectedOutgoingVersion: 7,
        evidenceReference: 'Provider:case-123/verified', confirmedNoDelivery: true });
      if (number === 4 || number === 5) expect(state.retries[0].request).toEqual({ expectedFence: 23,
        expectedOriginalMailboxId: '00000000-0000-4000-8000-000000009001', expectedCurrentMailboxId: '00000000-0000-4000-8000-000000009002',
        expectedMailboxVersion: 5, expectedOutgoingVersion: 7, confirmedNewSender: true });
      expect(state.actions).toEqual([]); expect(state.submissions).toEqual([]);
    });

    test('retry preview and mutation failures leave review and refresh operable', async ({ page, request }) => {
      await request.get(`${fixture}/reset?scenario=retries`);
      await ready(page, '/admin/pending-emails?search=INC-SYN-0004');
      await request.get(`${fixture}/fail?enabled=true`);
      await page.getByRole('button', { name: 'Review / retry', exact: true }).click();
      await expect(page.getByText('Delivery retry could not be queued. Refresh and review the delivery.')).toBeVisible({ timeout: 35_000 });
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      expect((await (await request.get(`${fixture}/state`)).json()).retries).toEqual([]);
      await request.get(`${fixture}/fail?enabled=false`); await request.get(`${fixture}/action-options?fail=true`);
      await page.getByRole('button', { name: 'Review / retry', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Queue with current sender', exact: true }).click();
      await expect(page.getByRole('dialog')).toBeHidden();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      await request.get(`${fixture}/action-options?fail=false`);
      await page.getByRole('button', { name: 'Refresh', exact: true }).click();
      await page.getByRole('button', { name: 'Review / retry', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
      await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
      const state = await (await request.get(`${fixture}/state`)).json(); expect(state.retries).toHaveLength(1); expect(state.submissions).toEqual([]);
    });
  });
}

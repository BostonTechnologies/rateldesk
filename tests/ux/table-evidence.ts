import { expect, test, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const captures = new Map<string, number>();

/** Optional rendered evidence for the owner's complete table audit. Never reads a remote instance. */
export async function captureTables(page: Page) {
  const directory = process.env.RATELDESK_TABLE_EVIDENCE_DIR;
  if (!directory || page.isClosed() || page.url() === 'about:blank') return;
  const url = new URL(page.url());
  if (!['127.0.0.1', 'localhost'].includes(url.hostname)) throw new Error('Table evidence requires an isolated loopback fixture.');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  const info = test.info();
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation =>
    animation.playState !== 'finished' && animation.playState !== 'idle' && Number.isFinite(animation.effect?.getComputedTiming().endTime)
  ).length)).toBe(0);
  const ordinal = (captures.get(info.testId) ?? 0) + 1;
  captures.set(info.testId, ordinal);
  const context = `${path.basename(info.file, '.ts')}-${info.line}-${info.testId.slice(-8)}-${ordinal}`;
  const destination = path.join(directory, context);
  await mkdir(destination, { recursive: true });
  const receipts: object[] = [];
  const instances = new Map<string, number>();
  const dialog = page.getByRole('dialog').last();
  const surfaces = await dialog.count() ? dialog.locator('[data-table-surface]') : page.locator('[data-table-surface]');
  for (const surface of await surfaces.all()) {
    if (!await surface.isVisible()) continue;
    const id = (await surface.getAttribute('data-table-surface'))!;
    const instance = (instances.get(id) ?? 0) + 1;
    instances.set(id, instance);
    const hostContext = await surface.evaluate(element => element.closest('section[aria-label], [role="region"][aria-label]')?.getAttribute('aria-label') ?? null);
    for (const action of await surface.locator('.workspace-actions .mud-icon-button, .workspace-action .mud-icon-button').all())
      await expect(action, `${id}: row action has an accessible name`).toHaveAccessibleName(/.+/);
    const scrollHandle = await surface.evaluateHandle(element => {
      const candidates = [element.querySelector('.mud-table-container'), element.closest('.chat-table-scroll'), element].filter((value): value is Element => value !== null);
      return candidates.find(value => value.scrollHeight > value.clientHeight + 1 || value.scrollWidth > value.clientWidth + 1) ?? candidates[0];
    });
    const scroll = scrollHandle.asElement()!;
    const focusTarget = surface.locator('button:enabled:visible, a[href]:visible, input:enabled:not([type="hidden"]):visible, [tabindex="0"]:not([aria-disabled="true"]):visible').first();
    let keyboardFocus = await focusTarget.count() > 0;
    if (keyboardFocus) { await focusTarget.focus(); await expect(focusTarget).toBeFocused(); }
    await surface.scrollIntoViewIfNeeded();
    const scrollable = await scroll.evaluate(element => ({ vertical: element.scrollHeight > element.clientHeight + 1, horizontal: element.scrollWidth > element.clientWidth + 1 }));
    if (!keyboardFocus && (scrollable.vertical || scrollable.horizontal)) {
      await scroll.focus();
      expect(await scroll.evaluate(element => document.activeElement === element), `${id}: content scroll region receives keyboard focus`).toBe(true);
      await scroll.evaluate(element => {
        element.setAttribute('data-review-keyboard-scroll', 'pending');
        element.addEventListener('scrollend', () => element.setAttribute('data-review-keyboard-scroll', 'settled'), { once: true });
      });
      await scroll.press(scrollable.vertical ? 'PageDown' : 'ArrowRight');
      await expect.poll(() => scroll.evaluate(element => Math.max(element.scrollTop, element.scrollLeft)), { message: `${id}: content scrolls from the keyboard` }).toBeGreaterThan(0);
      await expect.poll(() => scroll.getAttribute('data-review-keyboard-scroll')).toBe('settled');
      keyboardFocus = true;
    }
    const scrollMetrics = await scroll.evaluate(element => ({ scrollHeight: element.scrollHeight, clientHeight: element.clientHeight, scrollViewportTop: element.getBoundingClientRect().top }));
    await scroll.evaluate(element => {
      element.scrollTop = 0; element.scrollLeft = 0;
      return new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    });
    const before = await surface.evaluate(element => {
      const table = element.matches('table') ? element : element.querySelector('table');
      const header = table?.querySelector('thead th');
      const pager = element.querySelector('.mud-table-pagination');
      const bounds = element.matches('table') ? element.closest('.chat-table-scroll') ?? element : element;
      const box = bounds.getBoundingClientRect();
      return { rows: table?.querySelectorAll('tbody tr').length ?? 0, headers: table?.querySelectorAll('thead th').length ?? 0,
        headerVisible: !!header && getComputedStyle(header).display !== 'none' && header.getBoundingClientRect().height > 0,
        headerTop: header?.getBoundingClientRect().top ?? 0,
        pagerTop: pager?.getBoundingClientRect().top ?? null,
        width: box.width, height: box.height,
        viewport: { width: innerWidth, height: innerHeight }, theme: document.documentElement.dataset.helpdeskTheme,
        pageOverflow: document.documentElement.scrollWidth - innerWidth };
    }).then(measurement => ({ ...measurement, ...scrollMetrics }));
    expect(before.pageOverflow, `${id}: content stays contained`).toBeLessThanOrEqual(2);
    expect(before.width, `${id}: table region fits the viewport`).toBeLessThanOrEqual(before.viewport.width + 2);
    await scroll.evaluate(element => {
      element.scrollTop = element.scrollHeight;
      return new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    });
    const after = await surface.evaluate(element => {
      const table = element.matches('table') ? element : element.querySelector('table');
      const pager = element.querySelector('.mud-table-pagination');
      return { headerTop: table?.querySelector('thead th')?.getBoundingClientRect().top ?? 0,
        pagerTop: pager?.getBoundingClientRect().top ?? null,
        pagerBottom: pager?.getBoundingClientRect().bottom ?? null };
    }).then(async measurement => ({ ...measurement, ...await scroll.evaluate(element => ({ scrollTop: element.scrollTop, scrollViewportTop: element.getBoundingClientRect().top })) }));
    const scrolled = after.scrollTop > 1;
    await writeFile(path.join(destination, `${id}-${instance}-geometry.json`), JSON.stringify({ id, instance, before, after }, null, 2));
    if (scrolled && before.headerVisible) expect(Math.abs((after.headerTop - after.scrollViewportTop) - (before.headerTop - before.scrollViewportTop)), `${id}: header stays fixed`).toBeLessThan(2);
    if (after.pagerTop !== null) {
      expect(Math.abs(after.pagerTop - before.pagerTop!), `${id}: pager stays outside body scrolling`).toBeLessThan(2);
      expect(after.pagerBottom!, `${id}: pager is reachable`).toBeLessThanOrEqual(before.viewport.height + 2);
    }
    await scroll.evaluate(element => {
      element.scrollTop = 0; element.scrollLeft = 0;
      return new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    });
    const screenshot = `${id}-${instance}.png`;
    const target = await surface.evaluate(element => element.matches('table')) ? surface.locator('xpath=..') : surface;
    await target.screenshot({ path: path.join(destination, screenshot), animations: 'disabled',
      mask: [page.locator('[data-sensitive], [data-testid*=secret], [class*=credential-secret], input[type=password]')] });
    receipts.push({ id, instance, hostContext, route: url.pathname, test: info.title, screenshot, before, after,
      checked: { contained: true, scroll: scrolled, stickyHeader: scrolled && before.headerVisible, reachablePager: after.pagerTop !== null, keyboardFocus },
      headerMode: before.headerVisible ? 'column headers' : before.headers ? 'responsive labels' : 'semantic content without a column header' });
    await scrollHandle.dispose();
  }
  await writeFile(path.join(destination, 'tables.json'), JSON.stringify(receipts, null, 2));
}

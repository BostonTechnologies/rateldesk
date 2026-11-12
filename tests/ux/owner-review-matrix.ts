import { chromium, expect, test, type Browser, type Page, type TestInfo } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { captureTables } from './table-evidence';

export const baselineReview = process.env.HELPDESK_TABLE_BASELINE === 'true';
export const reviewModes = [
  ...['light', 'dark'].flatMap(theme => [[1366, 768], [1440, 900], [390, 844]].map(([width, height]) => ({ theme, width, height, zoom: 1, custom: false }))),
  ...['light', 'dark'].map(theme => ({ theme, width: 1440, height: 900, zoom: 1, custom: true })),
  ...['light', 'dark'].map(theme => ({ theme, width: 1440, height: 1000, zoom: 2, custom: false }))
];
export type ReviewMode = typeof reviewModes[number];
export const modeName = (mode: ReviewMode) => `${mode.theme} ${mode.width}x${mode.height}${mode.custom ? ' custom palette' : ''}${mode.zoom === 2 ? ' native 200%' : ''}`;

export async function reviewPage(browser: Browser, origin: string, mode: ReviewMode, run: (page: Page) => Promise<void>) {
  const profile = mode.zoom === 2 ? await mkdtemp(path.join(tmpdir(), 'rateldesk-table-review-')) : null;
  const extension = path.resolve('tests/ux/native-zoom-extension');
  const options = { viewport: { width: mode.width, height: mode.height }, ignoreHTTPSErrors: true, timezoneId: 'Africa/Johannesburg' };
  const context = profile ? await chromium.launchPersistentContext(profile, { ...options, channel: 'chromium', headless: false, args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`] }) : await browser.newContext(options);
  let page: Page | undefined;
  try {
    await context.addInitScript(theme => {
      if (window === window.top && ['127.0.0.1', 'localhost'].includes(location.hostname))
        localStorage.setItem('helpdesk.theme.preference', theme);
    }, mode.theme);
    page = await context.newPage();
    await page.bringToFront();
    await page.goto(`${origin}/auth/development`);
    if (profile) {
      await expect.poll(() => context.serviceWorkers().length).toBeGreaterThan(0);
      await context.serviceWorkers()[0].evaluate(async origin => {
        const chrome = (globalThis as any).chrome;
        const tab = (await chrome.tabs.query({})).find((item: any) => item.url.startsWith(origin));
        await chrome.tabs.setZoom(tab.id, 2);
        if (await chrome.tabs.getZoom(tab.id) !== 2) throw new Error('Native zoom was not applied.');
      }, origin);
      await expect.poll(() => page.evaluate(() => innerWidth)).toBe(mode.width / 2);
    }
    await run(page);
  } catch (failure) {
    if (page && !page.isClosed()) {
      await writeFile(test.info().outputPath('failure-accessibility.txt'), await page.locator('body').ariaSnapshot());
      await writeFile(test.info().outputPath('failure-frames.json'), JSON.stringify({
        owners: await page.locator('iframe').evaluateAll(elements => elements.map(element => ({ title: element.title, srcdoc: element.srcdoc, rect: element.getBoundingClientRect().toJSON() }))),
        frames: await Promise.all(page.frames().map(async frame => ({ url: frame.url(), document: await frame.evaluate(() => ({ ready: document.readyState, html: document.body?.innerHTML.slice(0, 4096), rect: document.body?.getBoundingClientRect().toJSON() })).catch(() => null) })))
      }, null, 2));
      await page.screenshot({ path: test.info().outputPath('failure.png'), fullPage: mode.zoom !== 2, mask: [page.locator('input[type=password], [data-sensitive]')] });
    }
    throw failure;
  } finally {
    await context.close();
    if (profile) await rm(profile, { recursive: true });
  }
}

export async function reviewOpen(page: Page, origin: string, route: string) {
  await page.goto(`${origin}${route}`, { waitUntil: 'domcontentloaded' });
  if (!route.startsWith('/request-approval/') && route !== '/submit-ticket' && route !== '/setup')
    await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
  else if (route !== '/setup' && !baselineReview)
    await expect(page.getByTestId('app-public-content')).toHaveAttribute('data-interactive', 'true', { timeout: 20_000 });
  else if (route === '/submit-ticket')
    await page.waitForFunction(() => [...document.querySelectorAll('input')].some(element => element.getAttributeNames().some(name => name.startsWith('_bl_'))));
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}

export async function reviewCapture(page: Page, info: TestInfo, mode: ReviewMode, label: string, expectedIds: string[]) {
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', mode.theme);
  if (mode.custom) {
    // Browser-only compatibility check of theme tokens; never modifies product branding.
    await page.addStyleTag({ content: ':root { --mud-palette-primary: #6868aa; --mud-palette-secondary: #547f80; --mud-palette-table-lines: #9381aa; --mud-palette-divider: #9381aa; }' });
  }
  if (!baselineReview) for (const id of expectedIds) await expect(page.locator(`[data-table-surface="${id}"]:visible`).first()).toBeVisible();
  if (!baselineReview) for (const tabs of await page.locator('.compact-tabs:visible').all()) {
    const slider = tabs.locator('.mud-tab-slider').first();
    if (!await slider.isVisible()) continue;
    const neutral = await tabs.evaluate(element => {
      const probe = document.createElement('span');
      probe.style.color = 'var(--mud-palette-text-secondary)';
      element.append(probe);
      const color = getComputedStyle(probe).color;
      probe.remove();
      return color;
    });
    await expect(slider, 'Tabs use the neutral theme indicator').toHaveCSS('background-color', neutral);
  }
  await page.evaluate(async () => { await document.fonts.ready; });
  await captureTables(page, label);
  const screenshot = info.outputPath(`${label}.png`);
  await page.screenshot({ path: screenshot, fullPage: mode.zoom !== 2, animations: 'disabled', mask: [page.locator('input[type=password], [data-sensitive]')] });
  await writeFile(info.outputPath(`${label}.json`), JSON.stringify({ baseline: baselineReview, route: new URL(page.url()).pathname, mode, expectedIds, screenshot: path.basename(screenshot), screenshotScope: mode.zoom === 2 ? 'native viewport' : 'full page', visibleTables: await page.locator('table:visible').count() }, null, 2));
}

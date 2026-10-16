import { chromium, expect, test, type Page } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
const suite = process.env.HELPDESK_NATIVE_ZOOM_SUITE!;
const routes: Record<string, string[]> = {
  integrations: ['/account/integration-credentials?tab=credentials'],
  admin: ['/admin/roles', '/admin/resources/data-management', '/admin/support-routing', '/admin/tenant-sla-settings'],
  details: ['/incidents/incidents-1', '/requests/requests-1', '/changes/changes-1'],
  email: ['/incidents'],
  'ticket-filters': ['/incidents', '/requests', '/changes'],
  operations: ['/self-service', '/ops/slas', '/ops/workflows', '/ops/hangfire'],
  'pending-mail': ['/admin/pending-emails']
};
const ports: Record<string, number> = { admin: 18500, integrations: 18505, details: 18600, email: 18304, 'ticket-filters': 18330, operations: 18400, 'pending-mail': 18700 };
async function settleFiniteAnimations(page: Page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  });
  // CDP capture does not perform Playwright's animation settlement. Await real
  // finite transitions (including preview opacity), without disabling them or
  // waiting forever for an intentionally looping loading indicator.
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation =>
    animation.playState !== 'finished' && animation.playState !== 'idle' &&
    Number.isFinite(animation.effect?.getComputedTiming().endTime)
  ).length)).toBe(0);
  await page.evaluate(() => new Promise<void>(resolve =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}
for (const theme of ['light', 'dark']) for (const width of [1280, 1920]) {
  test(`${suite} ${theme} native browser zoom 200 percent at ${width}px`, async ({ baseURL, request }, info) => {
    await request.get(`http://127.0.0.1:${ports[suite]}/fixture/reset`);
    const profile = await mkdtemp(path.join(tmpdir(), 'rateldesk-zoom-'));
    const extension = path.resolve('tests/ux/native-zoom-extension');
    const context = await chromium.launchPersistentContext(profile, {
      channel: process.env.HELPDESK_E2E_CHROMIUM_PATH ? undefined : 'chromium',
      executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH, headless: true, ignoreHTTPSErrors: true,
      viewport: { width, height: 1000 },
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`]
    });
    try {
      const worker = context.serviceWorkers()[0] ?? await context.waitForEvent('serviceworker');
      const page = await context.newPage();
      await context.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
      await page.goto(`${baseURL}/auth/development`);
      for (const route of routes[suite]) {
        await page.goto(`${baseURL}${route}`);
        await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
        const zoom = await worker.evaluate(async origin => {
          const chrome = (globalThis as any).chrome;
          const tabs = await chrome.tabs.query({});
          const tab = tabs.find((item: any) => item.url.startsWith(origin));
          await chrome.tabs.setZoom(tab.id, 2);
          return chrome.tabs.getZoom(tab.id);
        }, baseURL!);
        expect(zoom).toBe(2);
        // This verifies browser layout zoom, not CSS zoom or deviceScaleFactor.
        await expect.poll(() => page.evaluate(() => innerWidth)).toBe(width / 2);
        // Both native test widths become narrower than the shell's Lg drawer
        // breakpoint after zoom. Wait for the real responsive close operation,
        // rather than capturing or interacting through a half-translated drawer.
        const drawer = page.getByTestId('app-navigation-drawer');
        await expect.poll(() => drawer.evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(1);
        await settleFiniteAnimations(page);
        await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
        await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
        const tabs = page.getByRole('tab');
        for (let index = 0; index < await tabs.count(); index++) {
          await tabs.nth(index).scrollIntoViewIfNeeded();
          await tabs.nth(index).focus();
          await expect(tabs.nth(index)).toBeFocused();
          await tabs.nth(index).press('Enter');
          await expect(tabs.nth(index)).toHaveAttribute('aria-selected', 'true');
        }
        if (suite === 'integrations') {
          await page.getByLabel('Client code', { exact: true }).fill('ABCD-TEST');
          await page.getByRole('button', { name: 'Review client', exact: true }).click();
          const review = page.getByRole('dialog', { name: 'Connection consent', exact: true });
          await expect(review).toBeVisible();
          await review.getByLabel('Organization', { exact: true }).selectOption('org-a');
          await review.getByRole('button', { name: /^Select \d+ displayed eligible permissions$/ }).click();
          const bounds = await review.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          const footer = await review.locator('footer').boundingBox();
          expect(footer!.y + footer!.height).toBeLessThanOrEqual(501);
          await expect(review.getByRole('button', { name: 'Approve connection', exact: true })).toBeVisible();
        }
        if (suite === 'email') {
          await page.getByRole('button', { name: 'Preview: Synthetic service notice' }).focus();
          const preview = page.getByRole('dialog', { name: 'Email preview' });
          await expect(preview).toBeVisible();
          const bounds = await preview.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          await expect(page.frameLocator('iframe[title="Original email content"]').first().getByText('Readable original foreground')).toBeVisible();
        }
        await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(2);
        // Playwright's fullPage screenshot supplies a CSS-pixel clip. Chromium
        // native zoom applies that clip to device pixels, truncating the right
        // half at 200%. Capture the physical viewport without a clip instead.
        await page.evaluate(() => scrollTo(0, 0));
        await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
        await settleFiniteAnimations(page);
        await expect.poll(() => drawer.evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(1);
        const cdp = await context.newCDPSession(page);
        let screenshot: Buffer;
        try {
          const capture = await cdp.send('Page.captureScreenshot', {
            format: 'png', fromSurface: true, captureBeyondViewport: false
          });
          screenshot = Buffer.from(capture.data, 'base64');
        } finally {
          await cdp.detach();
        }
        const captureWidth = screenshot.readUInt32BE(16);
        const captureHeight = screenshot.readUInt32BE(20);
        expect(captureWidth).toBe(width);
        expect(captureHeight).toBe(1000);
        // Route queries remain part of navigation and evidence, but artifact
        // paths must also be valid on Windows and GitHub's artifact service.
        const routeFileName = route.replace(/[^a-zA-Z0-9._-]+/g, '-');
        await writeFile(info.outputPath(`${routeFileName}-${theme}-${width}-native-200.png`), screenshot);
        await info.attach(`${route}-native-zoom`, { body: JSON.stringify({ zoom, physicalWidth: width, layoutWidth: await page.evaluate(() => innerWidth), theme,
          capture: 'physical viewport without CSS clip', captureWidth, captureHeight }), contentType: 'application/json' });
      }
    } finally {
      await context.close();
      await rm(profile, { recursive: true, force: true });
    }
  });
}

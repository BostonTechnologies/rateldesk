import { chromium, expect, test } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
const suite = process.env.HELPDESK_NATIVE_ZOOM_SUITE!;
const routes: Record<string, string[]> = {
  admin: ['/admin/roles', '/admin/resources/data-management', '/admin/support-routing', '/admin/tenant-sla-settings'],
  details: ['/incidents/incidents-1', '/requests/requests-1', '/changes/changes-1'],
  email: ['/incidents'],
  'ticket-filters': ['/incidents', '/requests', '/changes'],
  operations: ['/self-service', '/ops/slas', '/ops/workflows', '/ops/hangfire'],
  'pending-mail': ['/admin/pending-emails']
};
const ports: Record<string, number> = { admin: 18500, details: 18600, email: 18304, 'ticket-filters': 18330, operations: 18400, 'pending-mail': 18700 };
for (const theme of ['light', 'dark']) for (const width of [1280, 1920]) {
  test(`${suite} ${theme} native browser zoom 200 percent at ${width}px`, async ({ baseURL, request }, info) => {
    await request.get(`http://127.0.0.1:${ports[suite]}/fixture/reset`);
    const profile = await mkdtemp(path.join(tmpdir(), 'rateldesk-zoom-'));
    const extension = path.resolve('tests/ux/native-zoom-extension');
    const context = await chromium.launchPersistentContext(profile, {
      channel: 'chromium', headless: true, ignoreHTTPSErrors: true,
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
        await page.screenshot({ path: info.outputPath(`${route.replaceAll('/', '-')}-${theme}-${width}-native-200.png`), fullPage: true });
        await info.attach(`${route}-native-zoom`, { body: JSON.stringify({ zoom, physicalWidth: width, layoutWidth: await page.evaluate(() => innerWidth), theme }), contentType: 'application/json' });
      }
    } finally {
      await context.close();
      await rm(profile, { recursive: true, force: true });
    }
  });
}

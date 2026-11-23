import { createHmac } from 'node:crypto';
import { expect, type Page } from '@playwright/test';

export async function expectSignedRasterIsolation(page: Page, signingKey: string) {
  const expiry = Number(process.env.HELPDESK_E2E_ASSET_EXPIRY);
  for (const [collection, scope] of [['incidents', 'incident'], ['worklogs', 'worklog']]) {
    for (const filename of ['sample.svg', 'sample.png', 'raster.png']) {
      const payload = [scope, 'browser-fixture', filename, expiry].join('|');
      const signature = createHmac('sha256', signingKey).update(payload).digest('base64url');
      const token = Buffer.from(payload + '|' + signature).toString('base64url');
      const response = await page.goto('/api/' + collection + '/browser-fixture/images/' + filename + '?token=' + token);
      expect(response?.headers()['x-content-type-options']).toBe('nosniff');
      expect(response?.headers()['content-security-policy']).toContain('sandbox');
      if (filename === 'raster.png') {
        expect(response?.status()).toBe(200);
        expect(response?.headers()['content-type']).toContain('image/png');
        await expect.poll(() => page.locator('img').evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);
      } else {
        expect(response?.status()).toBe(404);
        expect(response?.headers()['content-type'] ?? '').not.toContain('image/svg+xml');
        await expect(page.getByText('Synthetic raster fixture marker')).toHaveCount(0);
      }
    }
  }
}

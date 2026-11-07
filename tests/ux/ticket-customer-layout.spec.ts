import { expect, test } from '@playwright/test';

const fixture = 'http://127.0.0.1:18330/fixture';
test.beforeEach(async ({ page, request }) => {
  await request.get(fixture + '/reset');
  await request.get(fixture + '/customer-layout');
  await page.goto('/auth/development', { waitUntil: 'domcontentloaded' });
});

for (const module of ['incidents', 'requests', 'changes']) {
  for (const width of [1440, 1366, 1280, 390]) {
    test(`${module} full customer values wrap within For at ${width}px`, async ({ page }, info) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`/${module}?sort=trackingId&descending=false`);
      await expect(page.getByText('1-25 of 60')).toBeVisible();
      await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true');
      await page.evaluate(() => document.fonts.ready);
      if (width >= 1280) {
        const drawer = await page.getByTestId('app-navigation-drawer').boundingBox();
        expect(drawer?.x).toBeGreaterThanOrEqual(0);
        expect(drawer?.width).toBeGreaterThan(200);
      }
      const cells = page.locator('td[data-label="For"]');
      await expect(cells).toHaveCount(25);
      const measures = await cells.evaluateAll(elements => elements.slice(0, 6).map(cell => {
        const content = cell.querySelector('.ticket-for-content')!;
        const chip = content.querySelector('.mud-chip');
        const subject = cell.parentElement!.querySelector('[data-label="Subject"]')!;
        const box = content.getBoundingClientRect();
        return { content: box.width, cell: cell.getBoundingClientRect().width,
          overflow: content.scrollWidth - content.clientWidth,
          chip: chip?.getBoundingClientRect().width ?? 0,
          chipHeight: chip?.getBoundingClientRect().height ?? 0,
          subject: subject.getBoundingClientRect().width,
          wrap: getComputedStyle(chip?.querySelector('.mud-chip-content') ?? content).overflowWrap,
          label: cell.getAttribute('data-label') };
      }));
      await info.attach('computed customer geometry', { body: JSON.stringify(measures, null, 2), contentType: 'application/json' });
      for (const measure of measures) {
        expect(measure.content).toBeGreaterThanOrEqual(150);
        expect(measure.content).toBeLessThanOrEqual(273);
        expect(measure.content).toBeLessThanOrEqual(measure.cell);
        expect(measure.chip).toBeLessThanOrEqual(measure.content + 1);
        expect(measure.overflow).toBeLessThanOrEqual(1);
        expect(measure.subject).toBeGreaterThan(width < 960 ? 200 : 140);
        expect(measure.wrap).toBe('anywhere');
        expect(measure.label).toBe('For');
      }
      expect(measures[1].chipHeight).toBeGreaterThan(24);
      expect(await cells.nth(2).innerText()).toContain('SyntheticUnbrokenCustomerName'.repeat(8));
      expect(await cells.nth(3).innerText()).toContain('person@example.test');
      expect(await cells.nth(4).innerText()).toContain('Žluťoučký');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
      await page.screenshot({ path: info.outputPath(`customer-${module}-${width}.png`) });

      // Historic lists share the same custom/default containment and stacked labels.
      await page.goto(`/${module}?view=historic&sort=trackingId&descending=false`);
      await expect(page.getByText('1-5 of 5')).toBeVisible();
      expect(await page.locator('.ticket-for-content').evaluateAll(elements =>
        elements.every(element => element.getBoundingClientRect().width <= 273 && element.scrollWidth <= element.clientWidth + 1))).toBeTruthy();
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    });
  }
}

for (const module of ['incidents', 'requests']) {
  for (const width of [1440, 1366, 1280, 390]) {
  test(`${module} constrained picker assigns once and row navigation still works at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`/${module}?sort=trackingId&descending=false`);
    await expect(page.getByText('1-25 of 60')).toBeVisible();
    const row = page.locator('tbody tr').first();
    const originalUrl = page.url();
    const picker = row.getByPlaceholder('Select user');
    await picker.fill('Regional');
    await page.getByRole('option').filter({ hasText: 'Example Company Regional Recruitment' }).click();
    await expect(row.locator('[data-label="For"]')).toContainText('Regional Recruitment');
    expect(page.url()).toBe(originalUrl);
    const state = await (await request.get(fixture + '/state')).json();
    expect(state.mutations).toHaveLength(1);
    expect(state.mutations[0].path).toBe(`/api/v1/${module}/row-0/customer`);
    await row.locator('[data-label="Tracking ID"]').click();
    await expect(page).toHaveURL(new RegExp(`/${module}/row-0\\?`));
    await page.goBack();
    await expect(page.getByText('1-25 of 60')).toBeVisible();
    await expect(page.locator('tbody tr').first().locator('[data-label="For"]')).toContainText('Regional Recruitment');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
  }
}

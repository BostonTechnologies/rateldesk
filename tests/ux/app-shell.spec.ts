import { expect, Page, test } from '@playwright/test';
import { assertNoHorizontalOverflow, authenticate, clearThemeOverride, selectTheme } from './auth';

type DrawerGeometry = {
  drawerIntersectsViewport: boolean;
  isClosed: boolean;
  mainStartsAtViewportEdge: boolean;
  mainUsesViewportWidth: boolean;
  overlayCapturesViewport: boolean;
  drawerCapturesLeftPage: boolean;
};

type CatalogCardGeometry = {
  arrowRight: number | null;
  bodyLeft: number | null;
  cardLeft: number;
  cardRight: number;
  cardWidth: number;
  footerLeft: number | null;
  iconLeft: number | null;
  topLeft: number | null;
};

async function getDrawerGeometry(page: Page): Promise<DrawerGeometry> {
  return page.getByTestId('app-navigation-drawer').evaluate((drawer) => {
    const drawerRect = drawer.getBoundingClientRect();
    const mainRect = document.querySelector<HTMLElement>('[data-testid="app-main-content"]')?.getBoundingClientRect();
    const overlay = document.querySelector<HTMLElement>('.mud-overlay-drawer');
    const overlayRect = overlay?.getBoundingClientRect();
    const overlayStyle = overlay ? window.getComputedStyle(overlay) : undefined;
    const leftPageTarget = document.elementFromPoint(8, Math.min(160, window.innerHeight - 16));

    return {
      drawerIntersectsViewport:
        drawerRect.width > 0 &&
        drawerRect.height > 0 &&
        drawerRect.left < window.innerWidth &&
        drawerRect.right > 0,
      isClosed: drawer.classList.contains('mud-drawer--closed'),
      mainStartsAtViewportEdge: (mainRect?.left ?? Number.POSITIVE_INFINITY) <= 1,
      mainUsesViewportWidth: (mainRect?.width ?? 0) >= window.innerWidth - 2,
      overlayCapturesViewport:
        Boolean(overlayRect) &&
        overlayRect!.width > 0 &&
        overlayRect!.height > 0 &&
        overlayStyle?.visibility !== 'hidden' &&
        overlayStyle?.pointerEvents !== 'none',
      drawerCapturesLeftPage: Boolean(leftPageTarget && drawer.contains(leftPageTarget))
    };
  });
}

async function expectDrawerFullyClosed(page: Page): Promise<void> {
  await expect.poll(async () => {
    const geometry = await getDrawerGeometry(page);
    return geometry.isClosed &&
      !geometry.drawerIntersectsViewport &&
      !geometry.overlayCapturesViewport &&
      !geometry.drawerCapturesLeftPage &&
      geometry.mainStartsAtViewportEdge &&
      geometry.mainUsesViewportWidth;
  }).toBe(true);

  const residualNavigationPixels = await page.getByTestId('app-navigation-drawer').locator('.mud-icon-root, .mud-nav-link').evaluateAll((elements) =>
    elements.some((element) => {
      const rect = element.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0 && rect.left < window.innerWidth && rect.right > 0;
    }));

  expect(residualNavigationPixels, 'closed drawer should leave no navigation pixels in the viewport').toBe(false);
  await assertNoHorizontalOverflow(page);
}

async function expectDrawerOpen(page: Page): Promise<void> {
  await expect.poll(async () => {
    const geometry = await getDrawerGeometry(page);
    return !geometry.isClosed && geometry.drawerIntersectsViewport;
  }).toBe(true);

  await expect(page.getByTestId('app-navigation-drawer').getByRole('link', { name: 'Home' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
}

async function closeDrawer(page: Page): Promise<void> {
  const scrim = page.locator('.mud-overlay-scrim');
  const box = await scrim.boundingBox();
  expect(box, 'open drawer scrim should have a viewport box').not.toBeNull();
  await scrim.click({ position: { x: box!.width - 16, y: 16 } });
  await expectDrawerFullyClosed(page);
}

async function getCatalogCardGeometry(page: Page): Promise<CatalogCardGeometry[]> {
  return page.locator('.self-service-catalog-card').evaluateAll((cards) => {
    const getRect = (card: Element, selector: string) => {
      const element = card.querySelector(selector);
      if (!element) {
        return null;
      }

      const rect = element.getBoundingClientRect();
      return { left: rect.left, right: rect.right };
    };

    return cards
      .map((card) => {
        const cardRect = card.getBoundingClientRect();
        const top = getRect(card, '.self-service-card-top');
        const body = getRect(card, '.self-service-card-body');
        const footer = getRect(card, '.self-service-card-footer');
        const icon = getRect(card, '.self-service-icon-shell');
        const arrow = getRect(card, '.self-service-card-arrow');

        return {
          arrowRight: arrow?.right ?? null,
          bodyLeft: body?.left ?? null,
          cardLeft: cardRect.left,
          cardRight: cardRect.right,
          cardWidth: cardRect.width,
          footerLeft: footer?.left ?? null,
          iconLeft: icon?.left ?? null,
          topLeft: top?.left ?? null
        };
      })
      .filter((card) => card.cardWidth > 0);
  });
}

function expectWithinTolerance(actual: number | null, expected: number | null, description: string): void {
  expect(actual, `${description} should be present`).not.toBeNull();
  expect(expected, `${description} baseline should be present`).not.toBeNull();
  expect(Math.abs(actual! - expected!), `${description}: ${actual} versus ${expected}`).toBeLessThanOrEqual(2);
}

async function expectPhoneCatalogGeometry(page: Page): Promise<void> {
  const cards = page.locator('.self-service-catalog-card');
  await expect(cards.first()).toBeVisible();
  await expect.poll(async () => (await getCatalogCardGeometry(page)).length).toBeGreaterThan(1);

  const geometry = await getCatalogCardGeometry(page);
  const first = geometry[0];

  for (const card of geometry.slice(1)) {
    expectWithinTolerance(card.cardLeft, first.cardLeft, 'catalog card left edge');
    expectWithinTolerance(card.cardRight, first.cardRight, 'catalog card right edge');
    expectWithinTolerance(card.cardWidth, first.cardWidth, 'catalog card width');
    expectWithinTolerance(card.topLeft, first.topLeft, 'catalog card top content edge');
    expectWithinTolerance(card.bodyLeft, first.bodyLeft, 'catalog card body content edge');
    expectWithinTolerance(card.footerLeft, first.footerLeft, 'catalog card footer content edge');
    expectWithinTolerance(card.iconLeft, first.iconLeft, 'catalog card icon edge');
    expectWithinTolerance(card.arrowRight, first.arrowRight, 'catalog card arrow edge');
  }

  await assertNoHorizontalOverflow(page);
}

test.beforeEach(async ({ page }) => {
  await authenticate(page);
});

test('renders the application shell and opens global search', async ({ page }) => {
  await expect(page.getByText('RatelDesk')).toBeVisible();
  await page.getByRole('button', { name: 'Search' }).click();
  await expect(page.getByPlaceholder('Search...')).toBeVisible();
});

test('uses system light and dark preferences when no override is stored', async ({ browser }) => {
  for (const [colorScheme, expectedTheme] of [['light', 'light'], ['dark', 'dark']] as const) {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      colorScheme,
      ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
    });
    const page = await context.newPage();
    await authenticate(page);
    await clearThemeOverride(page);
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', expectedTheme);
    await context.close();
  }
});

test('persists manual light and dark choices and can return to system mode', async ({ page }) => {
  await selectTheme(page, 'Light');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'light');
  await page.reload();
  await page.waitForTimeout(1_000);
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'light');

  await selectTheme(page, 'Dark');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'dark');
  await page.reload();
  await page.waitForTimeout(1_000);
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', 'dark');

  await selectTheme(page, 'System');
  const systemTheme = await page.evaluate(() =>
    window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', systemTheme);
});

test('desktop navigation reaches the incidents workspace without overflow', async ({ page }) => {
  const workspace = page.getByRole('navigation', { name: 'Workspace' });
  const toggle = workspace.getByRole('button', { name: 'Toggle Workspace' });
  if (await toggle.getAttribute('aria-expanded') !== 'true') {
    await toggle.click();
  }
  await workspace.getByRole('link', { name: 'Incidents' }).click();
  await expect(page.getByRole('heading', { name: 'Incidents' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
});

test('representative incident detail and admin pages do not overflow', async ({ page }) => {
  await page.goto('/incidents');
  await expect(page.getByRole('heading', { name: 'Incidents' })).toBeVisible();
  // The subject opens the email preview; the tracking cell opens the ticket.
  await page.locator('tbody tr').first().locator('td[data-label="Tracking ID"]').click();
  await expect(page).toHaveURL(/\/incidents\//);
  await assertNoHorizontalOverflow(page);

  await page.goto('/admin/users');
  await expect(page.getByRole('heading', { name: 'Team' })).toBeVisible();
  await assertNoHorizontalOverflow(page);
});

test('primary create dialog fits the phone viewport', async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'dark',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);
  await page.goto('/incidents');
  const dialog = page.locator('.mud-dialog');
  for (let attempt = 0; attempt < 3 && !await dialog.isVisible(); attempt++) {
    await page.getByRole('button', { name: 'Create' }).click();
    await dialog.waitFor({ state: 'visible', timeout: 5_000 }).catch(() => undefined);
  }
  await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.width).toBeLessThanOrEqual(380);
  expect(box!.height).toBeLessThanOrEqual(834);
  await context.close();
});

test('mobile drawer starts closed, opens, fully closes, and leaves the page interactive', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'light',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);
  await expectDrawerFullyClosed(page);
  await page.screenshot({ path: testInfo.outputPath('mobile-home-initially-closed.png'), fullPage: true });

  await page.getByTestId('navigation-toggle').click();
  await expectDrawerOpen(page);
  await page.screenshot({ path: testInfo.outputPath('mobile-home-drawer-open.png'), fullPage: true });

  await closeDrawer(page);
  await page.screenshot({ path: testInfo.outputPath('mobile-home-drawer-closed.png'), fullPage: true });
  await page.getByRole('button', { name: 'Search' }).click();
  await expect(page.getByPlaceholder('Search...')).toBeVisible();
  await context.close();
});

test('mobile navigation auto-closes the drawer and can be reopened', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    colorScheme: 'light',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);

  await page.getByTestId('navigation-toggle').click();
  await expectDrawerOpen(page);
  await page.getByTestId('app-navigation-drawer').getByText('Portal', { exact: true }).click();
  const selfServiceLink = page.getByTestId('app-navigation-drawer').getByRole('link', { name: 'Self Service' });
  await expect(selfServiceLink).toBeVisible();
  await selfServiceLink.click();
  await expect(page).toHaveURL(/\/self-service$/);
  await expect(page.locator('.self-service-page')).toBeVisible();
  await expectDrawerFullyClosed(page);
  await page.screenshot({ path: testInfo.outputPath('mobile-self-service-after-navigation.png'), fullPage: true });

  await page.getByTestId('navigation-toggle').click();
  await expectDrawerOpen(page);
  await closeDrawer(page);

  await page.getByTestId('navigation-toggle').click();
  await page.getByTestId('app-navigation-drawer').getByText('Workspace', { exact: true }).click();
  const incidentsLink = page.getByTestId('app-navigation-drawer').getByText('Incidents', { exact: true });
  await expect(incidentsLink).toBeVisible();
  await incidentsLink.click();
  await expect(page.getByRole('heading', { name: 'Incidents' })).toBeVisible();
  await expectDrawerFullyClosed(page);
  await context.close();
});

for (const [width, height, colorScheme] of [
  [390, 844, 'light'],
  [430, 932, 'dark']
] as const) {
  test(`Self Service cards share phone geometry at ${width}x${height} in ${colorScheme} mode`, async ({ browser }, testInfo) => {
    const context = await browser.newContext({
      viewport: { width, height },
      colorScheme,
      ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
    });
    const page = await context.newPage();
    await authenticate(page);
    await expectDrawerFullyClosed(page);
    await page.getByTestId('navigation-toggle').click();
    await expectDrawerOpen(page);
    await closeDrawer(page);
    await page.goto('/self-service');
    await expectPhoneCatalogGeometry(page);
    await page.screenshot({
      path: testInfo.outputPath(`mobile-self-service-${width}x${height}-${colorScheme}.png`),
      fullPage: true
    });
    await context.close();
  });
}

test('portrait tablet uses an aligned two-column catalogue and mobile drawer lifecycle', async ({ browser }, testInfo) => {
  const context = await browser.newContext({
    viewport: { width: 768, height: 1024 },
    colorScheme: 'light',
    ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
  });
  const page = await context.newPage();
  await authenticate(page);
  await expectDrawerFullyClosed(page);
  await page.getByTestId('navigation-toggle').click();
  await expectDrawerOpen(page);
  await closeDrawer(page);

  await page.goto('/self-service');
  const cards = page.locator('.self-service-catalog-card');
  await expect(cards.first()).toBeVisible();
  await expect.poll(async () => (await getCatalogCardGeometry(page)).length).toBeGreaterThan(1);
  let geometry: CatalogCardGeometry[] = [];
  await expect.poll(async () => {
    geometry = await getCatalogCardGeometry(page);
    return geometry.reduce<number[]>((leftEdges, card) =>
      leftEdges.some((left) => Math.abs(left - card.cardLeft) <= 2)
        ? leftEdges
        : [...leftEdges, card.cardLeft], []).length;
  }).toBe(2);

  const columns = geometry.reduce<number[]>((leftEdges, card) =>
    leftEdges.some((left) => Math.abs(left - card.cardLeft) <= 2)
      ? leftEdges
      : [...leftEdges, card.cardLeft], []);

  expect(columns, 'tablet catalogue should use two intentional columns').toHaveLength(2);
  const baselineWidth = geometry[0].cardWidth;
  for (const card of geometry.slice(1)) {
    expectWithinTolerance(card.cardWidth, baselineWidth, 'tablet catalog card width');
  }
  await assertNoHorizontalOverflow(page);
  await page.screenshot({
    path: testInfo.outputPath('tablet-self-service-768x1024-light.png'),
    fullPage: true
  });
  await context.close();
});

test('mobile drawer remains fully closed around the responsive layout edges', async ({ browser }) => {
  for (const width of [599, 600, 601, 799, 800, 801, 1023, 1024]) {
    const context = await browser.newContext({
      viewport: { width, height: 900 },
      colorScheme: 'light',
      ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
    });
    const page = await context.newPage();
    await authenticate(page);
    await expectDrawerFullyClosed(page);
    await page.getByTestId('navigation-toggle').click();
    await expectDrawerOpen(page);
    await closeDrawer(page);
    await context.close();
  }
});

test('desktop drawer remains persistent and the Self Service page stays aligned at wide viewports', async ({ browser }, testInfo) => {
  for (const viewport of [{ width: 1440, height: 900 }, { width: 1920, height: 1080 }]) {
    const context = await browser.newContext({
      viewport,
      colorScheme: 'light',
      ignoreHTTPSErrors: process.env.HELPDESK_E2E_IGNORE_HTTPS_ERRORS === 'true'
    });
    const page = await context.newPage();
    await authenticate(page);
    await expectDrawerOpen(page);
    await page.goto('/self-service');
    await expect(page.locator('.self-service-page')).toBeVisible();
    await assertNoHorizontalOverflow(page);
    await page.screenshot({
      path: testInfo.outputPath(`desktop-self-service-${viewport.width}x${viewport.height}.png`),
      fullPage: true
    });
    await context.close();
  }
});

test('smoke routes stay free of uncaught browser exceptions', async ({ page }) => {
  const exceptions: string[] = [];
  page.on('pageerror', (error) => exceptions.push(error.message));

  for (const route of ['/home', '/self-service', '/requests', '/changes', '/notifications', '/admin/support-routing']) {
    await page.goto(route);
    await page.waitForLoadState('domcontentloaded');
  }

  expect(exceptions).toEqual([]);
});

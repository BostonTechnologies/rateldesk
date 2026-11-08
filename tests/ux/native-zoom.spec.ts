import { chromium, expect, test, type Page } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
const suite = process.env.HELPDESK_NATIVE_ZOOM_SUITE!;
const routes: Record<string, string[]> = {
  integrations: ['/account/integration-credentials?tab=credentials', '/account/integration-credentials?tab=connections&organizationId=org-b'],
  admin: ['/admin/roles', '/admin/resources/data-management', '/admin/support-routing', '/admin/tenant-sla-settings'],
  details: ['/incidents/incidents-1', '/requests/requests-1', '/changes/changes-1'],
  email: ['/incidents'],
  'ticket-filters': ['/incidents', '/requests', '/changes'],
  assignee: ['/incidents', '/requests', '/changes'],
  operations: ['/self-service', '/ops/slas', '/ops/workflows', '/ops/hangfire', '/tasks', '/notifications', '/admin/email-rules'],
  'pending-mail': ['/admin/pending-emails'],
  'request-editor': ['/admin/requests/services/service-1']
};
const ports: Record<string, number> = { admin: 18500, integrations: 18505, details: 18600, email: 18304, 'ticket-filters': 18330, operations: 18400, 'pending-mail': 18700, assignee: 18300, 'request-editor': 18530 };
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
    if (suite === 'operations') await request.get('http://127.0.0.1:18400/fixture/premium');
    const profile = await mkdtemp(path.join(tmpdir(), 'rateldesk-zoom-'));
    const extension = path.resolve('tests/ux/native-zoom-extension');
    const context = await chromium.launchPersistentContext(profile, {
      channel: process.env.HELPDESK_E2E_CHROMIUM_PATH ? undefined : 'chromium',
      executablePath: process.env.HELPDESK_E2E_CHROMIUM_PATH, headless: true, ignoreHTTPSErrors: true,
      viewport: { width, height: 1000 },
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`]
    });
    context.setDefaultTimeout(15_000);
    // Admin enters a transient Graph secret before capturing safe Test access evidence.
    const captureTrace = suite !== 'admin';
    if (captureTrace) await context.tracing.start({ screenshots: true, snapshots: true, sources: false });
    try {
      await expect.poll(() => context.serviceWorkers().length, {
        message: 'The isolated native zoom extension service worker must become available before opening the application.'
      }).toBeGreaterThan(0);
      const worker = context.serviceWorkers()[0];
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
        if (suite === 'operations' && ['/tasks', '/notifications', '/admin/email-rules'].includes(route)) {
          if (route === '/admin/email-rules') {
            for (const tab of ['Rules', 'Audit']) {
              await page.getByRole('tab', { name: tab, exact: true }).click();
              await expect(page.locator('.premium-workspace tbody tr').first()).toBeVisible();
              await expect(page.locator('.premium-workspace .mud-table-pagination').last()).toBeInViewport({ ratio: 1 });
            }
          } else {
            await expect(page.locator('.premium-workspace .mud-table-pagination')).toBeInViewport({ ratio: 1 });
          }
        }
        if (suite === 'integrations')
          await expect(page.getByText('Netclaw settings could not be loaded.', { exact: true })).not.toBeVisible();
        if (suite === 'request-editor') {
          await page.getByRole('button', { name: 'Edit Example access request', exact: true }).click();
          const editor = page.locator('.request-form-builder-dialog');
          await expect(editor).toBeVisible();
          await expect(editor.getByRole('tab', { name: 'Workflow (3)' })).toHaveAttribute('aria-selected', 'true');
          if (width / 2 <= 760) await editor.locator('[data-task-id]').first().click();
          await expect(editor.getByRole('textbox', { name: /^Task Name\*?$/ })).toHaveValue('Prepare access — synthetic automation');
          await expect(editor.getByTestId('task-automation')).toBeVisible();
          await expect(editor.getByRole('button', { name: 'Save', exact: true })).toBeInViewport();
          await expect(editor.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport();
          const bounds = await editor.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.y).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(501);
          expect(await editor.evaluate(el => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(2);
          for (const tab of ['Form fields (2)', 'Form setup', 'Workflow (3)']) {
            const section = editor.getByRole('tab', { name: tab, exact: true });
            await section.focus(); await page.keyboard.press('Enter');
            await expect(section).toHaveAttribute('aria-selected', 'true');
          }
          const state = await (await request.get('http://127.0.0.1:18530/fixture/state')).json();
          expect(state.saves).toEqual([]);
        }
        if (suite === 'integrations' && route.includes('tab=connections')) {
          await page.getByRole('tab', { name: 'System connections', exact: true }).click();
          const panel = page.getByTestId('system-connections-panel');
          await expect(panel.getByTestId('system-connection-row')).toHaveCount(7);
          await panel.locator('[data-connection-id="mapping-1"]').getByRole('button', { name: 'View', exact: true }).click();
          const review = page.getByRole('dialog', { name: 'Connection configuration', exact: true });
          await expect(review).toBeVisible();
          await expect(review.getByRole('combobox', { name: 'RatelDesk organization', exact: true })).toHaveText('Synthetic Alpha');
          const bounds = await review.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.y).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(501);
          const footer = await review.locator('footer').boundingBox();
          expect(footer!.y + footer!.height).toBeLessThanOrEqual(501);
          expect(await review.evaluate(el => el.scrollHeight - el.clientHeight)).toBeLessThanOrEqual(2);
          await expect(review.getByTestId('save-system-connection')).toBeVisible();
          await review.getByRole('button', { name: 'Back to connections', exact: true }).focus();
          await page.keyboard.press('Tab');
          expect(await review.evaluate(el => el.contains(document.activeElement))).toBe(true);
        }
        if (suite === 'integrations' && !route.includes('tab=connections')) {
          // Keyboard traversal deliberately leaves the final Integration tab
          // active. Restore the owning panel before exercising its consent flow.
          await page.getByRole('tab', { name: 'API & MCP credentials', exact: true }).click();
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
        if (suite === 'pending-mail') {
          await expect(page.locator('.pending-mail .mud-table-pagination')).toBeInViewport({ ratio: 1 });
          await page.locator('.pending-mail tbody input[type="checkbox"]').first().check();
          await page.getByRole('button', { name: 'Cancel selected', exact: true }).click();
          const review = page.getByRole('dialog');
          await expect(review).toContainText('1 explicitly selected record(s)');
          await review.getByRole('textbox', { name: 'Reason' }).fill('Synthetic native zoom review');
          const confirm = review.getByRole('button', { name: 'Cancel deliveries', exact: true });
          await expect(confirm).toBeEnabled();
          await confirm.scrollIntoViewIfNeeded();
          const bounds = await review.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.y).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(501);
          await confirm.focus(); await page.keyboard.press('Tab');
          expect(await review.evaluate(el => el.contains(document.activeElement))).toBe(true);
          const state = await (await request.get('http://127.0.0.1:18700/fixture/state')).json();
          expect(state.actions).toEqual([]); expect(state.retries).toEqual([]); expect(state.submissions).toEqual([]);
        }
        if (suite === 'assignee') {
          await page.getByRole('button', { name: 'Create', exact: true }).click();
          const create = page.getByRole('dialog').first();
          const organization = create.getByRole('combobox', { name: /^Organization\*?$/ });
          await organization.fill('Partner');
          const option = page.getByRole('option', { name: /Zulu Partner/ });
          await expect(option).toBeVisible();
          const popupBounds = await option.boundingBox();
          expect(popupBounds!.x).toBeGreaterThanOrEqual(0);
          expect(popupBounds!.x + popupBounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(popupBounds!.y).toBeGreaterThanOrEqual(0);
          expect(popupBounds!.y + popupBounds!.height).toBeLessThanOrEqual(501);
          await option.click();
          const participant = create.getByRole('combobox', { name: route === '/changes' ? /^Requested for\*?$/ : /^User the ticket is for\*?$/ });
          await participant.fill('remote-');
          await expect(page.getByRole('option', { name: /Zulu (Participant|Customer)/ })).toBeVisible();
          await participant.press('Escape');
          if (route === '/changes') {
            await create.getByRole('button', { name: /^3\. Schedule/ }).click();
            const approvers = create.getByRole('combobox', { name: 'Approvers', exact: true });
            await approvers.fill('Duplicate');
            await page.getByRole('option', { name: /one@example.test/ }).click();
            await expect(create.getByLabel('Selected approvers', { exact: true })).toContainText('one@example.test');
            for (const label of [/^1\. Overview/, /^2\. Plan/, /^3\. Schedule/, /^4\. Review/]) {
              await create.getByRole('button', { name: label }).click();
              await expect(create.locator('.mud-dialog-actions')).toBeInViewport({ ratio: 1 });
              await expect(create.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport({ ratio: 1 });
            }
            await expect(create.getByRole('button', { name: 'Create draft', exact: true })).toBeInViewport({ ratio: 1 });
          }
          const cancel = create.getByRole('button', { name: 'Cancel', exact: true });
          await cancel.scrollIntoViewIfNeeded();
          await expect(cancel).toBeVisible();
          const state = await (await request.get('http://127.0.0.1:18300/fixture/state')).json();
          expect(state.saves).toEqual([]);
        }
        if (suite === 'admin' && route === '/admin/resources/data-management') {
          await page.getByRole('button', { name: 'New dataset', exact: true }).click();
          const dataset = page.getByRole('dialog', { name: 'New custom dataset', exact: true });
          await dataset.getByRole('textbox', { name: /^Dataset name\*?$/ }).fill('Synthetic native zoom draft');
          await dataset.getByRole('button', { name: 'Continue', exact: true }).click();
          await expect(dataset.getByRole('heading', { name: 'Design schema', exact: true })).toBeVisible();
          await expect(dataset.getByRole('button', { name: 'Continue', exact: true })).toBeInViewport();
          const datasetBounds = await dataset.boundingBox();
          expect(datasetBounds!.x).toBeGreaterThanOrEqual(0); expect(datasetBounds!.y).toBeGreaterThanOrEqual(0);
          expect(datasetBounds!.x + datasetBounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(datasetBounds!.y + datasetBounds!.height).toBeLessThanOrEqual(501);
          await settleFiniteAnimations(page);
          const datasetCapture = await context.newCDPSession(page);
          try {
            const capture = await datasetCapture.send('Page.captureScreenshot', { format: 'png', fromSurface: true, captureBeyondViewport: false });
            await writeFile(info.outputPath(`dataset-wizard-${theme}-${width}-native-200.png`), Buffer.from(capture.data, 'base64'));
          } finally { await datasetCapture.detach(); }
          await dataset.getByRole('button', { name: 'Cancel', exact: true }).click();
          await page.getByRole('dialog').filter({ has: page.getByText('Discard dataset draft?', { exact: true }) }).getByRole('button', { name: 'Discard', exact: true }).click();
          await page.getByRole('button', { name: 'Set up Microsoft Graph', exact: true }).click();
          const graph = page.getByRole('dialog', { name: 'Microsoft Graph setup', exact: true });
          await graph.getByRole('textbox', { name: /^Tenant ID\*?$/ }).fill('synthetic-tenant');
          await graph.getByRole('textbox', { name: /^Application \/ client ID\*?$/ }).fill('synthetic-client');
          await graph.getByLabel('Client secret', { exact: true }).fill('synthetic-draft-only');
          await graph.getByRole('button', { name: 'Continue', exact: true }).click();
          await graph.getByRole('checkbox', { name: 'Users', exact: true }).check();
          await graph.getByRole('button', { name: 'Continue', exact: true }).click();
          await graph.getByRole('button', { name: 'Test connection', exact: true }).click();
          await expect(graph.getByText(/Authentication: Passed/)).toBeVisible();
          await expect(graph.getByRole('button', { name: 'Continue', exact: true })).toBeInViewport();
          const graphBounds = await graph.boundingBox();
          expect(graphBounds!.x).toBeGreaterThanOrEqual(0); expect(graphBounds!.y).toBeGreaterThanOrEqual(0);
          expect(graphBounds!.x + graphBounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(graphBounds!.y + graphBounds!.height).toBeLessThanOrEqual(501);
          const state = await (await request.get('http://127.0.0.1:18500/fixture/state')).json();
          expect(state.saves).toEqual([]); expect(state.graphTests).toHaveLength(1);
        }
        if (suite === 'ticket-filters') {
          const trigger = page.locator('tbody tr').first().locator('button.state-trigger');
          await expect(trigger).toBeEnabled();
          await trigger.scrollIntoViewIfNeeded();
          await trigger.focus();
          await page.keyboard.press('Enter');
          const popup = page.locator('.ticket-state-popover.mud-popover-open');
          await expect(popup).toBeVisible();
          await expect(popup.getByText(route === '/changes' ? 'Current: Draft' : 'Current: New', { exact: true })).toBeVisible();
          const bounds = await popup.boundingBox();
          expect(bounds!.x).toBeGreaterThanOrEqual(0);
          expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width / 2 + 1);
          expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(501);
          const state = await (await request.get('http://127.0.0.1:18330/fixture/state')).json();
          expect(state.mutations).toEqual([]);
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
        if (suite === 'admin' && route === '/admin/resources/data-management') {
          const graph = page.getByRole('dialog', { name: 'Microsoft Graph setup', exact: true });
          await graph.getByRole('button', { name: 'Cancel', exact: true }).click();
          await page.getByRole('dialog').filter({ has: page.getByText('Discard Graph draft?', { exact: true }) })
            .getByRole('button', { name: 'Discard', exact: true }).click();
          await expect(graph).toBeHidden();
        }
      }
    } finally {
      if (captureTrace) await context.tracing.stop({ path: info.outputPath('native-zoom-trace.zip') });
      await context.close();
      await rm(profile, { recursive: true, force: true });
    }
  });
}

import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const output = dirname(fileURLToPath(import.meta.url));
const candidateRoot = resolve(process.env.CANDIDATE_ROOT || resolve(output, '../../../../..'));
const baselineRoot = process.env.BASELINE_ROOT;
if (!baselineRoot) throw new Error('Set BASELINE_ROOT to the clean, already-built baseline checkout.');
const nodeModules = process.env.NODE_MODULES_ROOT || resolve(candidateRoot, 'node_modules');
const { chromium, expect } = await import(pathToFileURL(resolve(nodeModules, '@playwright/test/index.mjs')).href);
const executablePath = process.env.HELPDESK_E2E_CHROMIUM_PATH || '/usr/bin/chromium';
const fixture = process.env.DETAIL_FIXTURE_ORIGIN || 'http://127.0.0.1:18600/fixture';
const original = resolve(output, '../source-receipt.json');
const source = JSON.parse(await readFile(original, 'utf8'));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const originalReceiptSha = sha(await readFile(original));
for (const [root, expected] of [[baselineRoot, source.baseline.webAssembly.sha256], [candidateRoot, source.candidate.webAssembly.sha256]]) {
  if (sha(await readFile(resolve(root, 'src/HelpDesk.NewWeb/bin/Debug/net10.0/HelpDesk.NewWeb.dll'))) !== expected)
    throw new Error('Frozen Web assembly hash changed; no rebuild or replacement is permitted for this supplement.');
}
const captures = [];

async function settle(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  });
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation =>
    animation.playState !== 'finished' && animation.playState !== 'idle' && Number.isFinite(animation.effect?.getComputedTiming().endTime)
  ).length), { timeout: 15_000 }).toBe(0);
}

async function open(context, origin, theme) {
  await context.addInitScript(value => localStorage.setItem('helpdesk.theme.preference', value), theme);
  const page = await context.newPage();
  await page.request.get(`${fixture}/reset`);
  await page.request.post(`${fixture}/request-tasks`, { data: { count: 3, state: 'running' } });
  await page.goto(`${origin}/auth/development`);
  await page.goto(`${origin}/requests/requests-1`);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive', 'true', { timeout: 30_000 });
  return page;
}

async function selectTasks(page, phase) {
  const tab = page.getByRole('tab', { name: 'Tasks', exact: true });
  await tab.focus(); await page.keyboard.press('Enter');
  await expect(tab).toHaveAttribute('aria-selected', 'true');
  if (phase === 'before') await expect(page.getByRole('button', { name: 'Running', exact: true })).toHaveCount(3);
  else {
    await expect(page.locator('.request-task-state')).toHaveCount(3);
    await expect(page.getByRole('button', { name: 'Running', exact: true })).toHaveCount(0);
    await expect(page.getByText('synthetic-provider-run-0', { exact: true })).toHaveCount(0);
  }
  await page.evaluate(() => window.scrollTo(0, 0));
  await settle(page);
}

async function geometry(page) {
  return page.evaluate(() => {
    const table = document.querySelector('[data-testid=request-tasks-table]');
    const bounds = table?.getBoundingClientRect();
    const clipped = table ? [...table.querySelectorAll('.request-task-name,.request-task-description,.request-task-state,.request-task-details,.request-task-details dd')]
      .filter(element => { const rect = element.getBoundingClientRect(); return rect.width <= 0 || rect.left < bounds.left - 1 || rect.right > bounds.right + 1; }).length : null;
    return { layoutWidth: innerWidth, layoutHeight: innerHeight, scrollWidth: document.documentElement.scrollWidth,
      devicePixelRatio, htmlCssZoom: getComputedStyle(document.documentElement).zoom,
      bodyCssZoom: getComputedStyle(document.body).zoom, taskStates: document.querySelectorAll('.request-task-state').length,
      clippedTaskElements: clipped, errorUiHeight: document.getElementById('blazor-error-ui')?.getBoundingClientRect().height ?? 0 };
  });
}

async function retain(page, context, phase, theme, width, height, native, zoom = 1) {
  const metrics = await geometry(page);
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.layoutWidth + 1);
  expect(metrics.errorUiHeight).toBe(0);
  expect(metrics.htmlCssZoom).toBe('1'); expect(metrics.bodyCssZoom).toBe('1');
  if (phase === 'after') expect(metrics.clippedTaskElements).toBe(0);
  let png;
  if (native) {
    const cdp = await context.newCDPSession(page);
    try { const result = await cdp.send('Page.captureScreenshot', { format: 'png', fromSurface: true, captureBeyondViewport: false }); png = Buffer.from(result.data, 'base64'); }
    finally { await cdp.detach(); }
  } else png = await page.screenshot({ fullPage: false });
  const imageWidth = png.readUInt32BE(16), imageHeight = png.readUInt32BE(20);
  expect(imageWidth).toBe(width); expect(imageHeight).toBe(height);
  const file = `${phase}-${theme}-${width}x${height}${native ? '-native-200' : ''}.png`;
  await writeFile(resolve(output, file), png);
  captures.push({ file, phase, theme, physicalWidth: width, physicalHeight: height, zoom, nativeZoom: native,
    sourceRevision: phase === 'before' ? source.baseline.sourceRevision : source.candidate.sourceRevision,
    imageWidth, imageHeight, sha256: sha(png), browserVersion: context.browser()?.version(), ...metrics });
}

const origins = [['before', process.env.BASELINE_ORIGIN || 'https://127.0.0.1:5193'], ['after', process.env.CANDIDATE_ORIGIN || 'https://127.0.0.1:5190']];
const browser = await chromium.launch({ executablePath, headless: true });
try {
  for (const theme of ['light', 'dark']) for (const [width, height] of [[1920, 1080], [1366, 768], [768, 1024]]) {
    for (const [phase, origin] of origins) {
      const context = await browser.newContext({ viewport: { width, height }, ignoreHTTPSErrors: true });
      try { const page = await open(context, origin, theme); await selectTasks(page, phase); await retain(page, context, phase, theme, width, height, false); }
      finally { await context.close(); }
    }
  }
} finally { await browser.close(); }

const extension = resolve(candidateRoot, 'tests/ux/native-zoom-extension');
for (const theme of ['light', 'dark']) for (const width of [1280, 1920]) for (const [phase, origin] of origins) {
  const profile = await mkdtemp(resolve(process.env.TEMP_ROOT || '/workspace/work', 'request-native-profile-'));
  const context = await chromium.launchPersistentContext(profile, { executablePath, headless: true,
    ignoreDefaultArgs: ['--disable-extensions'], ignoreHTTPSErrors: true, viewport: { width, height: 1000 },
    args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`] });
  context.setDefaultTimeout(30_000);
  try {
    await expect.poll(() => context.serviceWorkers().length, { timeout: 30_000 }).toBeGreaterThan(0);
    const worker = context.serviceWorkers()[0];
    const page = await open(context, origin, theme);
    const zoom = await worker.evaluate(async origin => {
      const chrome = globalThis.chrome;
      const tab = (await chrome.tabs.query({})).find(item => item.url.startsWith(origin));
      await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
    }, origin);
    expect(zoom).toBe(2);
    await expect.poll(() => page.evaluate(() => innerWidth)).toBe(width / 2);
    await expect.poll(() => page.getByTestId('app-navigation-drawer').evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(1);
    await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme', theme);
    await selectTasks(page, phase);
    await retain(page, context, phase, theme, width, 1000, true, zoom);
    if (phase === 'after') {
      const details = page.getByRole('button', { name: 'Task details: Long harmless automation task with a readable complete disclosure name', exact: true });
      await details.focus(); await page.keyboard.press('Enter');
      await expect(details).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByRole('region', { name: 'Details for Long harmless automation task with a readable complete disclosure name', exact: true })).toContainText('synthetic-provider-run-0');
      expect((await geometry(page)).clippedTaskElements).toBe(0);
      for (const name of ['Workflow', 'Tasks']) { const tab = page.getByRole('tab', { name, exact: true }); await tab.focus(); await page.keyboard.press('Enter'); await expect(tab).toHaveAttribute('aria-selected', 'true'); }
      await expect(details).toHaveAttribute('aria-expanded', 'true');
      captures[captures.length - 1].keyboardDisclosureAndTabReturnPassed = true;
    }
  } finally { await context.close(); await rm(profile, { recursive: true, force: true }); }
}
if (sha(await readFile(original)) !== originalReceiptSha) throw new Error('Original source receipt changed.');
await writeFile(resolve(output, 'matrix-receipt.json'), JSON.stringify({ scope: 'Supplemental #383 synthetic presentation matrix; existing evidence remains unchanged.',
  capturedAtUtc: new Date().toISOString(), baselineSource: source.baseline.sourceRevision, candidateSource: source.candidate.sourceRevision,
  originalSourceReceiptSha256: originalReceiptSha, baselineAssemblySha256: source.baseline.webAssembly.sha256,
  candidateAssemblySha256: source.candidate.webAssembly.sha256, fixtureSourceSha256: sha(await readFile(resolve(candidateRoot, 'tests/ux/ticket-details-fixture-api.mjs'))),
  ordinaryAdded: 12, nativeAdded: 8, fixtureTaskCount: 3, actualNativeZoomMechanism: 'Existing isolated MV3 extension chrome.tabs.setZoom(2), getZoom verification, half-width layout assertion and physical CDP screenshot capture; no CSS zoom or deviceScaleFactor override.',
  providerOrGatewayExecutionClaimed: false, captures }, null, 2) + '\n');

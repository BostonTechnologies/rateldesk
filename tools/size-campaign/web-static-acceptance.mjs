import { chromium, expect } from '@playwright/test';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const [origin, destination] = process.argv.slice(2);
if (!origin || !destination || new URL(origin).hostname !== '127.0.0.1') throw new Error('Expected disposable loopback Web origin and output directory');
await mkdir(destination, { recursive: true });
const report = { status: 'FAIL', scope: 'Real browser against published static assets; component journeys are separate owning suites', origin, checks: [], failedRequests: [], workers: [] };
let browser;
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  page.on('requestfailed', request => report.failedRequests.push({ url: request.url(), failure: request.failure()?.errorText }));
  page.on('response', response => {
    if (/worker.*\.(?:js|mjs)/.test(response.url())) report.workers.push({ url: response.url(), status: response.status() });
  });
  // Only this fixture document is synthesized. Scripts, styles, fonts, worker
  // modules and images all come from the actual published Web container.
  await page.route('**/__size_fixture', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><base href="/"><div id="editor" style="height:500px;width:900px"></div>' }));
  await page.goto(origin + '/__size_fixture');
  await page.evaluate(async () => {
    const loader = await import('/js/helpdesk-monaco-loader.js');
    await loader.ensureBlazorMonaco();
    window.fixtureEditor = monaco.editor.create(document.querySelector('#editor'), { value: '', language: 'plaintext', automaticLayout: true });
  });
  report.registeredLanguages = await page.evaluate(() => monaco.languages.getLanguages().map(item => item.id).sort());
  for (const language of ['markdown', 'plaintext', 'html', 'shell', 'powershell', 'javascript', 'typescript', 'css', 'json']) {
    expect(report.registeredLanguages).toContain(language);
    const result = await page.evaluate(language => {
      const editor = window.fixtureEditor;
      monaco.editor.setModelLanguage(editor.getModel(), language);
      editor.setValue('Campaign Unicode: café 日本語 🦡');
      editor.executeEdits('fixture', [{ range: editor.getModel().getFullModelRange(), text: 'Edited café 日本語 🦡' }]);
      editor.pushUndoStop();
      const edited = editor.getValue();
      editor.trigger('fixture', 'undo');
      editor.layout({ width: 600, height: 300 });
      return { edited, restored: editor.getValue(), language: editor.getModel().getLanguageId() };
    }, language);
    expect(result.edited).toBe('Edited café 日本語 🦡');
    expect(result.restored).toBe('Campaign Unicode: café 日本語 🦡');
    expect(result.language).toBe(language);
    report.checks.push({ kind: 'language edit/undo/resize', language, status: 'PASS' });
  }
  for (const theme of ['vs', 'vs-dark', 'hc-black']) {
    await page.evaluate(theme => monaco.editor.setTheme(theme), theme);
    await expect(page.locator('.monaco-editor').first()).toBeVisible();
    report.checks.push({ kind: 'editor theme', theme, status: 'PASS' });
  }
  for (const [language, input] of [['html', '<html><body><div>Campaign</div></body></html>'], ['javascript', 'const example={value:42};example.value;'], ['typescript', 'const example:{value:number}={value:42};example.value;'], ['css', 'body{color:red;background:white}'], ['json', '{"campaign":{"accepted":true}}']]) {
    await page.evaluate(({ language, input }) => { monaco.editor.setModelLanguage(window.fixtureEditor.getModel(), language); window.fixtureEditor.setValue(input); }, { language, input });
    await page.waitForFunction(() => window.fixtureEditor.getAction('editor.action.formatDocument')?.isSupported());
    await page.evaluate(() => window.fixtureEditor.getAction('editor.action.formatDocument').run());
    const formatted = await page.evaluate(() => window.fixtureEditor.getValue());
    expect(formatted).not.toBe(input);
    report.checks.push({ kind: 'worker formatting', language, status: 'PASS' });
  }
  await page.evaluate(() => {
    const editor = window.fixtureEditor;
    monaco.editor.setModelLanguage(editor.getModel(), 'javascript');
    editor.setValue('const example={value:42};\nexample.');
    editor.setPosition({ lineNumber: 2, column: 9 });
    editor.focus();
    editor.trigger('fixture', 'editor.action.triggerSuggest');
  });
  await expect(page.locator('.suggest-widget.visible')).toBeVisible();
  await expect(page.locator('.suggest-widget.visible')).toContainText('value');
  report.checks.push({ kind: 'JavaScript worker completion', status: 'PASS' });
  const images = [];
  for (const name of ['/branding/rateldesk-mark.png', '/pwa-512x512.png']) {
    const response = await page.request.get(origin + name);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('image/png');
    const data = await response.body();
    images.push({ path: name, bytes: data.length, sha256: createHash('sha256').update(data).digest('hex') });
  }
  expect(images[0].sha256).toBe(images[1].sha256);
  report.images = images;
  report.checks.push({ kind: 'Both PNG URLs retain identical bytes and MIME', status: 'PASS' });
  await page.evaluate(() => import('/_content/Gotho.BlazorPdf/blazorpdf.min.js'));
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>'];
  const content = 'BT /F1 18 Tf 25 150 Td (Campaign acceptance) Tj ET';
  objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  let pdf = '%PDF-1.4\n'; const offsets = [0];
  for (const [index, object] of objects.entries()) { offsets.push(Buffer.byteLength(pdf)); pdf += `${index + 1} 0 obj\n${object}\nendobj\n`; }
  const start = Buffer.byteLength(pdf);
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(offset => String(offset).padStart(10, '0') + ' 00000 n \n').join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`;
  report.pdf = await page.evaluate(async ({ origin, encoded }) => {
    pdfjsLib.GlobalWorkerOptions.workerSrc = origin + '/_content/Gotho.BlazorPdf/pdf.worker-5.1.91.min.mjs';
    const document = await pdfjsLib.getDocument({ data: Uint8Array.from(atob(encoded), c => c.charCodeAt(0)), useSystemFonts: true }).promise;
    const first = await document.getPage(1);
    const text = (await first.getTextContent()).items.map(item => item.str).join(' ');
    const viewport = first.getViewport({ scale: 1 });
    const canvas = window.document.createElement('canvas'); canvas.width = viewport.width; canvas.height = viewport.height;
    await first.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
    const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    const darkPixels = Array.from({ length: pixels.length / 4 }, (_, i) => pixels[i * 4] < 100 && pixels[i * 4 + 3] > 0).filter(Boolean).length;
    const pages = document.numPages;
    await document.destroy();
    return { pages, text, darkPixels };
  }, { origin, encoded: Buffer.from(pdf).toString('base64') });
  expect(report.pdf.text).toContain('Campaign acceptance');
  expect(report.pdf.darkPixels).toBeGreaterThan(50);
  report.checks.push({ kind: 'PDF worker text extraction and canvas rendering', status: 'PASS' });
  expect(report.failedRequests).toEqual([]);
  expect(report.workers.some(item => /ts.worker/.test(item.url) && item.status === 200)).toBe(true);
  expect(report.workers.some(item => /html.worker/.test(item.url) && item.status === 200)).toBe(true);
  expect(report.workers.some(item => /pdf.worker/.test(item.url) && item.status === 200)).toBe(true);
  report.status = 'PASS';
  await page.screenshot({ path: path.join(destination, 'editor.png') });
} catch (error) {
  report.error = String(error);
} finally {
  await browser?.close();
  await writeFile(path.join(destination, 'web-static.json'), JSON.stringify(report, null, 2) + '\n');
}
console.log(JSON.stringify({ status: report.status, checks: report.checks.length, error: report.error }));
process.exitCode = report.status === 'PASS' ? 0 : 1;

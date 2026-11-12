import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
const fixture = 'http://127.0.0.1:18530/fixture';
for (const mode of reviewModes) test(`request editor table contexts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset`);
  await reviewPage(browser, baseURL!, mode, async page => {
    await reviewOpen(page, baseURL!, '/admin/requests/services/service-1');
    await page.locator('.svc-card .svc-actions button').first().click();
    const capture = (name: string, ...ids: string[]) => reviewCapture(page, info, mode, name, ids);
    await page.getByRole('tab', { name: /^Form fields/ }).click();
    await capture('form-fields', 'pages-admin-requests-requestformbuilder-1');
    await page.getByRole('tab', { name: /^Workflow/ }).click();
    await page.locator('[data-task-id]').first().click();
    await page.getByRole('button', { name: 'Drift', exact: true }).click();
    await capture('automation-input-drift-and-mappings', 'pages-admin-requests-requestformbuilder-2', 'pages-admin-requests-requestformbuilder-3');
    const taskList = page.locator('[data-task-id]');
    if (await taskList.count() === 0 || !await taskList.last().isVisible()) await page.getByRole('button', { name: /Back to tasks|Show tasks|Task list/i }).click();
    await taskList.last().click();
    await expect(page.getByRole('textbox', { name: /^Task Name\*?$/ })).toHaveValue('Approve completion');
    await page.getByRole('button', { name: 'Browse', exact: true }).click();
    await capture('approval-approver-picker', 'pages-admin-requests-approvalapproverpickerdialog-1');
    await page.getByRole('dialog').last().getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  });
});

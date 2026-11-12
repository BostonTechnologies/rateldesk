import { expect, test } from '@playwright/test';
import { baselineReview, modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
const fixture = 'http://127.0.0.1:18400/fixture';
for (const mode of reviewModes) test(`Operations complete table hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset`); await request.get(`${fixture}/premium`); await request.get(`${fixture}/many-ops`);
  await reviewPage(browser, baseURL!, mode, async page => {
    const open = (route: string) => reviewOpen(page, baseURL!, route);
    const capture = (name: string, ...ids: string[]) => reviewCapture(page, info, mode, name, ids);
    for (const [index, category] of ['overdue', 'escalated', 'retries', 'critical', 'callbacks', 'bindings'].entries()) {
      await open(`/ops/workflows?category=${category}`);
      await capture(`workflow-${category}`, `pages-ops-workflowopsdashboard-${index + 1}`);
    }
    await open('/ops/slas'); await capture('sla-approaching', 'pages-ops-slaopsdashboard-1');
    await page.getByRole('button', { name: 'Breached (137)', exact: true }).click();
    await capture('sla-breached', 'pages-ops-slaopsdashboard-2');
    await open('/tasks'); await capture('tasks', 'pages-tasks-taskdashboard-1');
    if (baselineReview) await page.locator('details.task-execution-timing summary').first().click();
    else await page.getByRole('button', { name: /^Expand details for / }).first().click();
    await capture('expanded-task', 'pages-tasks-taskdashboard-1');
    await open('/notifications'); await capture('notifications', 'pages-notifications-1');
    await open('/admin/email-rules'); await capture('email-rules', 'pages-admin-emailrules-emailrulesadmin-1');
    await page.getByRole('button', { name: 'View details', exact: true }).first().click();
    await page.getByRole('dialog').getByText('Advanced', { exact: true }).click();
    await capture('inbound-rule-details', 'pages-admin-emailrules-inboundemailruledetailsdialog-1', 'pages-admin-emailrules-inboundemailruledetailsdialog-2');
    await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('tab', { name: 'Audit', exact: true }).click();
    await capture('email-rule-audit', 'pages-admin-emailrules-emailrulesadmin-2');
    expect((await (await request.get(`${fixture}/state`)).json()).mutations).toHaveLength(0);
  });
});

import { expect, test } from '@playwright/test';
import { modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';
const fixture = 'http://127.0.0.1:18600/fixture';
for (const mode of reviewModes) test(`embedded detail and relation picker hosts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset`); await request.get(`${fixture}/table-review`);
  await request.post(`${fixture}/request-tasks`, { data: { count: 30 } });
  await reviewPage(browser, baseURL!, mode, async page => {
    const open = (route: string) => reviewOpen(page, baseURL!, route);
    const capture = (name: string, ...ids: string[]) => reviewCapture(page, info, mode, name, ids);
    await open('/requests/requests-1');
    await page.getByRole('tab', { name: /^Tasks/ }).click();
    await capture('request-tasks', 'pages-requests-requestdetail-1');
    await open('/incidents/incidents-1');
    await page.getByRole('tab', { name: /^Relations/ }).click();
    await capture('incident-relations', 'shared-ticketrelations-1');
    await page.getByRole('button', { name: 'Close / Link', exact: true }).click();
    await page.getByRole('dialog').locator('.mud-dialog-content .mud-icon-button').first().click();
    await capture('incident-relation-picker', 'pages-incidents-incidentrelationtargetpickerdialog-1');
    await page.getByRole('dialog').last().getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await (await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  });
});

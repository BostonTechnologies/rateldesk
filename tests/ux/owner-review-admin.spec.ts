import { expect, test } from '@playwright/test';
import { baselineReview, modeName, reviewCapture, reviewModes, reviewOpen, reviewPage } from './owner-review-matrix';

const fixture = 'http://127.0.0.1:18500/fixture';
for (const mode of reviewModes) test(`all admin table contexts ${modeName(mode)}`, async ({ browser, request, baseURL }, info) => {
  await request.get(`${fixture}/reset`); await request.get(`${fixture}/table-review`);
  await reviewPage(browser, baseURL!, mode, async page => {
    const open = (route: string) => reviewOpen(page, baseURL!, route);
    const capture = (name: string, ...ids: string[]) => reviewCapture(page, info, mode, name, ids);
    await open('/admin/roles');
    await capture('roles', 'pages-admin-role-rolemanagement-1');
    await page.getByRole('tab', { name: /^Built-in roles/ }).click();
    await capture('built-in-roles', 'pages-admin-role-rolemanagement-1');
    await expect(page.getByRole('button', { name: 'Edit role', exact: true })).toHaveCount(0);
    await page.getByRole('tab', { name: /^Custom roles/ }).click();
    const customRole = page.getByRole('row').filter({ hasText: 'synthetic-custom' });
    await expect(customRole).toBeVisible();
    await customRole.getByRole('button', { name: '237 assignments', exact: true }).click();
    await page.getByRole('dialog').getByRole('tab', { name: 'Assignments', exact: true }).click();
    await expect(page.getByText('Synthetic Operator 000', { exact: true })).toBeVisible();
    await capture('role-assignments', 'pages-admin-role-roleassignmentdetailsdialog-1');
    await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();

    await open('/admin/tenant-sla-settings');
    await capture('sla-policies', 'pages-admin-sla-tenantslapoliciespanel-1');
    await page.getByRole('tab', { name: 'Reporting', exact: true }).click();
    await capture('sla-reporting', 'pages-admin-sla-tenantslareportingpanel-1');
    await page.getByRole('tab', { name: 'Working calendars', exact: true }).click();
    await capture('sla-calendars', 'pages-admin-sla-tenantworkingcalendarspanel-1');
    await page.getByRole('tabpanel', { name: 'Working calendars', exact: true }).getByRole('button', { name: 'Edit', exact: true }).first().click();
    await capture('calendar-editor', 'pages-admin-sla-workingcalendardialog-1');
    if (!baselineReview) {
      await page.getByRole('textbox', { name: 'Start', exact: true }).nth(1).click();
      await expect(page.locator('.mud-picker-time-container')).toBeVisible();
      await page.getByRole('dialog', { name: 'Start', exact: true }).press('Escape');
      await expect(page.getByRole('dialog', { name: 'Start', exact: true })).toBeHidden();
      await expect(page.getByRole('dialog')).toHaveCount(1);
    }
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();

    await open('/admin/support-routing');
    await capture('support-coverage', 'pages-admin-supportnotifications-organizationsupportcoveragepanel-1');
    await page.getByRole('combobox', { name: 'Routing concern', exact: true }).click();
    await page.getByRole('option', { name: 'Notification rules', exact: true }).click();
    await capture('support-subscriptions', 'pages-admin-supportnotifications-supportnotificationsubscriptionspanel-1');
    await page.getByRole('tab', { name: 'Support groups', exact: true }).click();
    await capture('support-groups', 'pages-admin-supportnotifications-supportgroupmemberspanel-1');
    await page.getByRole('button', { name: 'Manage Members', exact: true }).click();
    await capture('support-group-members', 'pages-admin-supportnotifications-supportgroupmembersdialog-1');
    await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('tab', { name: 'Diagnostics', exact: true }).click();
    await page.getByText('Details', { exact: true }).click();
    await capture('support-recipients', 'pages-admin-supportnotifications-supportrecipientpreviewpanel-1');

    for (const [route, id] of [
      ['/assets', 'pages-assets-assetlist-1'], ['/admin/layouts', 'pages-admin-emaillayoutmanagement-1'],
      ['/admin/templates', 'pages-admin-emailtemplatemanagement-1'], ['/admin/automation', 'pages-admin-automation-automationrulemanagement-1'],
      ['/admin/ai-assistant-webhooks', 'pages-admin-aiassistantwebhooks-1'],
      ['/auth', 'pages-auth-1']
    ]) {
      await open(route); await capture(route.replaceAll('/', '-'), id);
      if (route === '/admin/templates') for (const category of ['Incident', 'Request', 'Other']) {
        await page.getByRole('tab', { name: new RegExp(`^${category}`) }).click();
        await capture(`email-templates-${category.toLowerCase()}`, id);
      }
    }
    await open('/admin/layouts');
    if (baselineReview) await page.locator('table tbody tr').first().getByRole('button').first().click();
    else await page.getByRole('button', { name: 'Edit layout', exact: true }).first().click();
    await page.bringToFront();
    await expect(page.locator('iframe[title="Layout Preview"]')).toHaveAttribute('srcdoc', /View Ticket/);
    await page.locator('iframe[title="Layout Preview"]').scrollIntoViewIfNeeded();
    const authored = page.frameLocator('iframe[title="Layout Preview"]');
    await expect(authored.locator('table[role="presentation"]')).toBeVisible();
    await expect(authored.getByRole('link', { name: 'View Ticket', exact: true })).toHaveCSS('color', 'rgb(255, 255, 255)');
    await capture('authored-email-semantic-exception');
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await open('/admin/email-settings/79000000-0000-4000-8000-000000000001/ingestion');
    await expect(page.getByText('Synthetic retained receipt; no effects execute').first()).toBeVisible();
    await capture('mailbox-receipts', 'pages-admin-email-mailboxingestion-2');
    await page.getByRole('button', { name: 'Import existing mail…', exact: true }).click();
    await page.getByRole('button', { name: 'Preview', exact: true }).click();
    await capture('historical-mail-preview', 'pages-admin-email-mailboxingestion-1', 'pages-admin-email-mailboxingestion-2');
    await expect(page.getByRole('button', { name: 'Import selected', exact: true })).toBeDisabled();

    await open('/admin/resources/data-management');
    await capture('dataset-rows', 'pages-admin-resources-datamanagement-2');
    await page.getByRole('tab', { name: 'Schema', exact: true }).click();
    await capture('dataset-schema', 'pages-admin-resources-datasetschemadesigner-1');
    await page.getByRole('button', { name: 'New dataset', exact: true }).click();
    let dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: /^Dataset name\*?$/ }).fill('Synthetic review dataset');
    await dialog.getByRole('button', { name: 'Continue', exact: true }).click();
    await dialog.getByRole('button', { name: 'Continue', exact: true }).click();
    await capture('dataset-create-review', 'pages-admin-resources-createdatasetwizard-1');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Discard', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.getByRole('button', { name: /^(Set up Microsoft Graph|Manage connection)$/ }).click();
    dialog = page.getByRole('dialog', { name: 'Microsoft Graph setup', exact: true });
    await dialog.getByRole('textbox', { name: /^Tenant ID\*?$/ }).fill('synthetic-tenant');
    await dialog.getByRole('textbox', { name: /^Application \/ client ID\*?$/ }).fill('synthetic-client');
    await dialog.getByLabel('Client secret', { exact: true }).fill('synthetic-draft-only');
    await dialog.getByRole('button', { name: 'Continue', exact: true }).click();
    await dialog.getByRole('checkbox', { name: 'Users', exact: true }).check();
    await dialog.getByRole('button', { name: 'Continue', exact: true }).click();
    await dialog.getByRole('button', { name: 'Test connection', exact: true }).click();
    await dialog.getByRole('button', { name: 'Continue', exact: true }).click();
    // Only the synthetic fixture responds; no Graph connection or live import occurs.
    await dialog.getByRole('button', { name: 'Save and import', exact: true }).click();
    await capture('graph-import-results', 'pages-admin-resources-graphsetupwizard-1');
    await dialog.locator('.mud-dialog-actions').getByRole('button', { name: 'Done', exact: true }).click();
    await page.getByRole('button', { name: 'View data', exact: true }).first().click();
    await page.getByRole('tab', { name: 'Schema', exact: true }).click();
    await capture('managed-dataset-schema', 'pages-admin-resources-datamanagement-1');
    await request.get(`${fixture}/directories`); await request.get(`${fixture}/team`);
    for (const [route, id] of [
      ['/admin/users', 'pages-admin-user-usermanagement-1'], ['/admin/organizations', 'pages-admin-organization-organizationmanagement-1'],
      ['/admin/customers', 'pages-admin-customer-customermanagement-1'], ['/admin/categories', 'pages-admin-categories-1']
    ]) { await open(route); await capture(route.replaceAll('/', '-'), id); }
  });
  const delegatedUrl = new URL(baseURL!);
  delegatedUrl.port = String(Number(delegatedUrl.port) + 1);
  const delegatedOrigin = delegatedUrl.origin;
  await reviewPage(browser, delegatedOrigin, mode, async page => {
    for (const [name, route] of [
      ['organizations', '/admin/organizations?section=members&organizationId=org-1'],
      ['users', '/admin/users?section=members&organizationId=org-1'],
      ['legacy-alias', '/tenant-administration?organizationId=org-1']
    ]) {
      await reviewOpen(page, delegatedOrigin, route);
      await reviewCapture(page, info, mode, `delegated-members-${name}`, ['pages-admin-user-tenantadministration-1']);
    }
  });
});

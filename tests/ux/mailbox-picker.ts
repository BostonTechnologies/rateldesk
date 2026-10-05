import { expect, type Page } from '@playwright/test';

function escapeRegularExpression(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export async function selectMailboxOrganization(page: Page, organizationName: string): Promise<void> {
  // These fixtures choose an available organization rather than test search filtering.
  // Typing can leave a pending debounce that reopens the menu after selecting an already visible option.
  await page.getByRole('combobox', { name: /^RatelDesk organization/ }).click();
  await page.getByRole('option', {
    name: new RegExp(`^${escapeRegularExpression(organizationName)}\\s+—`)
  }).click();
  await waitForMailboxOrganizationSelection(page, organizationName);
}

export async function waitForMailboxOrganizationSelection(page: Page, organizationName: string): Promise<void> {
  // Blazor Server selection and MudAutocomplete's focus/close sequence finish asynchronously.
  // Wait for the bound draft state and natural popup closure before interacting with the next field.
  const status = page.getByRole('complementary', { name: 'Selected mailbox status' });
  await expect(status).toContainText(`Dedicated · ${organizationName} ·`);
  const organizationOption = page.getByRole('option', {
    name: new RegExp(`^${escapeRegularExpression(organizationName)}\\s+—`)
  });
  await expect(organizationOption).toBeHidden();
}

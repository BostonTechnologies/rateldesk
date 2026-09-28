import { expect, type Page } from '@playwright/test';

function escapeRegularExpression(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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

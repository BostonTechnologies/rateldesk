import { expect, type Page } from '@playwright/test';

export async function waitForMailboxOrganizationSelection(page: Page, organizationName: string): Promise<void> {
  // Blazor Server selection and MudAutocomplete's focus/close sequence finish asynchronously.
  // Wait for the bound draft state and natural popup closure before interacting with the next field.
  const status = page.getByRole('complementary', { name: 'Selected mailbox status' });
  await expect(status).toContainText(`Dedicated · ${organizationName} ·`);
  await expect(page.getByRole('option').filter({ hasText: organizationName })).toBeHidden();
}

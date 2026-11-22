import { expect, type Locator, type Page } from '@playwright/test';

export async function selectOrganization(page: Page, input: Locator, name: string) {
  await input.fill(name);
  await page.getByRole('option', { name, exact: true }).click();
  await expect(input).toHaveValue(name);
}

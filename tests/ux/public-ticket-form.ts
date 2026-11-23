import { expect, type Page } from '@playwright/test';

export async function openPublicTicketReview(page: Page) {
  await page.goto('/submit-ticket');
  await page.getByRole('textbox', { name: /^Your Name\*?$/ }).fill('Synthetic public requester');
  await page.getByRole('textbox', { name: /^Email(?: Address)?\*?$/ }).fill('requester@example.test');
  await page.getByRole('textbox', { name: /^Confirm Email Address\*?$/ }).fill('requester@example.test');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await page.getByRole('textbox', { name: /^Subject of your issue\*?$/ }).fill('Synthetic intake verification');
  await page.getByRole('textbox', { name: 'Please describe the issue in detail', exact: true }).fill('Synthetic public intake browser fixture.');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'CAPTCHA', exact: true })).toBeVisible();
}

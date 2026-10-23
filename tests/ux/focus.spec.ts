import { expect, test, type Locator } from '@playwright/test';
import { authenticate } from './auth';

test('navigation announces the public heading without a frame and keeps keyboard link focus visible', async ({ page }) => {
  for (const visit of ['initial', 'reload'] as const) {
    if (visit === 'initial') await page.goto('/');
    else await page.reload();

    const heading = page.getByRole('heading', { level: 1, name: /^Welcome to / });
    await expect(heading).toBeFocused();
    await expect(heading).toHaveAttribute('tabindex', '-1');
    await expect(heading).toHaveCSS('outline-style', 'none');

    // FocusOnNavigate still sets the starting point for keyboard navigation.
    await page.keyboard.press('Shift+Tab');
    const signIn = page.getByRole('link', { name: 'Admin sign-in' });
    await expect(signIn).toBeFocused();
    await expect(signIn).toHaveCSS('outline-style', 'solid');
    await expect(signIn).toHaveCSS('outline-width', '2px');
  }
});

test('outlined fields keep one visible focus border with pointer and keyboard input', async ({ page }) => {
  await authenticate(page);
  await page.goto('/incidents');
  await expect(page.getByRole('heading', { name: 'Incidents', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  const dialog = page.getByRole('dialog');
  const title = dialog.getByRole('textbox', { name: 'Title' });
  const description = dialog.getByPlaceholder('Enter the ticket description...');

  await title.click();
  await expectSingleFieldFocus(title);
  await page.keyboard.press('Tab');
  await expectSingleFieldFocus(description);
  await page.keyboard.press('Shift+Tab');
  await expectSingleFieldFocus(title);

  // Ticket search uses the same single outlined focus border as the form fields.
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  const search = page.getByTestId('app-main-content').getByRole('textbox', { name: 'Search tickets', exact: true });
  await search.click();
  await expectSingleFieldFocus(search);
});

async function expectSingleFieldFocus(input: Locator) {
  await expect(input).toBeFocused();
  await expect(input).toHaveCSS('outline-style', 'none');
  const field = input.locator('..');
  await expect(field.locator('.mud-input-outlined-border')).toHaveCSS('border-top-width', '2px');
  await expect(field.locator('.mud-input-outlined-border')).toHaveCSS('border-top-style', 'solid');
}

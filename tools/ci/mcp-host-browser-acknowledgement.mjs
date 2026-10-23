import { expect } from '@playwright/test';

export async function expectConsentAcknowledged(page) {
  await expect(page.getByTestId('integration-pairing-approval').getByRole('status'))
    .toContainText('Connection approved');
}

import { test as setup, expect } from '@playwright/test';
import * as path from 'path';

const authFile = path.join(__dirname, '../.auth/user.json');

/**
 * Global setup for authentication
 * This runs once before all tests and saves the authenticated state
 */
setup('authenticate', async ({ page }) => {
  const accessCode = process.env.E2E_ACCESS_CODE;
  if (!accessCode) {
    throw new Error('E2E_ACCESS_CODE is required to run auth setup.');
  }

  await page.goto('/login');
  await page.locator('input[name="code"]').fill(accessCode);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/search/, { timeout: 10000 });

  const token = await page.evaluate(() => localStorage.getItem('auth_token'));
  expect(token).toBeTruthy();

  await page.context().storageState({ path: authFile });
});

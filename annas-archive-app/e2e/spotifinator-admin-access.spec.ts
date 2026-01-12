import { test, expect } from '@playwright/test';

/**
 * Spotifinator Admin Access E2E Tests
 *
 * Test suite for spotifinator feature admin access control:
 * - Admin can see and access spotifinator
 * - Non-admin users cannot see or access spotifinator
 * - Unauthenticated users are redirected
 * - Page renders with correct Material Design styling
 */

test.describe('Spotifinator Admin Access', () => {
  const validAccessCode = process.env.E2E_ACCESS_CODE;
  if (!validAccessCode) {
    throw new Error('E2E_ACCESS_CODE is required to run spotifinator tests.');
  }

  // Clear all state before each test for isolation
  test.beforeEach(async ({ context }) => {
    await context.clearCookies();
  });

  // Clean up after each test
  test.afterEach(async ({ page }) => {
    try {
      const url = page.url();
      if (!url.includes('#/login')) {
        const logoutButton = page.locator('button:has-text("Logout")').first();
        if (await logoutButton.isVisible({ timeout: 1000 })) {
          await logoutButton.click();
          await page.waitForTimeout(500);
        }
      }
    } catch (error) {
      await page.evaluate(() => {
        localStorage.clear();
        sessionStorage.clear();
      }).catch(() => {});
    }
  });

  test('admin user can see "Spotif-inator" menu item in navigation', async ({ page }) => {
    // Login as admin
    await page.goto('/login', { timeout: 30000 });
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    const codeInput = page.locator('input[name="code"]');
    await expect(codeInput).toBeVisible({ timeout: 20000 });
    await codeInput.fill(validAccessCode);

    const signInButton = page.locator('button[type="submit"]');
    await expect(signInButton).toBeEnabled({ timeout: 10000 });
    await signInButton.click();

    // Wait for successful login
    await expect(page).toHaveURL(/#\/search/, { timeout: 20000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });
    await expect(page.locator('app-book-search')).toBeVisible({ timeout: 15000 });
    await page.waitForTimeout(2000);

    // Verify admin status
    const isAdmin = await page.evaluate(() => {
      return localStorage.getItem('auth_admin') === 'true';
    });
    expect(isAdmin).toBeTruthy();

    // Open navigation menu
    const navMenuButton = page.locator('button:has-text("Navigation")');
    await expect(navMenuButton).toBeVisible({ timeout: 5000 });
    await navMenuButton.click();

    // Wait for menu to open
    await page.waitForTimeout(500);

    // Verify Spotif-inator menu item is visible
    const spotifinatorMenuItem = page.locator('button[mat-menu-item]:has-text("Spotif-inator")');
    await expect(spotifinatorMenuItem).toBeVisible({ timeout: 5000 });

    // Verify it has the correct icon
    const musicIcon = spotifinatorMenuItem.locator('mat-icon:has-text("library_music")');
    await expect(musicIcon).toBeVisible();
  });

  test('admin user can navigate to /spotifinator route', async ({ page }) => {
    // Login as admin
    await page.goto('/login', { timeout: 30000 });
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    const codeInput = page.locator('input[name="code"]');
    await codeInput.fill(validAccessCode);
    const signInButton = page.locator('button[type="submit"]');
    await signInButton.click();

    await expect(page).toHaveURL(/#\/search/, { timeout: 20000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });
    await page.waitForTimeout(2000);

    // Navigate to spotifinator
    await page.goto('/#/spotifinator', { timeout: 30000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });

    // Should successfully access spotifinator page
    await expect(page).toHaveURL(/#\/spotifinator/, { timeout: 15000 });
    await expect(page.locator('app-spotifinator')).toBeVisible({ timeout: 15000 });
  });

  test('unauthenticated user is redirected from /spotifinator to /login', async ({ page }) => {
    // Clear storage
    await page.goto('/login');
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    // Try to access spotifinator without authentication
    await page.goto('/#/spotifinator');

    // Should be redirected to login
    await expect(page).toHaveURL(/#\/login/, { timeout: 5000 });
  });

  test('spotifinator page displays correct title and content', async ({ page }) => {
    // Login as admin
    await page.goto('/login', { timeout: 30000 });
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    const codeInput = page.locator('input[name="code"]');
    await codeInput.fill(validAccessCode);
    const signInButton = page.locator('button[type="submit"]');
    await signInButton.click();

    await expect(page).toHaveURL(/#\/search/, { timeout: 20000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });
    await page.waitForTimeout(2000);

    // Navigate to spotifinator
    await page.goto('/#/spotifinator', { timeout: 30000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });

    // Check page title
    const title = page.locator('.coming-soon-title');
    await expect(title).toBeVisible({ timeout: 5000 });
    await expect(title).toContainText('Spotif-inator');

    // Check subtitle
    const subtitle = page.locator('.coming-soon-subtitle').first();
    await expect(subtitle).toBeVisible();
    await expect(subtitle).toContainText('Admin Feature');

    // Check loading dots are present
    const dots = page.locator('.dot');
    await expect(dots).toHaveCount(3);
  });

  test('spotifinator page uses Material Design styling (no purple gradient)', async ({ page }) => {
    // Login as admin
    await page.goto('/login', { timeout: 30000 });
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    const codeInput = page.locator('input[name="code"]');
    await codeInput.fill(validAccessCode);
    const signInButton = page.locator('button[type="submit"]');
    await signInButton.click();

    await expect(page).toHaveURL(/#\/search/, { timeout: 20000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });
    await page.waitForTimeout(2000);

    // Navigate to spotifinator
    await page.goto('/#/spotifinator', { timeout: 30000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });

    // Check container styling
    const container = page.locator('.spotifinator-container');
    await expect(container).toBeVisible({ timeout: 5000 });

    // Verify it's not using purple gradient (Lucy Quiz styling)
    const containerBg = await container.evaluate((el) => {
      return window.getComputedStyle(el).background;
    });

    // Should not contain purple gradient colors
    expect(containerBg).not.toContain('667eea');
    expect(containerBg).not.toContain('764ba2');

    // Check content card exists
    const contentCard = page.locator('.content-card');
    await expect(contentCard).toBeVisible();
  });

  test('spotifinator page is responsive on mobile viewport', async ({ page }) => {
    // Set mobile viewport
    await page.setViewportSize({ width: 375, height: 667 });

    // Login as admin
    await page.goto('/login', { timeout: 30000 });
    await page.evaluate(() => {
      localStorage.clear();
      sessionStorage.clear();
    });

    const codeInput = page.locator('input[name="code"]');
    await codeInput.fill(validAccessCode);
    const signInButton = page.locator('button[type="submit"]');
    await signInButton.click();

    await expect(page).toHaveURL(/#\/search/, { timeout: 20000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });
    await page.waitForTimeout(2000);

    // Navigate to spotifinator
    await page.goto('/#/spotifinator', { timeout: 30000 });
    await page.waitForLoadState('domcontentloaded', { timeout: 10000 });

    // Verify page is visible and content is accessible
    const container = page.locator('.spotifinator-container');
    await expect(container).toBeVisible({ timeout: 5000 });

    const title = page.locator('.coming-soon-title');
    await expect(title).toBeVisible();

    const contentCard = page.locator('.content-card');
    await expect(contentCard).toBeVisible();

    // Verify content card is within viewport
    const cardBoundingBox = await contentCard.boundingBox();
    expect(cardBoundingBox).toBeTruthy();
    if (cardBoundingBox) {
      expect(cardBoundingBox.width).toBeLessThanOrEqual(375);
    }
  });

  // TODO: Add test for non-admin user cannot see menu item (requires E2E_NON_ADMIN_CODE)
  // test('non-admin user cannot see "Spotif-inator" menu item in navigation', async ({ page }) => {
  //   const nonAdminCode = process.env.E2E_NON_ADMIN_CODE;
  //   if (!nonAdminCode) {
  //     test.skip();
  //     return;
  //   }
  //   // Implementation when non-admin code is available
  // });

  // TODO: Add test for non-admin redirect (requires E2E_NON_ADMIN_CODE)
  // test('non-admin user is redirected from /spotifinator to /search', async ({ page }) => {
  //   const nonAdminCode = process.env.E2E_NON_ADMIN_CODE;
  //   if (!nonAdminCode) {
  //     test.skip();
  //     return;
  //   }
  //   // Implementation when non-admin code is available
  // });
});

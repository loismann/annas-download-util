import { test, expect } from '@playwright/test';

test.describe('Library Tile Size Controls', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/login');
    await page.fill('input[name="accessCode"]', process.env.E2E_ADMIN_CODE || '');
    await page.click('button:has-text("Login")');
    await page.waitForURL('/search');
    await page.goto('/library');
    await page.waitForSelector('.library-grid');
  });

  test('should show tile size control buttons to the left of Sort by dropdown', async ({ page }) => {
    const tileControls = page.locator('.tile-size-controls');
    const sortCombo = page.locator('.sort-combo');

    await expect(tileControls).toBeVisible();
    await expect(sortCombo).toBeVisible();

    // Check positioning - tile controls should be before sort combo in DOM
    const controlsBox = await tileControls.boundingBox();
    const sortBox = await sortCombo.boundingBox();

    expect(controlsBox).toBeTruthy();
    expect(sortBox).toBeTruthy();
    expect(controlsBox!.x).toBeLessThan(sortBox!.x);
  });

  test('should display three tile size buttons', async ({ page }) => {
    const buttons = page.locator('.tile-size-btn');
    await expect(buttons).toHaveCount(3);

    // Check icons
    await expect(buttons.nth(0).locator('mat-icon')).toHaveText('view_comfy');
    await expect(buttons.nth(1).locator('mat-icon')).toHaveText('view_module');
    await expect(buttons.nth(2).locator('mat-icon')).toHaveText('view_agenda');
  });

  test('should have tile size buttons aligned with Sort by dropdown', async ({ page }) => {
    const tileButton = page.locator('.tile-size-btn').first();
    const sortField = page.locator('.sort-field');

    const buttonBox = await tileButton.boundingBox();
    const sortBox = await sortField.boundingBox();

    expect(buttonBox).toBeTruthy();
    expect(sortBox).toBeTruthy();

    // Both should have height of 56px
    expect(buttonBox!.height).toBeCloseTo(56, 2);
    expect(sortBox!.height).toBeCloseTo(56, 2);

    // Should be vertically aligned (same top position)
    expect(Math.abs(buttonBox!.y - sortBox!.y)).toBeLessThan(2);
  });

  test('should have medium tile size button active by default', async ({ page }) => {
    const mediumButton = page.locator('.tile-size-btn').nth(1);
    await expect(mediumButton).toHaveClass(/active/);

    const smallButton = page.locator('.tile-size-btn').nth(0);
    const largeButton = page.locator('.tile-size-btn').nth(2);

    await expect(smallButton).not.toHaveClass(/active/);
    await expect(largeButton).not.toHaveClass(/active/);
  });

  test('should switch to small tiles when small button is clicked', async ({ page }) => {
    const smallButton = page.locator('.tile-size-btn').nth(0);
    await smallButton.click();

    // Check button is active
    await expect(smallButton).toHaveClass(/active/);

    // Check cards have small class
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-small/);

    // Verify grid has more columns (smaller cards)
    const grid = page.locator('.library-grid');
    const gridStyles = await grid.evaluate((el) => {
      return window.getComputedStyle(el).gridTemplateColumns;
    });

    // Small tiles should have more columns (narrower cards)
    expect(gridStyles).toContain('120px');
  });

  test('should switch to large tiles when large button is clicked', async ({ page }) => {
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();

    // Check button is active
    await expect(largeButton).toHaveClass(/active/);

    // Check cards have large class
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-large/);

    // Verify grid has fewer columns (wider cards)
    const grid = page.locator('.library-grid');
    const gridStyles = await grid.evaluate((el) => {
      return window.getComputedStyle(el).gridTemplateColumns;
    });

    // Large tiles should have fewer columns (wider cards)
    expect(gridStyles).toContain('200px');
  });

  test('should switch back to medium tiles', async ({ page }) => {
    // First switch to large
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();
    await expect(largeButton).toHaveClass(/active/);

    // Then switch back to medium
    const mediumButton = page.locator('.tile-size-btn').nth(1);
    await mediumButton.click();

    await expect(mediumButton).toHaveClass(/active/);
    await expect(largeButton).not.toHaveClass(/active/);

    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-medium/);
  });

  test('should show active button with blue background', async ({ page }) => {
    const mediumButton = page.locator('.tile-size-btn').nth(1);

    // Get computed background color (active button should be blue)
    const backgroundColor = await mediumButton.evaluate((el) => {
      return window.getComputedStyle(el).backgroundColor;
    });

    // Material blue (#3f51b5) converts to rgb(63, 81, 181)
    expect(backgroundColor).toBe('rgb(63, 81, 181)');
  });

  test('should show hover effect on tile size buttons', async ({ page }) => {
    const smallButton = page.locator('.tile-size-btn').nth(0);

    // Hover over button
    await smallButton.hover();

    // Get background color during hover
    const hoverBg = await smallButton.evaluate((el) => {
      return window.getComputedStyle(el).backgroundColor;
    });

    // Should have light blue hover background (#eef2ff) which is rgb(238, 242, 255)
    expect(hoverBg).toBe('rgb(238, 242, 255)');
  });

  test('should persist tile size selection during filtering', async ({ page }) => {
    // Switch to small tiles
    const smallButton = page.locator('.tile-size-btn').nth(0);
    await smallButton.click();
    await expect(smallButton).toHaveClass(/active/);

    // Apply a genre filter
    const genreSelect = page.locator('mat-select[ng-reflect-name="genreFilter"]');
    await genreSelect.click();
    const firstGenre = page.locator('mat-option').first();
    await firstGenre.click();

    // Wait for filter to apply
    await page.waitForTimeout(500);

    // Verify small tiles are still active
    await expect(smallButton).toHaveClass(/active/);
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-small/);
  });

  test('should persist tile size selection during sorting', async ({ page }) => {
    // Switch to large tiles
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();
    await expect(largeButton).toHaveClass(/active/);

    // Change sort order
    const sortSelect = page.locator('mat-select[ng-reflect-name="sortOrder"]');
    await sortSelect.click();
    const titleOption = page.locator('mat-option:has-text("Title")');
    await titleOption.click();

    // Wait for sort to apply
    await page.waitForTimeout(500);

    // Verify large tiles are still active
    await expect(largeButton).toHaveClass(/active/);
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-large/);
  });

  test('should display smaller fonts in small tiles', async ({ page }) => {
    const firstCard = page.locator('.library-card').first();

    // Get font size in medium (default)
    const mediumTitleFontSize = await firstCard.locator('.title').evaluate((el) => {
      return window.getComputedStyle(el).fontSize;
    });

    // Switch to small
    const smallButton = page.locator('.tile-size-btn').nth(0);
    await smallButton.click();

    await page.waitForTimeout(100);

    // Get font size in small
    const smallTitleFontSize = await firstCard.locator('.title').evaluate((el) => {
      return window.getComputedStyle(el).fontSize;
    });

    // Font size should be smaller
    expect(parseFloat(smallTitleFontSize)).toBeLessThan(parseFloat(mediumTitleFontSize));
  });

  test('should display larger fonts in large tiles', async ({ page }) => {
    const firstCard = page.locator('.library-card').first();

    // Get font size in medium (default)
    const mediumTitleFontSize = await firstCard.locator('.title').evaluate((el) => {
      return window.getComputedStyle(el).fontSize;
    });

    // Switch to large
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();

    await page.waitForTimeout(100);

    // Get font size in large
    const largeTitleFontSize = await firstCard.locator('.title').evaluate((el) => {
      return window.getComputedStyle(el).fontSize;
    });

    // Font size should be larger
    expect(parseFloat(largeTitleFontSize)).toBeGreaterThan(parseFloat(mediumTitleFontSize));
  });

  test('should maintain cover aspect ratio across all tile sizes', async ({ page }) => {
    const getCoverAspectRatio = async () => {
      const coverFrame = page.locator('.cover-frame').first();
      const box = await coverFrame.boundingBox();
      if (!box) return 0;
      return box.width / box.height;
    };

    // Medium (default)
    const mediumRatio = await getCoverAspectRatio();

    // Small
    const smallButton = page.locator('.tile-size-btn').nth(0);
    await smallButton.click();
    await page.waitForTimeout(100);
    const smallRatio = await getCoverAspectRatio();

    // Large
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();
    await page.waitForTimeout(100);
    const largeRatio = await getCoverAspectRatio();

    // All should be approximately 2:3 (0.666...)
    expect(mediumRatio).toBeCloseTo(2 / 3, 1);
    expect(smallRatio).toBeCloseTo(2 / 3, 1);
    expect(largeRatio).toBeCloseTo(2 / 3, 1);
  });
});

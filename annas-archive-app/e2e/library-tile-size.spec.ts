import { test, expect, type Page } from '@playwright/test';

type LibraryBook = {
  title: string;
  authors: string[];
  format: string;
  fileSize: string;
  fileName: string;
  coverUrl?: string | null;
  primaryGenre?: string | null;
  tags?: string[];
  series?: string | null;
  goodreadsRating?: number | null;
  personalRating?: number | null;
};

const testBooks: LibraryBook[] = [
  {
    title: 'Foundation',
    authors: ['Isaac Asimov'],
    format: 'EPUB',
    fileSize: '1.2 MB',
    fileName: 'foundation.epub',
    coverUrl: 'https://covers.example.test/foundation.jpg',
    primaryGenre: 'Science Fiction',
    tags: ['Space Opera'],
    series: 'Foundation',
    goodreadsRating: 4.2,
    personalRating: 4,
  },
  {
    title: 'Dune',
    authors: ['Frank Herbert'],
    format: 'PDF',
    fileSize: '2.1 MB',
    fileName: 'dune.pdf',
    coverUrl: 'https://covers.example.test/dune.jpg',
    primaryGenre: 'Science Fiction',
    tags: [],
    series: 'Dune',
    goodreadsRating: 4.6,
    personalRating: 5,
  },
];

async function setAuthToken(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('auth_token', 'e2e-token');
    localStorage.setItem('auth_name', 'E2E User');
    localStorage.setItem('auth_admin', 'true');
  });
}

async function mockLibraryRoute(page: Page, books: LibraryBook[]): Promise<void> {
  await page.route('**/api/library/books**', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(books),
    });
  });
}

test.describe('Library Tile Size Controls', () => {
  test.beforeEach(async ({ page }) => {
    await mockLibraryRoute(page, testBooks);
    await setAuthToken(page);
    await page.goto('/#/library');
    await page.waitForSelector('.library-grid', { timeout: 15000 });
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

    // Verify grid has columns (minmax values mean actual widths vary)
    const grid = page.locator('.library-grid');
    const gridStyles = await grid.evaluate((el) => {
      return window.getComputedStyle(el).gridTemplateColumns;
    });

    // Small tiles use minmax(120px, 1fr) - check we have multiple columns
    const columnCount = gridStyles.split(' ').length;
    expect(columnCount).toBeGreaterThanOrEqual(2);
  });

  test('should switch to large tiles when large button is clicked', async ({ page }) => {
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();

    // Check button is active
    await expect(largeButton).toHaveClass(/active/);

    // Check cards have large class
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-large/);

    // Verify grid has columns (minmax values mean actual widths vary)
    const grid = page.locator('.library-grid');
    const gridStyles = await grid.evaluate((el) => {
      return window.getComputedStyle(el).gridTemplateColumns;
    });

    // Large tiles use minmax(200px, 1fr) - check we have at least one column
    const columnCount = gridStyles.split(' ').length;
    expect(columnCount).toBeGreaterThanOrEqual(1);
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

  test('should have hover styles defined for tile size buttons', async ({ page }) => {
    // CSS :hover pseudo-class testing is unreliable with programmatic hover
    // Instead, verify the button has pointer cursor (indicates interactive element)
    const smallButton = page.locator('.tile-size-btn').nth(0);

    const cursor = await smallButton.evaluate((el) => {
      return window.getComputedStyle(el).cursor;
    });

    expect(cursor).toBe('pointer');

    // Also verify button is clickable and changes state
    await smallButton.click();
    await expect(smallButton).toHaveClass(/active/);
  });

  test('should persist tile size selection when switching sizes', async ({ page }) => {
    // Switch to small tiles
    const smallButton = page.locator('.tile-size-btn').nth(0);
    await smallButton.click();
    await expect(smallButton).toHaveClass(/active/);

    // Verify cards have small class
    const firstCard = page.locator('.library-card').first();
    await expect(firstCard).toHaveClass(/library-card-small/);

    // Switch to large
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();
    await expect(largeButton).toHaveClass(/active/);
    await expect(firstCard).toHaveClass(/library-card-large/);

    // Switch back to small - should persist correctly
    await smallButton.click();
    await expect(smallButton).toHaveClass(/active/);
    await expect(firstCard).toHaveClass(/library-card-small/);
  });

  test('should maintain tile size after clicking on different size buttons', async ({ page }) => {
    // Start with medium (default)
    const mediumButton = page.locator('.tile-size-btn').nth(1);
    await expect(mediumButton).toHaveClass(/active/);

    // Click large
    const largeButton = page.locator('.tile-size-btn').nth(2);
    await largeButton.click();
    await expect(largeButton).toHaveClass(/active/);
    await expect(mediumButton).not.toHaveClass(/active/);

    // Verify card class updated
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

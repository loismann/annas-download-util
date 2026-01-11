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

const ACCESS_CODE = process.env.E2E_ACCESS_CODE;
if (!ACCESS_CODE) {
  throw new Error('E2E_ACCESS_CODE is required to run library tests.');
}

const libraryBooks: LibraryBook[] = [
  {
    title: 'Foundation',
    authors: ['Isaac Asimov'],
    format: 'EPUB',
    fileSize: '1.2 MB',
    fileName: 'foundation.epub',
    coverUrl: 'https://covers.example.test/foundation.jpg',
    primaryGenre: 'Science Fiction',
    tags: ["Dad's Books", 'Space Opera'],
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
    tags: ["Mom's Books"],
    series: 'Dune',
    goodreadsRating: 4.6,
    personalRating: 5,
  },
  {
    title: 'Untitled Mystery',
    authors: [],
    format: 'MOBI',
    fileSize: '900 KB',
    fileName: 'mystery.mobi',
    coverUrl: null,
    primaryGenre: 'Mystery',
    tags: ["Paul's Books"],
    series: null,
    goodreadsRating: 3.2,
    personalRating: 2,
  },
  {
    title: 'The Sun Also Rises',
    authors: ['Ernest Hemingway'],
    format: 'EPUB',
    fileSize: '1.5 MB',
    fileName: 'sun.epub',
    coverUrl: 'https://covers.example.test/sun.jpg',
    primaryGenre: 'Classics',
    tags: ["Dad's Books", 'Classics'],
    series: null,
    goodreadsRating: 3.8,
    personalRating: 3,
  },
];

const setAuthToken = async (page: Page) => {
  await page.addInitScript((accessCode) => {
    localStorage.setItem('auth_token', accessCode);
    localStorage.setItem('auth_name', 'E2E User');
    localStorage.setItem('auth_admin', 'true');
  }, ACCESS_CODE);
};

const mockLibraryRoute = async (page: Page, books: LibraryBook[]) => {
  await page.route('**/api/library/books**', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(books),
    });
  });
};

const openLibraryPage = async (page: Page) => {
  const responsePromise = page.waitForResponse('**/api/library/books**');
  await setAuthToken(page);
  await page.goto(`/#/library?e2e=${Date.now()}`);
  await responsePromise;
  await expect(page.locator('.library-loading')).not.toBeVisible({ timeout: 15000 });
};

const openGenreSelect = async (page: Page) => {
  const field = page.locator('mat-form-field', { has: page.locator('mat-label', { hasText: 'Genre' }) });
  await field.locator('mat-select').click();
};

const openAdminPanel = async (page: Page) => {
  const toggle = page.locator('.admin-toggle');
  await expect(toggle).toBeVisible();
  await toggle.click();
};

test.describe('Library - Search & Filter', () => {
  test.beforeEach(async ({ page }) => {
    await mockLibraryRoute(page, libraryBooks);
    await openLibraryPage(page);
  });

  test('Search library by title should filter results', async ({ page }) => {
    await expect(page.locator('.library-card')).toHaveCount(4);
    await page.locator('input[placeholder*="Title"]').fill('Foundation');
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Search library by author should filter results', async ({ page }) => {
    await expect(page.locator('.library-card')).toHaveCount(4);
    await page.locator('input[placeholder*="Title"]').fill('Asimov');
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Search library by series should filter results', async ({ page }) => {
    await expect(page.locator('.library-card')).toHaveCount(4);
    await page.locator('input[placeholder*="Title"]').fill('Dune');
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Search with no matches should show empty results', async ({ page }) => {
    await expect(page.locator('.library-card')).toHaveCount(4);
    await page.locator('input[placeholder*="Title"]').fill('NonexistentBook12345');
    await expect(page.locator('.library-card')).toHaveCount(0);
  });

  test('Clear search should restore all books', async ({ page }) => {
    await page.locator('input[placeholder*="Title"]').fill('Foundation');
    await expect(page.locator('.library-card')).toHaveCount(1);
    await page.locator('input[placeholder*="Title"]').fill('');
    await expect(page.locator('.library-card')).toHaveCount(4);
  });

  test('Genre filter should show only books in selected genre', async ({ page }) => {
    await openGenreSelect(page);
    await page.locator('mat-option').filter({ hasText: 'Science Fiction' }).click();
    await expect(page.locator('.library-card')).toHaveCount(2);
  });

  test('Genre filter dropdown should populate with available genres', async ({ page }) => {
    await openGenreSelect(page);
    await expect(page.locator('mat-option').filter({ hasText: 'All Genres' })).toBeVisible();
    await expect(page.locator('mat-option').filter({ hasText: 'Science Fiction' })).toBeVisible();
  });

  test('Multiple filters should work together (search + genre)', async ({ page }) => {
    await page.locator('input[placeholder*="Title"]').fill('Foundation');
    await openGenreSelect(page);
    await page.locator('mat-option').filter({ hasText: 'Science Fiction' }).click();
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Filter by owner tags (Dad\'s/Mom\'s/Paul\'s Books) should work', async ({ page }) => {
    await page.locator('button.owner-filter').filter({ hasText: "Paul's Books" }).click();
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Multiple owner tag selection should work', async ({ page }) => {
    await page.locator('button.owner-filter').filter({ hasText: "Paul's Books" }).click();
    await page.locator('button.owner-filter').filter({ hasText: "Dad's Books" }).click();
    await expect(page.locator('.library-card')).toHaveCount(3);
  });

  test('Filter missing author should show only books without authors', async ({ page }) => {
    await openAdminPanel(page);
    const checkbox = page.locator('label.admin-row').filter({ hasText: 'Book has no author' }).locator('input[type="checkbox"]');
    await checkbox.check();
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Filter missing cover should show only books without covers', async ({ page }) => {
    await openAdminPanel(page);
    const checkbox = page.locator('label.admin-row').filter({ hasText: 'Book cover image is missing' }).locator('input[type="checkbox"]');
    await checkbox.check();
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Filter by minimum personal rating should work', async ({ page }) => {
    const slider = page.locator('input[type="range"]').first();
    await slider.fill('4');
    await expect(page.locator('.library-card')).toHaveCount(2);
  });

  test('Filter by minimum Goodreads rating should work', async ({ page }) => {
    const slider = page.locator('input[type="range"]').nth(1);
    await slider.fill('4.4');
    await expect(page.locator('.library-card')).toHaveCount(1);
  });

  test('Genre count filter (less than/more than N) should work', async ({ page }) => {
    await openAdminPanel(page);
    const genreCountRow = page.locator('label.admin-row').filter({ has: page.locator('select') });
    await genreCountRow.locator('input[type="checkbox"]').first().check();
    await genreCountRow.locator('select').selectOption('less');
    await genreCountRow.locator('input[type="number"]').fill('3');
    await expect(page.locator('.library-card')).toHaveCount(2);
  });
});

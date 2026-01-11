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

const baseLibraryBooks: LibraryBook[] = [
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

const mockLibraryError = async (page: Page) => {
  await page.route('**/api/library/books**', route => {
    route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'Failed to load library.' }),
    });
  });
};

const openLibraryPage = async (page: Page) => {
  const responsePromise = page.waitForResponse('**/api/library/books**');
  await setAuthToken(page);
  await page.goto(`/#/library?e2e=${Date.now()}`);
  await responsePromise;
};

test.describe('Library - Basic Navigation', () => {
  test('Library page should load all saved books', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-loading')).not.toBeVisible({ timeout: 15000 });
    await expect(page.locator('.library-card')).toHaveCount(3);
  });

  test('Library should display book covers', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-card img.cover').first()).toBeVisible();
  });

  test('Library should display book titles', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-card .title')).toContainText(['Foundation', 'Dune']);
  });

  test('Library should display book authors', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-card .author')).toContainText(['Isaac Asimov', 'Frank Herbert']);
  });

  test('Library should display book formats', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-card .meta')).toContainText(['EPUB', 'PDF', 'MOBI']);
  });

  test('Library should display book file sizes', async ({ page }) => {
    await mockLibraryRoute(page, baseLibraryBooks);
    await openLibraryPage(page);

    await expect(page.locator('.library-card .meta')).toContainText(['1.2 MB', '2.1 MB', '900 KB']);
  });

  test('Empty library should show appropriate message', async ({ page }) => {
    await mockLibraryRoute(page, []);
    await openLibraryPage(page);

    await expect(page.locator('.library-card')).toHaveCount(0);
    await expect(page.locator('.library-grid')).toHaveCount(1);
  });

  test('Loading state should show spinner', async ({ page }) => {
    await page.route('**/api/library/books**', route => {
      setTimeout(() => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(baseLibraryBooks),
        });
      }, 1500);
    });

    await setAuthToken(page);
    await page.goto(`/#/library?e2e=${Date.now()}`);

    const loadingSpinner = page.locator('.library-loading mat-spinner');
    await expect(loadingSpinner).toBeVisible({ timeout: 2000 });
    await expect(loadingSpinner).not.toBeVisible({ timeout: 15000 });
  });

  test('Error loading library should display error message', async ({ page }) => {
    await mockLibraryError(page);
    await openLibraryPage(page);

    const errorMessage = page.locator('.library-error');
    await expect(errorMessage).toBeVisible();
    await expect(errorMessage).toContainText(/Failed to load library/i);
  });
});

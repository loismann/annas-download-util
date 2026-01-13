import { test, expect, type Page } from '@playwright/test';

/**
 * E2E Tests for Reader - Chapter Navigation
 *
 * Tests:
 * - Clicking chapter should load chapter content
 * - Chapter dropdown should show all chapters
 * - Selecting chapter from dropdown should load that chapter
 * - Empty state should show when no chapter selected
 * - Loading chapter content should show loading state
 * - Chapter without content should show error
 */

type LibraryReaderBook = {
  fileName: string;
  readerKey: string;
  title: string;
  authors: string[];
  format: string;
  hasSummaries: boolean;
};

type DropboxChapterContent = {
  id: number;
  title: string;
  content: string;
  characterCount: number;
  wordCount: number;
};

const ACCESS_CODE = process.env.E2E_ACCESS_CODE;
if (!ACCESS_CODE) {
  throw new Error('E2E_ACCESS_CODE is required to run reader tests.');
}

const testBook: LibraryReaderBook = {
  fileName: 'foundation.epub',
  readerKey: 'foundation.epub',
  title: 'Foundation',
  authors: ['Isaac Asimov'],
  format: 'EPUB',
  hasSummaries: false,
};

const mockChapters = [
  { id: 1, title: 'Chapter 1: The Psychohistorians', level: 0, wordCount: 2500, displayLabel: 'Chapter 1' },
  { id: 2, title: 'Chapter 2: The Encyclopedists', level: 0, wordCount: 3200, displayLabel: 'Chapter 2' },
  { id: 3, title: 'Chapter 3: The Mayors', level: 0, wordCount: 2800, displayLabel: 'Chapter 3' },
];

const mockChapterContent: Record<number, DropboxChapterContent> = {
  1: {
    id: 1,
    title: 'Chapter 1: The Psychohistorians',
    content: 'His name was Gaal Dornick and he was just a country boy who had never seen Trantor before.',
    characterCount: 89,
    wordCount: 2500,
  },
  2: {
    id: 2,
    title: 'Chapter 2: The Encyclopedists',
    content: 'The meeting of the Board of Trustees of the Encyclopedia Foundation took place in the Encyclopedia Building.',
    characterCount: 109,
    wordCount: 3200,
  },
  3: {
    id: 3,
    title: 'Chapter 3: The Mayors',
    content: 'The mayor of Terminus City looked up with an air of annoyance.',
    characterCount: 62,
    wordCount: 2800,
  },
};

const setAuthToken = async (page: Page) => {
  await page.addInitScript((accessCode) => {
    localStorage.setItem('auth_token', accessCode);
    localStorage.setItem('auth_name', 'E2E User');
    localStorage.setItem('auth_admin', 'true');
  }, ACCESS_CODE);
};

const mockReaderBooksRoute = async (page: Page, books: LibraryReaderBook[]) => {
  await page.route('**/api/library/reader/books**', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(books),
    });
  });
};

const mockChaptersRoute = async (page: Page, fileName: string, chapters: any[]) => {
  await page.route(url => url.pathname.includes('/api/library/reader/epub/chapters') && url.searchParams.get('fileName') === fileName, route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ chapters }),
    });
  });
};

const mockChapterContentRoute = async (page: Page, fileName: string, delay = 0) => {
  // Match chapter content endpoint (not chapters list)
  await page.route(url => {
    const urlStr = url.toString();
    // Check for /chapter but not /chapters, and must have our fileName
    return urlStr.includes('/api/library/reader/epub/chapter') &&
           !urlStr.includes('/chapters') &&
           urlStr.includes(fileName);
  }, route => {
    const url = new URL(route.request().url());
    const chapterIdParam = url.searchParams.get('chapterId');
    const chapterId = parseInt(chapterIdParam || '0');
    const content = mockChapterContent[chapterId];

    const fulfill = () => {
      if (content) {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(content),
        });
      } else {
        route.fulfill({
          status: 404,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'Chapter not found' }),
        });
      }
    };

    if (delay > 0) {
      setTimeout(fulfill, delay);
    } else {
      fulfill();
    }
  });
};

const mockChapterContentError = async (page: Page, fileName: string) => {
  await page.route(url => {
    const urlStr = url.toString();
    return urlStr.includes('/api/library/reader/epub/chapter') &&
           !urlStr.includes('/chapters') &&
           urlStr.includes(fileName);
  }, route => {
    route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'Failed to load chapter content' }),
    });
  });
};

const openReaderPage = async (page: Page) => {
  await setAuthToken(page);
  await page.goto(`/#/reader?e2e=${Date.now()}`);
  await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });
  await page.waitForTimeout(1000);
};

const selectBook = async (page: Page, bookTitle: string) => {
  const bookSelect = page.locator('.reader-select mat-select').first();
  await expect(bookSelect).toBeVisible({ timeout: 10000 });
  await bookSelect.click({ force: true });

  const chaptersResponsePromise = page.waitForResponse(response =>
    response.url().includes('/chapters') && response.status() === 200
  );
  await page.locator('.cdk-overlay-container mat-option').filter({ hasText: bookTitle }).click();
  await chaptersResponsePromise;
  await page.waitForTimeout(500);
};

test.describe('Reader - Chapter Navigation', () => {
  test('Clicking chapter should load chapter content', async ({ page }) => {
    // Set up routes BEFORE navigation
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockChapterContentRoute(page, testBook.fileName);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Open chapter dropdown
    // Use exact label match to avoid "Matching chapters" dropdown
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    // Set up response wait BEFORE clicking
    const contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );
    await page.locator('.cdk-overlay-container mat-option').filter({ hasText: 'Chapter 1' }).click();
    await contentResponsePromise;
    await page.waitForTimeout(500);

    // Verify content is displayed
    const textWindow = page.locator('.text-window').first();
    await expect(textWindow).toBeVisible();
    await expect(textWindow).toContainText('Gaal Dornick');
  });

  test('Chapter dropdown should show all chapters', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Open chapter dropdown
    // Use exact label match to avoid "Matching chapters" dropdown
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    // Verify all chapters are shown
    const options = page.locator('.cdk-overlay-container mat-option');
    await expect(options).toHaveCount(3);
    await expect(options.nth(0)).toContainText('Chapter 1');
    await expect(options.nth(1)).toContainText('Chapter 2');
    await expect(options.nth(2)).toContainText('Chapter 3');
  });

  test('Selecting chapter from dropdown should load that chapter', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockChapterContentRoute(page, testBook.fileName);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Open chapter dropdown and select Chapter 1
    // Use exact label match to avoid "Matching chapters" dropdown
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    let contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );
    await page.locator('.cdk-overlay-container mat-option').filter({ hasText: 'Chapter 1' }).click();
    await contentResponsePromise;
    await page.waitForTimeout(500);

    await expect(page.locator('.text-window').first()).toContainText('Gaal Dornick');

    // Select different chapter
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );
    await page.locator('.cdk-overlay-container mat-option').filter({ hasText: 'Chapter 2' }).click();
    await contentResponsePromise;
    await page.waitForTimeout(500);

    await expect(page.locator('.text-window').first()).toContainText('Encyclopedia Foundation');
  });

  test('Empty state should show when no chapter selected', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Don't select a chapter - verify empty state
    const emptyState = page.locator('.state').filter({ hasText: 'Choose a chapter to see its text' });
    await expect(emptyState).toBeVisible({ timeout: 10000 });
  });

  test('Loading chapter content should show loading state', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    // Add delay to chapter content loading
    await mockChapterContentRoute(page, testBook.fileName, 1500);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Open chapter dropdown and select a chapter
    // Use exact label match to avoid "Matching chapters" dropdown
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    await page.locator('.cdk-overlay-container mat-option').first().click();

    // Verify loading indicator appears
    const loadingState = page.locator('.state').filter({ hasText: 'Loading' });
    await expect(loadingState).toBeVisible({ timeout: 5000 });

    // Wait for content to load
    await page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );

    // Verify loading indicator disappears and content is shown
    await expect(loadingState).not.toBeVisible({ timeout: 10000 });
    await expect(page.locator('.text-window').first()).toBeVisible();
  });

  test('Chapter without content should show error', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockChapterContentError(page, testBook.fileName);

    await openReaderPage(page);
    await selectBook(page, 'Foundation');

    // Open chapter dropdown and try to select a chapter
    // Use exact label match to avoid "Matching chapters" dropdown
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await chapterSelect.click({ force: true });
    await page.waitForTimeout(300);

    const errorResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 500
    );
    await page.locator('.cdk-overlay-container mat-option').first().click();
    await errorResponsePromise;

    // Verify error is displayed
    const errorMessage = page.locator('.state.error');
    await expect(errorMessage).toBeVisible({ timeout: 10000 });
  });
});

import { test, expect, type Page } from '@playwright/test';

/**
 * E2E Tests for Reader - Deep Linking
 *
 * Tests:
 * - Deep linking with fileName should auto-load book
 * - Deep linking with fileName and chapterId should load specific chapter
 * - Reading position should persist in localStorage
 * - Returning to book should restore last position
 * - Previously viewed list should update when opening book
 * - Query params should be optional
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
    characterCount: 108,
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
  await page.route(url => {
    const urlStr = url.toString();
    return urlStr.includes('/api/library/reader/epub/chapters') && urlStr.includes(fileName);
  }, route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ title: 'Foundation', chapters }),
    });
  });
};

const mockAllChapterContent = async (page: Page, fileName: string) => {
  await page.route(url => {
    const urlStr = url.toString();
    return urlStr.includes('/api/library/reader/epub/chapter') &&
           !urlStr.includes('/chapters') &&
           urlStr.includes(fileName);
  }, route => {
    const url = new URL(route.request().url());
    const chapterIdParam = url.searchParams.get('chapterId');
    const chapterId = parseInt(chapterIdParam || '0');
    const content = mockChapterContent[chapterId];

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
  });
};

test.describe('Reader - Deep Linking', () => {
  test('Deep linking with fileName should auto-load book', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const chaptersResponsePromise = page.waitForResponse(response =>
      response.url().includes('/chapters') && response.status() === 200
    );

    // Navigate with fileName parameter
    await page.goto(`/#/reader?fileName=${encodeURIComponent(testBook.fileName)}&e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for chapters to load
    await chaptersResponsePromise;
    await page.waitForTimeout(500);

    // Verify chapter dropdown is enabled (book was loaded)
    const chapterSelect = page.getByRole('combobox', { name: 'Chapter', exact: true });
    await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
  });

  test('Deep linking with fileName and chapterId should load specific chapter', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiters BEFORE navigation
    const chaptersResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapters') && response.status() === 200
    );
    const contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );

    // Navigate with fileName and chapterId parameters
    await page.goto(`/#/reader?fileName=${encodeURIComponent(testBook.fileName)}&chapterId=2&e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for chapters and content to load
    await chaptersResponsePromise;
    await contentResponsePromise;
    await page.waitForTimeout(500);

    // Verify chapter 2 content is displayed
    const textWindow = page.locator('.text-window').first();
    await expect(textWindow).toBeVisible({ timeout: 10000 });
    await expect(textWindow).toContainText('Encyclopedia Foundation');
  });

  test('Reading position should persist in localStorage', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );

    // Navigate to reader and select book/chapter
    await page.goto(`/#/reader?fileName=${encodeURIComponent(testBook.fileName)}&chapterId=2&e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for chapter content to load
    await contentResponsePromise;
    await page.waitForTimeout(1000);

    // Check localStorage for reading position
    const lastPositions = await page.evaluate(() => {
      const positions = localStorage.getItem('epub_last_positions');
      return positions ? JSON.parse(positions) : null;
    });

    // Position should be saved
    expect(lastPositions).toBeTruthy();
    // Key is based on readerKey which may be the fileName
    const hasPosition = Object.keys(lastPositions).some(key =>
      key.includes('foundation') || lastPositions[key]?.chapterId === 2
    );
    expect(hasPosition).toBeTruthy();
  });

  test('Previously viewed list should update when opening book', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const contentResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapter') &&
      !response.url().includes('/chapters') &&
      response.status() === 200
    );

    // Navigate to reader and open a book
    await page.goto(`/#/reader?fileName=${encodeURIComponent(testBook.fileName)}&chapterId=1&e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for content to load
    await contentResponsePromise;
    await page.waitForTimeout(1000);

    // Check previously viewed in localStorage
    const previouslyViewed = await page.evaluate(() => {
      return localStorage.getItem('epub_recent');
    });

    expect(previouslyViewed).toBeTruthy();
    const viewedBooks = JSON.parse(previouslyViewed!);
    expect(viewedBooks.length).toBeGreaterThanOrEqual(1);
    // Check that foundation.epub is in the list
    const hasFoundation = viewedBooks.some((entry: any) =>
      entry.fileName === testBook.fileName || entry.title === testBook.title
    );
    expect(hasFoundation).toBeTruthy();
  });

  test('Query params should be optional', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');

    // Navigate to reader without any params
    await page.goto(`/#/reader?e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for books to load
    await booksResponsePromise;
    await page.waitForTimeout(500);

    // Should show empty state or book selection prompt
    const emptyState = page.locator('.state').filter({ hasText: /Pick a book|start reading/i });
    await expect(emptyState).toBeVisible({ timeout: 10000 });

    // Book select should be available
    const bookSelect = page.locator('.reader-select mat-select').first();
    await expect(bookSelect).toBeVisible();
  });

  test('Returning to book should show in previously viewed', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);

    // Set up previously viewed in localStorage BEFORE navigation
    await page.addInitScript(({ fileName, title }) => {
      localStorage.setItem('epub_recent', JSON.stringify([
        { fileName, readerKey: fileName, title, updatedAt: new Date().toISOString() },
      ]));
    }, { fileName: testBook.fileName, title: testBook.title });

    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');

    // Navigate to reader
    await page.goto(`/#/reader?e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for books to load
    await booksResponsePromise;
    await page.waitForTimeout(1000);

    // Open the previously viewed dropdown
    const prevViewedSelect = page.locator('.reader-select mat-select').nth(1);
    await expect(prevViewedSelect).toBeEnabled({ timeout: 10000 });
    await prevViewedSelect.click({ force: true });

    // Verify Foundation appears in previously viewed
    const options = page.locator('.cdk-overlay-container mat-option');
    await expect(options.first()).toContainText('Foundation');
  });

  test('Deep linking with invalid chapterId should handle gracefully', async ({ page }) => {
    await mockReaderBooksRoute(page, [testBook]);
    await mockChaptersRoute(page, testBook.fileName, mockChapters);
    await mockAllChapterContent(page, testBook.fileName);
    await setAuthToken(page);

    // Set up response waiter BEFORE navigation
    const chaptersResponsePromise = page.waitForResponse(response =>
      response.url().includes('/api/library/reader/epub/chapters') && response.status() === 200
    );

    // Navigate with invalid chapter
    await page.goto(`/#/reader?fileName=${encodeURIComponent(testBook.fileName)}&chapterId=999&e2e=${Date.now()}`);
    await page.locator('app-dropbox-reader').waitFor({ state: 'visible', timeout: 10000 });

    // Wait for chapters to load
    await chaptersResponsePromise;
    await page.waitForTimeout(500);

    // The app should handle this gracefully - not crash
    await expect(page.locator('app-dropbox-reader')).toBeVisible();
  });
});

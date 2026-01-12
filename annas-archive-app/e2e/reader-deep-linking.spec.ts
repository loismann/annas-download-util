// import { test, expect, type Page } from '@playwright/test';

// /**
//  * E2E Tests for Reader - Deep Linking
//  *
//  * Tests:
//  * - Deep linking from library should auto-load book and chapter
//  * - Deep linking with chapter ID should load specific chapter
//  * - Deep linking with invalid book should show error
//  * - Deep linking with invalid chapter should show error
//  * - Reading position should persist between sessions
//  * - Returning to book should restore last position
//  */

// type LibraryReaderBook = {
//   fileName: string;
//   title: string;
//   authors: string[];
//   format: string;
//   readerEnabled: boolean;
// };

// type DropboxChapterContent = {
//   id: number;
//   title: string;
//   html: string;
//   text: string;
//   wordCount: number;
// };

// const ACCESS_CODE = process.env.E2E_ACCESS_CODE;
// if (!ACCESS_CODE) {
//   throw new Error('E2E_ACCESS_CODE is required to run reader tests.');
// }

// const testBook: LibraryReaderBook = {
//   fileName: 'foundation.epub',
//   title: 'Foundation',
//   authors: ['Isaac Asimov'],
//   format: 'EPUB',
//   readerEnabled: true,
// };

// const mockChapters = [
//   { id: 1, title: 'Chapter 1: The Psychohistorians', level: 0, wordCount: 2500, displayLabel: 'Chapter 1' },
//   { id: 2, title: 'Chapter 2: The Encyclopedists', level: 0, wordCount: 3200, displayLabel: 'Chapter 2' },
//   { id: 3, title: 'Chapter 3: The Mayors', level: 0, wordCount: 2800, displayLabel: 'Chapter 3' },
// ];

// const mockChapterContent: Record<number, DropboxChapterContent> = {
//   1: {
//     id: 1,
//     title: 'Chapter 1: The Psychohistorians',
//     html: '<p>His name was Gaal Dornick and he was just a country boy who had never seen Trantor before.</p>',
//     text: 'His name was Gaal Dornick and he was just a country boy who had never seen Trantor before.',
//     wordCount: 2500,
//   },
//   2: {
//     id: 2,
//     title: 'Chapter 2: The Encyclopedists',
//     html: '<p>The meeting of the Board of Trustees of the Encyclopedia Foundation took place in the Encyclopedia Building.</p>',
//     text: 'The meeting of the Board of Trustees of the Encyclopedia Foundation took place in the Encyclopedia Building.',
//     wordCount: 3200,
//   },
//   3: {
//     id: 3,
//     title: 'Chapter 3: The Mayors',
//     html: '<p>The mayor of Terminus City looked up with an air of annoyance.</p>',
//     text: 'The mayor of Terminus City looked up with an air of annoyance.',
//     wordCount: 2800,
//   },
// };

// const setAuthToken = async (page: Page) => {
//   await page.addInitScript((accessCode) => {
//     localStorage.setItem('auth_token', accessCode);
//     localStorage.setItem('auth_name', 'E2E User');
//     localStorage.setItem('auth_admin', 'true');
//   }, ACCESS_CODE);
// };

// const mockReaderBooksRoute = async (page: Page, books: LibraryReaderBook[]) => {
//   await page.route('**/api/library/reader/books**', route => {
//     route.fulfill({
//       status: 200,
//       contentType: 'application/json',
//       body: JSON.stringify(books),
//     });
//   });
// };

// const mockChaptersRoute = async (page: Page, fileName: string, chapters: any[]) => {
//   await page.route(`**/api/dropbox/epub/${encodeURIComponent(fileName)}/chapters**`, route => {
//     route.fulfill({
//       status: 200,
//       contentType: 'application/json',
//       body: JSON.stringify(chapters),
//     });
//   });
// };

// const mockChapterContentRoute = async (page: Page, fileName: string, chapterId: number, content: DropboxChapterContent) => {
//   await page.route(`**/api/dropbox/epub/${encodeURIComponent(fileName)}/chapters/${chapterId}/content**`, route => {
//     route.fulfill({
//       status: 200,
//       contentType: 'application/json',
//       body: JSON.stringify(content),
//     });
//   });
// };

// const mockAllChapterContent = async (page: Page, fileName: string) => {
//   for (const [id, content] of Object.entries(mockChapterContent)) {
//     await mockChapterContentRoute(page, fileName, parseInt(id), content);
//   }
// };

// test.describe('Reader - Deep Linking', () => {
//   test.beforeEach(async ({ page }) => {
//     await mockReaderBooksRoute(page, [testBook]);
//     await mockChaptersRoute(page, testBook.fileName, mockChapters);
//     await mockAllChapterContent(page, testBook.fileName);
//   });

//   test('Deep linking with book parameter should auto-load book', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate with book parameter
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for chapters to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Verify book is selected
//     const bookSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Select library book' }) });
//     await expect(bookSelect).toHaveValue(testBook.fileName);

//     // Verify chapters are loaded
//     const chapterSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//     await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
//   });

//   test('Deep linking with book and chapter should auto-load chapter', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate with book and chapter parameters
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&chapter=2&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for chapters to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Wait for chapter content to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters/2/content') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Verify chapter 2 is selected and content is displayed
//     const textWindow = page.locator('.text-window').first();
//     await expect(textWindow).toBeVisible({ timeout: 10000 });
//     await expect(textWindow).toContainText('Encyclopedia Foundation');
//   });

//   test('Deep linking with invalid book should show error', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Mock error for invalid book
//     await page.route('**/api/dropbox/epub/invalid-book.epub/chapters**', route => {
//       route.fulfill({
//         status: 404,
//         contentType: 'application/json',
//         body: JSON.stringify({ error: 'Book not found' }),
//       });
//     });

//     // Navigate with invalid book
//     await page.goto(`/#/reader?book=invalid-book.epub&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for error response
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters') && response.status() === 404,
//       { timeout: 10000 }
//     );

//     // Verify error is displayed
//     const errorMessage = page.locator('.state.error');
//     await expect(errorMessage).toBeVisible({ timeout: 10000 });
//   });

//   test('Deep linking with invalid chapter should handle gracefully', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Mock error for invalid chapter
//     await page.route(`**/api/dropbox/epub/${encodeURIComponent(testBook.fileName)}/chapters/999/content**`, route => {
//       route.fulfill({
//         status: 404,
//         contentType: 'application/json',
//         body: JSON.stringify({ error: 'Chapter not found' }),
//       });
//     });

//     // Navigate with invalid chapter
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&chapter=999&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for chapters to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // The app should handle this gracefully - either show error or fallback to first chapter
//     // Let's check that it doesn't crash
//     await expect(page.locator('app-dropbox-reader')).toBeVisible();
//   });

//   test('Reading position should persist in localStorage', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate to reader and select book/chapter
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&chapter=2&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for content to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters/2/content') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Wait for content to be visible
//     await expect(page.locator('.text-window').first()).toBeVisible({ timeout: 10000 });

//     // Check localStorage for reading position
//     const lastPosition = await page.evaluate(() => {
//       const positions = localStorage.getItem('reader_last_positions');
//       return positions ? JSON.parse(positions) : null;
//     });

//     // Position should be saved
//     expect(lastPosition).toBeTruthy();
//     expect(lastPosition[testBook.fileName]).toBeDefined();
//     expect(lastPosition[testBook.fileName].chapterId).toBe(2);
//   });

//   test('Returning to book should restore last position', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Set up initial position in localStorage
//     await page.addInitScript(({ fileName, chapterId }) => {
//       const positions: any = {};
//       positions[fileName] = {
//         chapterId: chapterId,
//         wordOffset: 0,
//         updatedAt: new Date().toISOString(),
//       };
//       localStorage.setItem('reader_last_positions', JSON.stringify(positions));
//     }, { fileName: testBook.fileName, chapterId: 3 });

//     // Navigate to reader with only book parameter
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for chapters to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Should automatically load chapter 3 from saved position
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters/3/content') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Verify chapter 3 content is displayed
//     const textWindow = page.locator('.text-window').first();
//     await expect(textWindow).toBeVisible({ timeout: 10000 });
//     await expect(textWindow).toContainText('mayor of Terminus City');
//   });

//   test('Previously viewed list should update when opening book', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate to reader and open a book
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&chapter=1&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for content to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters/1/content') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Check previously viewed in localStorage
//     const previouslyViewed = await page.evaluate(() => {
//       return localStorage.getItem('reader_previously_viewed');
//     });

//     expect(previouslyViewed).toBeTruthy();
//     const viewedBooks = JSON.parse(previouslyViewed!);
//     expect(viewedBooks).toHaveLength(1);
//     expect(viewedBooks[0].fileName).toBe(testBook.fileName);
//     expect(viewedBooks[0].title).toBe(testBook.title);
//   });

//   test('Query params should be optional', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate to reader without any params
//     await page.goto(`/#/reader?e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Should show empty state
//     const emptyState = page.locator('.state').filter({ hasText: /Pick a book|start reading/i });
//     await expect(emptyState).toBeVisible({ timeout: 10000 });

//     // Book select should be available
//     const bookSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Select library book' }) });
//     await expect(bookSelect).toBeVisible();
//   });

//   test('Navigating away and back should preserve state', async ({ page }) => {
//     const booksResponsePromise = page.waitForResponse('**/api/library/reader/books**');
//     await setAuthToken(page);

//     // Navigate to reader and select book/chapter
//     await page.goto(`/#/reader?book=${encodeURIComponent(testBook.fileName)}&chapter=2&e2e=${Date.now()}`);
//     await booksResponsePromise;
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for content to load
//     await page.waitForResponse(response =>
//       response.url().includes('/chapters/2/content') && response.status() === 200,
//       { timeout: 10000 }
//     );

//     // Verify content is displayed
//     await expect(page.locator('.text-window').first()).toContainText('Encyclopedia Foundation');

//     // Navigate to library
//     await page.goto(`/#/library?e2e=${Date.now()}`);
//     await expect(page.locator('app-library')).toBeVisible({ timeout: 10000 });

//     // Navigate back to reader
//     await page.goto(`/#/reader?e2e=${Date.now()}`);
//     await expect(page.locator('app-dropbox-reader')).toBeVisible({ timeout: 10000 });

//     // Wait for books to load
//     await page.waitForResponse('**/api/library/reader/books**');

//     // Verify previously viewed is populated
//     const prevViewedSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Previously viewed' }) });
//     await expect(prevViewedSelect).toBeEnabled({ timeout: 10000 });
//   });
// });

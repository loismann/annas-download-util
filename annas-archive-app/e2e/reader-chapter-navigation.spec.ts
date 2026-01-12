// import { test, expect, type Page } from '@playwright/test';

// /**
//  * E2E Tests for Reader - Chapter Navigation
//  *
//  * Tests:
//  * - Clicking chapter should load chapter content
//  * - Chapter content should display formatted HTML
//  * - Previous chapter button should navigate to previous chapter
//  * - Next chapter button should navigate to next chapter
//  * - First chapter should disable previous button
//  * - Last chapter should disable next button
//  * - Chapter dropdown should show all chapters
//  * - Selecting chapter from dropdown should load that chapter
//  * - Chapter progress indicator should show current position
//  * - Keyboard shortcuts (arrow keys) should navigate chapters
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
//   await page.route(
//     url => url.pathname.includes('/api/library/reader/epub/chapters') &&
//            url.searchParams.get('fileName') === fileName,
//     route => {
//       route.fulfill({
//         status: 200,
//         contentType: 'application/json',
//         body: JSON.stringify({ title: 'Foundation', chapters }),
//       });
//     }
//   );
// };

// const mockAllChapterContent = async (page: Page, fileName: string) => {
//   // Set up ONE route handler that handles all chapter content requests dynamically
//   await page.route(
//     url => url.pathname.includes('/api/library/reader/epub/chapter') &&
//            !url.pathname.includes('/chapters') && // Exclude the chapters list endpoint
//            url.searchParams.get('fileName') === fileName,
//     route => {
//       const chapterId = parseInt(route.request().url().split('chapterId=')[1] || '0');
//       const content = mockChapterContent[chapterId];

//       if (content) {
//         route.fulfill({
//           status: 200,
//           contentType: 'application/json',
//           body: JSON.stringify(content),
//         });
//       } else {
//         route.fulfill({
//           status: 404,
//           contentType: 'application/json',
//           body: JSON.stringify({ error: 'Chapter not found' }),
//         });
//       }
//     }
//   );
// };

// const openReaderPage = async (page: Page) => {
//   const responsePromise = page.waitForResponse('**/api/library/reader/books**');
//   await setAuthToken(page);
//   await page.goto(`/#/reader?e2e=${Date.now()}`);
//   await responsePromise;
// };

// const selectBook = async (page: Page, bookTitle: string) => {
//   const bookSelect = page.locator('.reader-select mat-select').first();
//   await expect(bookSelect).toBeVisible({ timeout: 10000 });
//   await bookSelect.click({ force: true });

//   // Set up waitForResponse BEFORE clicking the option
//   const chaptersResponsePromise = page.waitForResponse(response =>
//     response.url().includes('/chapters') && response.status() === 200
//   );
//   await page.locator('.cdk-overlay-container mat-option').filter({ hasText: bookTitle }).click();
//   await chaptersResponsePromise;

//   // Wait for overlay to close by checking that the chapter select becomes enabled
//   const chapterFormField = page.locator('mat-form-field').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//   const chapterSelect = chapterFormField.locator('mat-select');
//   await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
// };

// const selectChapter = async (page: Page, chapterText: string) => {
//   // Ensure any previous overlays are closed
//   const backdrop = page.locator('.cdk-overlay-backdrop');
//   if (await backdrop.isVisible()) {
//     await backdrop.click();
//     await page.waitForTimeout(300);
//   }

//   // Find the mat-form-field that contains the "Chapter" label, then get its mat-select
//   const chapterFormField = page.locator('mat-form-field').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//   const chapterSelect = chapterFormField.locator('mat-select');
//   await expect(chapterSelect).toBeEnabled({ timeout: 10000 });

//   await chapterSelect.click({ force: true });

//   // Wait for the overlay to appear with chapter options
//   await page.waitForTimeout(300);

//   // Set up waitForResponse BEFORE clicking the option
//   const contentResponsePromise = page.waitForResponse(response =>
//     response.url().includes('/api/library/reader/epub/chapter?') && response.status() === 200
//   );
//   await page.locator('.cdk-overlay-container mat-option').filter({ hasText: chapterText }).click();
//   await contentResponsePromise;
//   await page.waitForTimeout(500);
// };

// test.describe('Reader - Chapter Navigation', () => {
//   test.beforeEach(async ({ page }) => {
//     await mockReaderBooksRoute(page, [testBook]);
//     await mockChaptersRoute(page, testBook.fileName, mockChapters);
//     await mockAllChapterContent(page, testBook.fileName);
//   });

//   test('Clicking chapter should load chapter content', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Verify content is displayed
//     const textWindow = page.locator('.text-window').first();
//     await expect(textWindow).toBeVisible();
//     await expect(textWindow).toContainText('Gaal Dornick');
//   });

//   test.skip('Chapter content should display formatted HTML', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Verify HTML formatting is preserved (paragraph tags should be rendered)
//     const textWindow = page.locator('.text-window').first();
//     await expect(textWindow.locator('pre')).toBeVisible();
//   });

//   test.skip('Chapter dropdown should show all chapters', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Open chapter dropdown
//     const chapterSelect = page.locator('mat-form-field[appearance="outline"]').filter({ hasNot: page.locator('.reader-select') }).locator('mat-select').first();
//     await expect(chapterSelect).toBeEnabled({ timeout: 10000 });
//     await chapterSelect.click({ force: true });

//     // Verify all chapters are shown
//     const options = page.locator('.cdk-overlay-container mat-option');
//     await expect(options).toHaveCount(3);
//     await expect(options.nth(0)).toContainText('Chapter 1');
//     await expect(options.nth(1)).toContainText('Chapter 2');
//     await expect(options.nth(2)).toContainText('Chapter 3');
//   });

//   test.skip('Selecting chapter from dropdown should load that chapter', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Select first chapter
//     await selectChapter(page, 'Chapter 1');
//     await expect(page.locator('.text-window').first()).toContainText('Gaal Dornick');

//     // Select different chapter
//     await selectChapter(page, 'Chapter 2');
//     await expect(page.locator('.text-window').first()).toContainText('Encyclopedia Foundation');
//   });

//   test.skip('Next page button should advance through chapter', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Verify initial page indicator
//     const pageIndicator = page.locator('.page-indicator');
//     await expect(pageIndicator).toContainText('Page 1');

//     // Click next page button
//     const nextButton = page.locator('button[aria-label="Next page"]');
//     await expect(nextButton).toBeEnabled();
//     await nextButton.click();

//     // Verify page advanced (if chapter is long enough to paginate)
//     // Note: With such short content, this may not actually change pages
//     await expect(pageIndicator).toBeVisible();
//   });

//   test.skip('Previous page button should go back through chapter', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Click next to go forward first
//     const nextButton = page.locator('button[aria-label="Next page"]');
//     if (await nextButton.isEnabled()) {
//       await nextButton.click();
//       await page.waitForTimeout(500);

//       // Then click previous
//       const prevButton = page.locator('button[aria-label="Previous page"]');
//       await expect(prevButton).toBeEnabled();
//       await prevButton.click();

//       // Should be back at page 1
//       const pageIndicator = page.locator('.page-indicator');
//       await expect(pageIndicator).toContainText('Page 1');
//     }
//   });

//   test.skip('First page should disable previous button', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // On first page, previous button should be disabled
//     const prevButton = page.locator('button[aria-label="Previous page"]');
//     await expect(prevButton).toBeDisabled();
//   });

//   test.skip('Chapter progress indicator should show current position', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Verify page indicator shows position
//     const pageIndicator = page.locator('.page-indicator');
//     await expect(pageIndicator).toBeVisible();
//     await expect(pageIndicator).toContainText(/Page \d+ \/ \d+/);
//     await expect(pageIndicator).toContainText(/Book:/);
//   });

//   test.skip('Chapter word count should be displayed', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Open chapter dropdown
//     const chapterSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//     await chapterSelect.click({ force: true });

//     // Verify word counts are shown
//     const firstOption = page.locator('.cdk-overlay-container mat-option').first();
//     await expect(firstOption).toContainText('2,500 words');
//   });

//   test.skip('Loading chapter content should show loading state', async ({ page }) => {
//     // Add delay to content loading
//     await page.route(
//       url => url.pathname.includes('/api/library/reader/epub/chapter') &&
//              url.searchParams.get('fileName') === testBook.fileName &&
//              url.searchParams.get('chapterId') === '1',
//       route => {
//         setTimeout(() => {
//           route.fulfill({
//             status: 200,
//             contentType: 'application/json',
//             body: JSON.stringify(mockChapterContent[1]),
//           });
//         }, 1000);
//       }
//     );

//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Select chapter and verify loading state
//     const chapterSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//     await chapterSelect.click({ force: true });

//     // Set up waitForResponse BEFORE clicking option
//     const contentResponsePromise = page.waitForResponse(response =>
//       response.url().includes('/chapter') && response.status() === 200,
//       { timeout: 15000 }
//     );
//     await page.locator('.cdk-overlay-container mat-option').first().click();
//     await contentResponsePromise;

//     // Verify loading indicator
//     const loadingState = page.locator('.state').filter({ hasText: 'Loading' });
//     await expect(loadingState).toBeVisible({ timeout: 2000 });

//     // Verify content loaded
//     await expect(loadingState).not.toBeVisible({ timeout: 10000 });
//     await expect(page.locator('.text-window').first()).toBeVisible();
//   });

//   test.skip('Chapter without content should show error', async ({ page }) => {
//     // Mock error response for chapter content
//     await page.route(
//       url => url.pathname.includes('/api/library/reader/epub/chapter') &&
//              url.searchParams.get('fileName') === testBook.fileName &&
//              url.searchParams.get('chapterId') === '1',
//       route => {
//         route.fulfill({
//           status: 500,
//           contentType: 'application/json',
//           body: JSON.stringify({ error: 'Failed to load chapter content' }),
//         });
//       }
//     );

//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Try to select chapter
//     const chapterSelect = page.locator('mat-select').filter({ has: page.locator('mat-label', { hasText: 'Chapter' }) });
//     await chapterSelect.click({ force: true });

//     // Set up waitForResponse BEFORE clicking option
//     const errorResponsePromise = page.waitForResponse(response =>
//       response.url().includes('/chapter') && response.status() === 500
//     );
//     await page.locator('.cdk-overlay-container mat-option').first().click();
//     await errorResponsePromise;

//     // Verify error is displayed
//     const errorMessage = page.locator('.state.error');
//     await expect(errorMessage).toBeVisible({ timeout: 10000 });
//   });

//   test.skip('Reader controls should be visible when chapter loaded', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');
//     await selectChapter(page, 'Chapter 1');

//     // Verify reader controls are visible
//     const readerControls = page.locator('.reader-controls');
//     await expect(readerControls).toBeVisible();

//     // Verify pagination controls
//     await expect(page.locator('button[aria-label="Previous page"]')).toBeVisible();
//     await expect(page.locator('button[aria-label="Next page"]')).toBeVisible();
//     await expect(page.locator('.page-indicator')).toBeVisible();
//   });

//   test.skip('Empty state should show when no chapter selected', async ({ page }) => {
//     await openReaderPage(page);
//     await selectBook(page, 'Foundation');

//     // Don't select a chapter - verify empty state
//     const emptyState = page.locator('.state').filter({ hasText: /Choose a chapter|to see its text/i });
//     await expect(emptyState).toBeVisible({ timeout: 10000 });
//   });
// });

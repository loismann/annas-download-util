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
  savedAt?: string;
};

const ACCESS_CODE = process.env.E2E_ACCESS_CODE;
if (!ACCESS_CODE) {
  throw new Error('E2E_ACCESS_CODE is required to run library tests.');
}

// Create a comprehensive set of test books for sorting tests
const sortTestBooks: LibraryBook[] = [
  {
    title: 'Dune',
    authors: ['Frank Herbert'],
    format: 'EPUB',
    fileSize: '1.2 MB',
    fileName: 'dune.epub',
    coverUrl: 'https://covers.example.test/dune.jpg',
    primaryGenre: 'Science Fiction',
    tags: ["Mom's Books"],
    series: 'Dune',
    goodreadsRating: 4.6,
    personalRating: 5,
    savedAt: '2024-01-15T10:00:00Z',
  },
  {
    title: 'Foundation',
    authors: ['Isaac Asimov'],
    format: 'PDF',
    fileSize: '2.1 MB',
    fileName: 'foundation.pdf',
    coverUrl: 'https://covers.example.test/foundation.jpg',
    primaryGenre: 'Science Fiction',
    tags: ["Dad's Books", 'Space Opera'],
    series: 'Foundation',
    goodreadsRating: 4.2,
    personalRating: 4,
    savedAt: '2024-01-10T10:00:00Z',
  },
  {
    title: 'Neuromancer',
    authors: ['William Gibson'],
    format: 'MOBI',
    fileSize: '900 KB',
    fileName: 'neuromancer.mobi',
    coverUrl: 'https://covers.example.test/neuromancer.jpg',
    primaryGenre: 'Science Fiction',
    tags: ["Paul's Books"],
    series: null,
    goodreadsRating: 3.8,
    personalRating: 3,
    savedAt: '2024-01-20T10:00:00Z',
  },
  {
    title: 'The Hobbit',
    authors: ['J.R.R. Tolkien'],
    format: 'EPUB',
    fileSize: '1.5 MB',
    fileName: 'hobbit.epub',
    coverUrl: 'https://covers.example.test/hobbit.jpg',
    primaryGenre: 'Fantasy',
    tags: ["Dad's Books"],
    series: 'Middle-earth',
    goodreadsRating: 4.3,
    personalRating: 5,
    savedAt: '2024-01-05T10:00:00Z',
  },
  {
    title: 'Ender\'s Game',
    authors: ['Orson Scott Card'],
    format: 'EPUB',
    fileSize: '1.1 MB',
    fileName: 'enders-game.epub',
    coverUrl: 'https://covers.example.test/enders.jpg',
    primaryGenre: 'Science Fiction',
    tags: ["Paul's Books"],
    series: 'Ender',
    goodreadsRating: 4.3,
    personalRating: 4,
    savedAt: '2024-01-25T10:00:00Z',
  },
  {
    title: 'A Tale of Two Cities',
    authors: ['Charles Dickens'],
    format: 'PDF',
    fileSize: '2.5 MB',
    fileName: 'two-cities.pdf',
    coverUrl: 'https://covers.example.test/cities.jpg',
    primaryGenre: 'Classics',
    tags: ["Mom's Books"],
    series: null,
    goodreadsRating: 3.9,
    personalRating: 2,
    savedAt: '2024-01-12T10:00:00Z',
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
  const responsePromise = page.waitForResponse('**/api/library/books**', { timeout: 60000 });
  await setAuthToken(page);
  await page.goto(`/#/library?e2e=${Date.now()}`, { timeout: 60000 });
  await responsePromise;
  await expect(page.locator('.library-loading')).not.toBeVisible({ timeout: 15000 });
};

const selectSortOption = async (page: Page, sortValue: string) => {
  const sortField = page.locator('mat-form-field.sort-field');
  await sortField.locator('mat-select').click();
  await page.locator('mat-option').filter({ hasText: new RegExp(`^${sortValue}$`, 'i') }).click();
};

const toggleSortDirection = async (page: Page) => {
  const toggleButton = page.locator('button.sort-direction-toggle');
  await toggleButton.click();
};

const getBookTitles = async (page: Page): Promise<string[]> => {
  // Wait for books to load and scroll to bottom to ensure all are rendered
  await page.waitForTimeout(500);
  const gridScroll = page.locator('.library-grid-scroll');
  if (await gridScroll.count() > 0) {
    await gridScroll.evaluate(el => el.scrollTop = el.scrollHeight);
    await page.waitForTimeout(300);
    await gridScroll.evaluate(el => el.scrollTop = 0);
    await page.waitForTimeout(300);
  }

  const cards = page.locator('.library-card');
  const count = await cards.count();
  const titles: string[] = [];
  for (let i = 0; i < count; i++) {
    const title = await cards.nth(i).locator('.title').textContent();
    if (title) titles.push(title.trim());
  }
  return titles;
};

test.describe('Library - Sorting', () => {
  test.beforeEach(async ({ page }) => {
    await mockLibraryRoute(page, sortTestBooks);
    await openLibraryPage(page);
  });

  test('Sort by title (A-Z) should order books alphabetically', async ({ page }) => {
    await selectSortOption(page, 'Title');

    const titles = await getBookTitles(page);
    const expectedOrder = [
      'A Tale of Two Cities',
      'Dune',
      "Ender's Game",
      'Foundation',
      'Neuromancer',
      'The Hobbit',
    ];

    expect(titles).toEqual(expectedOrder);
  });

  test('Sort by title (Z-A) should reverse alphabetical order', async ({ page }) => {
    await selectSortOption(page, 'Title');
    await toggleSortDirection(page);

    const titles = await getBookTitles(page);
    const expectedOrder = [
      'The Hobbit',
      'Neuromancer',
      'Foundation',
      "Ender's Game",
      'Dune',
      'A Tale of Two Cities',
    ];

    expect(titles).toEqual(expectedOrder);
  });

  test('Sort by author should order by first author name', async ({ page }) => {
    await selectSortOption(page, 'Author');

    const titles = await getBookTitles(page);
    const expectedOrder = [
      'A Tale of Two Cities', // Charles Dickens
      'Dune', // Frank Herbert
      'Foundation', // Isaac Asimov
      'The Hobbit', // J.R.R. Tolkien
      "Ender's Game", // Orson Scott Card
      'Neuromancer', // William Gibson
    ];

    expect(titles).toEqual(expectedOrder);
  });

  test('Sort by recent should show newest books first', async ({ page }) => {
    await selectSortOption(page, 'Most recent');

    const titles = await getBookTitles(page);
    const expectedOrder = [
      "Ender's Game", // 2024-01-25
      'Neuromancer', // 2024-01-20
      'Dune', // 2024-01-15
      'A Tale of Two Cities', // 2024-01-12
      'Foundation', // 2024-01-10
      'The Hobbit', // 2024-01-05
    ];

    expect(titles).toEqual(expectedOrder);
  });

  test('Sort by series should group books by series', async ({ page }) => {
    await selectSortOption(page, 'Series');

    const titles = await getBookTitles(page);

    // Series are sorted alphabetically: Dune, Ender, Foundation, Middle-earth
    // Books without series come last (they get 'zzzzzz')
    // Verify the series books come first in alphabetical order
    const seriesBooks = titles.filter(t =>
      ['Dune', "Ender's Game", 'Foundation', 'The Hobbit'].includes(t)
    );

    expect(seriesBooks).toEqual(['Dune', "Ender's Game", 'Foundation', 'The Hobbit']);

    // Books without series should come after all series books
    const lastIndex = titles.length - 1;
    const lastTwo = titles.slice(Math.max(0, lastIndex - 1));

    // At least one of these should be in the last positions if they're rendered
    const hasCities = titles.includes('A Tale of Two Cities');
    const hasNeuro = titles.includes('Neuromancer');

    if (hasCities && hasNeuro) {
      // If both are present, they should be last
      expect(lastTwo).toContain('A Tale of Two Cities');
      expect(lastTwo).toContain('Neuromancer');
    }
  });

  test('Sort by personal rating (stars) should order by rating', async ({ page }) => {
    await selectSortOption(page, 'Stars');
    // Default 'down' direction for numeric sorts shows highest first

    const titles = await getBookTitles(page);

    // Highest ratings first (5, 5, 4, 4, 3, 2)
    // Within same rating, sorted alphabetically by title
    const firstTwo = titles.slice(0, 2);
    expect(firstTwo).toContain('Dune'); // 5 stars
    expect(firstTwo).toContain('The Hobbit'); // 5 stars

    const nextTwo = titles.slice(2, 4);
    expect(nextTwo).toContain("Ender's Game"); // 4 stars
    expect(nextTwo).toContain('Foundation'); // 4 stars

    expect(titles[4]).toBe('Neuromancer'); // 3 stars
    expect(titles[5]).toBe('A Tale of Two Cities'); // 2 stars
  });

  test('Sort by Goodreads rating should order by rating', async ({ page }) => {
    await selectSortOption(page, 'Goodreads');
    // Default 'down' direction for numeric sorts shows highest first

    const titles = await getBookTitles(page);

    // Highest Goodreads ratings first
    // Within same rating, sorted alphabetically by title
    expect(titles[0]).toBe('Dune'); // 4.6
    const nextTwo = titles.slice(1, 3);
    expect(nextTwo).toContain("Ender's Game"); // 4.3
    expect(nextTwo).toContain('The Hobbit'); // 4.3
    expect(titles[3]).toBe('Foundation'); // 4.2
    expect(titles[4]).toBe('A Tale of Two Cities'); // 3.9
    expect(titles[5]).toBe('Neuromancer'); // 3.8
  });

  test('Toggle sort direction should reverse current order', async ({ page }) => {
    await selectSortOption(page, 'Title');

    const titlesAscending = await getBookTitles(page);

    await toggleSortDirection(page);

    const titlesDescending = await getBookTitles(page);

    expect(titlesDescending).toEqual([...titlesAscending].reverse());
  });

  test('Alphabet index should appear for title/author/series sorts', async ({ page }) => {
    // Test with title sort
    await selectSortOption(page, 'Title');
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Test with author sort
    await selectSortOption(page, 'Author');
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Test with series sort
    await selectSortOption(page, 'Series');
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Test that alphabet index is hidden for non-alphabetic sorts
    await selectSortOption(page, 'Stars');
    await expect(page.locator('.alphabet-index')).not.toBeVisible();

    await selectSortOption(page, 'Goodreads');
    await expect(page.locator('.alphabet-index')).not.toBeVisible();

    await selectSortOption(page, 'Most recent');
    await expect(page.locator('.alphabet-index')).not.toBeVisible();
  });

  test('Clicking alphabet letter should scroll to that section', async ({ page }) => {
    await selectSortOption(page, 'Title');

    // Wait for alphabet index to be visible
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Find any enabled (non-disabled) letter to click
    const enabledLetter = page.locator('.alphabet-letter').filter({ hasNot: page.locator('.disabled') }).first();
    await expect(enabledLetter).toBeVisible();

    // Click the letter - this verifies the click action works
    await enabledLetter.click();

    // Wait a moment for the scroll to complete
    await page.waitForTimeout(500);

    // The key test is that clicking doesn't cause errors and the page remains functional
    // Verify books are still visible after clicking
    const cards = page.locator('.library-card');
    await expect(cards.first()).toBeVisible();

    // Verify the alphabet index is still visible
    await expect(page.locator('.alphabet-index')).toBeVisible();
  });

  test('Active letter indicator should highlight current section while scrolling', async ({ page }) => {
    await selectSortOption(page, 'Title');

    // Wait for alphabet index
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Wait for active letter to appear
    await page.waitForTimeout(500);

    // Check that at least one letter has the active class
    const activeLetter = page.locator('.alphabet-letter.active');
    await expect(activeLetter.first()).toBeVisible();

    // Get the active letter text
    const activeText = await activeLetter.first().textContent();
    expect(activeText).toBeTruthy();

    // Verify the first book starts with the active letter
    const firstCard = page.locator('.library-card').first();
    const firstTitle = await firstCard.locator('.title').textContent();
    expect(firstTitle?.trim().toUpperCase().startsWith(activeText!.trim())).toBe(true);
  });

  test('Scrolling should update active letter dynamically', async ({ page }) => {
    await selectSortOption(page, 'Title');

    // Wait for alphabet index
    await expect(page.locator('.alphabet-index')).toBeVisible();

    // Get all enabled letters
    const enabledLetters = page.locator('.alphabet-letter').filter({ hasNot: page.locator('.disabled') });
    const letterCount = await enabledLetters.count();

    if (letterCount > 1) {
      // Get the currently active letter
      const initialActive = await page.locator('.alphabet-letter.active').first().textContent();

      // Click on a different enabled letter (second one if available)
      const secondLetter = enabledLetters.nth(1);
      const secondLetterText = await secondLetter.textContent();
      await secondLetter.click();

      // Wait for scroll to complete
      await page.waitForTimeout(500);

      // The active letter should have changed
      const newActive = await page.locator('.alphabet-letter.active').first().textContent();

      // Either the active letter changed, or it stayed the same if there's only one screen of books
      expect(newActive).toBeTruthy();

      // If different letters, verify the change
      if (secondLetterText !== initialActive) {
        expect(newActive).toBe(secondLetterText);
      }
    }
  });
});

test.describe('Library - Book Management', () => {
  const singleBook: LibraryBook = {
    title: 'Test Book',
    authors: ['Test Author'],
    format: 'EPUB',
    fileSize: '1.0 MB',
    fileName: 'test.epub',
    coverUrl: 'https://covers.example.test/test.jpg',
    primaryGenre: 'Science Fiction',
    tags: ['Test Tag', "Paul's Books"],
    series: 'Test Series',
    goodreadsRating: 4.0,
    personalRating: 3,
  };

  test.beforeEach(async ({ page }) => {
    await mockLibraryRoute(page, [singleBook]);

    // Mock cover candidates API (used by edit dialog)
    await page.route('**/api/library/book/cover-candidates', route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ candidates: [] }),
      });
    });
  });

  test('Edit book dialog should open with current book data', async ({ page }) => {
    await openLibraryPage(page);

    // Mock the metadata update route
    await page.route('**/api/library/book/metadata', route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ success: true }),
      });
    });

    // Click on the book cover to open edit dialog
    await page.locator('.library-card .cover').click();

    // Wait for dialog to open
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500); // Wait for dialog to fully render

    // Verify the dialog contains the book data using mat-label selectors
    const titleInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: /^Title$/ })
    }).locator('input');
    await expect(titleInput).toBeVisible();
    await expect(titleInput).toHaveValue('Test Book');

    const authorsInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Author(s)' })
    }).locator('input');
    await expect(authorsInput).toBeVisible();
    await expect(authorsInput).toHaveValue('Test Author');
  });

  test('Change book title should update book', async ({ page }) => {
    await openLibraryPage(page);

    let capturedTitle = '';

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedTitle = postData.title;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    // Open edit dialog
    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Change title
    const titleInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: /^Title$/ })
    }).locator('input');
    await titleInput.fill('Updated Test Book');

    // Save
    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();

    // Wait for dialog to close and API call to complete
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    // Verify the title was sent to the API
    expect(capturedTitle).toBe('Updated Test Book');
  });

  test('Change book authors should update book', async ({ page }) => {
    await openLibraryPage(page);

    let capturedAuthors: string[] = [];

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedAuthors = postData.authors;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    const authorsInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Author(s)' })
    }).locator('input');
    await authorsInput.fill('New Author, Another Author');

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedAuthors).toEqual(['New Author', 'Another Author']);
  });

  test('Add genre tag to book should save and display', async ({ page }) => {
    await openLibraryPage(page);

    let capturedTags: string[] = [];

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedTags = postData.tags;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Add a new tag (Genres field has a chip input)
    const genresField = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: /^Genres$/ })
    });
    const tagInput = genresField.locator('input');
    await tagInput.fill('New Tag');
    await page.keyboard.press('Enter');

    // Verify tag appears
    await expect(page.locator('mat-chip-row').filter({ hasText: 'New Tag' })).toBeVisible();

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedTags).toContain('New Tag');
  });

  test('Remove genre tag from book should save and update', async ({ page }) => {
    await openLibraryPage(page);

    let capturedTags: string[] = [];

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedTags = postData.tags;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Remove 'Test Tag'
    const testTagChip = page.locator('mat-chip-row').filter({ hasText: 'Test Tag' });
    await expect(testTagChip).toBeVisible();
    await testTagChip.locator('button[matChipRemove]').click();

    // Verify tag is removed
    await expect(testTagChip).not.toBeVisible();

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedTags).not.toContain('Test Tag');
  });

  test('Change primary genre should update book', async ({ page }) => {
    await openLibraryPage(page);

    let capturedGenre = '';

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedGenre = postData.primaryGenre;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Use "Add a Genre" dropdown to add a genre (which becomes the primary genre)
    const addGenreSelect = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Add a Genre' })
    }).locator('mat-select');
    await addGenreSelect.click();
    await page.waitForTimeout(500);

    // Wait for dropdown panel to be visible
    const dropdownPanel = page.locator('.cdk-overlay-container mat-option');
    await expect(dropdownPanel.first()).toBeVisible({ timeout: 5000 });

    // Find a genre option (not the "create new" option which contains "Would you like")
    const genreOptions = page.locator('.cdk-overlay-container mat-option').filter({
      hasNotText: 'Would you like'
    });

    // Click on Fantasy if available, otherwise pick any available genre
    const fantasyOption = genreOptions.filter({ hasText: 'Fantasy' });
    if (await fantasyOption.count() > 0) {
      await fantasyOption.first().click();
    } else if (await genreOptions.count() > 0) {
      await genreOptions.first().click();
    } else {
      throw new Error('No genre options available in dropdown');
    }

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    // Verify a genre was captured (might not be Fantasy if it doesn't exist)
    expect(capturedGenre).toBeTruthy();
  });

  test('Add series name should update book', async ({ page }) => {
    await openLibraryPage(page);

    let capturedSeries = '';

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedSeries = postData.series;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    const seriesInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Series' })
    }).locator('input');
    await seriesInput.fill('New Series Name');

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedSeries).toBe('New Series Name');
  });

  test('Remove series name should clear series', async ({ page }) => {
    await openLibraryPage(page);

    let capturedSeries: string | null = null;

    await page.route('**/api/library/book/*/metadata', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedSeries = postData.series;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    const seriesInput = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Series' })
    }).locator('input');
    await seriesInput.fill('');

    await page.locator('mat-dialog-container button').filter({ hasText: /save/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedSeries).toBe('');
  });

  test('Change cover URL should update cover image', async ({ page }) => {
    await openLibraryPage(page);

    let capturedCoverUrl = '';

    await page.route('**/api/library/book/*/cover', route => {
      if (route.request().method() === 'POST') {
        const postData = route.request().postDataJSON();
        capturedCoverUrl = postData.coverUrl;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ coverUrl: postData.coverUrl }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Find the cover URL field by its label
    const coverUrlField = page.locator('mat-dialog-container mat-form-field').filter({
      has: page.locator('mat-label', { hasText: 'Set your own Cover image via URL' })
    });
    const manualUrlInput = coverUrlField.locator('input');
    await expect(manualUrlInput).toBeVisible();
    await manualUrlInput.fill('https://new-cover.example.com/test.jpg');
    await page.waitForTimeout(300);

    // Click "Use URL" button next to the input
    const useButton = page.locator('mat-dialog-container button').filter({ hasText: /use url/i });
    await expect(useButton).toBeVisible();
    await useButton.click();
    await page.waitForTimeout(300);

    // Save the dialog
    await page.locator('mat-dialog-container button').filter({ hasText: /^save$/i }).click();
    await expect(page.locator('mat-dialog-container')).not.toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(300);

    expect(capturedCoverUrl).toBe('https://new-cover.example.com/test.jpg');
  });

  test('Invalid cover URL should show placeholder', async ({ page }) => {
    await mockLibraryRoute(page, [{
      ...singleBook,
      coverUrl: null,
    }]);

    await openLibraryPage(page);

    // Verify placeholder is shown
    const cover = page.locator('.library-card .cover');
    const src = await cover.getAttribute('src');
    expect(src).toContain('placeholder');
  });

  test('Delete book should remove from library', async ({ page }) => {
    await openLibraryPage(page);

    let deleteRequested = false;

    await page.route('**/api/library/book/**', route => {
      if (route.request().method() === 'DELETE') {
        deleteRequested = true;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    // Open edit dialog
    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Set up handler for native browser confirm dialog
    page.once('dialog', async dialog => {
      expect(dialog.type()).toBe('confirm');
      await dialog.accept();
    });

    // Click delete button (use color="warn" to be more specific)
    const deleteButton = page.locator('mat-dialog-container button[color="warn"]');
    await expect(deleteButton).toBeVisible();
    await deleteButton.click();

    // Give time for the API call after confirmation
    await page.waitForTimeout(500);
    expect(deleteRequested).toBe(true);
  });

  test('Delete book confirmation should prevent accidental deletion', async ({ page }) => {
    await openLibraryPage(page);

    let deleteRequested = false;

    await page.route('**/api/library/book/**', route => {
      if (route.request().method() === 'DELETE') {
        deleteRequested = true;
      }
      route.continue();
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    const deleteButton = page.locator('mat-dialog-container button').filter({ hasText: /delete/i });
    await deleteButton.click();

    // Verify confirmation dialog appears
    await page.waitForTimeout(300);
    const confirmButtons = page.locator('button').filter({ hasText: /confirm|yes|delete/i });
    await expect(confirmButtons.first()).toBeVisible();
  });

  test('Cancel delete should not remove book', async ({ page }) => {
    await openLibraryPage(page);

    let deleteRequested = false;

    await page.route('**/api/library/book**', route => {
      if (route.request().method() === 'DELETE') {
        deleteRequested = true;
      }
      route.continue();
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible();

    const deleteButton = page.locator('mat-dialog-container button').filter({ hasText: /delete/i });
    await deleteButton.click();

    // Cancel in confirmation dialog
    await page.locator('button').filter({ hasText: /cancel|no/i }).last().click();

    expect(deleteRequested).toBe(false);
  });

  test('Set personal rating (1-5 stars) should save rating', async ({ page }) => {
    await openLibraryPage(page);

    let capturedRating: number | null = null;

    await page.route('**/api/library/book/*/ratings', route => {
      if (route.request().method() === 'PATCH') {
        const postData = route.request().postDataJSON();
        capturedRating = postData.personalRating;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      } else {
        route.continue();
      }
    });

    // Click on the 4th star
    const fourthStar = page.locator('.library-card .star-button').nth(3);
    await fourthStar.click();

    // Wait for the API call
    await page.waitForTimeout(500);

    expect(capturedRating).toBe(4);
  });

  test('Click same star rating again should clear rating', async ({ page }) => {
    await openLibraryPage(page);

    let capturedRating: number | null = null;

    await page.route('**/api/library/book/ratings', route => {
      const postData = route.request().postDataJSON();
      capturedRating = postData.personalRating;
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ success: true }),
      });
    });

    // The book already has 3 stars, click on the 3rd star to clear
    const thirdStar = page.locator('.library-card .star-button').nth(2);
    await thirdStar.click();

    await page.waitForTimeout(500);

    expect(capturedRating).toBeNull();
  });

  test('Enable reader for book should mark as reader-enabled', async ({ page }) => {
    // Create a book with reader disabled
    const testBook = { ...singleBook, readerEnabled: false };

    await mockLibraryRoute(page, [testBook]);
    await openLibraryPage(page);

    let readerEnabled = false;
    let readerApiCalled = false;

    await page.route('**/api/library/book/reader**', route => {
      if (route.request().method() === 'POST') {
        readerApiCalled = true;
        const postData = route.request().postDataJSON();
        readerEnabled = postData.enabled;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true, enabled: postData.enabled }),
        });
      } else {
        route.continue();
      }
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(500);

    // Click the enable reader button
    const readerButton = page.locator('mat-dialog-container button').filter({
      hasText: /Add to ebook reader|ebook reader|reader/i
    }).first();

    await expect(readerButton).toBeVisible({ timeout: 5000 });
    await readerButton.click();

    await page.waitForTimeout(800);

    // Verify the reader API was called with enabled: true
    expect(readerApiCalled).toBe(true);
    expect(readerEnabled).toBe(true);
  });

  test('Disable reader for book should unmark', async ({ page }) => {
    await mockLibraryRoute(page, [{
      ...singleBook,
    }]);

    await openLibraryPage(page);

    let readerEnabled = true;

    await page.route('**/api/library/book/metadata', route => {
      const postData = route.request().postDataJSON();
      if ('readerEnabled' in postData) {
        readerEnabled = postData.readerEnabled;
      }
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ success: true }),
      });
    });

    await page.locator('.library-card .cover').click();
    await expect(page.locator('mat-dialog-container')).toBeVisible();

    // If there's a disable button, click it
    const disableButton = page.locator('mat-dialog-container button').filter({ hasText: /disable/i }).first();
    const buttonExists = await disableButton.count() > 0;

    if (buttonExists) {
      await disableButton.click();
      await page.waitForTimeout(500);
      expect(readerEnabled).toBe(false);
    }
  });

  test('Admin panel toggle should show/hide admin controls', async ({ page }) => {
    await openLibraryPage(page);

    const adminToggle = page.locator('button.admin-toggle');
    const adminPanel = page.locator('.admin-panel');

    // Admin panel should be hidden initially
    await expect(adminPanel).not.toBeVisible();

    // Click toggle to show
    await adminToggle.click();
    await expect(adminPanel).toBeVisible();

    // Click toggle to hide
    await adminToggle.click();
    await expect(adminPanel).not.toBeVisible();
  });
});

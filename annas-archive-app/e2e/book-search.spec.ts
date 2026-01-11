import { test, expect, type Page } from '@playwright/test';

type BookFixture = {
  title: string;
  md5: string;
  authors: string[];
  language: string;
  format: string;
  source: string;
  fileSize: string;
  bookType: string;
  publisher: string;
  year: number | null;
  isbn: string | null;
  coverCandidates: string[];
};

const aiLiveEnabled = process.env.E2E_AI_LIVE === 'true';
const e2eAccessCode = process.env.E2E_ACCESS_CODE;

const baseBooks: BookFixture[] = [
  {
    title: 'Dune',
    md5: 'md5-dune-001',
    authors: ['Frank Herbert'],
    language: 'en',
    format: 'EPUB',
    source: 'anna',
    fileSize: '1.2 MB',
    bookType: 'Fiction',
    publisher: 'Ace',
    year: 1965,
    isbn: '9780441172719',
    coverCandidates: ['https://covers.example.test/dune.jpg'],
  },
  {
    title: 'Foundation',
    md5: 'md5-foundation-002',
    authors: ['Isaac Asimov'],
    language: 'en',
    format: 'PDF',
    source: 'anna',
    fileSize: '2.3 MB',
    bookType: 'Fiction',
    publisher: 'Gnome',
    year: 1951,
    isbn: '9780553293357',
    coverCandidates: [],
  },
];

const longTitle =
  'The Incredibly Long and Unnecessarily Verbose Book Title That Keeps Going and Going Without End';
const unicodeTitle = 'Les Miserables — 漢字とかな';
const filterBooks: BookFixture[] = [
  {
    title: 'Foundation',
    md5: 'md5-foundation-101',
    authors: ['Isaac Asimov'],
    language: 'en',
    format: 'EPUB',
    source: 'anna',
    fileSize: '1.0 MB',
    bookType: 'Fiction',
    publisher: 'Gnome',
    year: 1951,
    isbn: '9780553293357',
    coverCandidates: [],
  },
  {
    title: 'Dune Messiah',
    md5: 'md5-dune-102',
    authors: ['Frank Herbert'],
    language: 'en',
    format: 'PDF',
    source: 'anna',
    fileSize: '2.2 MB',
    bookType: 'Fiction',
    publisher: 'Putnam',
    year: 1969,
    isbn: '9780441172696',
    coverCandidates: [],
  },
  {
    title: 'Neuromancer',
    md5: 'md5-neuro-103',
    authors: ['William Gibson'],
    language: 'en',
    format: 'MOBI',
    source: 'anna',
    fileSize: '900 KB',
    bookType: 'Fiction',
    publisher: 'Ace',
    year: 1984,
    isbn: '9780441569595',
    coverCandidates: [],
  },
];

async function mockBaseRoutes(page: Page): Promise<void> {
  await page.route('**/api/anna/slum-health', route => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/anna/mirror-health', route => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/anna/download-status', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accountFastInfo: { downloadsLeft: 3, downloadsPerDay: 10 } }),
    });
  });
  await page.route('**/api/anna/book/cover**', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ coverUrl: null }),
    });
  });
  await page.route('**/api/ai/suggest-authors', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ authors: [] }),
    });
  });
}

async function setAuthToken(page: Page, accelerateTimeouts = false): Promise<void> {
  await page.addInitScript(({ shouldAccelerate }) => {
    localStorage.setItem('auth_token', 'e2e-token');
    localStorage.setItem('auth_name', 'E2E User');
    localStorage.setItem('auth_admin', 'true');

    if (shouldAccelerate) {
      const originalSetTimeout = window.setTimeout;
      const originalSetInterval = window.setInterval;
      const clampDelay = (timeout?: number) =>
        typeof timeout === 'number' && timeout >= 1000 ? 5 : timeout;
      window.setTimeout = (handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
        return originalSetTimeout(handler, clampDelay(timeout), ...args);
      };
      window.setInterval = (handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
        return originalSetInterval(handler, clampDelay(timeout), ...args);
      };
    }
  }, { shouldAccelerate: accelerateTimeouts });
}

async function openSearchPage(page: Page, accelerateTimeouts = false): Promise<void> {
  await setAuthToken(page, accelerateTimeouts);
  await page.goto('/#/search');
  await expect(page.locator('app-book-search')).toBeVisible();
}

async function loginAndOpenSearchPage(page: Page): Promise<void> {
  if (!e2eAccessCode) {
    throw new Error('E2E_ACCESS_CODE is required for live AI tests.');
  }
  await page.goto('/login');
  const codeInput = page.locator('input[name="code"]');
  await expect(codeInput).toBeVisible();
  await codeInput.fill(e2eAccessCode);
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/#\/search/, { timeout: 10000 });
  await expect(page.locator('app-book-search')).toBeVisible();
}

async function runSearch(page: Page, query: string): Promise<void> {
  const input = page.locator('input[name="searchTerm"]');
  await input.fill(query);
  await page.locator('button[type="submit"]').click();
}

async function mockSearch(page: Page, books: BookFixture[]): Promise<void> {
  await page.route(/\/api\/anna\/book\?.*/, route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(books),
    });
  });
}

async function openMatSelect(page: Page, labelText: string): Promise<void> {
  const field = page.locator('mat-form-field', { has: page.locator(`mat-label:text("${labelText}")`) });
  const select = field.locator('mat-select');
  const options = page.locator('.cdk-overlay-container mat-option');
  await select.scrollIntoViewIfNeeded();

  for (let attempt = 0; attempt < 2; attempt += 1) {
    await select.click({ force: true });
    try {
      await expect(options.first()).toBeVisible({ timeout: 3000 });
      return;
    } catch {
      await page.keyboard.press('Escape');
      await page.waitForTimeout(200);
    }
  }

  await expect(options.first()).toBeVisible({ timeout: 5000 });
}

async function selectMatOption(page: Page, labelText: string, optionText: string): Promise<void> {
  await openMatSelect(page, labelText);
  const overlayOptions = page.locator('.cdk-overlay-container mat-option');
  if (optionText === 'All') {
    await overlayOptions.first().click();
    return;
  }
  await overlayOptions.filter({ hasText: optionText }).first().click();
}

test.describe('Book Search', () => {
  test.beforeEach(async ({ page }) => {
    await mockBaseRoutes(page);
  });

  test('Search for a book title should return results', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    await expect(page.locator('.book-card')).toHaveCount(2);
  });

  test('Search with empty query should show validation error', async ({ page }) => {
    await openSearchPage(page);
    await page.locator('button[type="submit"]').click();
    await expect(page.locator('.error')).toContainText('Please enter a search term.');
  });

  test('Search results should display book covers', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    await expect(page.locator('.book-card img.cover').first()).toBeVisible();
  });

  test('Search results should display book titles', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    const duneCard = page.locator('.book-card', { hasText: 'Dune' }).first();
    await expect(duneCard.locator('mat-card-title')).toContainText('Dune');
  });

  test('Search results should display book authors', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    const duneCard = page.locator('.book-card', { hasText: 'Dune' }).first();
    await expect(duneCard.locator('mat-card-subtitle')).toContainText('Frank Herbert');
  });

  test('Search results should display book format (EPUB, MOBI, PDF, etc.)', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    const duneCard = page.locator('.book-card', { hasText: 'Dune' }).first();
    await expect(duneCard.locator('mat-card-content')).toContainText('Format: EPUB');
  });

  test('Search results should display file size', async ({ page }) => {
    await mockSearch(page, baseBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    const duneCard = page.locator('.book-card', { hasText: 'Dune' }).first();
    await expect(duneCard.locator('mat-card-content')).toContainText('Size: 1.2 MB');
  });

  test('Search with no results should display appropriate message', async ({ page }) => {
    await mockSearch(page, []);
    await openSearchPage(page);
    await runSearch(page, 'No Such Book');
    await expect(page.locator('text=No results')).toBeVisible();
  });

  test('Network error during search should display connection error', async ({ page }) => {
    await page.route(/\/api\/anna\/book\?.*/, route => route.abort('failed'));
    await openSearchPage(page);
    await runSearch(page, 'Dune');
    await expect(page.locator('.error')).toContainText('Cannot connect to server. Please check your connection.');
  });

  test('Clicking placeholder cover should not cause errors', async ({ page }) => {
    await mockSearch(page, [
      {
        title: 'No Cover Book',
        md5: 'md5-nocover-003',
        authors: ['Unknown Author'],
        language: 'en',
        format: 'EPUB',
        source: 'anna',
        fileSize: '900 KB',
        bookType: 'Fiction',
        publisher: 'N/A',
        year: null,
        isbn: null,
        coverCandidates: [],
      },
    ]);
    await openSearchPage(page);
    await runSearch(page, 'No Cover Book');

    const cover = page.locator('.book-card img.cover');
    await expect(cover).toHaveAttribute('src', /placeholder\.jpg$/);
    await cover.click();
    await expect(page.locator('.error')).toHaveCount(0);
  });

  test('Cover image error should fall back to placeholder image', async ({ page }) => {
    await page.route('**/covers.example.test/**', route => route.abort());
    await mockSearch(page, [
      {
        title: 'Broken Cover',
        md5: 'md5-brokencover-004',
        authors: ['Broken Author'],
        language: 'en',
        format: 'EPUB',
        source: 'anna',
        fileSize: '1.1 MB',
        bookType: 'Fiction',
        publisher: 'N/A',
        year: 2020,
        isbn: null,
        coverCandidates: ['https://covers.example.test/missing.jpg'],
      },
    ]);
    await openSearchPage(page);
    await runSearch(page, 'Broken Cover');

    const cover = page.locator('.book-card img.cover');
    await expect(cover).toHaveAttribute('src', /placeholder\.jpg$/);
  });

  test('Long book titles should be displayed without breaking layout', async ({ page }) => {
    await mockSearch(page, [
      {
        title: longTitle,
        md5: 'md5-longtitle-005',
        authors: ['Verbose Writer'],
        language: 'en',
        format: 'PDF',
        source: 'anna',
        fileSize: '3.5 MB',
        bookType: 'Nonfiction',
        publisher: 'N/A',
        year: 1999,
        isbn: null,
        coverCandidates: [],
      },
    ]);
    await openSearchPage(page);
    await runSearch(page, 'Verbose Writer');
    await expect(page.locator('.book-card mat-card-title')).toContainText(longTitle);
    await expect(page.locator('.book-card')).toBeVisible();
  });

  test('Special characters in search query should be handled correctly', async ({ page }) => {
    const query = 'C++ & "Go" / regex?';
    let capturedUrl = '';

    await page.route(/\/api\/anna\/book\?.*/, route => {
      capturedUrl = route.request().url();
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(baseBooks),
      });
    });

    await openSearchPage(page);
    await runSearch(page, query);

    const url = new URL(capturedUrl);
    expect(url.searchParams.get('name')).toBe(query);
  });

  test('Filter by format (EPUB) should only show EPUB books', async ({ page }) => {
    await mockSearch(page, filterBooks);
    await openSearchPage(page);
    await runSearch(page, 'Foundation');

    await selectMatOption(page, 'Format', 'EPUB');
    await expect(page.locator('.book-card')).toHaveCount(1);
    await expect(page.locator('.book-card mat-card-content')).toContainText('Format: EPUB');
  });

  test('Filter by format (PDF) should only show PDF books', async ({ page }) => {
    await mockSearch(page, filterBooks);
    await openSearchPage(page);
    await runSearch(page, 'Dune');

    await selectMatOption(page, 'Format', 'PDF');
    await expect(page.locator('.book-card')).toHaveCount(1);
    await expect(page.locator('.book-card mat-card-content')).toContainText('Format: PDF');
  });

  test('Filter by format (MOBI) should only show MOBI books', async ({ page }) => {
    await mockSearch(page, filterBooks);
    await openSearchPage(page);
    await runSearch(page, 'Neuromancer');

    await selectMatOption(page, 'Format', 'MOBI');
    await expect(page.locator('.book-card')).toHaveCount(1);
    await expect(page.locator('.book-card mat-card-content')).toContainText('Format: MOBI');
  });

  test('Clear format filter should show all books again', async ({ page }) => {
    await mockSearch(page, filterBooks);
    await openSearchPage(page);
    await runSearch(page, 'Foundation');

    await selectMatOption(page, 'Format', 'EPUB');
    await expect(page.locator('.book-card')).toHaveCount(1, { timeout: 10000 });

    await selectMatOption(page, 'Format', 'All');
    await expect(page.locator('.book-card')).toHaveCount(3, { timeout: 10000 });
  });

  test('Search results should update when changing filters', async ({ page }) => {
    await mockSearch(page, filterBooks);
    await openSearchPage(page);
    await runSearch(page, 'Foundation');

    await selectMatOption(page, 'Format', 'EPUB');
    await expect(page.locator('.book-card')).toHaveCount(1, { timeout: 10000 });

    await selectMatOption(page, 'Format', 'PDF');
    await expect(page.locator('.book-card')).toHaveCount(1, { timeout: 10000 });
    await expect(page.locator('.book-card mat-card-content')).toContainText('Format: PDF');
  });

  test('Filter dropdown should populate with available formats', async ({ page }) => {
    await openSearchPage(page);
    await openMatSelect(page, 'Format');
    await expect(page.locator('mat-option')).toContainText(['All', 'EPUB', 'MOBI', 'PDF']);
  });

  test.describe.serial('AI Live', () => {
    test.skip(!aiLiveEnabled, 'E2E_AI_LIVE not enabled');
    test.skip(!e2eAccessCode, 'E2E_ACCESS_CODE not set for live AI tests');

    test('Author suggestions should appear after typing stops', async ({ page }) => {
      await page.unroute('**/api/ai/suggest-authors');
      await page.route('**/api/ai/suggest-authors', route => {
        route.continue({
          headers: {
            ...route.request().headers(),
            'x-force-openai': 'true',
            'x-openai-model': 'gpt-4o',
          },
        });
      });

      await loginAndOpenSearchPage(page);
      await page.locator('input[name="searchTerm"]').fill('The Hobbit');
      const response = await page.waitForResponse('**/api/ai/suggest-authors');
      if (!response.ok()) {
        const errorBody = await response.text().catch(() => '');
        throw new Error(`suggest-authors failed: ${response.status()} ${response.statusText()} ${errorBody}`);
      }
      const payload = await response.json().catch(() => ({ authors: [] }));
      expect(Array.isArray(payload.authors)).toBeTruthy();
      expect(payload.authors.length).toBeGreaterThan(0);

      const authorSelect = page.locator('mat-select[ng-reflect-name="selectedAuthor"]');
      await expect(authorSelect).toBeEnabled();
      await openMatSelect(page, 'Author');
      const options = page.locator('mat-option');
      await expect.poll(async () => options.count()).toBeGreaterThan(1);
    });

    test('Author suggestions should be debounced (not trigger on every keystroke)', async ({ page }) => {
      let requestCount = 0;
      await page.unroute('**/api/ai/suggest-authors');
      await page.route('**/api/ai/suggest-authors', route => {
        requestCount += 1;
        route.continue({
          headers: {
            ...route.request().headers(),
            'x-force-openai': 'true',
            'x-openai-model': 'gpt-4o',
          },
        });
      });

      await loginAndOpenSearchPage(page);
      const input = page.locator('input[name="searchTerm"]');
      await input.fill('Foun');
      await input.fill('Founda');
      await input.fill('Foundation');
      await page.waitForTimeout(700);

      expect(requestCount).toBe(1);
    });
  });

  test('Unicode characters in book titles should display correctly', async ({ page }) => {
    await mockSearch(page, [
      {
        title: unicodeTitle,
        md5: 'md5-unicode-006',
        authors: ['Victor Hugo'],
        language: 'fr',
        format: 'EPUB',
        source: 'anna',
        fileSize: '2.0 MB',
        bookType: 'Fiction',
        publisher: 'N/A',
        year: 1862,
        isbn: null,
        coverCandidates: [],
      },
    ]);
    await openSearchPage(page);
    await runSearch(page, 'Les Miserables');
    await expect(page.locator('.book-card mat-card-title')).toContainText(unicodeTitle);
  });
});

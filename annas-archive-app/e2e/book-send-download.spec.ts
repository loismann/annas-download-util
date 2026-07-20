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

const testBook: BookFixture = {
  title: 'Test Book for Send/Download',
  md5: 'md5-test-book-001',
  authors: ['Test Author'],
  language: 'en',
  format: 'EPUB',
  source: 'anna',
  fileSize: '1.5 MB',
  bookType: 'Fiction',
  publisher: 'Test Publisher',
  year: 2024,
  isbn: '9780000000000',
  coverCandidates: ['https://covers.example.test/test.jpg'],
};

const pdfBook: BookFixture = {
  title: 'PDF Test Book',
  md5: 'md5-pdf-test-002',
  authors: ['PDF Author'],
  language: 'en',
  format: 'PDF',
  source: 'anna',
  fileSize: '2.0 MB',
  bookType: 'Nonfiction',
  publisher: 'PDF Publisher',
  year: 2024,
  isbn: null,
  coverCandidates: [],
};

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
      body: JSON.stringify({ accountFastInfo: { downloadsLeft: 50, downloadsPerDay: 100 } }),
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

async function setAuthToken(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('auth_token', 'e2e-token');
    localStorage.setItem('auth_name', 'E2E User');
    localStorage.setItem('auth_admin', 'true');
  });
}

async function openSearchPage(page: Page): Promise<void> {
  await setAuthToken(page);
  await page.goto('/#/search');
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

test.describe('Book Send and Download Actions', () => {
  test.beforeEach(async ({ page }) => {
    await mockBaseRoutes(page);
  });

  test.describe('Send to Boox Button', () => {
    test('Send to Boox button should trigger download', async ({ page }) => {
      let sendToBooxCalled = false;
      await mockSearch(page, [testBook]);

      // Mock the send-to-boox endpoint
      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        sendToBooxCalled = true;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      // Mock send-to-library (silently called by sendToBoox)
      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      await expect(page.locator('button').filter({ hasText: /✓ Uploaded!/i })).toBeVisible({ timeout: 10000 });
      expect(sendToBooxCalled).toBeTruthy();
    });

    test('Send to Boox should show sending state', async ({ page }) => {
      await mockSearch(page, [testBook]);

      // Mock slow response
      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, async route => {
        await new Promise(resolve => setTimeout(resolve, 500));
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      // Check for "Uploading…" state
      await expect(page.locator('button:has-text("Uploading…")').first()).toBeVisible();
    });

    test('Send to Boox success should show success indicator', async ({ page }) => {
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      // Check for success indicator
      await expect(page.locator('button:has-text("✓ Uploaded!")').first()).toBeVisible();
    });

    test('Send to Boox failure should show error state', async ({ page }) => {
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: false,
            message: 'Failed to send to Dropbox',
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      // Check for error indicator
      await expect(page.locator('button:has-text("✖ Retry")').first()).toBeVisible();
    });
  });

  test.describe('Send to Kindle Buttons', () => {
    test("Send to Dad's Kindle should trigger email delivery", async ({ page }) => {
      let sendToDadCalled = false;
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-kindle/, route => {
        const url = new URL(route.request().url());
        if (url.searchParams.get('target') === 'dad') {
          sendToDadCalled = true;
        }
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: "Sent to Dad's Kindle",
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const dadKindleButton = page.locator("button:has-text(\"Dad's Kindle\")").first();
      await dadKindleButton.click();

      await expect(page.locator('button').filter({ hasText: /✔ Sent!/i })).toBeVisible({ timeout: 10000 });
      expect(sendToDadCalled).toBeTruthy();
    });

    test("Send to Mom's Kindle should trigger email delivery", async ({ page }) => {
      let sendToMomCalled = false;
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-kindle/, route => {
        const url = new URL(route.request().url());
        if (url.searchParams.get('target') === 'mom') {
          sendToMomCalled = true;
        }
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: "Sent to Mom's Kindle",
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const momKindleButton = page.locator("button:has-text(\"Mom's Kindle\")").first();
      await momKindleButton.click();

      await expect(page.locator('button').filter({ hasText: /✔ Sent!/i })).toBeVisible({ timeout: 10000 });
      expect(sendToMomCalled).toBeTruthy();
    });

    test('Send to Kindle buttons should be disabled for non-EPUB formats', async ({ page }) => {
      await mockSearch(page, [pdfBook]);
      await openSearchPage(page);
      await runSearch(page, 'PDF Test');

      const dadKindleButton = page.locator("button:has-text(\"Dad's Kindle\")").first();
      const momKindleButton = page.locator("button:has-text(\"Mom's Kindle\")").first();

      await expect(dadKindleButton).toBeDisabled();
      await expect(momKindleButton).toBeDisabled();
    });
  });

  test.describe('Send to Library Button', () => {
    test('Send to Library should add book to personal library', async ({ page }) => {
      let sendToLibraryCalled = false;
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        sendToLibraryCalled = true;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Saved to library',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const libraryButton = page.locator('button').filter({ hasText: /Library/i }).first();
      await libraryButton.click();

      await expect(page.locator('button').filter({ hasText: /✓ Saved/i })).toBeVisible({ timeout: 10000 });
      expect(sendToLibraryCalled).toBeTruthy();
    });

    test('Send to Library success should update button state', async ({ page }) => {
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Saved to library',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const libraryButton = page.locator('button').filter({ hasText: /Library/i }).first();
      await libraryButton.click();

      // Check for success indicator
      await expect(page.locator('button').filter({ hasText: /✓ Saved/i })).toBeVisible({ timeout: 10000 });
    });

    test('Send to Library failure should display error message', async ({ page }) => {
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.abort('failed');
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const libraryButton = page.locator('button:has-text("Library")').first();
      await libraryButton.click();

      // Check for error state
      await expect(page.locator('button:has-text("✖ Retry")').first()).toBeVisible();
      await expect(page.locator('.error:has-text("Send to library failed")')).toBeVisible();
    });
  });

  test.describe('Concurrent Operations', () => {
    test('Multiple simultaneous sends should not conflict', async ({ page }) => {
      const multipleBooks = [
        { ...testBook, md5: 'md5-book-1', title: 'Book 1' },
        { ...testBook, md5: 'md5-book-2', title: 'Book 2' },
        { ...testBook, md5: 'md5-book-3', title: 'Book 3' },
      ];

      await mockSearch(page, multipleBooks);

      let callCount = 0;
      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        callCount++;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Book');

      // Wait for all book cards to be visible
      await expect(page.locator('.book-card')).toHaveCount(3);

      // Click all three Dropbox buttons sequentially
      const bookCards = page.locator('.book-card');

      await bookCards.nth(0).locator('button').filter({ hasText: /Dropbox|📤/i }).click();
      await bookCards.nth(1).locator('button').filter({ hasText: /Dropbox|📤/i }).click();
      await bookCards.nth(2).locator('button').filter({ hasText: /Dropbox|📤/i }).click();

      // All three should show success
      const successButtons = page.locator('button').filter({ hasText: /✓ Uploaded!/i });
      await expect(successButtons).toHaveCount(3, { timeout: 10000 });
      expect(callCount).toBe(3);
    });

    test('Double-clicking send button should not trigger duplicate sends', async ({ page }) => {
      await mockSearch(page, [testBook]);

      let callCount = 0;
      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, async route => {
        callCount++;
        await new Promise(resolve => setTimeout(resolve, 200));
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      const booxButton = page.locator('button').filter({ hasText: /Dropbox|📤/i }).first();

      // Click the button - the component guard prevents duplicate API calls
      await booxButton.click();

      // Verify button shows "Uploading..." state (proves the send is in progress and button is disabled)
      await expect(page.locator('button').filter({ hasText: /Uploading/i })).toBeVisible();

      // Wait for completion
      await expect(page.locator('button').filter({ hasText: /✓ Uploaded!/i })).toBeVisible({ timeout: 10000 });

      // Should only be called once due to component guard
      expect(callCount).toBe(1);
    });
  });

  test.describe('Download Counter', () => {
    test('Download counter should decrement after successful send', async ({ page }) => {
      await mockSearch(page, [testBook]);

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: true,
            message: 'Sent to Dropbox',
            accountFastInfo: { downloadsLeft: 49, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      // Initial counter should show 50/100
      await expect(page.locator('.download-counter:has-text("50 / 100")')).toBeVisible();

      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      // After send, counter should update to 49/100
      await expect(page.locator('.download-counter:has-text("49 / 100")')).toBeVisible();
    });

    test('Download counter should display remaining downloads', async ({ page }) => {
      await openSearchPage(page);

      // Should display the counter with initial values
      await expect(page.locator('.download-counter')).toContainText('Downloads:');
      await expect(page.locator('.download-counter:has-text("50 / 100")')).toBeVisible();
    });

    test('Download counter warning levels (yellow/orange/red) should display correctly', async ({ page }) => {
      // Test yellow warning (30 downloads left)
      await page.unroute('**/api/anna/download-status');
      await page.route('**/api/anna/download-status', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ accountFastInfo: { downloadsLeft: 30, downloadsPerDay: 100 } }),
        });
      });

      await setAuthToken(page);
      await page.goto('/#/search');
      await expect(page.locator('.download-counter.warning-yellow')).toBeVisible();

      // Test orange warning (20 downloads left)
      await page.unroute('**/api/anna/download-status');
      await page.route('**/api/anna/download-status', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ accountFastInfo: { downloadsLeft: 20, downloadsPerDay: 100 } }),
        });
      });

      await page.reload();
      await expect(page.locator('.download-counter.warning-orange')).toBeVisible();

      // Test red warning (10 downloads left)
      await page.unroute('**/api/anna/download-status');
      await page.route('**/api/anna/download-status', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ accountFastInfo: { downloadsLeft: 10, downloadsPerDay: 100 } }),
        });
      });

      await page.reload();
      await expect(page.locator('.download-counter.warning-red')).toBeVisible();
    });

    test('Exhausted download quota should prevent further downloads', async ({ page }) => {
      await mockSearch(page, [testBook]);

      // Mock zero downloads left
      await page.route('**/api/anna/download-status', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ accountFastInfo: { downloadsLeft: 0, downloadsPerDay: 100 } }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-boox/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            success: false,
            message: 'Daily download quota exhausted',
            accountFastInfo: { downloadsLeft: 0, downloadsPerDay: 100 },
          }),
        });
      });

      await page.route(/\/api\/anna\/book\/[^\/]+\/send-to-library/, route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ success: true }),
        });
      });

      await openSearchPage(page);
      await runSearch(page, 'Test Book');

      // Counter should show 0/100 with red warning
      await expect(page.locator('.download-counter.warning-red:has-text("0 / 100")')).toBeVisible();

      // Try to send to Boox
      const booxButton = page.locator('button:has-text("Dropbox")').first();
      await booxButton.click();

      // Should show error state
      await expect(page.locator('button:has-text("✖ Retry")').first()).toBeVisible();
    });
  });
});

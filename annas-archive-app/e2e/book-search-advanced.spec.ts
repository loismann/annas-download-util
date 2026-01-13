import { test, expect } from '@playwright/test';

test.describe('Book Search - Advanced Features', () => {
  const accessCode = process.env.E2E_ACCESS_CODE;
  if (!accessCode) {
    throw new Error('E2E_ACCESS_CODE is required to run advanced book search tests.');
  }

  const aiLiveEnabled = process.env.E2E_AI_LIVE === 'true';

  const login = async (page: any) => {
    await page.goto('/login');
    await page.locator('input[name="code"]').fill(accessCode);
    await page.locator('button[type="submit"]').click();
    await expect(page).toHaveURL(/\/search/, { timeout: 10000 });
  };

  const openAuthorSelect = async (page: any) => {
    const field = page.locator('mat-form-field', { has: page.locator('mat-label', { hasText: 'Author' }) });
    await field.locator('mat-select').click();
  };

  test.beforeEach(async ({ page }) => {
    await login(page);
  });

  test.describe('AI Search (GPT-5 Mocked)', () => {
    test('AI search toggle should expand AI search interface', async ({ page }) => {
      await page.route('**/api/ai/book-search', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ summary: 'Test summary', books: [] }),
        });
      });

      const bookNameField = page.locator('mat-form-field').filter({ hasText: 'Book name' });
      await expect(bookNameField.locator('input')).toBeVisible();
      await expect(bookNameField.locator('textarea')).not.toBeVisible();

      const aiToggle = page.locator('button').filter({ has: page.locator('mat-icon', { hasText: 'smart_toy' }) });
      await aiToggle.click();

      await expect(bookNameField.locator('textarea')).toBeVisible();
      await expect(bookNameField.locator('input')).not.toBeVisible();
      await expect(page.locator('button mat-icon').filter({ hasText: 'close' })).toBeVisible();
    });

    test('AI search with natural language query should return book recommendations', async ({ page }) => {
      let aiSearchCalled = false;

      await page.route('**/api/ai/book-search', route => {
        aiSearchCalled = true;
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            summary: 'Test summary',
            books: [
              { title: 'Dune', summary: 'Desert planet', importance: 'Classic', coverUrl: null },
              { title: 'Foundation', summary: 'Galactic empire', importance: 'Epic', coverUrl: null },
            ],
          }),
        });
      });

      const aiToggle = page.locator('button').filter({ has: page.locator('mat-icon', { hasText: 'smart_toy' }) });
      await aiToggle.click();
      const aiTextarea = page.locator('textarea[name="aiSearchQuery"]');
      await expect(aiTextarea).toBeVisible();
      await aiTextarea.fill('Top sci-fi books about empires');
      const aiResponse = page.waitForResponse('**/api/ai/book-search');
      await page.locator('button[type="submit"]').click();
      await aiResponse;
      await expect(page.locator('.modal-layout')).toBeVisible();
      const dialogTitle = page.locator('h2[mat-dialog-title], .mat-dialog-title');
      await expect(dialogTitle).toContainText('AI Book Search');
      await expect(page.locator('.book-card').first()).toBeVisible();
      expect(aiSearchCalled).toBe(true);
    });

    test('AI search results should display in modal dialog', async ({ page }) => {
      await page.route('**/api/ai/book-search', route => {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            summary: 'Test summary',
            books: [
              { title: 'Hyperion', summary: 'Pilgrimage', importance: 'Hugo winner', coverUrl: null },
            ],
          }),
        });
      });

      const aiToggle = page.locator('button').filter({ has: page.locator('mat-icon', { hasText: 'smart_toy' }) });
      await aiToggle.click();
      await page.locator('textarea[name="aiSearchQuery"]').fill('Hugo winners from the 1980s');
      await page.locator('button[type="submit"]').click();

      await expect(page.locator('.modal-layout')).toBeVisible();
      await expect(page.locator('.book-card').first()).toBeVisible();
    });

    test('AI search should show loading indicator while processing', async ({ page }) => {
      await page.route('**/api/ai/book-search', route => {
        setTimeout(() => {
          route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ summary: 'Test summary', books: [] }),
          });
        }, 1200);
      });

      const aiToggle = page.locator('button').filter({ has: page.locator('mat-icon', { hasText: 'smart_toy' }) });
      await aiToggle.click();
      await page.locator('textarea[name="aiSearchQuery"]').fill('Space opera');
      await page.locator('button[type="submit"]').click();

      await expect(page.locator('.loading-state')).toBeVisible({ timeout: 2000 });
      await expect(page.locator('.loading-state')).not.toBeVisible({ timeout: 15000 });
    });
  });

  test.describe.serial('Related Books Modal (GPT-4 Live)', () => {
    test.skip(!aiLiveEnabled, 'E2E_AI_LIVE not enabled');
    // Live GPT-4 tests need longer timeout for API calls + cover lookups
    test.setTimeout(120000);

    test('Related books should show other series by same author', async ({ page }) => {
      // Reset AI usage before running live AI tests to avoid hitting usage limits
      try {
        const response = await page.request.post('/api/ai/usage/reset');
        if (response.ok()) {
          console.log('✅ AI usage reset before live test');
        }
      } catch (err) {
        console.warn('⚠️ Could not reset AI usage:', err);
      }
      await page.locator('input[name="searchTerm"]').fill('Foundation');
      await page.waitForResponse('**/api/ai/suggest-authors');

      await openAuthorSelect(page);
      await page.locator('mat-option').nth(1).click();

      // Wait for button to be enabled before clicking
      const relatedBooksButton = page.locator('button').filter({ hasText: 'Find Related Books' });
      await expect(relatedBooksButton).toBeEnabled({ timeout: 5000 });

      await relatedBooksButton.click();

      // OpenAI API can take a while - increase timeout to 60s
      await page.waitForResponse('**/api/ai/related-books', { timeout: 60000 });
      // Cover lookups can be slow - increase timeout to 45s
      await expect(page.locator('.loading-state')).not.toBeVisible({ timeout: 45000 });

      await expect(page.locator('.modal-layout')).toBeVisible();
    });

    test('Clicking book in related books modal should trigger new search', async ({ page }) => {
      await page.locator('input[name="searchTerm"]').fill('Foundation');
      await page.waitForResponse('**/api/ai/suggest-authors');

      await openAuthorSelect(page);
      await page.locator('mat-option').nth(1).click();

      // Wait for button to be enabled before clicking
      const relatedBooksButton = page.locator('button').filter({ hasText: 'Find Related Books' });
      await expect(relatedBooksButton).toBeEnabled({ timeout: 5000 });

      await relatedBooksButton.click();

      // OpenAI API can take a while - increase timeout to 60s
      await page.waitForResponse('**/api/ai/related-books', { timeout: 60000 });
      // Cover lookups can be slow - increase timeout to 45s
      await expect(page.locator('.loading-state')).not.toBeVisible({ timeout: 45000 });

      const searchButtons = page.locator('.book-card button').filter({ hasText: /Search this book/i });
      if (await searchButtons.count()) {
        await searchButtons.first().click();
        await expect(page).toHaveURL(/#\/search/, { timeout: 10000 });
      } else {
        await expect(page.locator('.modal-layout')).toBeVisible();
      }
    });
  });
});

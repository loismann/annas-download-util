import { test, expect } from '@playwright/test';

// Mock auth token for admin access
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('auth_token', 'mock-test-token');
    localStorage.setItem('auth_name', 'Test User');
    localStorage.setItem('auth_admin', 'true');
  });

  // Mock API responses
  await page.route('**/api/auth/**', route => route.fulfill({ status: 200 }));
  await page.route('**/api/anna/mirror-health', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        mirrors: {
          org: { health: '95%', cert_exp: '90 days' },
          se: { health: '92%', cert_exp: '85 days' },
          li: { health: '88%', cert_exp: '80 days' },
          pm: { health: '90%', cert_exp: '75 days' },
          in: { health: '87%', cert_exp: '70 days' }
        }
      })
    });
  });
});

test.describe('Bulk Download Fixes', () => {
  test('should have independent scroll behavior for left and right columns', async ({ page }) => {
    await page.goto('/#/search');

    // Mock search results with many books
    await page.route('**/api/anna/book?*', route => {
      const books = Array.from({ length: 20 }, (_, i) => ({
        title: `Test Book ${i + 1}`,
        md5: `test-md5-${i + 1}`,
        authors: ['Test Author'],
        language: 'English',
        format: 'EPUB',
        source: 'annas-archive',
        fileSize: '1.5 MB',
        bookType: 'book',
        publisher: 'Test Publisher',
        year: 2024,
        isbn: null,
        coverCandidates: []
      }));

      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(books)
      });
    });

    // Perform search
    await page.fill('input[name="searchTerm"]', 'test');
    await page.click('button[type="submit"]');
    await page.waitForSelector('.book-card', { timeout: 5000 });

    // Get scroll containers
    const leftColumn = page.locator('.search-form');
    const rightColumn = page.locator('.search-results');

    // Check that both columns have overflow-y auto
    const leftOverflow = await leftColumn.evaluate(el => window.getComputedStyle(el).overflowY);
    const rightOverflow = await rightColumn.evaluate(el => window.getComputedStyle(el).overflowY);

    expect(leftOverflow).toBe('auto');
    expect(rightOverflow).toBe('auto');

    // Get initial scroll positions
    const initialRightScroll = await rightColumn.evaluate(el => el.scrollTop);

    // Scroll right column
    await rightColumn.evaluate(el => el.scrollTo(0, 200));
    await page.waitForTimeout(100);

    // Verify right column scrolled
    const afterRightScroll = await rightColumn.evaluate(el => el.scrollTop);
    expect(afterRightScroll).toBeGreaterThan(initialRightScroll);

    // Verify left column did NOT scroll
    const leftScroll = await leftColumn.evaluate(el => el.scrollTop);
    expect(leftScroll).toBe(0);
  });

  test('should show author dropdown with full text visible', async ({ page }) => {
    await page.goto('/#/search');

    // Mock author suggestions with long names
    await page.route('**/api/ai/suggest-authors', route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          authors: [
            {
              author: 'Very Long Author Name That Should Not Be Truncated At All',
              confidence: 'high'
            },
            {
              author: 'Another Extremely Long Author Name With Multiple Words',
              confidence: 'medium'
            },
            {
              author: 'Short Name',
              confidence: 'low'
            }
          ]
        })
      });
    });

    // Type in search to trigger author suggestions
    await page.fill('input[name="searchTerm"]', 'test author');

    // Wait for debounce + author suggestions to load
    await page.waitForTimeout(1000);

    // Wait for the dropdown to be enabled by checking aria-disabled attribute
    await page.waitForFunction(() => {
      const select = document.querySelector('mat-select[name="selectedAuthor"]');
      return select && select.getAttribute('aria-disabled') === 'false';
    }, { timeout: 5000 });

    // Open author dropdown
    await page.click('mat-select[name="selectedAuthor"]');
    await page.waitForSelector('.author-select-panel', { timeout: 2000 });

    // Verify the panel has the custom class
    const panel = page.locator('.author-select-panel');
    await expect(panel).toBeVisible();

    // Verify long author name is fully visible (not truncated)
    const longAuthorOption = panel.locator('mat-option').filter({ hasText: 'Very Long Author Name' });
    await expect(longAuthorOption).toBeVisible();

    // Check that the panel has max width of 600px and text wraps normally
    const panelWidth = await panel.evaluate(el => el.getBoundingClientRect().width);
    expect(panelWidth).toBeLessThanOrEqual(600); // Should be constrained to 600px max

    // Verify text wraps normally (not nowrap)
    const whiteSpace = await longAuthorOption.evaluate(el =>
      window.getComputedStyle(el).whiteSpace
    );
    expect(whiteSpace).toBe('normal'); // Text should wrap when exceeding 200px
  });

  test('should author dropdown expand and show all options clearly', async ({ page }) => {
    await page.goto('/#/search');

    // Mock author suggestions
    await page.route('**/api/ai/suggest-authors', route => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          authors: [
            { author: 'Stephen King', confidence: 'high' },
            { author: 'J.K. Rowling', confidence: 'high' },
            { author: 'George R.R. Martin', confidence: 'medium' }
          ]
        })
      });
    });

    // Type in search to trigger author suggestions
    await page.fill('input[name="searchTerm"]', 'fantasy');

    // Wait for debounce + author suggestions to load
    await page.waitForTimeout(1000);

    // Wait for the dropdown to be enabled by checking aria-disabled attribute
    await page.waitForFunction(() => {
      const select = document.querySelector('mat-select[name="selectedAuthor"]');
      return select && select.getAttribute('aria-disabled') === 'false';
    }, { timeout: 5000 });

    // Open dropdown
    await page.click('mat-select[name="selectedAuthor"]');
    await page.waitForSelector('.author-select-panel');

    // Verify all options are visible
    const panel = page.locator('.author-select-panel');
    await expect(panel.locator('mat-option').filter({ hasText: 'Stephen King' })).toBeVisible();
    await expect(panel.locator('mat-option').filter({ hasText: 'J.K. Rowling' })).toBeVisible();
    await expect(panel.locator('mat-option').filter({ hasText: 'George R.R. Martin' })).toBeVisible();

    // Verify "All Authors" option is also present
    await expect(panel.locator('mat-option').filter({ hasText: 'All Authors' })).toBeVisible();
  });
});

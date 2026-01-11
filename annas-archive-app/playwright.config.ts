import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright E2E Test Configuration
 *
 * Configuration for end-to-end tests using Chrome browser.
 * Used by deployment script to validate application before production deployment.
 *
 * Key features:
 * - Uses system Chrome (not Chromium) via channel: 'chrome'
 * - Headless mode by default (headless: true), can be overridden with E2E_HEADED=1
 * - Text-only reporters (list, line) - no HTML reports
 * - Serial test execution (no parallelism)
 * - Isolated browser contexts per test
 *
 * Usage:
 * - Headless (deploy): npm run e2e
 * - Headed (debug): E2E_HEADED=1 npm run e2e
 */

// Check if headed mode is requested (for debugging)
const isHeaded = process.env.E2E_HEADED === '1';

export default defineConfig({
  testDir: './e2e',

  // Test execution settings
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,

  // Global timeout settings
  timeout: 30000, // 30s per test (fail fast)
  expect: {
    timeout: 5000 // 5s for assertions (fail fast)
  },

  // Text-only reporters (no HTML report generation)
  reporter: [
    ['list'],
    ['line']
  ],

  // Global test configuration
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    extraHTTPHeaders: process.env.E2E_WORKER_INDEX
      ? { 'x-e2e-worker': process.env.E2E_WORKER_INDEX }
      : {},

    // Viewport configuration
    viewport: { width: 1280, height: 720 },

    // Ensure each test has isolated state
    storageState: undefined,

    // Network configuration
    navigationTimeout: 30000,
    actionTimeout: 10000,
  },

  // Browser configuration
  projects: [
    {
      name: 'chrome',
      use: {
        ...devices['Desktop Chrome'],
        channel: 'chrome',
        headless: !isHeaded,

        // Additional context options for isolation
        contextOptions: {
          ignoreHTTPSErrors: true,
        },
      },
    },
  ],
});

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
 * - Configurable parallel execution: set PARALLEL_TESTS=4 for parallel test running
 * - Isolated browser contexts per test for true parallelism
 *
 * Usage:
 * - Sequential headless (deploy): npm run e2e
 * - Sequential headed (debug): E2E_HEADED=1 npm run e2e
 * - Parallel (4 workers): PARALLEL_TESTS=4 npm run e2e
 */

// Check if parallel mode is requested
const parallelWorkers = process.env.PARALLEL_TESTS ? parseInt(process.env.PARALLEL_TESTS, 10) : 1;
const isParallel = parallelWorkers > 1;

// Check if headed mode is requested (for debugging)
const isHeaded = process.env.E2E_HEADED === '1';

export default defineConfig({
  testDir: './e2e',

  // Test execution settings
  fullyParallel: isParallel,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: parallelWorkers,

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
    viewport: isParallel ? { width: 800, height: 600 } : { width: 1280, height: 720 },

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

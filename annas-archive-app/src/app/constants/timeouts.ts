/**
 * Timeout constants for HTTP requests and UI operations.
 * Centralized to ensure consistency across the application.
 */

// ============================================================================
// HTTP Request Timeouts
// ============================================================================

/** Default timeout for search operations (60 seconds) */
export const SEARCH_TIMEOUT_MS = 60000;

/** Timeout for AI operations which can take longer (5 minutes) */
export const AI_OPERATION_TIMEOUT_MS = 300000;

/** Timeout for quick lookups like covers and descriptions (10 seconds) */
export const QUICK_LOOKUP_TIMEOUT_MS = 10000;

// ============================================================================
// UI Debounce Delays
// ============================================================================

/** Debounce delay for search input (300ms) */
export const SEARCH_DEBOUNCE_MS = 300;

/** Debounce delay for resize events (150ms) */
export const RESIZE_DEBOUNCE_MS = 150;

/** Debounce delay for scroll events (100ms) */
export const SCROLL_DEBOUNCE_MS = 100;

// ============================================================================
// Staggered Loading Delays
// ============================================================================

/** Delay between cover lookups to avoid rate limiting (200ms) */
export const COVER_LOOKUP_STAGGER_MS = 200;

/** Delay between description fetches to avoid rate limiting (100ms) */
export const DESCRIPTION_FETCH_STAGGER_MS = 100;

/** Delay between related books cover lookups (120ms) */
export const RELATED_BOOKS_STAGGER_MS = 120;

// ============================================================================
// UI Feedback Delays
// ============================================================================

/** How long to show progress messages before auto-hide (5 seconds) */
export const PROGRESS_MESSAGE_DURATION_MS = 5000;

/** How long to show success messages (3 seconds) */
export const SUCCESS_MESSAGE_DURATION_MS = 3000;

/** Animation delay for transitions (100ms) */
export const ANIMATION_DELAY_MS = 100;

/** Delay after API response before updating UI (500ms) */
export const POST_API_DELAY_MS = 500;

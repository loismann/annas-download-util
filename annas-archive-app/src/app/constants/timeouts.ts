/**
 * Timeout constants for HTTP requests and UI operations.
 *
 * Every value here is read by something. Nine others sat alongside them unused —
 * an unreferenced constant still reads as authoritative, while the number that
 * actually governs the behaviour lives hardcoded somewhere else and drifts.
 */

// ============================================================================
// HTTP Request Timeouts
// ============================================================================

/** Default timeout for search operations (60 seconds) */
export const SEARCH_TIMEOUT_MS = 60000;

// ============================================================================
// Staggered Loading Delays
// ============================================================================

/** Delay between cover lookups to avoid rate limiting external APIs (750ms) */
export const COVER_LOOKUP_STAGGER_MS = 750;

/** Delay between description fetches to avoid rate limiting (100ms) */
export const DESCRIPTION_FETCH_STAGGER_MS = 100;

/** Delay between related books cover lookups to avoid rate limiting external APIs (600ms) */
export const RELATED_BOOKS_STAGGER_MS = 600;

/** How often to poll a background "send to library" download job for progress (1.5 seconds) */
export const DOWNLOAD_JOB_POLL_MS = 1500;

// ============================================================================
// UI Feedback Delays
// ============================================================================

/** How long to show progress messages before auto-hide (5 seconds) */
export const PROGRESS_MESSAGE_DURATION_MS = 5000;

/** How long to show success messages (3 seconds) */
export const SUCCESS_MESSAGE_DURATION_MS = 3000;

/** Render delay for DOM updates to settle (100ms) */
export const RENDER_DELAY_MS = 100;

/** Delay between batch operations to avoid overwhelming the server (2 seconds) */
export const BATCH_DELAY_MS = 2000;

/**
 * Limit constants for pagination, data fetching, and UI constraints.
 * Centralized to ensure consistency across the application.
 */

// ============================================================================
// Search & Pagination
// ============================================================================

/** Default number of search results to fetch */
export const DEFAULT_SEARCH_LIMIT = 50;

/** Maximum books to fetch descriptions for automatically (0 = disabled, users click to fetch) */
export const AUTO_DESCRIPTION_FETCH_LIMIT = 0;

/** Maximum books to fetch covers for automatically (0 = disabled to prevent rate limiting external APIs) */
export const AUTO_COVER_FETCH_LIMIT = 0;

// ============================================================================
// Reader & Content
// ============================================================================

/** Maximum words to analyze for vocabulary highlighting */
export const VOCAB_ANALYSIS_WORD_LIMIT = 1000;

/** Number of recent known words to use for highlighting */
export const KNOWN_WORDS_CONTEXT_LIMIT = 100;

/** Number of known words for chapter-level analysis */
export const CHAPTER_KNOWN_WORDS_LIMIT = 200;

/** Maximum page size in words for reader */
export const MAX_PAGE_SIZE_WORDS = 800;

/** Minimum page size in words for reader */
export const MIN_PAGE_SIZE_WORDS = 10;

// ============================================================================
// UI Display Limits
// ============================================================================

/** Maximum cover candidates to show in picker */
export const MAX_COVER_CANDIDATES = 12;

/** Maximum tags to extract from subjects */
export const MAX_EXTRACTED_TAGS = 5;

/** Maximum quick suggestions to show */
export const MAX_QUICK_SUGGESTIONS = 8;

/** Sample size for logging/debugging */
export const LOG_SAMPLE_SIZE = 3;

// ============================================================================
// Font Constraints
// ============================================================================

/** Minimum font size in pixels */
export const MIN_FONT_SIZE = 12;

/** Maximum font size in pixels */
export const MAX_FONT_SIZE = 28;

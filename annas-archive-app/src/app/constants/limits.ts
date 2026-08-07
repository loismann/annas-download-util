/**
 * Limit constants for pagination, data fetching, and UI constraints.
 *
 * Every value here is read by something. Seven others used to sit alongside
 * them, unreferenced — a constant nothing reads still looks authoritative, and
 * the number that actually governs the behaviour ends up hardcoded somewhere
 * else and drifts. If a value belongs here, wire it up; if nothing wants it,
 * it does not belong here.
 */

// ============================================================================
// Search & Pagination
// ============================================================================

/** Maximum books to fetch descriptions for automatically (0 = disabled, users click to fetch) */
export const AUTO_DESCRIPTION_FETCH_LIMIT = 0;

/** Maximum books to fetch covers for automatically (0 = disabled to prevent rate limiting external APIs) */
export const AUTO_COVER_FETCH_LIMIT = 0;

// ============================================================================
// Reader & Content
// ============================================================================

/** Upper bound on the reader's binary-searched page size — a cap for measurement cost, not a layout rule. */
export const MAX_PAGE_SIZE_WORDS = 800;

/** Floor on the reader's page size, and the low bracket the binary search falls back to. */
export const MIN_PAGE_SIZE_WORDS = 10;

// ============================================================================
// UI Display Limits
// ============================================================================

/** Sample size for logging/debugging */
export const LOG_SAMPLE_SIZE = 3;

// ============================================================================
// Font Constraints
// ============================================================================

/** Minimum reader font size in pixels. */
export const MIN_FONT_SIZE = 12;

/** Maximum reader font size in pixels. */
export const MAX_FONT_SIZE = 28;

/**
 * API path constants for all backend endpoints.
 * Centralized to ensure consistency and easy refactoring.
 */

// ============================================================================
// Base Paths
// ============================================================================

export const API_BASE = '/api';

export const ANNA_BASE = `${API_BASE}/anna`;
export const LIBGEN_BASE = `${API_BASE}/libgen`;
export const LIBRARY_BASE = `${API_BASE}/library`;
export const AI_BASE = `${API_BASE}/ai`;
export const VOCAB_BASE = `${API_BASE}/vocab`;
export const GAMING_BASE = `${API_BASE}/gaming`;
export const AUTH_BASE = `${API_BASE}/auth`;
export const QUIZ_BASE = `${API_BASE}/quiz`;
export const MEDIA_BASE = `${API_BASE}/media`;
export const HEALTH_BASE = '/health';

// ============================================================================
// Anna's Archive Endpoints
// ============================================================================

export const ANNA_SEARCH = `${ANNA_BASE}/book`;
export const ANNA_DOWNLOAD = `${ANNA_BASE}/download`;
export const ANNA_COVER = `${ANNA_BASE}/book/cover`;
export const ANNA_SLUM_HEALTH = `${ANNA_BASE}/slum-health`;
export const ANNA_MIRROR_HEALTH = `${ANNA_BASE}/mirror-health`;

// ============================================================================
// LibGen Endpoints
// ============================================================================

export const LIBGEN_SEARCH = `${LIBGEN_BASE}/book`;
export const LIBGEN_DOWNLOAD = `${LIBGEN_BASE}/download`;
export const LIBGEN_SEND_TO_LIBRARY = `${LIBGEN_BASE}/send-to-library`;

// ============================================================================
// Library Endpoints
// ============================================================================

export const LIBRARY_BOOKS = `${LIBRARY_BASE}/books`;
export const LIBRARY_READER_BOOKS = `${LIBRARY_BASE}/reader/books`;
export const LIBRARY_BOOK = `${LIBRARY_BASE}/book`;
export const LIBRARY_COVER = `${LIBRARY_BASE}/cover`;
export const LIBRARY_READER_EPUB = `${LIBRARY_BASE}/reader/epub`;

// ============================================================================
// AI Endpoints
// ============================================================================

export const AI_SUMMARIZE = `${AI_BASE}/summarize`;
export const AI_FLASHCARDS = `${AI_BASE}/flashcards`;
export const AI_CHARACTERS = `${AI_BASE}/characters`;
export const AI_BOOK_SEARCH = `${AI_BASE}/book-search`;
export const AI_RELATED_BOOKS = `${AI_BASE}/related-books`;
export const AI_SUGGEST_AUTHORS = `${AI_BASE}/suggest-authors`;
export const AI_USAGE = `${AI_BASE}/usage`;
export const AI_SECTION_SUMMARY = `${AI_BASE}/section-summary`;

// ============================================================================
// Vocabulary Endpoints
// ============================================================================

export const VOCAB_KNOWN = `${VOCAB_BASE}/known`;
export const VOCAB_KNOWN_VOCABULARY = `${VOCAB_BASE}/known/vocabulary`;

// ============================================================================
// Gaming Endpoints
// ============================================================================

export const GAMING_TOGGLE = `${GAMING_BASE}/toggle`;
export const GAMING_STATUS = `${GAMING_BASE}/status`;

// ============================================================================
// Auth Endpoints
// ============================================================================

export const AUTH_LOGIN = `${AUTH_BASE}/login`;
export const AUTH_REFRESH = `${AUTH_BASE}/refresh`;

// ============================================================================
// Health Check Endpoints
// ============================================================================

export const HEALTH_ALL = HEALTH_BASE;
export const HEALTH_READY = `${HEALTH_BASE}/ready`;
export const HEALTH_LIVE = `${HEALTH_BASE}/live`;
export const HEALTH_EXTERNAL = `${HEALTH_BASE}/external`;

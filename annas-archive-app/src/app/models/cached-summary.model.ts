/**
 * Cached chapter summary from AI service.
 */
export interface CachedChapterSummary {
  summary: string;
}

/**
 * Map of chapter ID to cached summary.
 */
export interface CachedSummariesMap {
  [chapterId: number]: CachedChapterSummary;
}

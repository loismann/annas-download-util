import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import { CachedSummariesMap } from '../models/cached-summary.model';
import {
  SummarizeResponse,
  SummarizeRequestPayload,
  LearnMoreRequestPayload,
  LearnMoreResponse,
  FlashcardRequestPayload,
  FlashcardItem,
  FlashcardResult,
  WikiImagesResponse,
  FullChapterSummaryRequest,
  FullChapterSummaryResponse,
  UltraChapterSummaryRequest,
  UltraChapterSummaryResponse,
  TokenUsageResponse,
  UserTokenUsage,
  ChunkBoundariesResponse,
  SectionSummaryRequest,
  SectionSummaryResponse,
  CharacterGraphResponse,
  CharacterGraphRequest,
  CharacterGraphUpdateRequest
} from '../models/dropbox-epub.model';

/* ─────────────── AI book search response shapes ─────────────────────── */
export interface AiBookSearchItem {
  title: string;
  author: string;
  summary: string;
  importance: string;
  coverUrl?: string | null;
  descriptionSource?: string | null;
}

export interface AiBookSearchResult {
  summary?: string | null;
  books: AiBookSearchItem[];
}

/* ─────────────── author suggestion response ─────────────────────── */
export interface AuthorSuggestion {
  author: string;
  confidence: string;
}

export interface SuggestAuthorsResponse {
  authors: AuthorSuggestion[];
}

/* ─────────────── related books response ─────────────────────── */
export interface SeriesBook {
  title: string;
  order: number;
  description: string;
  coverUrl?: string;
  descriptionSource?: string | null;
}

export interface AuthorSeriesInfo {
  seriesName: string;
  bookCount: number;
  books: SeriesBook[];
  description: string;
  summary: string;
}

export interface RelatedBooksResponse {
  sameSeries: SeriesBook[];
  otherSeries: AuthorSeriesInfo[];
  seriesSummary: string | null;
}

/* ─────────────── series book matching ─────────────────────── */
export interface BookWithCandidates {
  title: string;
  order: number;
  candidates: CandidateBook[];
}

export interface CandidateBook {
  md5: string;
  title: string;
  authors: string[];
  format: string;
  fileSize: string;
}

export interface SeriesBookMatch {
  bookTitle: string;
  order: number;
  status: string;
  selectedMd5?: string;
  selectedTitle?: string;
  confidence: string;
  reason: string;
}

export interface MatchSeriesBooksRequest {
  seriesName?: string;
  author: string;
  preferredFormat?: string;
  books: BookWithCandidates[];
}

export interface MatchSeriesBooksResponse {
  matches: SeriesBookMatch[];
}

/**
 * Service for AI-powered features.
 * Handles summarization, flashcards, vocabulary learning, character graphs, and book recommendations.
 */
@Injectable({ providedIn: 'root' })
export class AiApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : '';
  private readonly aiBaseUrl = `${this.apiHost}/api/ai`;
  private readonly vocabBaseUrl = `${this.apiHost}/api/vocab`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[AiApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /* ══════════════════════════════════════════════════════════════
     SUMMARIZATION ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Summarize a text selection.
   */
  summarizeText(payload: SummarizeRequestPayload): Observable<SummarizeResponse> {
    return this.http.post<SummarizeResponse>(
      `${this.aiBaseUrl}/summarize`,
      payload
    );
  }

  /**
   * Generate a full chapter summary.
   */
  summarizeFullChapter(payload: FullChapterSummaryRequest): Observable<FullChapterSummaryResponse> {
    return this.http.post<FullChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter`,
      payload
    );
  }

  /**
   * Get cached full chapter summary.
   */
  getFullChapterSummary(dropboxPath: string, chapterId: number): Observable<FullChapterSummaryResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<FullChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter`,
      { params }
    );
  }

  /**
   * Get cached ultra chapter summary.
   */
  getUltraChapterSummary(dropboxPath: string, chapterId: number): Observable<UltraChapterSummaryResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<UltraChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter/dummy`,
      { params }
    );
  }

  /**
   * Generate an ultra chapter summary.
   */
  generateUltraChapterSummary(payload: UltraChapterSummaryRequest): Observable<UltraChapterSummaryResponse> {
    return this.http.post<UltraChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter/dummy`,
      payload
    );
  }

  /**
   * Get all cached summaries for a book.
   */
  getAllCachedSummaries(dropboxPath: string): Observable<CachedSummariesMap> {
    const params = new HttpParams().set('dropboxPath', dropboxPath);
    return this.http.get<CachedSummariesMap>(
      `${this.aiBaseUrl}/summarize/book`,
      { params }
    );
  }

  /**
   * Get chunk boundaries for a chapter.
   */
  getChunkBoundaries(dropboxPath: string, chapterId: number): Observable<ChunkBoundariesResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<ChunkBoundariesResponse>(
      `${this.aiBaseUrl}/chunk-boundaries`,
      { params }
    );
  }

  /**
   * Get cached section summary (no generation).
   */
  getCachedSectionSummary(dropboxPath: string, chapterId: number, sectionIndex: number): Observable<SectionSummaryResponse> {
    return this.http.get<SectionSummaryResponse>(
      `${this.aiBaseUrl}/section-summary?dropboxPath=${encodeURIComponent(dropboxPath)}&chapterId=${chapterId}&sectionIndex=${sectionIndex}`
    );
  }

  /**
   * Generate section summary using AI.
   */
  generateSectionSummary(payload: SectionSummaryRequest): Observable<SectionSummaryResponse> {
    return this.http.post<SectionSummaryResponse>(
      `${this.aiBaseUrl}/section-summary`,
      payload
    );
  }

  /* ══════════════════════════════════════════════════════════════
     TOKEN USAGE ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get current user's token usage.
   */
  getTokenUsage(): Observable<TokenUsageResponse> {
    return this.http.get<TokenUsageResponse>(
      `${this.aiBaseUrl}/usage`
    );
  }

  /**
   * Get all users' token usage (admin).
   */
  getAllUsersTokenUsage(): Observable<UserTokenUsage[]> {
    return this.http.get<UserTokenUsage[]>(
      `${this.aiBaseUrl}/usage/all-users`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     FLASHCARD ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Create flashcard(s) from text.
   */
  createFlashcard(payload: FlashcardRequestPayload): Observable<FlashcardItem[]> {
    return this.http.post<FlashcardResult>(
      `${this.aiBaseUrl}/flashcards`,
      payload
    ).pipe(
      map(res => {
        const items = (res as any)?.cards || res;
        return Array.isArray(items) ? items : [items];
      })
    );
  }

  /**
   * Get all flashcards for a book.
   */
  getFlashcards(dropboxPath: string): Observable<FlashcardItem[]> {
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.get<FlashcardItem[]>(
      `${this.aiBaseUrl}/flashcards`,
      { params }
    );
  }

  /**
   * Clear all flashcards for a book.
   */
  clearFlashcards(dropboxPath: string): Observable<{ cleared: boolean }> {
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.delete<{ cleared: boolean }>(
      `${this.aiBaseUrl}/flashcards`,
      { params }
    );
  }

  /**
   * Delete a single flashcard.
   */
  deleteFlashcard(dropboxPath: string, term: string): Observable<{ deleted: boolean }> {
    const params = new HttpParams()
      .set('path', dropboxPath)
      .set('term', term);
    return this.http.delete<{ deleted: boolean }>(
      `${this.aiBaseUrl}/flashcard`,
      { params }
    );
  }

  /**
   * Save section vocabulary to cache.
   */
  saveSectionVocab(dropboxPath: string, chapterId: number, sectionIndex: number, vocab: FlashcardItem[]): Observable<{ success: boolean; vocabCount: number }> {
    return this.http.post<{ success: boolean; vocabCount: number }>(
      `${this.aiBaseUrl}/section-vocab`,
      { dropboxPath, chapterId, sectionIndex, vocab }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     VOCABULARY LEARNING ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Learn more about a term/topic.
   */
  learnMore(payload: LearnMoreRequestPayload): Observable<LearnMoreResponse> {
    return this.http.post<LearnMoreResponse>(
      `${this.aiBaseUrl}/vocab/learn-more`,
      payload
    );
  }

  /**
   * Get Wikipedia images for a term.
   */
  getWikiImages(term: string): Observable<WikiImagesResponse> {
    const params = new HttpParams().set('term', term);
    return this.http.get<WikiImagesResponse>(
      `${this.aiBaseUrl}/media/wiki-images`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     SERVER-SIDE VOCABULARY STORAGE
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get all known words.
   */
  getKnownWords(): Observable<{ [term: string]: string[] }> {
    return this.http.get<{ [term: string]: string[] }>(`${this.vocabBaseUrl}/known`);
  }

  /**
   * Add a word to known words.
   */
  addKnownWord(term: string, bookId?: string): Observable<void> {
    return this.http.post<void>(`${this.vocabBaseUrl}/known`, { term, bookId });
  }

  /**
   * Remove a word from known words.
   */
  removeKnownWord(term: string): Observable<void> {
    return this.http.delete<void>(`${this.vocabBaseUrl}/known/${encodeURIComponent(term)}`);
  }

  /**
   * Get all study words.
   */
  getStudyWords(): Observable<{ [term: string]: { definition: string; books: string[] } }> {
    return this.http.get<{ [term: string]: { definition: string; books: string[] } }>(`${this.vocabBaseUrl}/study`);
  }

  /**
   * Add a word to study list.
   */
  addStudyWord(term: string, definition?: string, bookId?: string): Observable<void> {
    return this.http.post<void>(`${this.vocabBaseUrl}/study`, { term, definition, bookId });
  }

  /**
   * Remove a word from study list.
   */
  removeStudyWord(term: string): Observable<void> {
    return this.http.delete<void>(`${this.vocabBaseUrl}/study/${encodeURIComponent(term)}`);
  }

  /**
   * Delete all vocabulary for a book.
   */
  deleteBookVocab(bookId: string): Observable<{ success: boolean; knownWordsAffected: number; studyWordsAffected: number; totalRemoved: number }> {
    return this.http.delete<{ success: boolean; knownWordsAffected: number; studyWordsAffected: number; totalRemoved: number }>(
      `${this.vocabBaseUrl}/book/${encodeURIComponent(bookId)}`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     CHARACTER GRAPH ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Generate a character relationship graph.
   */
  generateCharacterGraph(payload: CharacterGraphRequest): Observable<CharacterGraphResponse> {
    return this.http.post<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/graph`,
      payload
    );
  }

  /**
   * Get cached character graph.
   */
  getCharacterGraph(dropboxPath: string): Observable<CharacterGraphResponse> {
    const params = new HttpParams().set('dropboxPath', dropboxPath);
    return this.http.get<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/graph`,
      { params }
    );
  }

  /**
   * Update character graph.
   */
  updateCharacterGraph(payload: CharacterGraphUpdateRequest): Observable<CharacterGraphResponse> {
    return this.http.post<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/update`,
      payload
    );
  }

  /* ══════════════════════════════════════════════════════════════
     BOOK SEARCH & RECOMMENDATIONS
     ══════════════════════════════════════════════════════════════ */

  /**
   * AI-powered book search.
   */
  aiBookSearch(query: string): Observable<AiBookSearchResult> {
    return this.http.post<AiBookSearchResult>(
      `${this.aiBaseUrl}/book-search`,
      { query }
    );
  }

  /**
   * Suggest authors for a book title.
   */
  suggestAuthors(bookTitle: string): Observable<SuggestAuthorsResponse> {
    return this.http.post<SuggestAuthorsResponse>(
      `${this.aiBaseUrl}/suggest-authors`,
      { bookTitle }
    );
  }

  /**
   * Get related books (same series + other series by author).
   */
  getRelatedBooks(bookTitle: string, author: string): Observable<RelatedBooksResponse> {
    return this.http.post<RelatedBooksResponse>(
      `${this.aiBaseUrl}/related-books`,
      { bookTitle, author }
    );
  }

  /**
   * Match series books using AI.
   */
  matchSeriesBooks(request: MatchSeriesBooksRequest): Observable<MatchSeriesBooksResponse> {
    return this.http.post<MatchSeriesBooksResponse>(
      `${this.aiBaseUrl}/match-series-books`,
      request
    );
  }
}

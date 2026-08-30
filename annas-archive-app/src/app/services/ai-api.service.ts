import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';
import { apiBase } from './api-base';

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

/* ─────────────── AI TV/movie search response shapes ─────────────────── */
export interface AiMediaSearchItem {
  title: string;
  year?: number | null;
  /** The model's own best judgment — "tv" or "movie" — resolved against
   * Sonarr or Radarr accordingly by the caller. */
  type: 'tv' | 'movie';
  blurb?: string | null;
}

export interface AiMediaSearchResult {
  summary?: string | null;
  results: AiMediaSearchItem[];
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

/* ─────────────── search result grouping (duplicate/format detection) ─── */
export interface GroupableBook {
  md5: string;
  title: string;
  authors: string[];
  format: string;
  year: number | null;
}

/** Each inner array is the md5s of one group of "same book" results —
 *  every md5 from the request appears in exactly one group. */
export interface GroupSearchResultsResponse {
  groups: string[][];
}

/**
 * Service for AI-powered features: book search, author and series suggestions,
 * result grouping, and TV/movie search.
 *
 * The reader's own AI calls do not come through here — Reader II owns them in
 * `Reader2ApiService`, one method per route.
 */
@Injectable({ providedIn: 'root' })
export class AiApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = apiBase();
  private readonly aiBaseUrl = `${this.apiHost}/api/ai`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[AiApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
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

  /**
   * Groups book search results that represent the same underlying book
   * (different format, or a duplicate upload/scan) so the frontend can
   * collapse them into one card instead of one row per file. Every input
   * md5 comes back in exactly one group, including books with no duplicates.
   */
  groupSearchResults(books: GroupableBook[]): Observable<GroupSearchResultsResponse> {
    return this.http.post<GroupSearchResultsResponse>(
      `${this.aiBaseUrl}/group-search-results`,
      { books }
    );
  }

  /**
   * AI-powered TV/movie search — natural language in, a list of suggested
   * titles (each tagged tv or movie) out. Resolving each title into a real,
   * addable result happens client-side (see MediaSearchComponent), not here.
   */
  aiMediaSearch(query: string): Observable<AiMediaSearchResult> {
    return this.http.post<AiMediaSearchResult>(
      `${this.aiBaseUrl}/media-search`,
      { query }
    );
  }
}

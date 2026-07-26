import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, BehaviorSubject, of } from 'rxjs';
import { tap, shareReplay, map } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import {
  DropboxChapterContent,
  DropboxEpubChaptersResponse,
  DropboxEpubStatus,
  DropboxBookSearchResult,
  LibraryReaderBook
} from '../models/dropbox-epub.model';
import { apiBase } from './api-base';

/* ─────────────── Library book response shapes ──────────────── */
export interface LibraryBook {
  fileName: string;
  title: string;
  authors: string[];
  format: string;
  fileSize: string;
  coverUrl?: string | null;
  primaryGenre?: string | null;
  tags?: string[];
  series?: string | null;
  goodreadsRating?: number | null;
  personalRating?: number | null;
  /** Names of household members who have favorited this book — per-owner, not a shared flag. */
  favoritedBy?: string[];
  readerEnabled?: boolean;
  dateAdded?: string;
}

export interface LibraryBookMetadata {
  primaryGenre: string;
  tags: string[];
  series: string | null;
  title?: string;
  authors?: string[];
}

export interface LibraryBookRatings {
  goodreadsRating?: number | null;
  personalRating?: number | null;
}

export interface CoverCandidatesResponse {
  covers: string[];
}

export interface SendToKindleResponse {
  success: boolean;
  message?: string;
}

export interface LibraryBookSummaryResponse {
  summary: string | null;
  source: string | null;
}

export interface LibraryUploadResponse {
  success: boolean;
  fileName: string;
  fileSize: string;
  message: string;
}

export interface LibrarySupportedFormatsResponse {
  formats: string[];
  maxFileSizeMb: number;
}

export interface LibraryBooksPaginatedResponse {
  books: LibraryBook[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface LibrarySearchParams {
  q?: string;
  genre?: string;
  ownerTags?: string[];
  minPersonalRating?: number;
  minGoodreadsRating?: number;
  favoritesOnly?: boolean;
  missingAuthor?: boolean;
  missingCover?: boolean;
  genreCountLessThan?: number;
  genreCountMoreThan?: number;
  sortBy?: 'title' | 'author' | 'date' | 'series' | 'stars' | 'goodreads';
  sortDesc?: boolean;
  skip?: number;
  take?: number;
}

export interface LibrarySearchResponse {
  books: LibraryBook[];
  totalCount: number;
  skip: number;
  take: number;
  genres: string[];
}

/* ─────────────── Daily library-review modal ──────────────── */
export interface LibraryReviewStatus {
  phase: 'cull' | 'genre' | 'complete';
  shouldShow: boolean;
  remainingInPhase: number;
  sessionInProgress: boolean;
}

export interface LibraryReviewBook {
  fileName: string;
  title: string;
  authors: string[];
  tags: string[];
  series: string | null;
  coverUrl: string | null;
  format: string;
  favoritedBy: string[];
}

export interface LibraryReviewSession {
  phase: 'cull' | 'genre' | 'complete';
  books: LibraryReviewBook[];
  /** Size of the whole eligible pool for this phase, including this session's own batch —
   *  lets the UI show overall progress, not just position within today's 20. */
  totalRemainingInPhase: number;
}

export type LibraryReviewDecision = 'keep' | 'delete' | 'genreSet';

/**
 * Service for library management operations.
 * Handles library books, metadata, covers, ratings, and reader functionality.
 */
@Injectable({ providedIn: 'root' })
export class LibraryApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = apiBase();
  private readonly libraryBaseUrl = `${this.apiHost}/api/library`;

  // Client-side cache for library books
  private cachedBooks$ = new BehaviorSubject<LibraryBook[] | null>(null);
  private cacheTimestamp: number | null = null;
  private readonly cacheMaxAgeMs = 5 * 60 * 1000; // 5 minutes

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[LibraryApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /**
   * Invalidate the client-side library cache.
   * Call this after any operation that modifies library books.
   */
  invalidateCache(): void {
    this.cachedBooks$.next(null);
    this.cacheTimestamp = null;
    this.logger.log('[LibraryApiService] Cache invalidated');
  }

  /**
   * Check if cache is valid (exists and not expired).
   */
  private isCacheValid(): boolean {
    if (!this.cachedBooks$.value || !this.cacheTimestamp) {
      return false;
    }
    const age = Date.now() - this.cacheTimestamp;
    return age < this.cacheMaxAgeMs;
  }

  /* ══════════════════════════════════════════════════════════════
     LIBRARY BOOK ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get all books in the library.
   * Uses client-side caching to avoid re-fetching on page navigation.
   */
  getLibraryBooks(): Observable<LibraryBook[]> {
    // Return cached data if valid
    if (this.isCacheValid()) {
      this.logger.log('[LibraryApiService] Returning cached books', { count: this.cachedBooks$.value?.length });
      return of(this.cachedBooks$.value!);
    }

    // Fetch from server and update cache
    this.logger.log('[LibraryApiService] Fetching books from server');
    return this.http.get<LibraryBook[]>(`${this.libraryBaseUrl}/books`).pipe(
      tap(books => {
        this.cachedBooks$.next(books);
        this.cacheTimestamp = Date.now();
        this.logger.log('[LibraryApiService] Cache updated', { count: books.length });
      })
    );
  }

  /**
   * Get library books with pagination support.
   * Use this for initial load to get books faster.
   */
  getLibraryBooksPaginated(
    skip = 0,
    take = 50,
    sortBy: 'title' | 'date' | 'author' = 'date',
    sortDesc = true
  ): Observable<LibraryBooksPaginatedResponse> {
    const params = new HttpParams()
      .set('skip', skip.toString())
      .set('take', take.toString())
      .set('sortBy', sortBy)
      .set('sortDesc', sortDesc.toString());

    this.logger.log('[LibraryApiService] Fetching paginated books', { skip, take, sortBy, sortDesc });
    return this.http.get<LibraryBooksPaginatedResponse>(`${this.libraryBaseUrl}/books`, { params });
  }

  /**
   * Search and filter library books with full server-side processing.
   * This is the OPTIMIZED endpoint for large libraries - all filtering, sorting,
   * and pagination happens on the server so clients never need to load all books.
   * Use this with infinite scroll for best performance on large libraries.
   */
  searchLibraryBooks(params: LibrarySearchParams): Observable<LibrarySearchResponse> {
    let httpParams = new HttpParams();

    if (params.q) httpParams = httpParams.set('q', params.q);
    if (params.genre) httpParams = httpParams.set('genre', params.genre);
    if (params.ownerTags && params.ownerTags.length > 0) {
      httpParams = httpParams.set('ownerTags', params.ownerTags.join(','));
    }
    if (params.minPersonalRating) httpParams = httpParams.set('minPersonalRating', params.minPersonalRating.toString());
    if (params.minGoodreadsRating) httpParams = httpParams.set('minGoodreadsRating', params.minGoodreadsRating.toString());
    if (params.favoritesOnly) httpParams = httpParams.set('favoritesOnly', 'true');
    if (params.missingAuthor) httpParams = httpParams.set('missingAuthor', 'true');
    if (params.missingCover) httpParams = httpParams.set('missingCover', 'true');
    if (params.genreCountLessThan) httpParams = httpParams.set('genreCountLessThan', params.genreCountLessThan.toString());
    if (params.genreCountMoreThan) httpParams = httpParams.set('genreCountMoreThan', params.genreCountMoreThan.toString());
    if (params.sortBy) httpParams = httpParams.set('sortBy', params.sortBy);
    if (params.sortDesc !== undefined) httpParams = httpParams.set('sortDesc', params.sortDesc.toString());
    if (params.skip !== undefined) httpParams = httpParams.set('skip', params.skip.toString());
    if (params.take !== undefined) httpParams = httpParams.set('take', params.take.toString());

    this.logger.log('[LibraryApiService] Searching books', params);
    return this.http.get<LibrarySearchResponse>(`${this.libraryBaseUrl}/books/search`, { params: httpParams });
  }

  /**
   * Get books available for the reader.
   */
  getLibraryReaderBooks(): Observable<LibraryReaderBook[]> {
    return this.http.get<LibraryReaderBook[]>(`${this.libraryBaseUrl}/reader/books`);
  }

  /**
   * Send a library book to Kindle via email.
   */
  sendLibraryToKindle(
    fileName: string,
    title: string | undefined,
    target: 'dad' | 'mom',
    toDropbox = false
  ): Observable<SendToKindleResponse> {
    let params = new HttpParams()
      .set('fileName', fileName)
      .set('target', target)
      .set('toDropbox', toDropbox.toString());
    if (title) {
      params = params.set('title', title);
    }
    return this.http.post<SendToKindleResponse>(
      `${this.libraryBaseUrl}/book/send-to-kindle`,
      null,
      { params }
    );
  }

  /**
   * Update book metadata (genre, tags, series, title, authors).
   */
  updateLibraryBookMetadata(fileName: string, metadata: LibraryBookMetadata): Observable<LibraryBook> {
    return this.http.patch<LibraryBook>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/metadata`,
      metadata
    );
  }

  /**
   * Update book cover from URL.
   */
  updateLibraryBookCover(fileName: string, coverUrl: string): Observable<{ success?: boolean; coverUrl?: string | null }> {
    return this.http.post<{ success?: boolean; coverUrl?: string | null }>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/cover`,
      { coverUrl }
    );
  }

  /**
   * Upload book cover from image bytes (base64 encoded).
   * Use this when the image needs to be fetched by the browser to bypass hotlink protection.
   */
  uploadLibraryBookCoverBytes(
    fileName: string,
    imageBase64: string,
    mimeType?: string
  ): Observable<{ success?: boolean; coverUrl?: string | null }> {
    return this.http.post<{ success?: boolean; coverUrl?: string | null }>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/cover-bytes`,
      { imageBase64, mimeType }
    );
  }

  /**
   * Update book ratings (Goodreads and personal).
   */
  updateLibraryBookRatings(fileName: string, ratings: LibraryBookRatings): Observable<LibraryBook> {
    return this.http.patch<LibraryBook>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/ratings`,
      ratings
    );
  }

  /**
   * Favorite/unfavorite a book on behalf of whoever's logged in — the acting owner is
   * resolved server-side from the session, not passed from here.
   */
  setLibraryBookFavorite(fileName: string, favorited: boolean): Observable<{ success: boolean; favoritedBy: string[] }> {
    return this.http.post<{ success: boolean; favoritedBy: string[] }>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/favorite`,
      { favorited }
    );
  }

  /**
   * Enable or disable reader for a book.
   */
  updateLibraryBookReaderEnabled(fileName: string, enabled: boolean): Observable<{ success: boolean; enabled: boolean }> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.post<{ success: boolean; enabled: boolean }>(
      `${this.libraryBaseUrl}/book/reader`,
      { enabled },
      { params }
    );
  }

  /**
   * Wipe all genre data from library books.
   */
  wipeLibraryGenres(): Observable<{ success: boolean; updated: number }> {
    return this.http.post<{ success: boolean; updated: number }>(
      `${this.libraryBaseUrl}/books/genres/wipe`,
      null
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Delete a book from the library.
   */
  deleteLibraryBook(fileName: string): Observable<{ success: boolean }> {
    return this.http.delete<{ success: boolean }>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}`
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Get book summary. Pass `deep = true` to use the higher-quality ("deep") AI model for the
   * fallback generation step, if neither Google Books nor Open Library has a real description.
   */
  getLibraryBookSummary(fileName: string, deep = false): Observable<LibraryBookSummaryResponse> {
    const params = new HttpParams().set('deep', deep.toString());
    return this.http.get<LibraryBookSummaryResponse>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/summary`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     DAILY LIBRARY-REVIEW MODAL (admin only)
     ══════════════════════════════════════════════════════════════ */

  /**
   * Whether the daily review modal should currently be shown, and what phase it's in.
   */
  getLibraryReviewStatus(): Observable<LibraryReviewStatus> {
    return this.http.get<LibraryReviewStatus>(`${this.libraryBaseUrl}/review/status`);
  }

  /**
   * Starts today's batch of up to 20 books, or resumes it if already in progress.
   * Always resets the 24h "last shown" timer — used for both the automatic daily
   * trigger and the on-demand "Review Library" button.
   */
  startLibraryReviewSession(): Observable<LibraryReviewSession> {
    return this.http.post<LibraryReviewSession>(`${this.libraryBaseUrl}/review/session/start`, null);
  }

  /**
   * Records a keep/delete/genreSet decision for one book in the current review session.
   */
  submitLibraryReviewDecision(fileName: string, decision: LibraryReviewDecision): Observable<{ success: boolean }> {
    return this.http.post<{ success: boolean }>(`${this.libraryBaseUrl}/review/decision`, { fileName, decision }).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Fetch cover candidates for a book.
   */
  fetchLibraryCoverCandidates(title: string, author?: string): Observable<CoverCandidatesResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<CoverCandidatesResponse>(
      `${this.libraryBaseUrl}/book/cover-candidates`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     LIBRARY READER ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get chapter list for a library EPUB.
   */
  getLibraryReaderChapters(fileName: string): Observable<DropboxEpubChaptersResponse> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.get<DropboxEpubChaptersResponse>(
      `${this.libraryBaseUrl}/reader/epub/chapters`,
      { params }
    );
  }

  /**
   * Get chapter content for a library EPUB.
   */
  getLibraryReaderChapterContent(fileName: string, chapterId: number): Observable<DropboxChapterContent> {
    const params = new HttpParams()
      .set('fileName', fileName)
      .set('chapterId', chapterId.toString());
    return this.http.get<DropboxChapterContent>(
      `${this.libraryBaseUrl}/reader/epub/chapter`,
      { params }
    );
  }

  /**
   * Get indexing status for a library EPUB.
   */
  getLibraryReaderStatus(fileName: string): Observable<DropboxEpubStatus> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.get<DropboxEpubStatus>(
      `${this.libraryBaseUrl}/reader/epub/status`,
      { params }
    );
  }

  /**
   * Start indexing a library EPUB.
   */
  startLibraryReaderIndex(fileName: string): Observable<{ success: boolean }> {
    return this.http.post<{ success: boolean }>(
      `${this.libraryBaseUrl}/reader/epub/index`,
      { fileName }
    );
  }

  /**
   * Delete index cache for a library EPUB.
   */
  deleteLibraryReaderIndex(fileName: string): Observable<{ success: boolean }> {
    return this.http.request<{ success: boolean }>(
      'delete',
      `${this.libraryBaseUrl}/reader/epub/index`,
      { body: { fileName } }
    );
  }

  /**
   * Search within a library EPUB.
   */
  searchLibraryReaderBook(fileName: string, query: string): Observable<DropboxBookSearchResult[]> {
    const params = new HttpParams()
      .set('fileName', fileName)
      .set('query', query);
    return this.http.get<DropboxBookSearchResult[]>(
      `${this.libraryBaseUrl}/reader/epub/search`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     LIBRARY UPLOAD ENDPOINTS (Admin only)
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get supported upload formats and max file size.
   */
  getSupportedFormats(): Observable<LibrarySupportedFormatsResponse> {
    return this.http.get<LibrarySupportedFormatsResponse>(
      `${this.libraryBaseUrl}/upload/supported-formats`
    );
  }

  /**
   * Upload a book file to the library.
   * Admin only - the backend will reject non-admin users.
   */
  uploadBook(file: File): Observable<LibraryUploadResponse> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<LibraryUploadResponse>(
      `${this.libraryBaseUrl}/book/upload`,
      formData
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }
}

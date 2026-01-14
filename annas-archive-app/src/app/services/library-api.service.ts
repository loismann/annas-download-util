import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';
import {
  DropboxChapterContent,
  DropboxEpubChaptersResponse,
  DropboxEpubStatus,
  DropboxBookSearchResult,
  LibraryReaderBook
} from '../models/dropbox-epub.model';

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

/**
 * Service for library management operations.
 * Handles library books, metadata, covers, ratings, and reader functionality.
 */
@Injectable({ providedIn: 'root' })
export class LibraryApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : 'https://fs01pfbooks.synology.me:5051';
  private readonly libraryBaseUrl = `${this.apiHost}/api/library`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[LibraryApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /* ══════════════════════════════════════════════════════════════
     LIBRARY BOOK ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get all books in the library.
   */
  getLibraryBooks(): Observable<LibraryBook[]> {
    return this.http.get<LibraryBook[]>(`${this.libraryBaseUrl}/books`);
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
    );
  }

  /**
   * Delete a book from the library.
   */
  deleteLibraryBook(fileName: string): Observable<{ success: boolean }> {
    return this.http.delete<{ success: boolean }>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}`
    );
  }

  /**
   * Get book summary.
   */
  getLibraryBookSummary(fileName: string): Observable<LibraryBookSummaryResponse> {
    return this.http.get<LibraryBookSummaryResponse>(
      `${this.libraryBaseUrl}/book/${encodeURIComponent(fileName)}/summary`
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
}

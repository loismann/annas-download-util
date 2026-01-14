import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { finalize, map, tap, timeout, catchError } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import { BookDto } from '../models/book-dto.model';

/* ─────────────── Download response shapes ──────────────── */
export interface DownloadMemberResponse {
  downloadUrl: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

export interface SendToTargetResponse {
  success: boolean;
  dropboxPath?: string;
  dropboxFileId?: string;
  message?: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

/* ─────────────── Cover and description lookup ─────────────────────── */
export interface CoverLookupResponse {
  coverUrl: string | null;
}

export interface DescriptionLookupResponse {
  description: string | null;
}

/* ─────────────── Download status ─────────────────────── */
export interface DownloadStatusResponse {
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

/**
 * Service for book search, download, and metadata operations.
 * Handles both Anna's Archive and LibGen sources.
 */
@Injectable({ providedIn: 'root' })
export class BookSearchApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : 'https://fs01pfbooks.synology.me:5051';
  private readonly baseUrl = `${this.apiHost}/api/anna`;
  private readonly libgenBaseUrl = `${this.apiHost}/api/libgen`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[BookSearchApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /* ══════════════════════════════════════════════════════════════
     ANNA'S ARCHIVE ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Search for books on Anna's Archive.
   * Always returns an array, even when the API returns a single object.
   */
  searchBooks(name: string, exact: boolean): Observable<BookDto[]> {
    const params = new HttpParams()
      .set('name', name)
      .set('exact', exact.toString());

    const label = `searchBooks:${name}:${exact}`;
    this.logger.debug('timer-start: ' + label);
    this.logger.log('[searchBooks] start', { name, exact });

    return this.http
      .get<BookDto | BookDto[]>(`${this.baseUrl}/book`, { params })
      .pipe(
        map(res => (Array.isArray(res) ? res : [res])),
        tap(list => {
          const sample = list.slice(0, 3).map(b => ({
            title: b.title,
            md5: b.md5,
            format: b.format,
          }));
          this.logger.log('[searchBooks] result', { count: list.length, sample });
        }),
        finalize(() => {
          this.logger.debug('timer-end: ' + label);
          this.logger.log('[searchBooks] done');
        })
      );
  }

  /**
   * Download a book file using member credentials.
   * Optionally replaces the cover with a custom URL.
   */
  downloadMember(md5: string, title: string, coverUrl?: string): Observable<Blob> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post(
      `${this.baseUrl}/book/${md5}/download/member`,
      null,
      { params, responseType: 'blob' }
    );
  }

  /**
   * Send a book to the local library.
   */
  sendToLibrary(
    md5: string,
    title: string,
    coverUrl?: string,
    authors?: string,
    format?: string,
    fileSize?: string,
    source?: string,
    description?: string
  ): Observable<SendToTargetResponse> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) params = params.set('coverUrl', coverUrl);
    if (authors) params = params.set('authors', authors);
    if (format) params = params.set('format', format);
    if (fileSize) params = params.set('fileSize', fileSize);
    if (source) params = params.set('source', source);
    if (description) params = params.set('description', description);

    return this.http.post<SendToTargetResponse>(
      `${this.baseUrl}/book/${md5}/send-to-library`,
      null,
      { params }
    );
  }

  /**
   * Send a book to Boox device via Dropbox.
   */
  sendToBoox(md5: string, title: string, coverUrl?: string): Observable<SendToTargetResponse> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post<SendToTargetResponse>(
      `${this.baseUrl}/book/${md5}/send-to-boox`,
      null,
      { params }
    );
  }

  /**
   * Send a book to Kindle via email.
   */
  sendToKindle(md5: string, title: string, target: 'dad' | 'mom', coverUrl?: string): Observable<SendToTargetResponse> {
    let params = new HttpParams()
      .set('title', title)
      .set('target', target);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post<SendToTargetResponse>(
      `${this.baseUrl}/book/${md5}/send-to-kindle`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     LIBGEN ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Search for books on LibGen.
   * Always returns an array, even when the API returns a single object.
   */
  searchBooksLibGen(name: string, exact: boolean): Observable<BookDto[]> {
    const params = new HttpParams()
      .set('name', name)
      .set('exact', exact.toString());

    const label = `searchBooksLibGen:${name}:${exact}`;
    this.logger.debug('timer-start: ' + label);
    this.logger.log('[searchBooksLibGen] start', { name, exact });

    return this.http
      .get<BookDto | BookDto[]>(`${this.libgenBaseUrl}/book`, { params })
      .pipe(
        timeout(60000),
        map(res => (Array.isArray(res) ? res : [res])),
        tap(list => {
          const sample = list.slice(0, 3).map(b => ({
            title: b.title,
            md5: b.md5,
            format: b.format,
          }));
          this.logger.log('[searchBooksLibGen] result', { count: list.length, sample });
        }),
        catchError(error => {
          this.logger.error('[searchBooksLibGen] ERROR:', error);
          if (error.name === 'TimeoutError') {
            this.logger.error('[searchBooksLibGen] Request timed out after 60 seconds');
          }
          throw error;
        }),
        finalize(() => {
          this.logger.debug('timer-end: ' + label);
          this.logger.log('[searchBooksLibGen] done');
        })
      );
  }

  /**
   * Download a book file from LibGen using member credentials.
   */
  downloadMemberLibGen(md5: string, title: string, coverUrl?: string): Observable<Blob> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post(
      `${this.libgenBaseUrl}/book/${md5}/download/member`,
      null,
      { params, responseType: 'blob' }
    );
  }

  /**
   * Send a book from LibGen to the local library.
   */
  sendToLibraryLibGen(
    md5: string,
    title: string,
    coverUrl?: string,
    authors?: string,
    format?: string,
    fileSize?: string,
    source?: string,
    description?: string
  ): Observable<SendToTargetResponse> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) params = params.set('coverUrl', coverUrl);
    if (authors) params = params.set('authors', authors);
    if (format) params = params.set('format', format);
    if (fileSize) params = params.set('fileSize', fileSize);
    if (source) params = params.set('source', source);
    if (description) params = params.set('description', description);

    return this.http.post<SendToTargetResponse>(
      `${this.libgenBaseUrl}/book/${md5}/send-to-library`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     STATUS & HEALTH ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get current download status (remaining downloads).
   */
  getDownloadStatus(): Observable<DownloadStatusResponse> {
    return this.http.get<DownloadStatusResponse>(`${this.baseUrl}/download-status`);
  }

  /**
   * Check SLUM service health (proxied through backend to avoid CORS).
   */
  getSlumHealth(): Observable<unknown> {
    return this.http.get<unknown>(`${this.baseUrl}/slum-health`);
  }

  /**
   * Check mirror service health.
   */
  getMirrorHealth(): Observable<unknown> {
    return this.http.get<unknown>(`${this.baseUrl}/mirror-health`);
  }

  /* ══════════════════════════════════════════════════════════════
     COVER & DESCRIPTION LOOKUP
     ══════════════════════════════════════════════════════════════ */

  /**
   * Fetch cover URL for a book from external sources.
   */
  fetchCover(title: string, author?: string): Observable<CoverLookupResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<CoverLookupResponse>(
      `${this.baseUrl}/book/cover`,
      { params }
    );
  }

  /**
   * Fetch book description from Google Books.
   */
  fetchDescriptionFromGoogleBooks(title: string, author?: string): Observable<DescriptionLookupResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<DescriptionLookupResponse>(
      `${this.baseUrl}/book/description/google-books`,
      { params }
    );
  }

  /**
   * Fetch book description from OpenLibrary.
   */
  fetchDescriptionFromOpenLibrary(title: string, author?: string): Observable<DescriptionLookupResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<DescriptionLookupResponse>(
      `${this.baseUrl}/book/description/openlibrary`,
      { params }
    );
  }

  /**
   * Fetch book description from GPT-4.
   */
  fetchDescriptionFromGPT4(title: string, author?: string): Observable<DescriptionLookupResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<DescriptionLookupResponse>(
      `${this.baseUrl}/book/description/gpt`,
      { params }
    );
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { finalize, map, tap, timeout, catchError } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import { BookDto } from '../models/book-dto.model';
import { SEARCH_TIMEOUT_MS, LOG_SAMPLE_SIZE } from '../constants';

/* ─────────────── existing member-download shape ──────────────── */
export interface DownloadMemberResponse {
  downloadUrl: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

/* ─────────────── new send-to-boox shape (via Dropbox) ─────────────────────── */
export interface SendToBooxResponse {
  success: boolean;
  dropboxPath?: string;
  dropboxFileId?: string;
  message?: string;
  accountFastInfo: {
    downloadsLeft: number;
    downloadsPerDay: number;
  } | null;
}

export interface CoverLookupResponse {
  coverUrl: string | null;
}

export interface CoverCandidatesResponse {
  covers: string[];
}

export interface DescriptionLookupResponse {
  description: string | null;
}

@Injectable({ providedIn: 'root' })
export class AnnaArchiveApiService {
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
      this.logger.log('🔧 LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /* ══════════════════════════════════════════════════════════════
     Search – always return an array, even when the API sent 1 obj
     ══════════════════════════════════════════════════════════════ */
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
          const sample = list.slice(0, LOG_SAMPLE_SIZE).map(b => ({
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

  /* ══════════════════════════════════════════════════════════════
     Member download – downloads file with optional cover replacement
     ══════════════════════════════════════════════════════════════ */
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

  sendToLibrary(
    md5: string,
    title: string,
    coverUrl?: string,
    authors?: string,
    format?: string,
    fileSize?: string,
    source?: string,
    description?: string
  ): Observable<any> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    if (authors) {
      params = params.set('authors', authors);
    }
    if (format) {
      params = params.set('format', format);
    }
    if (fileSize) {
      params = params.set('fileSize', fileSize);
    }
    if (source) {
      params = params.set('source', source);
    }
    if (description) {
      params = params.set('description', description);
    }
    return this.http.post(
      `${this.baseUrl}/book/${md5}/send-to-library`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     Check download status – returns current download counter
     ══════════════════════════════════════════════════════════════ */
  getDownloadStatus(): Observable<{ accountFastInfo: { downloadsLeft: number; downloadsPerDay: number } | null }> {
    return this.http.get<{ accountFastInfo: { downloadsLeft: number; downloadsPerDay: number } | null }>(
      `${this.baseUrl}/download-status`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     LIBGEN ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /* ══════════════════════════════════════════════════════════════
     LibGen Search – searches LibGen instead of Anna's Archive
     ══════════════════════════════════════════════════════════════ */
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
        timeout(SEARCH_TIMEOUT_MS),
        map(res => (Array.isArray(res) ? res : [res])),
        tap(list => {
          const sample = list.slice(0, LOG_SAMPLE_SIZE).map(b => ({
            title: b.title,
            md5: b.md5,
            format: b.format,
          }));
          this.logger.log('[searchBooksLibGen] result', { count: list.length, sample });
        }),
        catchError(error => {
          this.logger.error('[searchBooksLibGen] ERROR:', error);
          if (error.name === 'TimeoutError') {
            this.logger.error(`[searchBooksLibGen] Request timed out after ${SEARCH_TIMEOUT_MS / 1000} seconds`);
          }
          throw error;
        }),
        finalize(() => {
          this.logger.debug('timer-end: ' + label);
          this.logger.log('[searchBooksLibGen] done');
        })
      );
  }

  /* ══════════════════════════════════════════════════════════════
     LibGen Member download – downloads file with optional cover replacement
     ══════════════════════════════════════════════════════════════ */
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

  sendToLibraryLibGen(
    md5: string,
    title: string,
    coverUrl?: string,
    authors?: string,
    format?: string,
    fileSize?: string,
    source?: string,
    description?: string
  ): Observable<any> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    if (authors) {
      params = params.set('authors', authors);
    }
    if (format) {
      params = params.set('format', format);
    }
    if (fileSize) {
      params = params.set('fileSize', fileSize);
    }
    if (source) {
      params = params.set('source', source);
    }
    if (description) {
      params = params.set('description', description);
    }
    return this.http.post(
      `${this.libgenBaseUrl}/book/${md5}/send-to-library`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     SLUM Health Status – proxied through backend to avoid CORS
     ══════════════════════════════════════════════════════════════ */
  getSlumHealth(): Observable<any> {
    return this.http.get<any>(`${this.baseUrl}/slum-health`);
  }

  getMirrorHealth(): Observable<any> {
    return this.http.get<any>(`${this.baseUrl}/mirror-health`);
  }

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

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Dropbox for Boox sync
     – passes book title so backend can name it correctly
     – optionally passes coverUrl for cover replacement
     ══════════════════════════════════════════════════════════════ */
  sendToBoox(md5: string, title: string, coverUrl?: string): Observable<SendToBooxResponse> {
    let params = new HttpParams().set('title', title);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-boox`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Kindle via email
     – optionally passes coverUrl for cover replacement
     ══════════════════════════════════════════════════════════════ */
  sendToKindle(md5: string, title: string, target: 'dad' | 'mom', coverUrl?: string): Observable<SendToBooxResponse> {
    let params = new HttpParams()
      .set('title', title)
      .set('target', target);
    if (coverUrl) {
      params = params.set('coverUrl', coverUrl);
    }
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-kindle`,
      null,
      { params }
    );
  }

}

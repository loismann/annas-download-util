import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

import { BookDto } from '../models/book-dto.model';
import {
  DropboxChapterContent,
  DropboxEpubChaptersResponse,
  DropboxEpubFile,
  DropboxEpubStatus,
  DropboxBookSearchResult,
  SummarizeResponse,
  SummarizeRequestPayload,
  LearnMoreRequestPayload,
  LearnMoreResponse,
  FlashcardRequestPayload,
  FlashcardItem,
  WikiImagesResponse
} from '../models/dropbox-epub.model';

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

/* ─────────────── gaming PC control response ─────────────────────── */
export interface GamingToggleResponse {
  success: boolean;
  action: string;
  message: string;
  output?: string;
  error?: string;
}

/* ─────────────── gaming PC status response ─────────────────────── */
export interface GamingStatusResponse {
  isOnline: boolean;
  ipAddress: string;
  lastChecked: string;
  error?: string;
}

@Injectable({ providedIn: 'root' })
export class AnnaArchiveApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly baseUrl = this.isLocalDev
    ? 'http://localhost:5050/api/anna'
    : 'https://fs01pfbooks.synology.me:5051/api/anna';
  private readonly gamingBaseUrl = this.isLocalDev
    ? 'http://localhost:5050/api/gaming'
    : 'https://fs01pfbooks.synology.me:5051/api/gaming';

  constructor(private http: HttpClient) {
    if (this.isLocalDev) {
      console.log('🔧 LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /* ══════════════════════════════════════════════════════════════
     Search – always return an array, even when the API sent 1 obj
     ══════════════════════════════════════════════════════════════ */
  searchBooks(name: string, exact: boolean): Observable<BookDto[]> {
    const params = new HttpParams()
      .set('name', name)
      .set('exact', exact.toString());

    return this.http
      .get<BookDto | BookDto[]>(`${this.baseUrl}/book`, { params })
      .pipe(map(res => (Array.isArray(res) ? res : [res])));
  }

  /* ══════════════════════════════════════════════════════════════
     Member download – returns fast-download URL + counter
     ══════════════════════════════════════════════════════════════ */
  downloadMember(md5: string): Observable<DownloadMemberResponse> {
    return this.http.get<DownloadMemberResponse>(
      `${this.baseUrl}/book/${md5}/download/member`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Dropbox for Boox sync
     – passes book title so backend can name it correctly
     ══════════════════════════════════════════════════════════════ */
  sendToBoox(md5: string, title: string): Observable<SendToBooxResponse> {
    const params = new HttpParams().set('title', title);
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-boox`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Send the file to Kindle via email
     ══════════════════════════════════════════════════════════════ */
  sendToKindle(md5: string, title: string, target: 'dad' | 'mom'): Observable<SendToBooxResponse> {
    const params = new HttpParams()
      .set('title', title)
      .set('target', target);
    return this.http.post<SendToBooxResponse>(
      `${this.baseUrl}/book/${md5}/send-to-kindle`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Dropbox EPUB reader endpoints
     ══════════════════════════════════════════════════════════════ */
  getDropboxEpubs(): Observable<DropboxEpubFile[]> {
    return this.http.get<DropboxEpubFile[]>(`${this.baseUrl}/dropbox/epubs`);
  }

  getDropboxEpubChapters(path: string): Observable<DropboxEpubChaptersResponse> {
    const params = new HttpParams().set('path', path);
    return this.http.get<DropboxEpubChaptersResponse>(
      `${this.baseUrl}/dropbox/epub/chapters`,
      { params }
    );
  }

  getDropboxChapterContent(path: string, chapterId: number): Observable<DropboxChapterContent> {
    const params = new HttpParams()
      .set('path', path)
      .set('chapterId', chapterId.toString());

    return this.http.get<DropboxChapterContent>(
      `${this.baseUrl}/dropbox/epub/chapter`,
      { params }
    );
  }

  getDropboxEpubStatus(path: string): Observable<DropboxEpubStatus> {
    const params = new HttpParams().set('path', path);
    return this.http.get<DropboxEpubStatus>(
      `${this.baseUrl}/dropbox/epub/status`,
      { params }
    );
  }

  startDropboxIndex(path: string): Observable<{ started: boolean }> {
    const params = new HttpParams().set('path', path);
    return this.http.post<{ started: boolean }>(
      `${this.baseUrl}/dropbox/epub/index`,
      null,
      { params }
    );
  }

  deleteDropboxIndex(path: string): Observable<{ removed: boolean }> {
    const params = new HttpParams().set('path', path);
    return this.http.delete<{ removed: boolean }>(
      `${this.baseUrl}/dropbox/epub/index`,
      { params }
    );
  }

  searchDropboxBook(path: string, query: string): Observable<DropboxBookSearchResult[]> {
    const params = new HttpParams()
      .set('path', path)
      .set('query', query);

    return this.http.get<DropboxBookSearchResult[]>(
      `${this.baseUrl}/dropbox/epub/search`,
      { params }
    );
  }

  summarizeText(payload: SummarizeRequestPayload): Observable<SummarizeResponse> {
    // summarize endpoint is outside /api/anna prefix
    const apiBase = this.baseUrl.replace('/api/anna', '');
    return this.http.post<SummarizeResponse>(
      `${apiBase}/api/ai/summarize`,
      payload
    );
  }

  learnMore(payload: LearnMoreRequestPayload): Observable<LearnMoreResponse> {
    const apiBase = this.baseUrl.replace('/api/anna', '');
    return this.http.post<LearnMoreResponse>(
      `${apiBase}/api/ai/vocab/learn-more`,
      payload
    );
  }

  createFlashcard(payload: FlashcardRequestPayload): Observable<FlashcardItem> {
    const apiBase = this.baseUrl.replace('/api/anna', '');
    return this.http.post<FlashcardItem>(
      `${apiBase}/api/ai/flashcards`,
      payload
    );
  }

  getFlashcards(dropboxPath: string): Observable<FlashcardItem[]> {
    const apiBase = this.baseUrl.replace('/api/anna', '');
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.get<FlashcardItem[]>(
      `${apiBase}/api/ai/flashcards`,
      { params }
    );
  }

  clearFlashcards(dropboxPath: string): Observable<{ cleared: boolean }> {
    const apiBase = this.baseUrl.replace('/api/anna', '');
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.delete<{ cleared: boolean }>(
      `${apiBase}/api/ai/flashcards`,
      { params }
    );
  }

  getWikiImages(term: string): Observable<WikiImagesResponse> {
    const apiBase = this.baseUrl.replace('/api/anna', '');
    const params = new HttpParams().set('term', term);
    return this.http.get<WikiImagesResponse>(
      `${apiBase}/api/media/wiki-images`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Toggle gaming PC (wake/sleep)
     ══════════════════════════════════════════════════════════════ */
  toggleGamingPC(action: 1 | 2): Observable<GamingToggleResponse> {
    const params = new HttpParams().set('action', action.toString());
    return this.http.post<GamingToggleResponse>(
      `${this.gamingBaseUrl}/toggle`,
      null,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Check gaming PC status (online/offline)
     ══════════════════════════════════════════════════════════════ */
  getGamingPCStatus(): Observable<GamingStatusResponse> {
    return this.http.get<GamingStatusResponse>(
      `${this.gamingBaseUrl}/status`
    );
  }
}

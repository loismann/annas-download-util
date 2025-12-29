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
  FlashcardResult,
  WikiImagesResponse,
  FullChapterSummaryRequest,
  FullChapterSummaryResponse,
  TokenUsageResponse,
  ChunkBoundariesResponse,
  SectionSummaryRequest,
  SectionSummaryResponse,
  CharacterGraphResponse,
  CharacterGraphRequest,
  CharacterGraphUpdateRequest
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
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5050'
    : 'https://fs01pfbooks.synology.me:5051';
  private readonly baseUrl = `${this.apiHost}/api/anna`;
  private readonly aiBaseUrl = `${this.apiHost}/api/ai`;
  private readonly gamingBaseUrl = `${this.apiHost}/api/gaming`;

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
    return this.http.post<SummarizeResponse>(
      `${this.aiBaseUrl}/summarize`,
      payload
    );
  }

  summarizeFullChapter(payload: FullChapterSummaryRequest): Observable<FullChapterSummaryResponse> {
    return this.http.post<FullChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter`,
      payload
    );
  }

  getFullChapterSummary(dropboxPath: string, chapterId: number): Observable<FullChapterSummaryResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<FullChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter`,
      { params }
    );
  }

  getTokenUsage(): Observable<TokenUsageResponse> {
    return this.http.get<TokenUsageResponse>(
      `${this.aiBaseUrl}/usage`
    );
  }

  learnMore(payload: LearnMoreRequestPayload): Observable<LearnMoreResponse> {
    return this.http.post<LearnMoreResponse>(
      `${this.aiBaseUrl}/vocab/learn-more`,
      payload
    );
  }

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

  getFlashcards(dropboxPath: string): Observable<FlashcardItem[]> {
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.get<FlashcardItem[]>(
      `${this.aiBaseUrl}/flashcards`,
      { params }
    );
  }

  clearFlashcards(dropboxPath: string): Observable<{ cleared: boolean }> {
    const params = new HttpParams().set('path', dropboxPath);
    return this.http.delete<{ cleared: boolean }>(
      `${this.aiBaseUrl}/flashcards`,
      { params }
    );
  }

  getWikiImages(term: string): Observable<WikiImagesResponse> {
    const params = new HttpParams().set('term', term);
    return this.http.get<WikiImagesResponse>(
      `${this.aiBaseUrl}/media/wiki-images`,
      { params }
    );
  }

  getAllCachedSummaries(dropboxPath: string): Observable<{ [chapterId: number]: any }> {
    const params = new HttpParams().set('dropboxPath', dropboxPath);
    return this.http.get<{ [chapterId: number]: any }>(
      `${this.aiBaseUrl}/summarize/book`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Get chunk boundaries (auto-detects if not cached)
     ══════════════════════════════════════════════════════════════ */
  getChunkBoundaries(dropboxPath: string, chapterId: number): Observable<ChunkBoundariesResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<ChunkBoundariesResponse>(
      `${this.aiBaseUrl}/chunk-boundaries`,
      { params }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Get cached section summary (no generation)
     ══════════════════════════════════════════════════════════════ */
  getCachedSectionSummary(dropboxPath: string, chapterId: number, sectionIndex: number): Observable<SectionSummaryResponse> {
    return this.http.get<SectionSummaryResponse>(
      `${this.aiBaseUrl}/section-summary?dropboxPath=${encodeURIComponent(dropboxPath)}&chapterId=${chapterId}&sectionIndex=${sectionIndex}`
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Generate section summary using GPT-5.2
     ══════════════════════════════════════════════════════════════ */
  generateSectionSummary(payload: SectionSummaryRequest): Observable<SectionSummaryResponse> {
    return this.http.post<SectionSummaryResponse>(
      `${this.aiBaseUrl}/section-summary`,
      payload
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Save section vocabulary to cache
     ══════════════════════════════════════════════════════════════ */
  saveSectionVocab(dropboxPath: string, chapterId: number, sectionIndex: number, vocab: FlashcardItem[]): Observable<{ success: boolean; vocabCount: number }> {
    return this.http.post<{ success: boolean; vocabCount: number }>(
      `${this.aiBaseUrl}/section-vocab`,
      { dropboxPath, chapterId, sectionIndex, vocab }
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

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Character Graph APIs
     ══════════════════════════════════════════════════════════════ */
  generateCharacterGraph(payload: CharacterGraphRequest): Observable<CharacterGraphResponse> {
    return this.http.post<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/graph`,
      payload
    );
  }

  getCharacterGraph(dropboxPath: string): Observable<CharacterGraphResponse> {
    const params = new HttpParams().set('dropboxPath', dropboxPath);
    return this.http.get<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/graph`,
      { params }
    );
  }

  updateCharacterGraph(payload: CharacterGraphUpdateRequest): Observable<CharacterGraphResponse> {
    return this.http.post<CharacterGraphResponse>(
      `${this.aiBaseUrl}/characters/update`,
      payload
    );
  }
}

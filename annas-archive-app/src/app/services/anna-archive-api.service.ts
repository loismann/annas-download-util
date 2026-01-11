import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { finalize, map, tap, timeout, catchError } from 'rxjs/operators';

import { BookDto } from '../models/book-dto.model';
import {
  DropboxChapterContent,
  DropboxEpubChaptersResponse,
  DropboxEpubFile,
  DropboxEpubStatus,
  DropboxBookSearchResult,
  LibraryReaderBook,
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

export interface CoverLookupResponse {
  coverUrl: string | null;
}

export interface CoverCandidatesResponse {
  covers: string[];
}

export interface AiBookSearchItem {
  title: string;
  author: string;
  summary: string;
  importance: string;
  coverUrl?: string | null;
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

@Injectable({ providedIn: 'root' })
export class AnnaArchiveApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : 'https://fs01pfbooks.synology.me:5051';
  private readonly baseUrl = `${this.apiHost}/api/anna`;
  private readonly libgenBaseUrl = `${this.apiHost}/api/libgen`;
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

    const label = `searchBooks:${name}:${exact}`;
    console.time(label);
    console.log('[searchBooks] start', { name, exact });

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
          console.log('[searchBooks] result', { count: list.length, sample });
        }),
        finalize(() => {
          console.timeEnd(label);
          console.log('[searchBooks] done');
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
    source?: string
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
    console.time(label);
    console.log('[searchBooksLibGen] start', { name, exact });

    return this.http
      .get<BookDto | BookDto[]>(`${this.libgenBaseUrl}/book`, { params })
      .pipe(
        timeout(60000), // 60 second timeout for the entire request
        map(res => (Array.isArray(res) ? res : [res])),
        tap(list => {
          const sample = list.slice(0, 3).map(b => ({
            title: b.title,
            md5: b.md5,
            format: b.format,
          }));
          console.log('[searchBooksLibGen] result', { count: list.length, sample });
        }),
        catchError(error => {
          console.error('[searchBooksLibGen] ERROR:', error);
          if (error.name === 'TimeoutError') {
            console.error('[searchBooksLibGen] Request timed out after 60 seconds');
          }
          throw error;
        }),
        finalize(() => {
          console.timeEnd(label);
          console.log('[searchBooksLibGen] done');
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
    source?: string
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
    return this.http.post(
      `${this.libgenBaseUrl}/book/${md5}/send-to-library`,
      null,
      { params }
    );
  }

  getLibraryBooks(): Observable<any[]> {
    return this.http.get<any[]>(`${this.apiHost}/api/library/books`);
  }

  getLibraryReaderBooks(): Observable<LibraryReaderBook[]> {
    return this.http.get<LibraryReaderBook[]>(`${this.apiHost}/api/library/reader/books`);
  }

  sendLibraryToKindle(
    fileName: string,
    title: string | undefined,
    target: 'dad' | 'mom',
    toDropbox = false
  ): Observable<any> {
    let params = new HttpParams()
      .set('fileName', fileName)
      .set('target', target)
      .set('toDropbox', toDropbox.toString());
    if (title) {
      params = params.set('title', title);
    }
    return this.http.post(
      `${this.apiHost}/api/library/book/send-to-kindle`,
      null,
      { params }
    );
  }

  updateLibraryBookMetadata(fileName: string, metadata: {
    primaryGenre: string;
    tags: string[];
    series: string | null;
    title?: string;
    authors?: string[];
  }): Observable<any> {
    return this.http.patch(
      `${this.apiHost}/api/library/book/${encodeURIComponent(fileName)}/metadata`,
      metadata
    );
  }

  updateLibraryBookCover(fileName: string, coverUrl: string): Observable<{ coverUrl: string | null }> {
    return this.http.post<{ coverUrl: string | null }>(
      `${this.apiHost}/api/library/book/${encodeURIComponent(fileName)}/cover`,
      { coverUrl }
    );
  }

  updateLibraryBookRatings(fileName: string, ratings: {
    goodreadsRating?: number | null;
    personalRating?: number | null;
  }): Observable<any> {
    return this.http.patch(
      `${this.apiHost}/api/library/book/${encodeURIComponent(fileName)}/ratings`,
      ratings
    );
  }

  updateLibraryBookReaderEnabled(fileName: string, enabled: boolean): Observable<{ success: boolean; enabled: boolean }> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.post<{ success: boolean; enabled: boolean }>(
      `${this.apiHost}/api/library/book/reader`,
      { enabled },
      { params }
    );
  }

  wipeLibraryGenres(): Observable<{ success: boolean; updated: number }> {
    return this.http.post<{ success: boolean; updated: number }>(
      `${this.apiHost}/api/library/books/genres/wipe`,
      null
    );
  }

  deleteLibraryBook(fileName: string): Observable<{ success: boolean }> {
    return this.http.delete<{ success: boolean }>(
      `${this.apiHost}/api/library/book/${encodeURIComponent(fileName)}`
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

  fetchLibraryCoverCandidates(title: string, author?: string): Observable<CoverCandidatesResponse> {
    let params = new HttpParams().set('title', title);
    if (author) {
      params = params.set('author', author);
    }
    return this.http.get<CoverCandidatesResponse>(
      `${this.apiHost}/api/library/book/cover-candidates`,
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

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Library EPUB reader endpoints
     ══════════════════════════════════════════════════════════════ */
  getLibraryReaderChapters(fileName: string): Observable<DropboxEpubChaptersResponse> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.get<DropboxEpubChaptersResponse>(
      `${this.apiHost}/api/library/reader/epub/chapters`,
      { params }
    );
  }

  getLibraryReaderChapterContent(fileName: string, chapterId: number): Observable<DropboxChapterContent> {
    const params = new HttpParams()
      .set('fileName', fileName)
      .set('chapterId', chapterId.toString());
    return this.http.get<DropboxChapterContent>(
      `${this.apiHost}/api/library/reader/epub/chapter`,
      { params }
    );
  }

  getLibraryReaderStatus(fileName: string): Observable<DropboxEpubStatus> {
    const params = new HttpParams().set('fileName', fileName);
    return this.http.get<DropboxEpubStatus>(
      `${this.apiHost}/api/library/reader/epub/status`,
      { params }
    );
  }

  startLibraryReaderIndex(fileName: string): Observable<{ success: boolean }> {
    return this.http.post<{ success: boolean }>(
      `${this.apiHost}/api/library/reader/epub/index`,
      { fileName }
    );
  }

  deleteLibraryReaderIndex(fileName: string): Observable<{ success: boolean }> {
    return this.http.request<{ success: boolean }>(
      'delete',
      `${this.apiHost}/api/library/reader/epub/index`,
      { body: { fileName } }
    );
  }

  searchLibraryReaderBook(fileName: string, query: string): Observable<DropboxBookSearchResult[]> {
    const params = new HttpParams()
      .set('fileName', fileName)
      .set('query', query);
    return this.http.get<DropboxBookSearchResult[]>(
      `${this.apiHost}/api/library/reader/epub/search`,
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

  getUltraChapterSummary(dropboxPath: string, chapterId: number): Observable<UltraChapterSummaryResponse> {
    const params = new HttpParams()
      .set('dropboxPath', dropboxPath)
      .set('chapterId', chapterId.toString());
    return this.http.get<UltraChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter/dummy`,
      { params }
    );
  }

  generateUltraChapterSummary(payload: UltraChapterSummaryRequest): Observable<UltraChapterSummaryResponse> {
    return this.http.post<UltraChapterSummaryResponse>(
      `${this.aiBaseUrl}/summarize/chapter/dummy`,
      payload
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

  aiBookSearch(query: string): Observable<AiBookSearchResult> {
    return this.http.post<AiBookSearchResult>(
      `${this.aiBaseUrl}/book-search`,
      { query }
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

  deleteFlashcard(dropboxPath: string, term: string): Observable<{ deleted: boolean }> {
    const params = new HttpParams()
      .set('path', dropboxPath)
      .set('term', term);
    return this.http.delete<{ deleted: boolean }>(
      `${this.aiBaseUrl}/flashcard`,
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

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Suggest authors for a book title
     ══════════════════════════════════════════════════════════════ */
  suggestAuthors(bookTitle: string): Observable<SuggestAuthorsResponse> {
    return this.http.post<SuggestAuthorsResponse>(
      `${this.aiBaseUrl}/suggest-authors`,
      { bookTitle }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Get related books (same series + other series by author)
     ══════════════════════════════════════════════════════════════ */
  getRelatedBooks(bookTitle: string, author: string): Observable<RelatedBooksResponse> {
    return this.http.post<RelatedBooksResponse>(
      `${this.aiBaseUrl}/related-books`,
      { bookTitle, author }
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Match series books using GPT
     ══════════════════════════════════════════════════════════════ */
  matchSeriesBooks(request: MatchSeriesBooksRequest): Observable<MatchSeriesBooksResponse> {
    return this.http.post<MatchSeriesBooksResponse>(
      `${this.aiBaseUrl}/match-series-books`,
      request
    );
  }

  /* ══════════════════════════════════════════════════════════════
     NEW  ➜  Server-side vocabulary storage with book associations
     ══════════════════════════════════════════════════════════════ */
  getKnownWords(): Observable<{ [term: string]: string[] }> {
    return this.http.get<{ [term: string]: string[] }>(`${this.apiHost}/api/vocab/known`);
  }

  addKnownWord(term: string, bookId?: string): Observable<void> {
    return this.http.post<void>(`${this.apiHost}/api/vocab/known`, { term, bookId });
  }

  removeKnownWord(term: string): Observable<void> {
    return this.http.delete<void>(`${this.apiHost}/api/vocab/known/${encodeURIComponent(term)}`);
  }

  getStudyWords(): Observable<{ [term: string]: { definition: string; books: string[] } }> {
    return this.http.get<{ [term: string]: { definition: string; books: string[] } }>(`${this.apiHost}/api/vocab/study`);
  }

  addStudyWord(term: string, definition?: string, bookId?: string): Observable<void> {
    return this.http.post<void>(`${this.apiHost}/api/vocab/study`, { term, definition, bookId });
  }

  removeStudyWord(term: string): Observable<void> {
    return this.http.delete<void>(`${this.apiHost}/api/vocab/study/${encodeURIComponent(term)}`);
  }

  deleteBookVocab(bookId: string): Observable<{ success: boolean; knownWordsAffected: number; studyWordsAffected: number; totalRemoved: number }> {
    return this.http.delete<{ success: boolean; knownWordsAffected: number; studyWordsAffected: number; totalRemoved: number }>(
      `${this.apiHost}/api/vocab/book/${encodeURIComponent(bookId)}`
    );
  }
}

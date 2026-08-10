import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { apiBase } from '../../services/api-base';
import { AuthService } from '../../services/auth.service';
import {
  Book, Bookmark, Chapter, ChapterList, DeepDive, Flashcards, Lens, ProgressStep, Prose,
  ReadingPosition, ReadingPreferences, SearchHit, SectionInfo, SectionVocabulary,
  StoryModel, TermState, VocabularyTerm
} from '../reader2.models';

/**
 * One method per route, and nothing else.
 *
 * No caching, no state, no retry policy: the store above decides what to keep
 * and the server decides what a request costs. Reader I mixed all three into its
 * API layer, which is why "does opening a chapter spend money" had no single
 * answer.
 */
@Injectable({ providedIn: 'root' })
export class Reader2ApiService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = `${apiBase()}/api/reader2`;

  // ─── shelf ──────────────────────────────────────────────────────────

  lenses(): Observable<Lens[]> {
    return this.http.get<Lens[]>(`${this.base}/lenses`);
  }

  books(): Observable<Book[]> {
    return this.http.get<Book[]>(`${this.base}/books`);
  }

  enrol(fileName: string, lensKey?: string): Observable<Book> {
    return this.http.post<Book>(`${this.base}/books`, { fileName, lensKey });
  }

  setLens(bookId: string, lensKey: string): Observable<Book> {
    return this.http.patch<Book>(`${this.base}/books/${bookId}`, { lensKey });
  }

  unenrol(bookId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/books/${bookId}`);
  }

  // ─── reading ────────────────────────────────────────────────────────

  chapters(bookId: string): Observable<ChapterList> {
    return this.http.get<ChapterList>(`${this.base}/books/${bookId}/chapters`);
  }

  chapter(bookId: string, chapter: number): Observable<Chapter> {
    return this.http.get<Chapter>(`${this.base}/books/${bookId}/chapters/${chapter}`);
  }

  sections(bookId: string, chapter: number): Observable<SectionInfo[]> {
    return this.http.get<SectionInfo[]>(`${this.base}/books/${bookId}/chapters/${chapter}/sections`);
  }

  search(bookId: string, query: string): Observable<SearchHit[]> {
    return this.http.get<SearchHit[]>(`${this.base}/books/${bookId}/search`, {
      params: new HttpParams().set('q', query)
    });
  }

  position(bookId: string): Observable<ReadingPosition> {
    return this.http.get<ReadingPosition>(`${this.base}/books/${bookId}/position`);
  }

  savePosition(bookId: string, chapter: number, wordOffset: number): Observable<void> {
    return this.http.put<void>(`${this.base}/books/${bookId}/position`, { chapter, wordOffset });
  }

  bookmarks(bookId: string): Observable<Bookmark[]> {
    return this.http.get<Bookmark[]>(`${this.base}/books/${bookId}/bookmarks`);
  }

  /** Saving the same place twice re-labels the mark rather than adding another. */
  saveBookmark(
    bookId: string, chapter: number, wordOffset: number, label: string | null
  ): Observable<Bookmark> {
    return this.http.post<Bookmark>(
      `${this.base}/books/${bookId}/bookmarks`, { chapter, wordOffset, label });
  }

  removeBookmark(bookId: string, bookmarkId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/books/${bookId}/bookmarks/${bookmarkId}`);
  }

  preferences(): Observable<ReadingPreferences> {
    return this.http.get<ReadingPreferences>(`${this.base}/preferences`);
  }

  savePreferences(preferences: ReadingPreferences): Observable<void> {
    return this.http.put<void>(`${this.base}/preferences`, preferences);
  }

  dropIndex(bookId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/books/${bookId}/index`);
  }

  exportUrl(bookId: string): string {
    return `${this.base}/books/${bookId}/export?format=md`;
  }

  // ─── generating: every one a POST, every one a click ────────────────

  sectionSummary(bookId: string, chapter: number, section: number, force = false): Observable<Prose> {
    return this.http.post<Prose>(
      `${this.base}/books/${bookId}/chapters/${chapter}/sections/${section}/summary`,
      null, { params: this.force(force) });
  }

  explainSimply(bookId: string, chapter: number, force = false): Observable<Prose> {
    return this.http.post<Prose>(
      `${this.base}/books/${bookId}/chapters/${chapter}/explain-simply`, null,
      { params: this.force(force) });
  }

  analysePassage(
    bookId: string, chapter: number, wordOffset: number, text: string, force = false
  ): Observable<Prose> {
    return this.http.post<Prose>(
      `${this.base}/books/${bookId}/passage-analysis`,
      { chapter, wordOffset, text }, { params: this.force(force) });
  }

  // ─── vocabulary ─────────────────────────────────────────────────────

  vocabulary(state?: TermState): Observable<VocabularyTerm[]> {
    return this.http.get<VocabularyTerm[]>(`${this.base}/vocabulary`, {
      params: state ? new HttpParams().set('state', state) : new HttpParams()
    });
  }

  saveTerm(term: string, state: TermState, definition?: string, bookId?: string): Observable<void> {
    return this.http.post<void>(`${this.base}/vocabulary`, { term, state, definition, bookId });
  }

  removeTerm(term: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/vocabulary/${encodeURIComponent(term)}`);
  }

  clearVocabulary(state?: TermState): Observable<{ removed: number }> {
    return this.http.delete<{ removed: number }>(`${this.base}/vocabulary`, {
      params: state ? new HttpParams().set('state', state) : new HttpParams()
    });
  }

  /** Drops this book's vocabulary provenance; the reader's terms survive. */
  forgetBookVocabulary(bookId: string): Observable<{ removed: number }> {
    return this.http.delete<{ removed: number }>(`${this.base}/books/${bookId}/vocabulary`);
  }

  sectionVocabulary(bookId: string, chapter: number, section: number): Observable<SectionVocabulary> {
    return this.http.get<SectionVocabulary>(
      `${this.base}/books/${bookId}/chapters/${chapter}/sections/${section}/vocabulary`);
  }

  generateSectionVocabulary(
    bookId: string, chapter: number, section: number, force = false
  ): Observable<SectionVocabulary> {
    return this.http.post<SectionVocabulary>(
      `${this.base}/books/${bookId}/chapters/${chapter}/sections/${section}/vocabulary`,
      null, { params: this.force(force) });
  }

  learnMore(bookId: string, term: string, context?: string, force = false): Observable<DeepDive> {
    return this.http.post<DeepDive>(
      `${this.base}/books/${bookId}/vocabulary/learn-more`,
      { term, context }, { params: this.force(force) });
  }

  flashcards(bookId: string): Observable<Flashcards> {
    return this.http.get<Flashcards>(`${this.base}/books/${bookId}/flashcards`);
  }

  addFlashcard(bookId: string, term: string, definition: string): Observable<Flashcards> {
    return this.http.post<Flashcards>(`${this.base}/books/${bookId}/flashcards`, { term, definition });
  }

  removeFlashcard(bookId: string, term: string): Observable<Flashcards> {
    return this.http.delete<Flashcards>(
      `${this.base}/books/${bookId}/flashcards/${encodeURIComponent(term)}`);
  }

  clearFlashcards(bookId: string): Observable<Flashcards> {
    return this.http.delete<Flashcards>(`${this.base}/books/${bookId}/flashcards`);
  }

  // ─── streams ────────────────────────────────────────────────────────

  /**
   * Extracts a book, reporting progress.
   *
   * `fetch` rather than `EventSource`, because `EventSource` cannot send an
   * Authorization header and cannot POST — and every route that does work here
   * is a POST behind a token.
   */
  ingest(bookId: string, force = false): Observable<StreamEvent<ChapterList>> {
    return this.stream(`${this.base}/books/${bookId}/ingest${force ? '?force=true' : ''}`);
  }

  /** The three-tier ladder, streamed because it can take a minute. */
  chapterSummary(bookId: string, chapter: number, force = false): Observable<StreamEvent<Prose>> {
    return this.stream(
      `${this.base}/books/${bookId}/chapters/${chapter}/summary${force ? '?force=true' : ''}`);
  }

  chapterVocabulary(bookId: string, chapter: number, force = false): Observable<StreamEvent<SectionVocabulary>> {
    return this.stream(
      `${this.base}/books/${bookId}/chapters/${chapter}/vocabulary${force ? '?force=true' : ''}`);
  }

  /**
   * Builds the story model from the chapters already summarised.
   *
   * One extraction call per summarised chapter and no re-summarising, so it is
   * the cheap half of the work — but a three-hundred-chapter novel is still a
   * long wait, which is why it streams.
   */
  backFillStoryModel(bookId: string): Observable<StreamEvent<StoryModel>> {
    return this.stream(`${this.base}/books/${bookId}/story-model/back-fill`);
  }

  storyModel(bookId: string, throughChapter: number): Observable<StoryModel> {
    return this.http.get<StoryModel>(
      `${this.base}/books/${bookId}/story-model`,
      { params: new HttpParams().set('throughChapter', throughChapter) });
  }

  /**
   * Answers one of the merger's questions.
   *
   * Reaches no model, so it costs nothing — but accepting is the only way an
   * entry is ever removed, which is why it takes an explicit answer rather than
   * being inferred from a click anywhere.
   */
  resolveMerge(bookId: string, mergeId: string, accept: boolean): Observable<StoryModel> {
    return this.http.post<StoryModel>(
      `${this.base}/books/${bookId}/story-model/merges/${mergeId}/resolve`, { accept });
  }

  private force(force: boolean): HttpParams {
    return force ? new HttpParams().set('force', 'true') : new HttpParams();
  }

  private stream<T>(url: string): Observable<StreamEvent<T>> {
    return new Observable<StreamEvent<T>>(subscriber => {
      const controller = new AbortController();

      readEventStream<T>(url, this.auth.getToken(), controller.signal,
        event => subscriber.next(event))
        .then(() => subscriber.complete())
        .catch((error: unknown) => subscriber.error(error));

      // Aborting on unsubscribe is what makes "the reader navigated away" a
      // cancelled request rather than a summary nobody will ever see.
      return () => controller.abort();
    });
  }
}

/** Either a progress step, the final payload, or the reason it stopped. */
export type StreamEvent<T> =
  | { kind: 'progress'; step: ProgressStep }
  | { kind: 'result'; value: T }
  | { kind: 'error'; message: string };

/**
 * Reads a server-sent-event stream from a POST.
 *
 * Exported for its own test: SSE framing is fiddly — events are separated by a
 * blank line and a chunk can split one in half — and getting it wrong shows up
 * as a stream that silently stops rather than as an error.
 */
export async function readEventStream<T>(
  url: string,
  token: string | null,
  signal: AbortSignal,
  emit: (event: StreamEvent<T>) => void
): Promise<void> {
  const response = await fetch(url, {
    method: 'POST',
    headers: token ? { Authorization: `Bearer ${token}` } : {},
    signal
  });

  if (!response.ok) {
    const body = await response.json().catch(() => null);
    emit({ kind: 'error', message: body?.error ?? body?.detail ?? `Request failed (${response.status}).` });
    return;
  }

  const reader = response.body?.getReader();
  if (!reader) return;

  const decoder = new TextDecoder();
  let buffer = '';

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });

    // Everything up to the last blank line is complete; the remainder may be
    // half a frame and has to wait for the next chunk.
    const frames = buffer.split('\n\n');
    buffer = frames.pop() ?? '';

    for (const frame of frames) {
      const parsed = parseFrame<T>(frame);
      if (parsed) emit(parsed);
    }
  }
}

function parseFrame<T>(frame: string): StreamEvent<T> | null {
  const lines = frame.split('\n');
  const name = lines.find(l => l.startsWith('event:'))?.slice(6).trim();
  const data = lines.find(l => l.startsWith('data:'))?.slice(5).trim();

  if (!data) return null;

  let payload: unknown;
  try {
    payload = JSON.parse(data);
  } catch {
    return null;
  }

  if (name === 'result') return { kind: 'result', value: payload as T };

  const step = payload as ProgressStep;
  return step.stage === 'error'
    ? { kind: 'error', message: step.message }
    : { kind: 'progress', step };
}

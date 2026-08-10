import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks, quietly } from './reader-tasks';
import { FitAt, pageIndexOf, pageStarts } from './pagination';
import {
  Book, ChapterInfo, ChapterList, DEFAULT_PREFERENCES, Lens, ReadingPreferences, SearchHit, SectionInfo
} from '../reader2.models';

/**
 * Everything the reader is looking at, as signals.
 *
 * <p>Angular signals rather than the app's usual BehaviorSubject-per-field: this
 * is the one place in the codebase that has genuinely derived state — the page
 * follows the word offset, the word offset follows the chapter, the page count
 * follows the font size — and `computed` expresses that without a subscription
 * to leak. Reader I spent roughly 2,283 lines keeping the same facts in step by
 * hand.</p>
 *
 * <p><b>Nothing here generates.</b> Every method that spends money is named for
 * it and is only ever called from a control the reader clicked. Opening a book,
 * turning a page, and changing the font all stay free.</p>
 */
@Injectable()
export class ReaderStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);

  /** One banner for the whole reader; see {@link ReaderTasks}. */
  readonly busy = this.tasks.busy;
  readonly error = this.tasks.error;

  // ─── what is loaded ─────────────────────────────────────────────────

  readonly lenses = signal<Lens[]>([]);
  readonly books = signal<Book[]>([]);
  readonly book = signal<Book | null>(null);
  readonly chapters = signal<ChapterInfo[]>([]);
  readonly bookTitle = signal('');

  readonly chapterIndex = signal(0);
  readonly chapterText = signal('');
  readonly sections = signal<SectionInfo[]>([]);

  readonly preferences = signal<ReadingPreferences>(DEFAULT_PREFERENCES);
  readonly searchHits = signal<SearchHit[]>([]);

  // ─── where the reader is ────────────────────────────────────────────

  /**
   * The unit of position everywhere: reading position, bookmarks, search hits,
   * and section boundaries are all word offsets, so none of them need converting
   * to be compared.
   */
  readonly wordOffset = signal(0);

  /**
   * How many words fit on a page starting at an offset. Injected by the shell —
   * measured from the real surface once one exists, an estimate until then —
   * so this store stays free of the DOM and testable with a lambda.
   */
  private readonly fit = signal<FitAt>(() => 300);

  /** The chapter as words — the unit every offset, slice, and fit shares. */
  readonly chapterWords = computed(() => splitWords(this.chapterText()));

  readonly totalWords = computed(() => this.chapterWords().length);

  /**
   * Where every page starts. Variable-length pages, because a page of long
   * words holds fewer of them — one number per chapter is the estimate that
   * either ran text past the bottom edge or left a gap above it.
   */
  readonly pageBounds = computed(() => pageStarts(this.totalWords(), this.fit()));

  readonly pageTotal = computed(() => this.pageBounds().length);
  readonly page = computed(() => pageIndexOf(this.pageBounds(), this.wordOffset()));

  readonly currentChapter = computed<ChapterInfo | null>(
    () => this.chapters()[this.chapterIndex()] ?? null);

  /**
   * Where the export lives. A link rather than a fetch, so the browser's own
   * download handles the file — and asked of the store so the API's base URL
   * stays known in exactly one place.
   */
  readonly exportUrl = computed<string | null>(() => {
    const book = this.book();
    return book ? this.api.exportUrl(book.bookId) : null;
  });

  readonly lens = computed<Lens | null>(() => {
    const key = this.book()?.lensKey;
    return this.lenses().find(l => l.key === key) ?? null;
  });

  /** The words on screen. Derived, so nothing has to remember to re-slice. */
  readonly visibleText = computed(() => {
    const starts = this.pageBounds();
    const from = starts[this.page()];
    const to = starts[this.page() + 1] ?? this.totalWords();

    return this.chapterWords().slice(from, to).join(' ');
  });

  readonly canPageBack = computed(() => this.page() > 0 || this.chapterIndex() > 0);
  readonly canPageForward = computed(
    () => this.page() < this.pageTotal() - 1 || this.chapterIndex() < this.chapters().length - 1);

  // ─── opening a book ─────────────────────────────────────────────────

  async loadShelfAsync(): Promise<void> {
    await this.tasks.run('Loading your shelf', async () => {
      this.lenses.set(await firstValueFrom(this.api.lenses()));
      this.books.set(await firstValueFrom(this.api.books()));
      this.preferences.set(await firstValueFrom(this.api.preferences()));
    });
  }

  /**
   * Opens a book, extracting it first if it has never been read.
   *
   * <p>Ingestion is local work and free, which is why it can happen on open —
   * the one thing on this path that can reach a model is chapter labelling, and
   * that is switchable off server-side and tolerates its own failure.</p>
   */
  async openAsync(bookId: string): Promise<void> {
    await this.tasks.run('Opening', async () => {
      this.book.set(this.books().find(b => b.bookId === bookId) ?? null);

      try {
        this.applyChapters(await firstValueFrom(this.api.chapters(bookId)));
      } catch {
        await this.ingestAsync(bookId);
      }

      const position = await firstValueFrom(this.api.position(bookId));
      await this.goToAsync(position.chapter, position.wordOffset);
    });
  }

  async ingestAsync(bookId: string, force = false): Promise<void> {
    await this.tasks.stream<ChapterList>(
      'Indexing', this.api.ingest(bookId, force), list => this.applyChapters(list));
  }

  /** Loads a chapter and puts the reader at a word offset within it. */
  async goToAsync(chapter: number, wordOffset = 0): Promise<void> {
    const book = this.book();
    if (!book) return;

    const index = clampIndex(chapter, this.chapters().length);

    await this.tasks.run('Loading the chapter', async () => {
      const loaded = await firstValueFrom(this.api.chapter(book.bookId, index));

      this.chapterIndex.set(index);
      this.chapterText.set(loaded.text);
      this.sections.set(await firstValueFrom(this.api.sections(book.bookId, index)));
      this.wordOffset.set(Math.max(0, wordOffset));
    });
  }

  // ─── moving around ──────────────────────────────────────────────────

  /** Forward a page, rolling into the next chapter at the end of this one. */
  async pageForwardAsync(): Promise<void> {
    if (this.page() < this.pageTotal() - 1) {
      this.wordOffset.set(this.pageBounds()[this.page() + 1]);
      await this.rememberPositionAsync();
      return;
    }

    if (this.chapterIndex() < this.chapters().length - 1) await this.goToAsync(this.chapterIndex() + 1);
  }

  /** Back a page, landing on the *last* page of the previous chapter. */
  async pageBackAsync(): Promise<void> {
    if (this.page() > 0) {
      this.wordOffset.set(this.pageBounds()[this.page() - 1]);
      await this.rememberPositionAsync();
      return;
    }

    if (this.chapterIndex() === 0) return;

    await this.goToAsync(this.chapterIndex() - 1);
    this.wordOffset.set(this.pageBounds()[this.pageTotal() - 1]);
  }

  /**
   * Re-measures after a resize or a font change, keeping the reader on the same
   * words: the offset is the anchor, and the page number is derived from it, so
   * the page number moves and the reader does not.
   */
  resize(fit: FitAt): void {
    this.fit.set(fit);
  }

  async rememberPositionAsync(): Promise<void> {
    const book = this.book();
    if (!book) return;

    await quietly(() => firstValueFrom(
      this.api.savePosition(book.bookId, this.chapterIndex(), this.wordOffset())));
  }

  /**
   * Un-enrols a book. The server cascades to its artifacts, positions,
   * bookmarks, and extracted text; the reader's own vocabulary survives, because
   * a word you learnt is not the book's to take back.
   */
  async unenrolAsync(bookId: string): Promise<void> {
    const removed = await this.tasks.run(
      'Removing the book',
      async () => { await firstValueFrom(this.api.unenrol(bookId)); return true; });

    if (!removed) return;

    this.books.update(books => books.filter(b => b.bookId !== bookId));
    if (this.book()?.bookId === bookId) this.book.set(null);
  }

  /**
   * Drops the extracted text and extracts again, keeping every artifact.
   *
   * <p>For a bad extraction, not for throwing away work: summaries are keyed by
   * chapter and lens, not by the text file, so they survive and re-attach.</p>
   */
  async reIndexAsync(bookId: string): Promise<void> {
    const dropped = await this.tasks.run(
      'Clearing the index',
      async () => { await firstValueFrom(this.api.dropIndex(bookId)); return true; });

    if (dropped) await this.ingestAsync(bookId, true);
  }

  // ─── settings and searching, both free ──────────────────────────────

  async savePreferencesAsync(preferences: ReadingPreferences): Promise<void> {
    // Applied locally first, so the page re-renders at the new size even if the
    // save fails — appearance is the reader's, and it should feel immediate.
    this.preferences.set(preferences);
    await quietly(() => firstValueFrom(this.api.savePreferences(preferences)));
  }

  async searchAsync(query: string): Promise<void> {
    const book = this.book();
    if (!book) return;

    await this.tasks.run('Searching', async () =>
      this.searchHits.set(await firstValueFrom(this.api.search(book.bookId, query))));
  }

  async setLensAsync(lensKey: string): Promise<void> {
    const book = this.book();
    if (!book) return;

    // Costs nothing and destroys nothing: artifacts are keyed by lens, so the
    // previous reading is still there if the reader switches back.
    await this.tasks.run('Changing book type', async () =>
      this.book.set(await firstValueFrom(this.api.setLens(book.bookId, lensKey))));
  }

  // ─── plumbing ───────────────────────────────────────────────────────

  private applyChapters(list: ChapterList): void {
    this.bookTitle.set(list.title);
    this.chapters.set(list.chapters);
  }
}

/** The same word-splitting the server does, so offsets mean one thing. */
function splitWords(text: string): string[] {
  return text.trim().length === 0 ? [] : text.trim().split(/\s+/);
}

function clampIndex(index: number, length: number): number {
  return Math.min(Math.max(index, 0), Math.max(0, length - 1));
}

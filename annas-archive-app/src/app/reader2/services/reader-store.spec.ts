import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ReaderStore } from './reader-store';
import { ReaderTasks } from './reader-tasks';
import { Reader2ApiService } from './reader2-api.service';
import { DEFAULT_PREFERENCES, Book, Chapter, ChapterList, Lens, ReadingPosition, ReadingPreferences } from '../reader2.models';

const BOOK: Book = {
  bookId: '0123456789abcdef', fileName: 'book.epub', title: 'A Book', authors: ['An Author'],
  lensKey: 'literary', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true
};

const LENSES: Lens[] = [
  { key: 'literary', displayName: 'Ideas', description: '', icon: 'psychology',
    sortOrder: 0, isDefault: true, buildsStoryModel: false, storyVocabulary: null }
];

const PREFERENCES: ReadingPreferences = DEFAULT_PREFERENCES;

/** 1,000 words, so page arithmetic in the store is exact. */
const TEXT = Array.from({ length: 1000 }, (_, i) => `w${i}`).join(' ');

function chapterList(count = 3): ChapterList {
  return {
    title: 'A Book', lensKey: 'literary',
    chapters: Array.from({ length: count }, (_, i) => ({
      id: i, title: `Chapter ${i + 1}`, level: 0, wordCount: 1000, hasSummary: false, summaryIsStale: false
    }))
  };
}

describe('ReaderStore', () => {
  let api: jasmine.SpyObj<Reader2ApiService>;
  let store: ReaderStore;
  let saved: ReadingPosition | null;

  beforeEach(() => {
    saved = null;

    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'lenses', 'books', 'preferences', 'chapters', 'chapter', 'sections',
      'position', 'savePosition', 'savePreferences', 'search', 'setLens', 'ingest'
    ]);

    api.lenses.and.returnValue(of(LENSES));
    api.books.and.returnValue(of([BOOK]));
    api.preferences.and.returnValue(of(PREFERENCES));
    api.chapters.and.returnValue(of(chapterList()));
    api.chapter.and.callFake((_: string, n: number) =>
      of<Chapter>({ chapter: { id: n, title: `Chapter ${n + 1}`, level: 0, wordCount: 1000, hasSummary: false, summaryIsStale: false }, text: TEXT }));
    api.sections.and.returnValue(of([]));
    api.position.and.returnValue(of({ chapter: 0, wordOffset: 0, updatedAtUtc: '' }));
    api.savePosition.and.callFake((_: string, chapter: number, wordOffset: number) => {
      saved = { chapter, wordOffset, updatedAtUtc: '' };
      return of(void 0);
    });

    TestBed.configureTestingModule({
      providers: [ReaderTasks, ReaderStore, { provide: Reader2ApiService, useValue: api }]
    });

    store = TestBed.inject(ReaderStore);
  });

  async function open(): Promise<void> {
    await store.loadShelfAsync();
    await store.openAsync(BOOK.bookId);
    store.resize(() => 300);
  }

  it('loads the shelf and the reader’s own preferences', async () => {
    await store.loadShelfAsync();

    expect(store.lenses().length).toBe(1);
    expect(store.books()).toEqual([BOOK]);
    expect(store.preferences().fontSize).toBe(18);
  });

  it('opens a book at the position the reader left it', async () => {
    api.position.and.returnValue(of({ chapter: 2, wordOffset: 600, updatedAtUtc: '' }));

    await store.loadShelfAsync();
    await store.openAsync(BOOK.bookId);

    expect(store.chapterIndex()).toBe(2);
    expect(store.wordOffset()).toBe(600);
  });

  it('shows only the current page’s words', async () => {
    await open();

    expect(store.visibleParagraphs().join(' ').split(' ').length).toBe(300);
    expect(store.visibleParagraphs()[0].startsWith('w0 ')).toBeTrue();
  });

  it('pages forward and back within a chapter', async () => {
    await open();

    await store.pageForwardAsync();
    expect(store.page()).toBe(1);
    expect(store.visibleParagraphs()[0].startsWith('w300 ')).toBeTrue();

    await store.pageBackAsync();
    expect(store.page()).toBe(0);
  });

  it('rolls into the next chapter at the end of this one', async () => {
    await open();
    store.wordOffset.set(999);

    await store.pageForwardAsync();

    expect(store.chapterIndex()).toBe(1);
    expect(store.page()).toBe(0);
  });

  /** Landing on page one of the previous chapter would lose the reader's place. */
  it('lands on the last page of the previous chapter when paging back off the start', async () => {
    await open();
    await store.goToAsync(1);

    await store.pageBackAsync();

    expect(store.chapterIndex()).toBe(0);
    expect(store.page()).toBe(store.pageTotal() - 1);
  });

  it('stops at both ends of the book rather than wrapping', async () => {
    await open();
    expect(store.canPageBack()).toBeFalse();

    await store.goToAsync(2);
    store.wordOffset.set(999);
    expect(store.canPageForward()).toBeFalse();
  });

  /** Resizing a window must not move somebody in a book. */
  it('keeps the reader on the same words across a resize', async () => {
    await open();
    await store.pageForwardAsync();
    await store.pageForwardAsync();
    const before = store.wordOffset();

    store.resize(() => 150);

    expect(store.wordOffset()).toBe(before);
    expect(store.page()).toBe(4);
  });

  it('remembers the position when the reader turns a page', async () => {
    await open();

    await store.pageForwardAsync();

    expect(saved).toEqual({ chapter: 0, wordOffset: 300, updatedAtUtc: '' });
  });

  /** A failed save is not worth an error banner over the text. */
  it('does not surface an error when saving the position fails', async () => {
    await open();
    api.savePosition.and.returnValue(throwError(() => new Error('offline')));

    await store.pageForwardAsync();

    expect(store.error()).toBeNull();
  });

  it('surfaces the server’s own sentence when something fails', async () => {
    api.chapters.and.returnValue(
      throwError(() => ({ error: { error: 'This book has not been extracted yet.' } })));
    api.position.and.returnValue(throwError(() => new Error('no')));
    api.ingest.and.returnValue(throwError(() => ({ error: { error: 'nope' } })));

    await store.loadShelfAsync();
    await store.openAsync(BOOK.bookId);

    expect(store.error()).toBeTruthy();
  });

  it('clears the error and the busy flag when work succeeds', async () => {
    await open();

    expect(store.busy()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('changing the book type costs nothing and only re-reads the book', async () => {
    await open();
    api.setLens.and.returnValue(of({ ...BOOK, lensKey: 'fiction' }));

    await store.setLensAsync('fiction');

    expect(store.book()?.lensKey).toBe('fiction');
    expect(api.chapter).toHaveBeenCalledTimes(1);
  });

  it('an empty chapter still has one page and shows nothing', async () => {
    api.chapter.and.returnValue(
      of<Chapter>({ chapter: { id: 0, title: 'Empty', level: 0, wordCount: 0, hasSummary: false, summaryIsStale: false }, text: '' }));

    await open();

    expect(store.pageTotal()).toBe(1);
    expect(store.visibleParagraphs()).toEqual([]);
  });

  /**
   * Paragraphs are carried alongside the words, never inside them. A word offset
   * is what the reading position, every bookmark, every search hit and every
   * section boundary are counted in, and the server counts by splitting on
   * whitespace — so a blank line has to change how the page is drawn without
   * changing what any of those numbers mean.
   */
  it('splits a page into paragraphs without renumbering a single word', async () => {
    api.chapter.and.returnValue(of<Chapter>({
      chapter: { id: 0, title: 'Two', level: 0, wordCount: 4, hasSummary: false, summaryIsStale: false },
      text: 'w0 w1\n\nw2 w3'
    }));

    await open();

    expect(store.visibleParagraphs()).toEqual(['w0 w1', 'w2 w3']);
    expect(store.totalWords())
      .withContext('the blank line is not a word and must not become one')
      .toBe(4);
  });
});

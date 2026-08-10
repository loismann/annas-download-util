import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BookmarkStore } from './bookmark-store';
import { ReaderTasks } from './reader-tasks';
import { Reader2ApiService } from './reader2-api.service';
import { Bookmark } from '../reader2.models';

const BOOK = '0123456789abcdef';

function mark(id: string, chapter: number, wordOffset: number, label: string | null = null): Bookmark {
  return { id, chapter, wordOffset, label, createdAtUtc: '2026-01-01T00:00:00Z' };
}

describe('BookmarkStore', () => {
  let api: jasmine.SpyObj<Reader2ApiService>;
  let store: BookmarkStore;

  beforeEach(() => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'bookmarks', 'saveBookmark', 'removeBookmark'
    ]);

    api.bookmarks.and.returnValue(of([]));
    api.removeBookmark.and.returnValue(of(void 0));
    api.saveBookmark.and.callFake((_: string, chapter: number, wordOffset: number, label: string | null) =>
      of(mark(`${chapter}-${wordOffset}`, chapter, wordOffset, label)));

    TestBed.configureTestingModule({
      providers: [ReaderTasks, BookmarkStore, { provide: Reader2ApiService, useValue: api }]
    });

    store = TestBed.inject(BookmarkStore);
  });

  it('loads the marks for the book it is given', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 1, 10)]));

    await store.loadAsync(BOOK);

    expect(store.bookmarks().length).toBe(1);
  });

  /** Otherwise the previous book's marks show against the new one's chapters. */
  it('empties the list before loading another book', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 1, 10)]));
    await store.loadAsync(BOOK);

    api.bookmarks.and.returnValue(throwError(() => new Error('offline')));
    await store.loadAsync('fedcba9876543210');

    expect(store.bookmarks()).toEqual([]);
  });

  // ─── the toggle ─────────────────────────────────────────────────────

  it('knows there is no mark where the reader is standing', async () => {
    await store.loadAsync(BOOK);
    store.setPlace(2, 400);

    expect(store.markHere()).toBeNull();
  });

  it('finds the mark at the reader’s exact place, and only that one', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 2, 400), mark('b', 2, 401)]));
    await store.loadAsync(BOOK);

    store.setPlace(2, 400);
    expect(store.markHere()?.id).toBe('a');

    store.setPlace(2, 399);
    expect(store.markHere()).toBeNull();
  });

  it('marks the place when there is nothing there', async () => {
    await store.loadAsync(BOOK);
    store.setPlace(3, 250);

    await store.toggleAsync('the argument turns');

    expect(api.saveBookmark).toHaveBeenCalledWith(BOOK, 3, 250, 'the argument turns');
    expect(store.markHere()?.label).toBe('the argument turns');
  });

  it('removes the mark when there is already one there', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 3, 250)]));
    await store.loadAsync(BOOK);
    store.setPlace(3, 250);

    await store.toggleAsync();

    expect(api.removeBookmark).toHaveBeenCalledWith(BOOK, 'a');
    expect(api.saveBookmark).not.toHaveBeenCalled();
    expect(store.markHere()).toBeNull();
  });

  /**
   * The toggle is derived from where the reader is, not set when they press it.
   * A flag would have to be cleared on every page turn, and missing one would
   * leave the button claiming a page is marked after the reader has left it.
   */
  it('stops claiming a page is marked once the reader turns past it', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 3, 250)]));
    await store.loadAsync(BOOK);

    store.setPlace(3, 250);
    expect(store.markHere()).not.toBeNull();

    store.setPlace(3, 550);
    expect(store.markHere()).toBeNull();
  });

  // ─── keeping the list in order ──────────────────────────────────────

  it('puts a new mark in reading order rather than at the end', async () => {
    api.bookmarks.and.returnValue(of([mark('late', 5, 0), mark('early', 1, 0)]));
    await store.loadAsync(BOOK);

    await store.saveAsync(3, 100, null);

    expect(store.bookmarks().map(b => b.chapter)).toEqual([5, 1, 3].sort((a, b) => a - b));
  });

  it('replaces rather than duplicates when the same place is saved twice', async () => {
    await store.loadAsync(BOOK);

    await store.saveAsync(2, 400, 'first');
    await store.saveAsync(2, 400, 'second');

    expect(store.bookmarks().length).toBe(1);
    expect(store.bookmarks()[0].label).toBe('second');
  });

  it('leaves the list alone when the save fails', async () => {
    await store.loadAsync(BOOK);
    api.saveBookmark.and.returnValue(throwError(() => ({ error: { error: 'no' } })));

    await store.saveAsync(1, 0, null);

    expect(store.bookmarks()).toEqual([]);
    expect(TestBed.inject(ReaderTasks).error()).toBe('no');
  });

  it('leaves the mark in place when the removal fails', async () => {
    api.bookmarks.and.returnValue(of([mark('a', 1, 10)]));
    await store.loadAsync(BOOK);
    api.removeBookmark.and.returnValue(throwError(() => new Error('offline')));

    await store.removeAsync('a');

    expect(store.bookmarks().length).toBe(1);
  });

  it('does nothing at all before a book is open', async () => {
    store.setPlace(1, 10);

    await store.toggleAsync();

    expect(api.saveBookmark).not.toHaveBeenCalled();
  });
});

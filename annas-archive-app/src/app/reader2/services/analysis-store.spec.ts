import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { Observable, of, throwError } from 'rxjs';
import { AnalysisStore } from './analysis-store';
import { ReaderTasks } from './reader-tasks';
import { ReaderStore } from './reader-store';
import { Reader2ApiService, StreamEvent } from './reader2-api.service';
import { Book, Prose } from '../reader2.models';

const BOOK = '0123456789abcdef';

/** A stream that reports one step and then finishes with a result. */
function streamOf(markdown: string): Observable<StreamEvent<Prose>> {
  return new Observable<StreamEvent<Prose>>(subscriber => {
    subscriber.next({
      kind: 'progress',
      step: { stage: 'chunk', stepNumber: 1, totalSteps: 4, message: 'Chunk 1 of 4…' }
    });
    subscriber.next({ kind: 'result', value: { markdown } });
    subscriber.complete();
  });
}

describe('AnalysisStore', () => {
  let api: jasmine.SpyObj<Reader2ApiService>;
  let store: AnalysisStore;
  let tasks: ReaderTasks;
  let reader: { book: WritableSignal<Book | null>; chapterIndex: WritableSignal<number> };

  beforeEach(() => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'chapterSummary', 'explainSimply', 'analysePassage', 'sectionSummary', 'peekChapterSummary'
    ]);

    api.chapterSummary.and.returnValue(streamOf('a chapter summary'));
    api.explainSimply.and.returnValue(of<Prose>({ markdown: 'plainly put' }));
    api.analysePassage.and.returnValue(of<Prose>({ markdown: 'about that passage' }));
    api.sectionSummary.and.returnValue(of<Prose>({ markdown: 'a section summary' }));
    api.peekChapterSummary.and.returnValue(of(null));

    reader = {
      book: signal<Book | null>({
        bookId: BOOK, fileName: 'novel.epub', title: 'A Novel', authors: [],
        lensKey: 'fiction', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true
      }),
      chapterIndex: signal(2)
    };

    TestBed.configureTestingModule({
      providers: [
        ReaderTasks, AnalysisStore,
        { provide: Reader2ApiService, useValue: api },
        { provide: ReaderStore, useValue: reader }
      ]
    });

    store = TestBed.inject(AnalysisStore);
    tasks = TestBed.inject(ReaderTasks);
  });

  it('starts with nothing generated and nothing claimed', () => {
    expect(store.markdown()).toBeNull();
    expect(store.sectionMarkdown()).toBeNull();
    expect(store.openSection()).toBe(-1);
  });

  it('streams a chapter summary and clears the banner when it ends', async () => {
    await store.summariseChapterAsync(BOOK, 2, false);

    expect(store.markdown()).toBe('a chapter summary');
    expect(store.kind()).toBe('summary');
    expect(tasks.busy()).toBeNull();
  });

  it('passes force through to the server rather than deciding locally', async () => {
    await store.summariseChapterAsync(BOOK, 2, true);

    expect(api.chapterSummary).toHaveBeenCalledWith(BOOK, 2, true);
  });

  it('records which kind of analysis is on screen, so the panel labels it right', async () => {
    await store.explainSimplyAsync(BOOK, 1, false);
    expect(store.kind()).toBe('explain-simply');

    await store.analysePassageAsync(BOOK, 1, 300, 'a passage');
    expect(store.kind()).toBe('passage');
    expect(store.markdown()).toBe('about that passage');
  });

  /**
   * A section summary and a chapter summary are different purchases. Opening a
   * section must not wipe a chapter summary the household has already paid for.
   */
  it('keeps the chapter summary when a section is summarised', async () => {
    await store.summariseChapterAsync(BOOK, 2, false);

    await store.summariseSectionAsync(BOOK, 2, 1, false);

    expect(store.markdown()).toBe('a chapter summary');
    expect(store.sectionMarkdown()).toBe('a section summary');
    expect(store.openSection()).toBe(1);
  });

  /** Showing one chapter's summary against another chapter's text is a lie. */
  it('clears everything when the reader moves', async () => {
    await store.summariseChapterAsync(BOOK, 2, false);
    await store.summariseSectionAsync(BOOK, 2, 0, false);

    store.clear();

    expect(store.markdown()).toBeNull();
    expect(store.sectionMarkdown()).toBeNull();
    expect(store.openSection()).toBe(-1);
  });

  it('surfaces the server’s own sentence when a generation fails', async () => {
    api.explainSimply.and.returnValue(
      throwError(() => ({ error: { error: 'You have used your allowance.' } })));

    await store.explainSimplyAsync(BOOK, 1, false);

    expect(tasks.error()).toBe('You have used your allowance.');
    expect(store.markdown()).toBeNull();
  });

  it('clears the previous answer before asking, so a stale one is never shown as new', async () => {
    await store.explainSimplyAsync(BOOK, 1, false);
    api.explainSimply.and.returnValue(throwError(() => new Error('offline')));

    await store.explainSimplyAsync(BOOK, 2, false);

    expect(store.markdown()).toBeNull();
  });

  // ─── refreshAsync: the tick's promise kept ───────────────────────────

  /**
   * The whole reason this exists. The chapter list marks a chapter as already
   * summarised from stored data alone; without this, the panel had no way to
   * show what the tick promised.
   */
  it('shows whatever chapter summary is already stored, wherever the reader is', async () => {
    api.peekChapterSummary.and.returnValue(of({ markdown: 'already written' }));

    await store.refreshAsync();

    expect(api.peekChapterSummary).toHaveBeenCalledWith(BOOK, 2);
    expect(store.markdown()).toBe('already written');
    expect(store.kind()).toBe('summary');
  });

  it('leaves the pane empty, not erroring, when nothing is stored yet', async () => {
    api.peekChapterSummary.and.returnValue(of(null));

    await store.refreshAsync();

    expect(store.markdown()).toBeNull();
  });

  it('spends nothing to check — no busy banner, no error, on a network failure', async () => {
    api.peekChapterSummary.and.returnValue(throwError(() => new Error('offline')));

    await store.refreshAsync();

    expect(store.markdown()).toBeNull();
    expect(tasks.busy()).toBeNull();
    expect(tasks.error()).toBeNull();
  });

  /**
   * A stale label is worse than a stale blank: without this, a reader who had
   * "I'm a Dummy" open before navigating would see chapter-summary prose next
   * to a regenerate button still wired to explain-simply.
   */
  it('relabels the pane as showing a summary once one is found', async () => {
    store.kind.set('explain-simply');
    api.peekChapterSummary.and.returnValue(of({ markdown: 'already written' }));

    await store.refreshAsync();

    expect(store.kind()).toBe('summary');
  });

  it('does nothing when no book is open', async () => {
    reader.book.set(null);

    await store.refreshAsync();

    expect(api.peekChapterSummary).not.toHaveBeenCalled();
  });
});

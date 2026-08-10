import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { AnalysisStore } from './analysis-store';
import { ReaderTasks } from './reader-tasks';
import { Reader2ApiService, StreamEvent } from './reader2-api.service';
import { Prose } from '../reader2.models';

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

  beforeEach(() => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'chapterSummary', 'explainSimply', 'analysePassage', 'sectionSummary'
    ]);

    api.chapterSummary.and.returnValue(streamOf('a chapter summary'));
    api.explainSimply.and.returnValue(of<Prose>({ markdown: 'plainly put' }));
    api.analysePassage.and.returnValue(of<Prose>({ markdown: 'about that passage' }));
    api.sectionSummary.and.returnValue(of<Prose>({ markdown: 'a section summary' }));

    TestBed.configureTestingModule({
      providers: [ReaderTasks, AnalysisStore, { provide: Reader2ApiService, useValue: api }]
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
});

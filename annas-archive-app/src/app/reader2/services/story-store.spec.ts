import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { Observable, of } from 'rxjs';
import { StoryStore } from './story-store';
import { ReaderTasks } from './reader-tasks';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderConfirm } from './reader-confirm';
import { ReaderStore } from './reader-store';
import { Book, ChapterInfo, Lens, StoryModel } from '../reader2.models';

const MODEL: StoryModel = {
  actors: [],
  places: [],
  groups: [],
  edges: [],
  threads: [],
  openQuestions: [{
    id: 'm1', actorId: 'a1', otherActorId: 'a2', alias: 'Pierre',
    reason: 'unsure', proposedInChapter: 3
  }],
  chaptersIngested: [0, 1],
  vocabulary: { actors: 'Characters', groups: 'Factions', threads: 'Plot threads' },
  throughChapter: 4
};

function lens(key: string, buildsStoryModel: boolean): Lens {
  return {
    key, displayName: key, description: '', icon: '', sortOrder: 0,
    isDefault: false, buildsStoryModel, storyVocabulary: null
  };
}

function chapter(id: number, hasSummary: boolean): ChapterInfo {
  return {
    id, title: `Chapter ${id}`, level: 0, wordCount: 100, hasSummary, summaryIsStale: false
  };
}

describe('StoryStore', () => {
  let store: StoryStore;
  let api: jasmine.SpyObj<Reader2ApiService>;
  let confirm: jasmine.SpyObj<ReaderConfirm>;
  let reader: { book: WritableSignal<Book | null>; lenses: WritableSignal<Lens[]>; chapters: WritableSignal<ChapterInfo[]> };

  beforeEach(() => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'storyModel', 'backFillStoryModel'
    ]);
    confirm = jasmine.createSpyObj<ReaderConfirm>('ReaderConfirm', ['confirmBackFillAsync']);

    reader = {
      book: signal<Book | null>({
        bookId: 'book-1', fileName: 'novel.epub', title: 'A Novel', authors: [],
        lensKey: 'fiction', addedAtUtc: '', lastOpenedAtUtc: null, isAvailable: true,
        coverUrl: null
      }),
      lenses: signal<Lens[]>([lens('fiction', true), lens('literary', false)]),
      chapters: signal<ChapterInfo[]>([chapter(0, true), chapter(1, true), chapter(2, false)])
    };

    TestBed.configureTestingModule({
      providers: [
        StoryStore, ReaderTasks,
        { provide: Reader2ApiService, useValue: api },
        { provide: ReaderConfirm, useValue: confirm },
        { provide: ReaderStore, useValue: reader }
      ]
    });

    store = TestBed.inject(StoryStore);
  });

  /** A stream that reports one step and then the finished model. */
  function stream(model: StoryModel): Observable<unknown> {
    return of(
      { kind: 'progress', step: { stage: 'ingesting', stepNumber: 1, totalSteps: 2, message: '…' } },
      { kind: 'result', value: model });
  }

  it('holds nothing until something is loaded', () => {
    expect(store.model()).toBeNull();
    expect(store.openQuestions()).toEqual([]);
    expect(store.vocabulary()).toBeNull();
  });

  it('keeps what the server filtered rather than filtering again', async () => {
    api.storyModel.and.returnValue(of(MODEL));

    await store.loadAsync('book-1', 4);

    expect(api.storyModel).toHaveBeenCalledWith('book-1', 4);
    expect(store.model()?.throughChapter).toBe(4);
  });

  /**
   * The client holds no table of names for the three parts. A fourth book type
   * arrives with its own, and nothing here changes.
   */
  it('takes the names for its parts from the server', async () => {
    api.storyModel.and.returnValue(of(MODEL));

    await store.loadAsync('book-1', 4);

    expect(store.vocabulary()?.actors).toBe('Characters');
  });

  it('surfaces the questions waiting to be answered', async () => {
    api.storyModel.and.returnValue(of(MODEL));

    await store.loadAsync('book-1', 4);

    expect(store.openQuestions().length).toBe(1);
    expect(store.openQuestions()[0].alias).toBe('Pierre');
  });

  it('builds the model from existing summaries only when asked', async () => {
    api.backFillStoryModel.and.returnValue(stream(MODEL) as never);

    expect(api.backFillStoryModel).not.toHaveBeenCalled();

    await store.buildFromSummariesAsync('book-1');

    expect(api.backFillStoryModel).toHaveBeenCalledWith('book-1', false);
    expect(store.model()).toEqual(MODEL);
  });

  /**
   * A record gathered under extraction rules that have since changed cannot be
   * corrected any other way: every chapter already folded in is walked past for
   * free, so an ordinary build would re-read nothing.
   */
  it('passes the rebuild through, so a stale record can be discarded', async () => {
    api.backFillStoryModel.and.returnValue(stream(MODEL) as never);

    await store.buildFromSummariesAsync('book-1', true);

    expect(api.backFillStoryModel).toHaveBeenCalledWith('book-1', true);
  });

  it('forgets the cast when the book or its type changes', async () => {
    api.storyModel.and.returnValue(of(MODEL));
    await store.loadAsync('book-1', 4);

    store.clear();

    expect(store.model()).toBeNull();
  });

  // ─── the offer after a type switch ──────────────────────────────────

  /**
   * Switching a book's type is free. Building the cast is not — it is one
   * request per summarised chapter — so the switch offers and the reader
   * answers.
   */
  it('asks before building anything, and builds when the reader says yes', async () => {
    confirm.confirmBackFillAsync.and.resolveTo(true);
    api.backFillStoryModel.and.returnValue(stream(MODEL) as never);

    await store.offerBuildAsync('fiction');

    expect(confirm.confirmBackFillAsync).toHaveBeenCalledWith('2 chapters');
    expect(api.backFillStoryModel).toHaveBeenCalledWith('book-1', false);
  });

  it('builds nothing when the reader says no', async () => {
    confirm.confirmBackFillAsync.and.resolveTo(false);

    await store.offerBuildAsync('fiction');

    expect(api.backFillStoryModel).not.toHaveBeenCalled();
  });

  it('does not ask for a book type that keeps no cast', async () => {
    await store.offerBuildAsync('literary');

    expect(confirm.confirmBackFillAsync).not.toHaveBeenCalled();
  });

  /** A dialog offering to do nothing teaches the reader to dismiss dialogs. */
  it('does not ask when nothing has been summarised to build from', async () => {
    reader.chapters.set([chapter(0, false), chapter(1, false)]);

    await store.offerBuildAsync('fiction');

    expect(confirm.confirmBackFillAsync).not.toHaveBeenCalled();
  });

  it('counts one summarised chapter in the singular', async () => {
    reader.chapters.set([chapter(0, true), chapter(1, false)]);
    confirm.confirmBackFillAsync.and.resolveTo(false);

    await store.offerBuildAsync('fiction');

    expect(confirm.confirmBackFillAsync).toHaveBeenCalledWith('1 chapter');
  });
});

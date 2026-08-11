import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { VocabularyStore } from './vocabulary-store';
import { ReaderTasks } from './reader-tasks';
import { Reader2ApiService } from './reader2-api.service';
import { Definition, TermState, VocabularyTerm } from '../reader2.models';

const BOOK = '0123456789abcdef';

function found(term: string): Definition {
  return { term, meaning: `what ${term} means`, norm: term.toLowerCase() };
}

function filed(term: string, state: TermState): VocabularyTerm {
  return {
    term, state, termNorm: term.toLowerCase(),
    definition: null, firstSeenBookId: null, updatedAtUtc: '2026-01-01T00:00:00Z'
  };
}

describe('VocabularyStore', () => {
  let api: jasmine.SpyObj<Reader2ApiService>;
  let store: VocabularyStore;

  beforeEach(() => {
    api = jasmine.createSpyObj<Reader2ApiService>('Reader2ApiService', [
      'vocabulary', 'saveTerm', 'removeTerm', 'sectionVocabulary'
    ]);

    api.vocabulary.and.returnValue(of([]));
    api.saveTerm.and.returnValue(of(undefined as void));
    api.removeTerm.and.returnValue(of(undefined as void));
    api.sectionVocabulary.and.returnValue(of({ terms: [] }));

    TestBed.configureTestingModule({
      providers: [ReaderTasks, VocabularyStore, { provide: Reader2ApiService, useValue: api }]
    });

    store = TestBed.inject(VocabularyStore);
  });

  /**
   * The whole visible result of pressing the tick. The server excludes filed
   * terms from every section it serves *after* this, so without the local drop
   * the word sat there unchanged and marking it known looked like a dead button.
   */
  it('takes a filed word off the passage list', async () => {
    store.sectionTerms.set([found('rhizome'), found('striated')]);

    await store.saveTermAsync('rhizome', 'Known', 'a root that spreads sideways', BOOK);

    expect(store.sectionTerms().map(t => t.term)).toEqual(['striated']);
  });

  it('does the same for a word kept for study, which is also dealt with', async () => {
    store.sectionTerms.set([found('rhizome')]);

    await store.saveTermAsync('rhizome', 'Studying', undefined, BOOK);

    expect(store.sectionTerms()).toEqual([]);
  });

  /** Or a word vanishes from the passage having been filed nowhere. */
  it('leaves the passage list alone when the save fails', async () => {
    store.sectionTerms.set([found('rhizome')]);
    api.saveTerm.and.returnValue(throwError(() => new Error('offline')));

    await store.saveTermAsync('rhizome', 'Known', undefined, BOOK);

    expect(store.sectionTerms().map(t => t.term)).toEqual(['rhizome']);
  });

  it('re-reads the filed lists, which is what the two sublists show', async () => {
    api.vocabulary.and.returnValue(of([filed('rhizome', 'Known')]));

    await store.saveTermAsync('rhizome', 'Known', undefined, BOOK);

    expect(store.known().map(t => t.term)).toEqual(['rhizome']);
    expect(store.studying()).toEqual([]);
  });
});

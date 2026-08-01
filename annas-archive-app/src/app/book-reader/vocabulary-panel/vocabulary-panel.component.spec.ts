/**
 * Tests for the vocabulary/flashcards/Learn More modal extracted from
 * BookReaderComponent. These run against a real fixture — see the header of
 * `book-reader.component.spec.ts` for why that matters here.
 *
 * The behaviour worth protecting is the book-selection rule: several actions
 * apply to "the book chosen in the filter, falling back to the open book", and
 * the two fall back in opposite directions depending on the action.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { VocabularyPanelComponent } from './vocabulary-panel.component';
import { AiApiService } from '../../services/ai-api.service';
import { VocabularyService } from '../../services/vocabulary.service';
import { LoggerService } from '../../services/logger.service';

describe('VocabularyPanelComponent', () => {
  let component: VocabularyPanelComponent;
  let fixture: ComponentFixture<VocabularyPanelComponent>;
  let aiApi: jasmine.SpyObj<AiApiService>;
  let vocabulary: jasmine.SpyObj<VocabularyService>;

  beforeEach(async () => {
    aiApi = jasmine.createSpyObj<AiApiService>('AiApiService', [
      'getFlashcards', 'clearFlashcards', 'deleteFlashcard', 'createFlashcard',
      'learnMore', 'getWikiImages'
    ]);
    aiApi.getFlashcards.and.returnValue(of([]));
    aiApi.clearFlashcards.and.returnValue(of({} as any));
    aiApi.deleteFlashcard.and.returnValue(of({} as any));
    aiApi.createFlashcard.and.returnValue(of([]));
    aiApi.learnMore.and.returnValue(of({ detail: '<p>text</p>' } as any));
    aiApi.getWikiImages.and.returnValue(of({ images: [] } as any));

    vocabulary = jasmine.createSpyObj<VocabularyService>(
      'VocabularyService',
      [
        'registerBook', 'getBookFilters', 'getKnownWords', 'getUnknownWords',
        'getCachedDefinition', 'getStudyWordDefinition', 'markAsKnown', 'markAsUnknown',
        'clearKnown', 'clearUnknown', 'clearAll', 'deleteBook',
        'getCachedLearnMore', 'cacheLearnMore'
      ],
      { knownWords$: of(new Set<string>()), studyWords$: of(new Map<string, string>()) }
    );
    vocabulary.getBookFilters.and.returnValue([
      { id: 'all', name: 'All books' },
      { id: 'book-a', name: 'Book A' }
    ]);
    vocabulary.getKnownWords.and.returnValue([]);
    vocabulary.getUnknownWords.and.returnValue(new Map<string, string>());
    vocabulary.getCachedDefinition.and.returnValue('');
    vocabulary.getStudyWordDefinition.and.returnValue('');
    vocabulary.getCachedLearnMore.and.returnValue(null as any);

    await TestBed.configureTestingModule({
      imports: [VocabularyPanelComponent, NoopAnimationsModule],
      providers: [
        { provide: AiApiService, useValue: aiApi },
        { provide: VocabularyService, useValue: vocabulary },
        {
          provide: LoggerService,
          useValue: jasmine.createSpyObj('LoggerService', ['log', 'error', 'warn'])
        },
        { provide: MatDialog, useValue: jasmine.createSpyObj('MatDialog', ['open']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(VocabularyPanelComponent);
    component = fixture.componentInstance;
  });

  /** Drives the @Input transition the way the reader does when the modal opens. */
  function open(bookPath: string | null = 'book-a', bookTitle: string | null = 'Book A'): void {
    component.bookPath = bookPath;
    component.bookTitle = bookTitle;
    component.open = true;
    component.ngOnChanges({
      open: { currentValue: true, previousValue: false, firstChange: false, isFirstChange: () => false }
    });
    fixture.detectChanges();
  }

  it('should create', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('should render nothing while closed', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.vocab-modal')).toBeNull();
  });

  describe('opening', () => {
    it('should register the open book so it can be named in the filter', () => {
      open();
      expect(vocabulary.registerBook).toHaveBeenCalledWith('book-a', 'Book A');
    });

    it('should preselect the open book in the filter', () => {
      open();
      expect(component.vocabFilter).toBe('book-a');
    });

    it('should stay on "all" when the open book has no vocabulary yet', () => {
      open('book-unknown', 'Unlisted');
      expect(component.vocabFilter).toBe('all');
    });

    it('should load the flashcards for the open book', () => {
      open();
      expect(aiApi.getFlashcards).toHaveBeenCalledWith('book-a');
    });

    it('should not register anything when no book is open', () => {
      open(null, null);
      expect(vocabulary.registerBook).not.toHaveBeenCalled();
      expect(component.flashcards).toEqual([]);
    });

    it('should still register a book whose title is empty', () => {
      // An untitled book is still an open book. Guarding on truthiness rather
      // than on null would silently drop it from the filter list.
      open('book-a', '');
      expect(vocabulary.registerBook).toHaveBeenCalledWith('book-a', '');
    });

    it('should show the modal', () => {
      open();
      expect(fixture.nativeElement.querySelector('.vocab-modal')).toBeTruthy();
    });
  });

  describe('closing', () => {
    it('should ask the reader to close rather than hiding itself', () => {
      // The reader owns `showVocabModal`, so the panel must not self-close —
      // otherwise the flag and the DOM would disagree.
      spyOn(component.closed, 'emit');
      open();
      component.close();
      expect(component.closed.emit).toHaveBeenCalled();
      expect(component.open).toBe(true);
    });

    it('should close on Escape while open', () => {
      spyOn(component.closed, 'emit');
      open();
      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));
      expect(component.closed.emit).toHaveBeenCalled();
    });

    it('should ignore Escape while closed', () => {
      spyOn(component.closed, 'emit');
      fixture.detectChanges();
      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));
      expect(component.closed.emit).not.toHaveBeenCalled();
    });
  });

  describe('list refresh', () => {
    it('should title-case and sort the known list', () => {
      vocabulary.getKnownWords.and.returnValue(['zebra', 'apple']);
      open();
      expect(component.vocabKnownList).toEqual(['Apple', 'Zebra']);
    });

    it('should sort the study list by term', () => {
      vocabulary.getUnknownWords.and.returnValue(
        new Map([['zebra', 'a striped horse'], ['apple', 'a fruit']])
      );
      open();
      expect(component.vocabUnknownList.map(w => w.term)).toEqual(['Apple', 'Zebra']);
    });

    it('should scope the lists to the filtered book', () => {
      open();
      vocabulary.getKnownWords.calls.reset();
      component.onVocabFilterChange('book-a');
      expect(vocabulary.getKnownWords).toHaveBeenCalledWith('book-a');
    });

    it('should pass no filter for "all"', () => {
      open();
      vocabulary.getKnownWords.calls.reset();
      component.onVocabFilterChange('all');
      expect(vocabulary.getKnownWords).toHaveBeenCalledWith(undefined);
    });
  });

  describe('which book an action applies to', () => {
    it('should move a word to study under the filtered book', () => {
      open();
      component.vocabFilter = 'book-b';
      component.moveKnownToStudy('term');
      expect(vocabulary.markAsUnknown).toHaveBeenCalledWith('term', '', 'book-b');
    });

    it('should fall back to the open book when the filter is "all"', () => {
      open();
      component.vocabFilter = 'all';
      component.moveStudyToKnown('term');
      expect(vocabulary.markAsKnown).toHaveBeenCalledWith('term', 'book-a', '');
    });

    it('should delete a flashcard from the filtered book', () => {
      open();
      component.vocabFilter = 'book-b';
      component.deleteFlashcard({ term: 'x' } as any);
      expect(aiApi.deleteFlashcard).toHaveBeenCalledWith('book-b', 'x');
    });

    it('should create a flashcard against the open book, preferring it over the filter', () => {
      // Opposite precedence to deleteFlashcard — the new card belongs to what
      // you are reading, not to what you are browsing.
      open();
      component.vocabFilter = 'book-b';
      component.makeFlashcard({ term: 'x', definition: 'y' });
      expect(aiApi.createFlashcard).toHaveBeenCalledWith(
        jasmine.objectContaining({ dropboxPath: 'book-a' })
      );
    });

    it('should fall back to the filtered book when nothing is open', () => {
      open(null, null);
      component.vocabFilter = 'book-b';
      component.makeFlashcard({ term: 'x', definition: 'y' });
      expect(aiApi.createFlashcard).toHaveBeenCalledWith(
        jasmine.objectContaining({ dropboxPath: 'book-b' })
      );
    });

    it('should refuse to create a flashcard with no book at all', () => {
      open(null, null);
      component.vocabFilter = 'all';
      component.makeFlashcard({ term: 'x', definition: 'y' });
      expect(aiApi.createFlashcard).not.toHaveBeenCalled();
    });

    it('should reload flashcards for the newly filtered book', () => {
      open();
      aiApi.getFlashcards.calls.reset();
      component.onVocabFilterChange('book-b');
      expect(aiApi.getFlashcards).toHaveBeenCalledWith('book-b');
    });
  });

  describe('flashcard merging', () => {
    it('should replace an existing card rather than duplicating it', () => {
      open();
      component.flashcards = [{ term: 'Alpha', definition: 'old' } as any];
      aiApi.createFlashcard.and.returnValue(of([{ term: 'alpha', definition: 'new' }] as any));

      component.makeFlashcard({ term: 'alpha', definition: 'new' });

      expect(component.flashcards.length).toBe(1);
      expect(component.flashcards[0].definition).toBe('new');
    });

    it('should append a card that is not already present', () => {
      open();
      component.flashcards = [{ term: 'alpha', definition: 'a' } as any];
      aiApi.createFlashcard.and.returnValue(of([{ term: 'beta', definition: 'b' }] as any));

      component.makeFlashcard({ term: 'beta', definition: 'b' });

      expect(component.flashcards.map(c => c.term)).toEqual(['alpha', 'beta']);
    });
  });

  describe('Learn More', () => {
    it('should serve a cached entry without calling the API', () => {
      vocabulary.getCachedLearnMore.and.returnValue({ detail: '<p>cached</p>', images: ['i.jpg'] } as any);
      open();

      component.learnMore({ term: 'liminal', definition: 'threshold' });

      expect(aiApi.learnMore).not.toHaveBeenCalled();
      expect(component.learnMoreImages).toEqual(['i.jpg']);
      expect(component.loadingLearnMore).toBe(false);
    });

    it('should fetch and cache a fresh entry', () => {
      open();
      component.learnMore({ term: 'liminal', definition: 'threshold' });

      expect(aiApi.learnMore).toHaveBeenCalled();
      expect(vocabulary.cacheLearnMore).toHaveBeenCalled();
      expect(component.loadingLearnMore).toBe(false);
    });

    it('should look up images for a linked Wikipedia article', () => {
      aiApi.learnMore.and.returnValue(
        of({ detail: '<a href="https://en.wikipedia.org/wiki/Dune">Dune</a>' } as any)
      );
      aiApi.getWikiImages.and.returnValue(of({ images: ['dune.jpg'] } as any));
      open();

      component.learnMore({ term: 'dune', definition: 'a sand hill' });

      expect(aiApi.getWikiImages).toHaveBeenCalledWith('Dune');
      expect(component.learnMoreImages).toEqual(['dune.jpg']);
    });

    it('should still show the text when the image lookup fails', () => {
      aiApi.learnMore.and.returnValue(
        of({ detail: '<a href="https://en.wikipedia.org/wiki/Dune">Dune</a>' } as any)
      );
      aiApi.getWikiImages.and.returnValue(throwError(() => new Error('boom')));
      open();

      component.learnMore({ term: 'dune', definition: 'a sand hill' });

      expect(component.learnMoreImages).toEqual([]);
      expect(component.learnMoreContent).toContain('Dune');
      expect(component.loadingLearnMore).toBe(false);
    });

    it('should report a failed lookup instead of hanging on "Loading…"', () => {
      aiApi.learnMore.and.returnValue(throwError(() => new Error('boom')));
      open();

      component.learnMore({ term: 'x', definition: 'y' });

      expect(component.learnMoreContent).toBe('Failed to load details.');
      expect(component.loadingLearnMore).toBe(false);
    });

    it('should ignore a second request while one is in flight', () => {
      open();
      component.loadingLearnMore = true;
      component.learnMore({ term: 'x', definition: 'y' });
      expect(aiApi.learnMore).not.toHaveBeenCalled();
    });

    it('should clear the term on close so the modal hides', () => {
      open();
      component.learnMoreTerm = 'x';
      component.closeLearnMore();
      expect(component.learnMoreTerm).toBeNull();
    });
  });

  describe('bulk clears', () => {
    it('should clear known words and refresh', () => {
      open();
      component.clearKnownWords();
      expect(vocabulary.clearKnown).toHaveBeenCalled();
    });

    it('should clear the study list and refresh', () => {
      open();
      component.clearUnknownWords();
      expect(vocabulary.clearUnknown).toHaveBeenCalled();
    });

    it('should clear flashcards only for the open book', () => {
      open();
      component.clearFlashcards();
      expect(aiApi.clearFlashcards).toHaveBeenCalledWith('book-a');
    });

    it('should not clear flashcards when no book is open', () => {
      open(null, null);
      aiApi.clearFlashcards.calls.reset();
      component.clearFlashcards();
      expect(aiApi.clearFlashcards).not.toHaveBeenCalled();
    });

    it('should refuse to delete a book while the filter is "all"', () => {
      open();
      component.vocabFilter = 'all';
      component.deleteSelectedBook();
      expect(vocabulary.deleteBook).not.toHaveBeenCalled();
    });
  });
});

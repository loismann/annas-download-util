import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, Subject, of, throwError } from 'rxjs';

import { LibraryReviewModalComponent } from './library-review-modal.component';
import {
  LibraryApiService, LibraryReviewBook, LibraryReviewSession
} from '../../services/library-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the library review modal.
 *
 * A review pass in the same series as the library pages. It found all three of
 * this modal's writes routed through its read guard — including the one that
 * deletes a book. See "leaving mid-decision".
 *
 * The genre phase gets its own section for the reason its own comment gives:
 * the metadata PATCH replaces the tag array wholesale, and owner tags live in
 * that same array, so anything that fails to round-trip them strips ownership.
 */
describe('LibraryReviewModalComponent (characterization)', () => {
  let fixture: ComponentFixture<LibraryReviewModalComponent>;
  let component: LibraryReviewModalComponent;
  let api: jasmine.SpyObj<LibraryApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<LibraryReviewModalComponent>>;
  let ownerName: string | null;

  function reviewBook(over: Partial<LibraryReviewBook> = {}): LibraryReviewBook {
    return {
      fileName: 'dune.epub', title: 'Dune', authors: ['Frank Herbert'],
      tags: ["Paul's Books", 'Sci-Fi'], series: 'Dune', coverUrl: null,
      format: 'epub', favoritedBy: [], ...over
    };
  }

  async function build(session: Partial<LibraryReviewSession> = {}): Promise<void> {
    ownerName = 'Paul';
    api = jasmine.createSpyObj<LibraryApiService>('LibraryApiService', [
      'getLibraryBookSummary', 'setLibraryBookFavorite',
      'updateLibraryBookMetadata', 'submitLibraryReviewDecision'
    ]);
    api.getLibraryBookSummary.and.returnValue(of({ summary: 'A summary.', source: 'ai' } as any));
    api.setLibraryBookFavorite.and.returnValue(of({ success: true, favoritedBy: ['Paul'] }));
    api.updateLibraryBookMetadata.and.returnValue(of({} as any));
    api.submitLibraryReviewDecision.and.returnValue(of({ success: true }));
    dialogRef = jasmine.createSpyObj<MatDialogRef<LibraryReviewModalComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [LibraryReviewModalComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            phase: 'cull', books: [reviewBook()], totalRemainingInPhase: 10, ...session
          } as LibraryReviewSession
        },
        { provide: LibraryApiService, useValue: api },
        { provide: AuthService, useValue: { getOwnerName: () => ownerName } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(LibraryReviewModalComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  // ─── Getting started ─────────────────────────────────────────────────

  describe('getting started', () => {
    it('should go straight to done with an empty batch', async () => {
      await build({ books: [] });

      component.ngOnInit();

      expect(component.state).toBe('allDone');
      expect(api.getLibraryBookSummary).not.toHaveBeenCalled();
    });

    it('should copy the books rather than edit the session it was handed', async () => {
      const books = [reviewBook()];
      await build({ books });

      component.ngOnInit();
      component.toggleFavorite();

      expect(books[0].favoritedBy).toEqual([]);
    });

    it('should show position in today\'s batch and in the phase', async () => {
      await build({ books: [reviewBook({ fileName: 'a' }), reviewBook({ fileName: 'b' })], totalRemainingInPhase: 40 });
      component.ngOnInit();

      expect(component.progressLabel).toBe('Book 1 of 2 today');
      expect(component.remainingLabel).toBe('40 books left to review');
    });

    it('should say what the genre phase is counting', async () => {
      await build({ phase: 'genre', totalRemainingInPhase: 1 });
      component.ngOnInit();

      expect(component.remainingLabel).toBe('1 book need a genre');
    });
  });

  // ─── Summary prefetch ────────────────────────────────────────────────

  describe('the summary prefetch', () => {
    it('should start every book\'s summary at once, not one at a time', async () => {
      // Book one loads while the user is looking at it; the rest are mostly
      // ready by the time they are reached.
      await build({ books: [reviewBook({ fileName: 'a' }), reviewBook({ fileName: 'b' }), reviewBook({ fileName: 'c' })] });

      component.ngOnInit();

      expect(api.getLibraryBookSummary).toHaveBeenCalledTimes(3);
      expect(component.books.every(b => !b.summaryLoading)).toBe(true);
      expect(component.books[0].summary).toBe('A summary.');
    });

    it('should hold the concurrency down', async () => {
      // Twenty simultaneous AI summary requests is what the cap exists to avoid.
      // build() recreates the spies, so the stub has to be set after it.
      await build({ books: Array.from({ length: 10 }, (_, i) => reviewBook({ fileName: `${i}.epub` })) });
      api.getLibraryBookSummary.and.returnValue(new Subject<any>().asObservable());

      component.ngOnInit();

      expect(api.getLibraryBookSummary).toHaveBeenCalledTimes(3);
    });

    it('should show a per-book error rather than failing the session', async () => {
      api.getLibraryBookSummary.and.returnValue(throwError(() => new Error('AI down')));

      component.ngOnInit();

      expect(component.books[0].summaryError).toBe('Failed to load summary');
      expect(component.books[0].summaryLoading).toBe(false);
      expect(component.state).toBe('showingBook');
    });

    it('should not prefetch summaries in the genre phase', async () => {
      // Nothing there asks the user whether to keep the book.
      await build({ phase: 'genre' });

      component.ngOnInit();

      expect(api.getLibraryBookSummary).not.toHaveBeenCalled();
      expect(component.books[0].summaryLoading).toBe(false);
    });

    it('should stop prefetching when the modal closes', async () => {
      await build({ books: Array.from({ length: 10 }, (_, i) => reviewBook({ fileName: `${i}.epub` })) });
      const pending = new Subject<any>();
      api.getLibraryBookSummary.and.returnValue(pending.asObservable());
      component.ngOnInit();
      const before = api.getLibraryBookSummary.calls.count();

      fixture.destroy();
      pending.next({ summary: 's', source: 'ai' });

      expect(api.getLibraryBookSummary.calls.count()).toBe(before);
    });
  });

  // ─── Deciding ────────────────────────────────────────────────────────

  describe('deciding', () => {
    beforeEach(() => component.ngOnInit());

    it('should keep on a thumbs up and move on', () => {
      component.thumbsUp();

      expect(api.submitLibraryReviewDecision).toHaveBeenCalledWith('dune.epub', 'keep');
      expect(component.state).toBe('allDone');
      expect(component.remainingInPhase).toBe(9);
    });

    it('should ask before deleting', () => {
      component.thumbsDown();

      expect(component.state).toBe('confirmingDelete');
      expect(api.submitLibraryReviewDecision).not.toHaveBeenCalled();
    });

    it('should let the user back out of a delete', () => {
      component.thumbsDown();

      component.cancelDelete();

      expect(component.state).toBe('showingBook');
      expect(api.submitLibraryReviewDecision).not.toHaveBeenCalled();
    });

    it('should delete once confirmed', () => {
      component.thumbsDown();

      component.confirmDelete();

      expect(api.submitLibraryReviewDecision).toHaveBeenCalledWith('dune.epub', 'delete');
    });

    it('should move to the next book rather than finish', async () => {
      await build({ books: [reviewBook({ fileName: 'a' }), reviewBook({ fileName: 'b' })] });
      component.ngOnInit();

      component.thumbsUp();

      expect(component.currentBook?.fileName).toBe('b');
      expect(component.state).toBe('showingBook');
    });

    it('should stay on the book and say why when the decision will not save', () => {
      api.submitLibraryReviewDecision.and.returnValue(throwError(() => new Error('down')));

      component.thumbsUp();

      expect(component.state).toBe('showingBook');
      expect(component.error).toContain('Could not save that decision');
      expect(component.remainingInPhase).toBe(10);
    });

    it('should word a failed delete as a delete', () => {
      api.submitLibraryReviewDecision.and.returnValue(throwError(() => new Error('down')));
      component.thumbsDown();

      component.confirmDelete();

      expect(component.error).toContain('Could not delete');
    });

    it('should not let the phase counter go below nought', async () => {
      await build({ books: [reviewBook({ fileName: 'a' }), reviewBook({ fileName: 'b' })], totalRemainingInPhase: 1 });
      component.ngOnInit();

      component.thumbsUp();
      component.thumbsUp();

      expect(component.remainingInPhase).toBe(0);
    });
  });

  // ─── The genre phase ─────────────────────────────────────────────────

  describe('the genre phase', () => {
    beforeEach(async () => {
      await build({ phase: 'genre' });
      component.ngOnInit();
    });

    it('should not offer Uncategorized as a genre to set', () => {
      // It is the state this phase exists to get books out of.
      expect(component.genreOptions as readonly string[]).not.toContain('Uncategorized');
      expect(component.genreOptions.length).toBeGreaterThan(0);
    });

    it('should do nothing until a genre is picked', () => {
      component.selectedGenre = '';

      component.saveGenreAndNext();

      expect(api.updateLibraryBookMetadata).not.toHaveBeenCalled();
    });

    /**
     * The metadata PATCH replaces the tag array wholesale, and ownership lives
     * in that array as tags like "Paul's Books". Sending only the genre would
     * silently strip the owner off every book that went through this phase.
     */
    it('should round-trip the existing tags and series', () => {
      component.selectedGenre = 'Sci-Fi';

      component.saveGenreAndNext();

      expect(api.updateLibraryBookMetadata).toHaveBeenCalledWith('dune.epub', {
        primaryGenre: 'Sci-Fi',
        tags: ["Paul's Books", 'Sci-Fi'],
        series: 'Dune'
      });
    });

    it('should record the decision only after the genre saved', () => {
      component.selectedGenre = 'Sci-Fi';

      component.saveGenreAndNext();

      expect(api.submitLibraryReviewDecision).toHaveBeenCalledWith('dune.epub', 'genreSet');
      expect(component.state).toBe('allDone');
    });

    it('should not record the decision when the genre save fails', () => {
      // Otherwise the book leaves the queue still without a genre.
      api.updateLibraryBookMetadata.and.returnValue(throwError(() => new Error('down')));
      component.selectedGenre = 'Sci-Fi';

      component.saveGenreAndNext();

      expect(api.submitLibraryReviewDecision).not.toHaveBeenCalled();
      expect(component.error).toContain('Could not save that genre');
      expect(component.state).toBe('showingBook');
    });

    it('should clear the picked genre before the next book', async () => {
      await build({ phase: 'genre', books: [reviewBook({ fileName: 'a' }), reviewBook({ fileName: 'b' })] });
      component.ngOnInit();
      component.selectedGenre = 'Sci-Fi';

      component.saveGenreAndNext();

      expect(component.selectedGenre).toBe('');
    });
  });

  // ─── Favourites ──────────────────────────────────────────────────────

  describe('favourites', () => {
    beforeEach(() => component.ngOnInit());

    it('should favourite without advancing', () => {
      // Marking a favourite is independent of the keep/delete decision.
      component.toggleFavorite();

      expect(api.setLibraryBookFavorite).toHaveBeenCalledWith('dune.epub', true);
      expect(component.currentIndex).toBe(0);
      expect(component.isCurrentBookFavorited).toBe(true);
    });

    it('should put the star back when the save fails', () => {
      api.setLibraryBookFavorite.and.returnValue(throwError(() => new Error('nope')));

      component.toggleFavorite();

      expect(component.isCurrentBookFavorited).toBe(false);
    });

    it('should do nothing for a session with no name', () => {
      ownerName = null;

      component.toggleFavorite();

      expect(api.setLibraryBookFavorite).not.toHaveBeenCalled();
    });
  });

  // ─── Leaving mid-decision ────────────────────────────────────────────

  describe('leaving mid-decision', () => {
    beforeEach(() => component.ngOnInit());

    /**
     * The defect this pass found.
     *
     * All three writes were piped through the modal's `destroy$` subject.
     * Unsubscribing an HttpClient call aborts the request, so closing the modal
     * on a decision still in flight threw that decision away — and for a
     * thumbs-down, that decision is the call that deletes the book. The user
     * confirmed a deletion, the modal closed, and nothing happened.
     */
    it('should let a decision already in flight survive the modal closing', () => {
      let aborted = false;
      api.submitLibraryReviewDecision.and.returnValue(
        new Observable<{ success: boolean }>(() => () => { aborted = true; }));
      component.thumbsDown();
      component.confirmDelete();

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should let a genre save survive the modal closing', async () => {
      let aborted = false;
      await build({ phase: 'genre' });
      component.ngOnInit();
      api.updateLibraryBookMetadata.and.returnValue(
        new Observable<any>(() => () => { aborted = true; }));
      component.selectedGenre = 'Sci-Fi';
      component.saveGenreAndNext();

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should let a favourite survive the modal closing', () => {
      let aborted = false;
      api.setLibraryBookFavorite.and.returnValue(
        new Observable<{ success: boolean; favoritedBy: string[] }>(() => () => { aborted = true; }));
      component.toggleFavorite();

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should close on the close button', () => {
      component.close();

      expect(dialogRef.close).toHaveBeenCalled();
    });
  });
});

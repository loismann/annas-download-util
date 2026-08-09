import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Subject, from, of } from 'rxjs';
import { catchError, mergeMap, switchMap, takeUntil, tap } from 'rxjs/operators';
import { LibraryApiService, LibraryReviewBook, LibraryReviewSession } from '../../services/library-api.service';
import { LoggerService } from '../../services/logger.service';
import { AuthService } from '../../services/auth.service';
import { STANDARD_GENRES } from '../../constants/book-genres';

type ReviewModalState = 'showingBook' | 'confirmingDelete' | 'submittingDecision' | 'allDone';

interface ReviewSessionBook extends LibraryReviewBook {
  summary?: string | null;
  summarySource?: string | null;
  summaryLoading: boolean;
  summaryError?: string;
}

/** Number of summary fetches allowed in flight at once during background prefetch. */
const PREFETCH_CONCURRENCY = 3;

@Component({
  selector: 'app-library-review-modal',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatSelectModule,
    MatFormFieldModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './library-review-modal.component.html',
  styleUrl: './library-review-modal.component.scss'
})
export class LibraryReviewModalComponent implements OnInit, OnDestroy {
  /**
   * Ends the summary prefetch when the modal closes.
   *
   * Reads only. Unsubscribing an HttpClient call aborts the request, so the
   * three writes on this modal — the favourite, the genre save and the review
   * decision itself — are deliberately left unguarded. They used to run through
   * here, which meant closing the modal on a decision still in flight threw the
   * decision away, including a confirmed delete.
   */
  private destroy$ = new Subject<void>();

  readonly phase: 'cull' | 'genre' | 'complete';
  readonly books: ReviewSessionBook[];
  readonly genreOptions = STANDARD_GENRES.filter(g => g !== 'Uncategorized');

  currentIndex = 0;
  state: ReviewModalState = 'showingBook';
  selectedGenre = '';
  error: string | null = null;

  /** Size of the whole eligible pool for this phase when the session started — decremented
   *  locally as each decision is submitted, so the modal can show real overall progress
   *  ("N left in this phase") rather than just position within today's batch of 20. */
  remainingInPhase: number;

  constructor(
    public dialogRef: MatDialogRef<LibraryReviewModalComponent>,
    @Inject(MAT_DIALOG_DATA) public data: LibraryReviewSession,
    private libraryApi: LibraryApiService,
    private logger: LoggerService,
    private authService: AuthService
  ) {
    this.phase = data.phase;
    this.books = data.books.map(book => ({ ...book, summaryLoading: this.phase === 'cull' }));
    this.remainingInPhase = data.totalRemainingInPhase;
  }

  ngOnInit(): void {
    if (this.books.length === 0) {
      this.state = 'allDone';
      return;
    }

    if (this.phase === 'cull') {
      this.startSummaryPrefetch();
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get currentBook(): ReviewSessionBook | undefined {
    return this.books[this.currentIndex];
  }

  get progressLabel(): string {
    return `Book ${this.currentIndex + 1} of ${this.books.length} today`;
  }

  get remainingLabel(): string {
    const noun = this.phase === 'genre' ? 'need a genre' : 'left to review';
    return `${this.remainingInPhase} book${this.remainingInPhase === 1 ? '' : 's'} ${noun}`;
  }

  /** Kick off every book's summary fetch immediately — book 1 starts loading right away while
   *  the user is looking at it, and bounded concurrency keeps later books mostly ready by the
   *  time the user reaches them without hammering the API with 20 simultaneous requests. */
  private startSummaryPrefetch(): void {
    from(this.books.map((_, i) => i)).pipe(
      mergeMap(i => this.libraryApi.getLibraryBookSummary(this.books[i].fileName, true).pipe(
        tap(res => {
          this.books[i].summary = res.summary;
          this.books[i].summarySource = res.source;
          this.books[i].summaryLoading = false;
        }),
        catchError(err => {
          this.logger.error('[library-review] Failed to load summary', err);
          this.books[i].summaryError = 'Failed to load summary';
          this.books[i].summaryLoading = false;
          return of(null);
        })
      ), PREFETCH_CONCURRENCY),
      takeUntil(this.destroy$)
    ).subscribe();
  }

  get isCurrentBookFavorited(): boolean {
    const ownerName = this.authService.getOwnerName();
    const book = this.currentBook;
    return !!ownerName && !!book && (book.favoritedBy ?? []).includes(ownerName);
  }

  /** Toggles favorite status without advancing — favoriting is independent of the
   *  keep/delete decision, so the user can mark a favorite and still separately decide. */
  toggleFavorite(): void {
    const book = this.currentBook;
    const ownerName = this.authService.getOwnerName();
    if (!book || !ownerName) return;

    const newValue = !this.isCurrentBookFavorited;
    // Optimistic update
    book.favoritedBy = newValue
      ? [...(book.favoritedBy ?? []), ownerName]
      : (book.favoritedBy ?? []).filter(o => o !== ownerName);

    // Not guarded — see the note on destroy$. This is a POST.
    this.libraryApi.setLibraryBookFavorite(book.fileName, newValue).subscribe({
      next: (resp) => {
        book.favoritedBy = resp.favoritedBy;
      },
      error: (err) => {
        this.logger.error('[library-review] Failed to update favorite', err);
        // Revert on error
        book.favoritedBy = newValue
          ? (book.favoritedBy ?? []).filter(o => o !== ownerName)
          : [...(book.favoritedBy ?? []), ownerName];
      }
    });
  }

  thumbsUp(): void {
    this.submitDecision('keep');
  }

  thumbsDown(): void {
    this.state = 'confirmingDelete';
  }

  cancelDelete(): void {
    this.state = 'showingBook';
  }

  confirmDelete(): void {
    this.submitDecision('delete');
  }

  saveGenreAndNext(): void {
    const book = this.currentBook;
    if (!book || !this.selectedGenre) return;

    this.state = 'submittingDecision';
    this.error = null;

    this.libraryApi.updateLibraryBookMetadata(book.fileName, {
      primaryGenre: this.selectedGenre,
      // Round-trip the book's existing tags/series — the metadata PATCH replaces Tags
      // wholesale, and owner tags (e.g. "Paul's Books") live inside that same array.
      // Omitting them here would silently strip ownership off every book in this phase.
      tags: book.tags,
      series: book.series
    // Not guarded — see the note on destroy$. A PATCH followed by a POST, and
    // aborting between the two would leave the genre saved but the book still
    // queued for review.
    }).pipe(
      switchMap(() => this.libraryApi.submitLibraryReviewDecision(book.fileName, 'genreSet'))
    ).subscribe({
      next: () => this.advance(),
      error: err => {
        this.logger.error('[library-review] Failed to save genre', err);
        this.error = 'Could not save that genre — please try again.';
        this.state = 'showingBook';
      }
    });
  }

  private submitDecision(decision: 'keep' | 'delete'): void {
    const book = this.currentBook;
    if (!book) return;

    this.state = 'submittingDecision';
    this.error = null;

    // Not guarded — see the note on destroy$. This is a POST, and for 'delete'
    // it is the call that removes the book.
    this.libraryApi.submitLibraryReviewDecision(book.fileName, decision).subscribe({
      next: () => this.advance(),
      error: err => {
        this.logger.error('[library-review] Failed to submit decision', err);
        this.error = decision === 'delete'
          ? 'Could not delete that book — please try again.'
          : 'Could not save that decision — please try again.';
        this.state = 'showingBook';
      }
    });
  }

  private advance(): void {
    this.error = null;
    this.selectedGenre = '';
    this.remainingInPhase = Math.max(0, this.remainingInPhase - 1);
    if (this.currentIndex + 1 < this.books.length) {
      this.currentIndex++;
      this.state = 'showingBook';
    } else {
      this.state = 'allDone';
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}

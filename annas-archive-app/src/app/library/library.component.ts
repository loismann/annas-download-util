import { ChangeDetectorRef, Component, HostListener, NgZone, OnInit, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subject } from 'rxjs';
import { takeUntil, debounceTime, distinctUntilChanged } from 'rxjs/operators';
import { ScrollingModule, CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { LibraryApiService, LibrarySearchParams } from '../services/library-api.service';
import { BookEditDialogComponent, BookEditDialogData, BookEditDialogResult } from '../components/book-edit-dialog/book-edit-dialog.component';
import { BulkEditDialogComponent, BookBulkEditDialogData, BookBulkEditDialogResult } from '../components/bulk-edit-dialog/bulk-edit-dialog.component';
import { FileUploadDialogComponent } from '../components/file-upload-dialog/file-upload-dialog.component';
import { BookCardComponent, LibraryBook } from '../components/book-card/book-card.component';
import { LibrarySidebarComponent } from '../components/library-sidebar/library-sidebar.component';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { BATCH_DELAY_MS } from '../constants/timeouts';

@Component({
  selector: 'app-library',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ScrollingModule,
    MatCardModule,
    MatProgressSpinnerModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule,
    BookCardComponent,
    LibrarySidebarComponent
  ],
  templateUrl: './library.component.html',
  styleUrls: ['./library.component.css']
})
export class LibraryComponent implements OnInit, OnDestroy {
  private destroy$ = new Subject<void>();
  private searchTrigger$ = new Subject<void>();  // Debounced search trigger

  placeholderUrl = '/assets/placeholder.jpg';
  loading = true;
  loadingMore = false;  // True when fetching more books for infinite scroll
  error: string | null = null;
  books: LibraryBook[] = [];  // Currently loaded books (server-filtered/sorted)
  totalCount = 0;  // Total count from server (for "showing X of Y")
  searchTerm = '';
  selectedGenre = '';
  genres: string[] = [];  // Available genres from server
  private readonly kindleFormats = new Set(['EPUB', 'KFX', 'AZW', 'AZW3', 'POBI', 'MOBI']);
  adminOpen = false;
  filterMissingAuthor = false;
  filterMissingCover = false;
  filterGenreCountEnabled = false;
  filterGenreCount = 1;
  filterGenreComparison: 'less' | 'more' = 'less';
  filterBookmarked = false;
  minPersonalRating = 0;
  minGoodreadsRating = 0;
  sortOrder: 'title' | 'author' | 'recent' | 'series' | 'stars' | 'goodreads' = 'recent';
  sortDirection: 'down' | 'up' = 'down';
  activeLetter = '#';
  private scrollFrameRequested = false;
  selectedOwnerTags = new Set<string>();
  readonly ownerTags = ["Dad's Books", "Mom's Books", "Paul's Books"];
  bulkEditMode = false;
  selectedBooksForBulk = new Set<string>();
  tileSize: 'small' | 'medium' | 'large' = 'medium';
  sidebarCollapsed = false;

  /** Server-side pagination state */
  private readonly PAGE_SIZE = 100;  // Number of books to fetch per request
  private hasMoreBooks = true;  // Whether more books can be loaded
  private currentSearchParams: LibrarySearchParams = {};  // Current search parameters

  @ViewChild(CdkVirtualScrollViewport) virtualScroll?: CdkVirtualScrollViewport;

  /** Cached items per row - recalculated on resize */
  private cachedItemsPerRow = 6;

  constructor(
    private libraryApi: LibraryApiService,
    private dialog: MatDialog,
    public authService: AuthService,
    private zone: NgZone,
    private cdr: ChangeDetectorRef,
    private logger: LoggerService
  ) {}

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  /** Row height for virtual scrolling - varies by tile size */
  get rowHeight(): number {
    switch (this.tileSize) {
      case 'small': return 320;
      case 'large': return 480;
      default: return 400;
    }
  }

  /** Get items per row based on tile size - used for virtual scroll row grouping */
  private getItemsPerRow(): number {
    // Fixed items per row based on tile size - CSS handles the actual layout
    switch (this.tileSize) {
      case 'small': return 8;
      case 'large': return 4;
      default: return 6;
    }
  }

  /** Recalculate items per row (called on resize and tile size change) */
  recalculateLayout(): void {
    this.cachedItemsPerRow = this.getItemsPerRow();
    // Check method exists for test compatibility
    if (this.virtualScroll && typeof this.virtualScroll.checkViewportSize === 'function') {
      this.virtualScroll.checkViewportSize();
    }
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.recalculateLayout();
  }

  /** Group filtered books into rows for virtual scrolling */
  get bookRows(): LibraryBook[][] {
    const books = this.filteredBooks;
    const perRow = this.cachedItemsPerRow;
    const rows: LibraryBook[][] = [];
    for (let i = 0; i < books.length; i += perRow) {
      rows.push(books.slice(i, i + perRow));
    }
    return rows;
  }

  /** Track rows by first book's filename for efficient rendering */
  trackByRow(index: number, row: LibraryBook[]): string {
    return row[0]?.fileName || `row-${index}`;
  }

  ngOnInit(): void {
    // Start with sidebar collapsed on mobile for better UX
    if (window.innerWidth <= 768) {
      this.sidebarCollapsed = true;
    }

    // Set up debounced search trigger (300ms debounce for filter changes)
    this.searchTrigger$.pipe(
      debounceTime(300),
      takeUntil(this.destroy$)
    ).subscribe(() => {
      this.executeSearch(true);  // Reset and search from beginning
    });

    // Initial load
    this.logger.log('[library] Starting server-side search load...');
    this.executeSearch(true);
  }

  /**
   * Execute server-side search with current filter/sort parameters.
   * @param reset If true, resets pagination and replaces all books. If false, appends to existing.
   */
  private executeSearch(reset: boolean): void {
    if (reset) {
      this.loading = true;
      this.books = [];
      this.hasMoreBooks = true;
    } else {
      if (!this.hasMoreBooks || this.loadingMore) return;
      this.loadingMore = true;
    }

    // Build search parameters from current filter state
    const params: LibrarySearchParams = {
      q: this.searchTerm.trim() || undefined,
      genre: this.selectedGenre || undefined,
      ownerTags: this.selectedOwnerTags.size > 0 ? Array.from(this.selectedOwnerTags) : undefined,
      minPersonalRating: this.minPersonalRating > 0 ? this.minPersonalRating : undefined,
      minGoodreadsRating: this.minGoodreadsRating > 0 ? this.minGoodreadsRating : undefined,
      bookmarked: this.filterBookmarked || undefined,
      missingAuthor: this.filterMissingAuthor || undefined,
      missingCover: this.filterMissingCover || undefined,
      genreCountLessThan: this.filterGenreCountEnabled && this.filterGenreComparison === 'less' ? this.filterGenreCount : undefined,
      genreCountMoreThan: this.filterGenreCountEnabled && this.filterGenreComparison === 'more' ? this.filterGenreCount : undefined,
      sortBy: this.sortOrder === 'recent' ? 'date' : this.sortOrder,
      sortDesc: this.sortDirection === 'down',
      skip: reset ? 0 : this.books.length,
      take: this.PAGE_SIZE
    };

    this.currentSearchParams = params;

    this.libraryApi.searchLibraryBooks(params).pipe(takeUntil(this.destroy$)).subscribe({
      next: (response) => {
        this.logger.log('[library] Search response', {
          booksReturned: response.books?.length ?? 0,
          totalCount: response.totalCount,
          skip: response.skip,
          reset
        });

        const newBooks = (response.books ?? []).map(book => ({
          ...book,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const
        }));

        if (reset) {
          this.books = newBooks;
        } else {
          this.books = [...this.books, ...newBooks];
        }

        this.totalCount = response.totalCount;
        this.genres = response.genres ?? [];
        this.hasMoreBooks = this.books.length < response.totalCount;
        this.loading = false;
        this.loadingMore = false;

        setTimeout(() => {
          this.recalculateLayout();
          this.onGridScroll();
        }, 0);
      },
      error: (err) => {
        this.logger.error('[library] Search failed', {
          status: err?.status,
          message: err?.message,
          error: err?.error
        });
        this.error = 'Failed to load library.';
        this.loading = false;
        this.loadingMore = false;
      }
    });
  }

  /**
   * Check if we need to load more books (infinite scroll trigger).
   * Called when virtual scroll position changes.
   */
  private checkInfiniteScroll(): void {
    if (!this.virtualScroll || !this.hasMoreBooks || this.loadingMore || this.loading) return;

    const range = this.virtualScroll.getRenderedRange();
    const totalRows = this.bookRows.length;

    // Load more when within 5 rows of the end
    if (range.end >= totalRows - 5) {
      this.logger.log('[library] Infinite scroll: loading more books', {
        renderedEnd: range.end,
        totalRows,
        currentBooks: this.books.length,
        totalCount: this.totalCount
      });
      this.executeSearch(false);
    }
  }

  onCoverError(evt: Event): void {
    const img = evt.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
  }

  /**
   * Trigger a debounced search when filters change.
   * This replaces the old client-side filtering - all filtering now happens server-side.
   */
  invalidateFilterCache(): void {
    this.searchTrigger$.next();
  }

  /**
   * Returns the server-filtered and sorted books.
   * Books are already filtered/sorted by the server - no client-side processing needed.
   */
  get filteredBooks(): LibraryBook[] {
    return this.books;
  }

  trackByFileName(index: number, book: LibraryBook): string {
    return book.fileName || `${book.title}-${index}`;
  }

  get availableLetters(): string[] {
    const letters = new Set(this.filteredBooks.map(book => this.getBookLetter(book)));
    return Array.from(letters).sort();
  }

  get alphabetIndex(): string[] {
    return ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('')];
  }

  get showAlphabetIndex(): boolean {
    return this.sortOrder === 'title' || this.sortOrder === 'author' || this.sortOrder === 'series';
  }

  onGridScroll(): void {
    if (!this.virtualScroll) return;

    // Check for infinite scroll (load more books when near end)
    this.checkInfiniteScroll();

    // Update alphabet index active letter
    if (!this.showAlphabetIndex) return;
    if (this.scrollFrameRequested) return;
    this.scrollFrameRequested = true;
    requestAnimationFrame(() => {
      this.scrollFrameRequested = false;
      // Get the current row index from virtual scroll (check method exists for tests)
      if (!this.virtualScroll || typeof this.virtualScroll.getRenderedRange !== 'function') return;
      const rowIndex = this.virtualScroll.getRenderedRange().start ?? 0;
      // Calculate book index from row index
      const bookIndex = rowIndex * this.cachedItemsPerRow;
      const book = this.filteredBooks[bookIndex];
      if (!book) return;
      const nextLetter = this.getBookLetter(book);
      if (nextLetter !== this.activeLetter) {
        this.zone.run(() => {
          this.logger.debug('[library-alpha] active letter changed', {
            from: this.activeLetter,
            to: nextLetter,
            rowIndex,
            bookIndex,
            title: book.title
          });
          this.activeLetter = nextLetter;
          this.cdr.markForCheck();
        });
      }
    });
  }

  scrollToLetter(letter: string): void {
    if (this.availableLetters.indexOf(letter) === -1) return;
    if (!this.virtualScroll) return;
    const books = this.filteredBooks;
    const bookIndex = books.findIndex(book => this.getBookLetter(book) === letter);
    if (bookIndex === -1) return;
    // Calculate row index from book index
    const rowIndex = Math.floor(bookIndex / this.cachedItemsPerRow);
    this.virtualScroll.scrollToIndex(rowIndex, 'smooth');
    this.zone.run(() => {
      this.activeLetter = letter;
      this.cdr.markForCheck();
    });
  }

  onSortChange(preserveDirection = false): void {
    if (!preserveDirection) {
      this.sortDirection = this.getDefaultSortDirection(this.sortOrder);
    }

    // Scroll to top using virtual scroll
    this.virtualScroll?.scrollToIndex(0);

    // Trigger server-side search with new sort parameters
    this.executeSearch(true);

    if (!this.showAlphabetIndex) {
      this.activeLetter = '#';
      return;
    }

    // Update active letter after search completes
    setTimeout(() => {
      const books = this.filteredBooks;
      if (books.length === 0) {
        this.activeLetter = '#';
        return;
      }
      this.activeLetter = this.getBookLetter(books[0]);
    }, 100);
  }

  toggleSortDirection(): void {
    this.sortDirection = this.sortDirection === 'down' ? 'up' : 'down';
    this.onSortChange(true);
  }

  setTileSize(size: 'small' | 'medium' | 'large'): void {
    this.tileSize = size;
    // Recalculate layout after tile size change
    setTimeout(() => this.recalculateLayout(), 0);
  }

  toggleBookmarkFilter(): void {
    this.filterBookmarked = !this.filterBookmarked;
    this.invalidateFilterCache();
  }

  resetView(): void {
    // Reset search and filters
    this.searchTerm = '';
    this.selectedGenre = '';
    this.minPersonalRating = 0;
    this.minGoodreadsRating = 0;
    this.selectedOwnerTags.clear();

    // Reset sorting to default
    this.sortOrder = 'recent';
    this.sortDirection = 'down';

    // Reset tile size
    this.tileSize = 'medium';

    // Reset admin filters
    this.filterMissingAuthor = false;
    this.filterMissingCover = false;
    this.filterGenreCountEnabled = false;
    this.filterGenreCount = 1;
    this.filterGenreComparison = 'less';
    this.filterBookmarked = false;

    // Exit bulk edit mode
    this.bulkEditMode = false;
    this.selectedBooksForBulk.clear();

    // Scroll grid to top using virtual scroll
    this.virtualScroll?.scrollToIndex(0);

    // Recalculate layout for medium tile size
    setTimeout(() => this.recalculateLayout(), 0);

    this.activeLetter = '#';

    // Trigger server-side search with reset filters
    this.executeSearch(true);
  }

  private getDefaultSortDirection(order: typeof this.sortOrder): 'down' | 'up' {
    return 'down';
  }

  private getBookLetter(book: LibraryBook): string {
    let value = '';
    switch (this.sortOrder) {
      case 'author':
        value = book.authors?.[0] || '';
        break;
      case 'series':
        value = book.series?.trim() || book.title || '';
        break;
      case 'recent':
      case 'stars':
      case 'goodreads':
      case 'title':
      default:
        value = book.title || '';
        break;
    }
    const letter = value.trim().charAt(0).toUpperCase();
    return letter >= 'A' && letter <= 'Z' ? letter : '#';
  }

  onCoverClick(book: LibraryBook): void {
    const dialogData: BookEditDialogData = {
      title: book.title,
      authors: book.authors,
      primaryGenre: book.primaryGenre || null,
      tags: book.tags || [],
      series: book.series || null,
      coverUrl: book.coverUrl,
      availableGenres: this.genres,
      fileName: book.fileName,
      format: book.format,
      canSendToKindle: this.canSendToKindle(book),
      readerEnabled: book.readerEnabled ?? false
    };

    const dialogRef = this.dialog.open(BookEditDialogComponent, {
      width: '920px',
      maxWidth: '95vw',
      data: dialogData,
      panelClass: 'book-edit-dialog-panel',
      backdropClass: 'book-edit-dialog-backdrop'
    });

    dialogRef.afterClosed().pipe(takeUntil(this.destroy$)).subscribe((result: BookEditDialogResult | undefined) => {
      if (result?.deleted) {
        this.books = this.books.filter(b => b !== book);
        this.genres = this.buildGenreList(this.books);
        return;
      }

      if (result && book.fileName) {
        // Update local book object
        book.primaryGenre = result.primaryGenre ?? book.primaryGenre;
        book.tags = result.tags ?? book.tags;
        book.series = result.series ?? book.series;
        if (result.title) {
          book.title = result.title;
        }
        if (result.authors) {
          book.authors = result.authors;
        }

        // Rebuild genre list for filter dropdown
        this.genres = this.buildGenreList(this.books);

        // Update on backend
        this.libraryApi.updateLibraryBookMetadata(book.fileName, {
          primaryGenre: result.primaryGenre ?? book.primaryGenre ?? 'Uncategorized',
          tags: result.tags ?? book.tags ?? [],
          series: result.series ?? book.series ?? null,
          title: result.title,
          authors: result.authors
        }).pipe(takeUntil(this.destroy$)).subscribe({
          next: () => {
            this.logger.log('[library] Updated book metadata:', book.fileName);
          },
          error: (err) => {
            this.logger.error('[library] Failed to update book metadata:', err);
          }
        });

        if (result.coverUrl) {
          this.logger.log('[library] Updating cover for', book.fileName, 'with URL:', result.coverUrl);
          this.updateBookCover(book, result.coverUrl);
        } else {
          this.logger.log('[library] No coverUrl in result, skipping cover update');
        }
      }
    });
  }

  sendToKindle(book: LibraryBook, target: 'dad' | 'mom'): void {
    if (!book.fileName) return;
    if (!this.canSendToKindle(book)) return;
    if (target === 'dad' && book.dadsKindleState === 'sending') return;
    if (target === 'mom' && book.momsKindleState === 'sending') return;

    if (target === 'dad') {
      book.dadsKindleState = 'sending';
    } else {
      book.momsKindleState = 'sending';
    }

    this.libraryApi.sendLibraryToKindle(book.fileName, book.title, target).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        if (target === 'dad') {
          book.dadsKindleState = success ? 'success' : 'error';
        } else {
          book.momsKindleState = success ? 'success' : 'error';
        }
      },
      error: (err) => {
        this.logger.error('[library] send-to-kindle failed', err);
        if (target === 'dad') {
          book.dadsKindleState = 'error';
        } else {
          book.momsKindleState = 'error';
        }
      }
    });
  }

  sendToDropbox(book: LibraryBook): void {
    if (!book.fileName) return;
    if (!this.canSendToKindle(book)) return;
    if (book.dadsKindleState === 'sending') return;

    book.dadsKindleState = 'sending';
    this.libraryApi.sendLibraryToKindle(book.fileName, book.title, 'dad', true).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        book.dadsKindleState = success ? 'success' : 'error';
      },
      error: (err) => {
        this.logger.error('[library] send-to-dropbox failed', err);
        book.dadsKindleState = 'error';
      }
    });
  }

  setPersonalRating(book: LibraryBook, rating: number): void {
    if (!book.fileName) return;
    const current = book.personalRating ?? 0;
    const nextRating = rating === 1 && current === 1 ? 0 : rating;
    if (nextRating === current) return;

    book.personalRating = nextRating;
    this.libraryApi.updateLibraryBookRatings(book.fileName, {
      personalRating: nextRating
    }).pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.logger.log('[library] Updated personal rating:', book.fileName, nextRating);
      },
      error: (err) => {
        this.logger.error('[library] Failed to update personal rating:', err);
      }
    });
  }

  onRatingChange(event: { book: LibraryBook; rating: number }): void {
    this.setPersonalRating(event.book, event.rating);
  }

  onBookmarkToggle(book: LibraryBook): void {
    if (!book.fileName) return;

    const newValue = !book.bookmarked;
    book.bookmarked = newValue;

    this.libraryApi.updateLibraryBookRatings(book.fileName, {
      bookmarked: newValue
    }).pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.logger.log('[library] Updated bookmark:', book.fileName, newValue);
      },
      error: (err) => {
        this.logger.error('[library] Failed to update bookmark:', err);
        // Revert on error
        book.bookmarked = !newValue;
      }
    });
  }

  onSendToKindle(event: { book: LibraryBook; target: 'dad' | 'mom' }): void {
    this.sendToKindle(event.book, event.target);
  }

  /**
   * Update book cover - tries to fetch as blob first (to bypass hotlink protection),
   * then falls back to URL-based upload if blob fetch fails.
   */
  private updateBookCover(book: LibraryBook, coverUrl: string): void {
    this.logger.log('[library] === COVER UPDATE START ===');
    this.logger.log('[library] Book:', book.fileName);
    this.logger.log('[library] Cover URL:', coverUrl);

    // Try to fetch the image as a blob from the browser
    // Browsers can bypass hotlink protection because they have valid referer headers
    this.fetchImageAsBase64(coverUrl)
      .then(({ base64, mimeType }) => {
        this.logger.log('[library] ✓ Blob fetch SUCCESS');
        this.logger.log('[library] MimeType:', mimeType);
        this.logger.log('[library] Base64 length:', base64.length, 'chars');
        this.logger.log('[library] Uploading bytes to backend...');

        this.libraryApi.uploadLibraryBookCoverBytes(book.fileName, base64, mimeType).pipe(takeUntil(this.destroy$)).subscribe({
          next: (resp) => {
            this.logger.log('[library] ✓ Bytes upload SUCCESS');
            this.handleCoverUpdateResponse(book, resp, coverUrl);
          },
          error: (err) => {
            this.logger.error('[library] ✗ Bytes upload FAILED:', err);
            this.logger.log('[library] Falling back to URL method...');
            this.updateBookCoverByUrl(book, coverUrl);
          }
        });
      })
      .catch((err) => {
        this.logger.warn('[library] ✗ Blob fetch FAILED (CORS?):', err.message || err);
        this.logger.log('[library] Falling back to URL method...');
        this.updateBookCoverByUrl(book, coverUrl);
      });
  }

  private updateBookCoverByUrl(book: LibraryBook, coverUrl: string): void {
    this.logger.log('[library] URL method: Sending URL to backend for server-side download...');
    this.libraryApi.updateLibraryBookCover(book.fileName, coverUrl).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        this.logger.log('[library] ✓ URL upload SUCCESS');
        this.handleCoverUpdateResponse(book, resp, coverUrl);
      },
      error: (err) => {
        this.logger.error('[library] ✗ URL upload FAILED:', err);
        this.logger.error('[library] Error details:', err.error);
        this.logger.log('[library] === COVER UPDATE FAILED ===');
      }
    });
  }

  private handleCoverUpdateResponse(
    book: LibraryBook,
    resp: { success?: boolean; coverUrl?: string | null },
    requestedCoverUrl: string
  ): void {
    this.logger.log('[library] Backend response:', JSON.stringify(resp));
    const timestamp = new Date().getTime();
    if (resp?.coverUrl) {
      const separator = resp.coverUrl.includes('?') ? '&' : '?';
      book.coverUrl = `${resp.coverUrl}${separator}t=${timestamp}`;
      this.logger.log('[library] ✓ Updated book.coverUrl to:', book.coverUrl);
      this.logger.log('[library] === COVER UPDATE SUCCESS ===');
    } else if (resp?.success) {
      this.logger.warn('[library] Backend returned success=true but no coverUrl');
      const separator = requestedCoverUrl.includes('?') ? '&' : '?';
      book.coverUrl = `${requestedCoverUrl}${separator}t=${timestamp}`;
      this.logger.log('[library] Using requested URL as fallback:', book.coverUrl);
      this.logger.log('[library] === COVER UPDATE PARTIAL SUCCESS ===');
    } else {
      this.logger.error('[library] ✗ Backend response missing both coverUrl and success flag!');
      this.logger.log('[library] === COVER UPDATE FAILED ===');
    }
  }

  private fetchImageAsBase64(url: string): Promise<{ base64: string; mimeType: string }> {
    return new Promise((resolve, reject) => {
      fetch(url, { mode: 'cors' })
        .then(response => {
          if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
          }
          const mimeType = response.headers.get('content-type') || 'image/jpeg';
          return response.blob().then(blob => ({ blob, mimeType }));
        })
        .then(({ blob, mimeType }) => {
          const reader = new FileReader();
          reader.onloadend = () => {
            const result = reader.result as string;
            // Remove data URL prefix to get pure base64
            const base64 = result.includes(',') ? result.split(',')[1] : result;
            resolve({ base64, mimeType });
          };
          reader.onerror = () => reject(new Error('FileReader error'));
          reader.readAsDataURL(blob);
        })
        .catch(reject);
    });
  }

  private buildGenreList(books: LibraryBook[]): string[] {
    const set = new Set<string>();
    books.forEach(book => {
      if (book.primaryGenre && !this.ownerTags.includes(book.primaryGenre)) {
        set.add(book.primaryGenre);
      }
      (book.tags ?? []).forEach(tag => {
        if (!this.ownerTags.includes(tag)) {
          set.add(tag);
        }
      });
    });
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }

  private buildGenreListForBulkEdit(books: LibraryBook[]): string[] {
    const set = new Set<string>();
    books.forEach(book => {
      if (book.primaryGenre) {
        set.add(book.primaryGenre);
      }
      (book.tags ?? []).forEach(tag => {
        set.add(tag);
      });
    });
    // Add owner tags explicitly to ensure they're always available
    this.ownerTags.forEach(tag => set.add(tag));
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }

  toggleOwnerTag(tag: string): void {
    if (this.selectedOwnerTags.has(tag)) {
      this.selectedOwnerTags.delete(tag);
    } else {
      this.selectedOwnerTags.add(tag);
    }
    this.invalidateFilterCache();
  }

  canSendToKindle(book: LibraryBook): boolean {
    if (!book.format) return false;
    return this.kindleFormats.has(book.format.toUpperCase());
  }

  toggleAdmin(): void {
    this.adminOpen = !this.adminOpen;
  }

  wipeAllGenres(): void {
    const first = window.confirm('Are you sure you want to wipe all genres? This cannot be undone.');
    if (!first) return;

    const second = window.confirm('Are you really sure? This will remove all genres and tags.');
    if (!second) return;

    this.libraryApi.wipeLibraryGenres().pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.books = this.books.map(book => ({
          ...book,
          primaryGenre: null,
          tags: []
        }));
        this.genres = this.buildGenreList(this.books);
      },
      error: (err) => {
        this.logger.error('[library] Failed to wipe genres', err);
      }
    });
  }

  toggleBulkEditMode(): void {
    this.bulkEditMode = !this.bulkEditMode;
    if (!this.bulkEditMode) {
      this.selectedBooksForBulk.clear();
    }
  }

  /** Toggle mobile sidebar collapsed state */
  toggleSidebar(): void {
    this.sidebarCollapsed = !this.sidebarCollapsed;
  }

  toggleBookSelection(book: LibraryBook): void {
    if (!this.bulkEditMode) return;

    if (this.selectedBooksForBulk.has(book.fileName)) {
      this.selectedBooksForBulk.delete(book.fileName);
    } else {
      this.selectedBooksForBulk.add(book.fileName);
    }
  }

  selectAllVisible(): void {
    if (!this.bulkEditMode) return;

    // Add all currently visible (filtered) books to the selection
    for (const book of this.filteredBooks) {
      this.selectedBooksForBulk.add(book.fileName);
    }
  }

  openBulkEditDialog(): void {
    if (this.selectedBooksForBulk.size === 0) return;

    const selectedBooks = this.books.filter(book => this.selectedBooksForBulk.has(book.fileName));
    const bookFileNames = selectedBooks.map(book => book.fileName);
    const bookTitles = selectedBooks.map(book => book.title);

    const dialogData: BookBulkEditDialogData = {
      bookFileNames,
      bookTitles,
      availableGenres: this.buildGenreListForBulkEdit(this.books)
    };

    const dialogRef = this.dialog.open(BulkEditDialogComponent, {
      width: '650px',
      data: dialogData
    });

    dialogRef.afterClosed().pipe(takeUntil(this.destroy$)).subscribe((result: BookBulkEditDialogResult | undefined) => {
      if (!result) return;

      // Handle bulk delete
      if (result.deleted) {
        this.bulkDeleteBooks(selectedBooks);
        return;
      }

      // Handle bulk bookmark
      if (result.bookmarkAll) {
        this.bulkBookmarkBooks(selectedBooks);
        return;
      }

      // Update all selected books with the new metadata
      for (const book of selectedBooks) {
        if (result.authors) {
          book.authors = result.authors;
        }
        if (result.primaryGenre !== undefined) {
          book.primaryGenre = result.primaryGenre;
        }
        if (result.tags) {
          // Handle append vs replace mode for tags
          if (result.tagsMode === 'append') {
            // Merge existing tags with new tags, avoiding duplicates
            const existingTags = book.tags ?? [];
            const newTags = result.tags;
            book.tags = [...new Set([...existingTags, ...newTags])];
          } else {
            // Replace mode: overwrite all tags
            book.tags = result.tags;
          }
        }
        if (result.series !== undefined) {
          book.series = result.series;
        }

        // Update on backend
        this.libraryApi.updateLibraryBookMetadata(book.fileName, {
          primaryGenre: result.primaryGenre ?? book.primaryGenre ?? 'Uncategorized',
          tags: book.tags ?? [],
          series: result.series ?? book.series ?? null,
          title: book.title,
          authors: result.authors ?? book.authors
        }).pipe(takeUntil(this.destroy$)).subscribe({
          next: () => {
            this.logger.log('[library] Updated book metadata:', book.fileName);
          },
          error: (err) => {
            this.logger.error('[library] Failed to update book metadata:', err);
          }
        });
      }

      // Rebuild genre list for filter dropdown
      this.genres = this.buildGenreList(this.books);

      // Exit bulk edit mode and clear selection
      this.bulkEditMode = false;
      this.selectedBooksForBulk.clear();
    });
  }

  async bulkSend(action: 'dropbox' | 'kindle-dad' | 'kindle-mom'): Promise<void> {
    if (this.selectedBooksForBulk.size === 0) return;

    const selectedBooks = this.books.filter(book => this.selectedBooksForBulk.has(book.fileName));

    for (let i = 0; i < selectedBooks.length; i++) {
      const book = selectedBooks[i];

      if (action === 'dropbox') {
        this.sendToDropbox(book);
      } else if (action === 'kindle-dad') {
        this.sendToKindle(book, 'dad');
      } else if (action === 'kindle-mom') {
        this.sendToKindle(book, 'mom');
      }

      // Wait between sends (except after last book)
      if (i < selectedBooks.length - 1) {
        await this.delay(BATCH_DELAY_MS);
      }
    }

    // Exit bulk edit mode after completion
    this.bulkEditMode = false;
    this.selectedBooksForBulk.clear();
  }

  private async bulkDeleteBooks(selectedBooks: LibraryBook[]): Promise<void> {
    let successCount = 0;
    let failCount = 0;

    for (const book of selectedBooks) {
      try {
        await this.libraryApi.deleteLibraryBook(book.fileName).toPromise();
        // Remove from local array
        this.books = this.books.filter(b => b.fileName !== book.fileName);
        successCount++;
        this.logger.log('[library] Deleted book:', book.fileName);
      } catch (err) {
        failCount++;
        this.logger.error('[library] Failed to delete book:', book.fileName, err);
      }
    }

    // Rebuild genre list after deletions
    this.genres = this.buildGenreList(this.books);

    // Exit bulk edit mode and clear selection
    this.bulkEditMode = false;
    this.selectedBooksForBulk.clear();

    // Log summary
    this.logger.log(`[library] Bulk delete complete: ${successCount} deleted, ${failCount} failed`);
  }

  private async bulkBookmarkBooks(selectedBooks: LibraryBook[]): Promise<void> {
    let successCount = 0;
    let failCount = 0;

    for (const book of selectedBooks) {
      try {
        await this.libraryApi.updateLibraryBookRatings(book.fileName, { bookmarked: true }).toPromise();
        // Update local state
        book.bookmarked = true;
        successCount++;
        this.logger.log('[library] Bookmarked book:', book.fileName);
      } catch (err) {
        failCount++;
        this.logger.error('[library] Failed to bookmark book:', book.fileName, err);
      }
    }

    // Exit bulk edit mode and clear selection
    this.bulkEditMode = false;
    this.selectedBooksForBulk.clear();

    // Log summary
    this.logger.log(`[library] Bulk bookmark complete: ${successCount} bookmarked, ${failCount} failed`);
  }

  private delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  openUploadDialog(): void {
    const dialogRef = this.dialog.open(FileUploadDialogComponent, {
      width: '650px',
      maxWidth: '95vw'
    });

    dialogRef.afterClosed().pipe(takeUntil(this.destroy$)).subscribe((hasUploads: boolean) => {
      if (hasUploads) {
        // Refresh the library to show newly uploaded books
        this.loading = true;
        this.libraryApi.getLibraryBooks().pipe(takeUntil(this.destroy$)).subscribe({
          next: (books) => {
            this.books = (books ?? []).map(book => ({
              ...book,
              dadsKindleState: 'idle',
              momsKindleState: 'idle'
            }));
            this.genres = this.buildGenreList(this.books);
            this.loading = false;
            setTimeout(() => {
              this.recalculateLayout();
              this.onGridScroll();
            }, 0);
          },
          error: (err) => {
            this.logger.error('[library] failed to reload books after upload', err);
            this.loading = false;
          }
        });
      }
    });
  }
}

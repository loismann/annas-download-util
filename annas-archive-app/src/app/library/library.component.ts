import { ChangeDetectorRef, Component, ElementRef, NgZone, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { BookEditDialogComponent, BookEditDialogData, BookEditDialogResult } from '../components/book-edit-dialog/book-edit-dialog.component';
import { BulkEditDialogComponent, BookBulkEditDialogData, BookBulkEditDialogResult } from '../components/bulk-edit-dialog/bulk-edit-dialog.component';
import { AuthService } from '../services/auth.service';

interface LibraryBook {
  title: string;
  authors: string[];
  format: string;
  fileSize: string;
  fileName: string;
  coverUrl?: string | null;
  source?: string | null;
  savedAt?: string | null;
  primaryGenre?: string | null;
  tags?: string[];
  series?: string | null;
  publishedDate?: string | null;
  pages?: string | null;
  md5?: string | null;
  goodreadsRating?: number | null;
  personalRating?: number | null;
  readerEnabled?: boolean | null;
  dadsKindleState?: 'idle' | 'sending' | 'success' | 'error';
  momsKindleState?: 'idle' | 'sending' | 'success' | 'error';
}

@Component({
  selector: 'app-library',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatProgressSpinnerModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule
  ],
  templateUrl: './library.component.html',
  styleUrls: ['./library.component.css']
})
export class LibraryComponent implements OnInit {
  placeholderUrl = '/assets/placeholder.jpg';
  loading = true;
  error: string | null = null;
  books: LibraryBook[] = [];
  searchTerm = '';
  selectedGenre = '';
  genres: string[] = [];
  private readonly kindleFormats = new Set(['EPUB', 'KFX', 'AZW', 'AZW3', 'POBI', 'MOBI']);
  adminOpen = false;
  filterMissingAuthor = false;
  filterMissingCover = false;
  filterGenreCountEnabled = false;
  filterGenreCount = 1;
  filterGenreComparison: 'less' | 'more' = 'less';
  minPersonalRating = 0;
  minGoodreadsRating = 0;
  sortOrder: 'title' | 'author' | 'recent' | 'series' | 'stars' | 'goodreads' = 'recent';
  sortDirection: 'down' | 'up' = 'down';
  activeLetter = '#';
  private scrollFrameRequested = false;
  readonly starRange = [1, 2, 3, 4, 5];
  selectedOwnerTags = new Set<string>();
  readonly ownerTags = ["Dad's Books", "Mom's Books", "Paul's Books"];
  bulkEditMode = false;
  selectedBooksForBulk = new Set<string>();
  tileSize: 'small' | 'medium' | 'large' = 'medium';

  @ViewChild('libraryGrid') libraryGrid?: ElementRef<HTMLDivElement>;

  constructor(
    private api: AnnaArchiveApiService,
    private dialog: MatDialog,
    public authService: AuthService,
    private zone: NgZone,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.api.getLibraryBooks().subscribe({
      next: (books) => {
        this.books = (books ?? []).map(book => ({
          ...book,
          dadsKindleState: 'idle',
          momsKindleState: 'idle'
        }));
        this.genres = this.buildGenreList(this.books);
        this.loading = false;
        setTimeout(() => this.onGridScroll(), 0);
      },
      error: (err) => {
        console.error('[library] failed to load books', err);
        this.error = 'Failed to load library.';
        this.loading = false;
      }
    });
  }

  onCoverError(evt: Event): void {
    const img = evt.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
  }

  get filteredBooks(): LibraryBook[] {
    const term = this.searchTerm.trim().toLowerCase();
    const genre = this.selectedGenre.toLowerCase();

    const filtered = this.books.filter(book => {
      if (this.sortOrder === 'series') {
        const seriesName = book.series?.trim();
        if (!seriesName) return false;
      }

      if (genre) {
        const primary = book.primaryGenre?.toLowerCase() ?? '';
        const tags = (book.tags ?? []).map(tag => tag.toLowerCase());
        if (primary !== genre && !tags.includes(genre)) {
          return false;
        }
      }

      if (this.selectedOwnerTags.size > 0) {
        const tags = book.tags ?? [];
        const matchesOwner = tags.some(tag => this.selectedOwnerTags.has(tag));
        if (!matchesOwner) return false;
      }

      const haystack = [
        book.title,
        ...(book.authors ?? []),
        book.series ?? '',
        book.primaryGenre ?? '',
        ...(book.tags ?? [])
      ]
        .join(' ')
        .toLowerCase();

      if (term && !haystack.includes(term)) {
        return false;
      }

      if (this.filterMissingAuthor) {
        const hasAuthor = (book.authors ?? []).some(author => author.trim().length > 0);
        if (hasAuthor) return false;
      }

      if (this.filterMissingCover) {
        if (book.coverUrl) return false;
      }

      if (this.filterGenreCountEnabled) {
        const tagCount = (book.tags ?? []).length;
        const count = tagCount + (book.primaryGenre ? 1 : 0);
        if (this.filterGenreComparison === 'less') {
          if (count >= this.filterGenreCount) return false;
        } else {
          if (count <= this.filterGenreCount) return false;
        }
      }

      if (this.minPersonalRating > 0) {
        const personal = book.personalRating ?? 0;
        if (personal < this.minPersonalRating) return false;
      }

      if (this.minGoodreadsRating > 0) {
        const goodreads = book.goodreadsRating ?? 0;
        if (goodreads < this.minGoodreadsRating) return false;
      }

      if (this.sortOrder === 'stars') {
        const personal = book.personalRating ?? 0;
        if (personal < 1) return false;
      }

      if (this.sortOrder === 'goodreads') {
        const goodreads = book.goodreadsRating ?? 0;
        if (goodreads <= 0) return false;
      }

      return true;
    });

    return filtered.slice().sort((a, b) => this.compareBooks(a, b));
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

  private compareBooks(a: LibraryBook, b: LibraryBook): number {
    switch (this.sortOrder) {
      case 'title':
        return this.applyDirection(
          (a.title || '').localeCompare(b.title || '', undefined, { sensitivity: 'base' })
        );
      case 'author':
        return this.applyDirection(
          (a.authors?.[0] || '').localeCompare(b.authors?.[0] || '', undefined, { sensitivity: 'base' })
        );
      case 'series': {
        const aSeries = a.series?.trim() || 'zzzzzz';
        const bSeries = b.series?.trim() || 'zzzzzz';
        const seriesCompare = aSeries.localeCompare(bSeries, undefined, { sensitivity: 'base' });
        if (seriesCompare !== 0) return seriesCompare;
        return this.applyDirection(
          (a.title || '').localeCompare(b.title || '', undefined, { sensitivity: 'base' })
        );
      }
      case 'stars': {
        const aStars = a.personalRating ?? 0;
        const bStars = b.personalRating ?? 0;
        if (aStars !== bStars) return this.applyDirection(aStars - bStars);
        return this.applyDirection(
          (a.title || '').localeCompare(b.title || '', undefined, { sensitivity: 'base' })
        );
      }
      case 'goodreads': {
        const aRating = a.goodreadsRating ?? 0;
        const bRating = b.goodreadsRating ?? 0;
        if (aRating !== bRating) return this.applyDirection(aRating - bRating);
        return this.applyDirection(
          (a.title || '').localeCompare(b.title || '', undefined, { sensitivity: 'base' })
        );
      }
      case 'recent':
      default: {
        const aTime = a.savedAt ? Date.parse(a.savedAt) : 0;
        const bTime = b.savedAt ? Date.parse(b.savedAt) : 0;
        return this.applyDirection(aTime - bTime);
      }
    }
  }

  private applyDirection(value: number): number {
    const isAlphaSort = this.sortOrder === 'title' || this.sortOrder === 'author' || this.sortOrder === 'series';
    const multiplier = isAlphaSort
      ? (this.sortDirection === 'down' ? 1 : -1)
      : (this.sortDirection === 'down' ? -1 : 1);
    return value * multiplier;
  }

  onGridScroll(): void {
    const gridEl = this.libraryGrid?.nativeElement;
    if (!gridEl) return;
    if (!this.showAlphabetIndex) return;
    if (this.scrollFrameRequested) return;
    this.scrollFrameRequested = true;
    requestAnimationFrame(() => {
      this.scrollFrameRequested = false;
      const scrollTop = gridEl.scrollTop + 12;
      const cards = Array.from(gridEl.querySelectorAll<HTMLElement>('.library-card'));
      if (cards.length === 0) return;
      let current = cards[0];
      for (const card of cards) {
        if (card.offsetTop <= scrollTop) {
          current = card;
        } else {
          break;
        }
      }
      const indexAttr = current.dataset['index'];
      const idx = indexAttr ? Number.parseInt(indexAttr, 10) : 0;
      const book = this.filteredBooks[idx];
      if (!book) return;
      const nextLetter = this.getBookLetter(book);
      if (!this.availableLetters.includes(nextLetter)) {
        console.debug('[library-alpha] letter not in available set', {
          nextLetter,
          idx,
          title: book.title
        });
      }
      if (nextLetter !== this.activeLetter) {
        this.zone.run(() => {
          console.debug('[library-alpha] active letter changed', {
            from: this.activeLetter,
            to: nextLetter,
            scrollTop,
            idx,
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
    const gridEl = this.libraryGrid?.nativeElement;
    if (!gridEl) return;
    const books = this.filteredBooks;
    const index = books.findIndex(book => this.getBookLetter(book) === letter);
    if (index === -1) return;
    const target = gridEl.querySelector<HTMLElement>(`.library-card[data-index="${index}"]`);
    if (!target) return;
    gridEl.scrollTo({ top: target.offsetTop - 8, behavior: 'smooth' });
    this.zone.run(() => {
      this.activeLetter = letter;
      this.cdr.markForCheck();
    });
  }

  onSortChange(preserveDirection = false): void {
    if (!preserveDirection) {
      this.sortDirection = this.getDefaultSortDirection(this.sortOrder);
    }
    const gridEl = this.libraryGrid?.nativeElement;
    if (gridEl) {
      gridEl.scrollTo({ top: 0 });
    }
    if (!this.showAlphabetIndex) {
      this.activeLetter = '#';
      return;
    }
    const books = this.filteredBooks;
    if (books.length === 0) {
      this.activeLetter = '#';
      return;
    }
    this.activeLetter = this.getBookLetter(books[0]);
    setTimeout(() => this.onGridScroll(), 0);
  }

  toggleSortDirection(): void {
    this.sortDirection = this.sortDirection === 'down' ? 'up' : 'down';
    this.onSortChange(true);
  }

  setTileSize(size: 'small' | 'medium' | 'large'): void {
    this.tileSize = size;
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
      width: '650px',
      data: dialogData
    });

    dialogRef.afterClosed().subscribe((result: BookEditDialogResult | undefined) => {
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
        this.api.updateLibraryBookMetadata(book.fileName, {
          primaryGenre: result.primaryGenre ?? book.primaryGenre ?? 'Uncategorized',
          tags: result.tags ?? book.tags ?? [],
          series: result.series ?? book.series ?? null,
          title: result.title,
          authors: result.authors
        }).subscribe({
          next: () => {
            console.log('[library] Updated book metadata:', book.fileName);
          },
          error: (err) => {
            console.error('[library] Failed to update book metadata:', err);
          }
        });

        if (result.coverUrl) {
          console.log('[library] Updating cover for', book.fileName, 'with URL:', result.coverUrl);
          this.api.updateLibraryBookCover(book.fileName, result.coverUrl).subscribe({
            next: (resp) => {
              console.log('[library] Cover update response:', resp);
              if (resp?.coverUrl) {
                // Add cache-busting timestamp to force browser to reload the image
                const timestamp = new Date().getTime();
                const separator = resp.coverUrl.includes('?') ? '&' : '?';
                book.coverUrl = `${resp.coverUrl}${separator}t=${timestamp}`;
                console.log('[library] Cover updated successfully to:', book.coverUrl);
              } else {
                console.warn('[library] Cover update succeeded but no coverUrl in response');
              }
            },
            error: (err) => {
              console.error('[library] Failed to update book cover:', err);
              console.error('[library] Error details:', err.error);
            }
          });
        } else {
          console.log('[library] No coverUrl in result, skipping cover update');
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

    this.api.sendLibraryToKindle(book.fileName, book.title, target).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        if (target === 'dad') {
          book.dadsKindleState = success ? 'success' : 'error';
        } else {
          book.momsKindleState = success ? 'success' : 'error';
        }
      },
      error: (err) => {
        console.error('[library] send-to-kindle failed', err);
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
    this.api.sendLibraryToKindle(book.fileName, book.title, 'dad', true).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        book.dadsKindleState = success ? 'success' : 'error';
      },
      error: (err) => {
        console.error('[library] send-to-dropbox failed', err);
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
    this.api.updateLibraryBookRatings(book.fileName, {
      personalRating: nextRating
    }).subscribe({
      next: () => {
        console.log('[library] Updated personal rating:', book.fileName, nextRating);
      },
      error: (err) => {
        console.error('[library] Failed to update personal rating:', err);
      }
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

    this.api.wipeLibraryGenres().subscribe({
      next: () => {
        this.books = this.books.map(book => ({
          ...book,
          primaryGenre: null,
          tags: []
        }));
        this.genres = this.buildGenreList(this.books);
      },
      error: (err) => {
        console.error('[library] Failed to wipe genres', err);
      }
    });
  }

  toggleBulkEditMode(): void {
    this.bulkEditMode = !this.bulkEditMode;
    if (!this.bulkEditMode) {
      this.selectedBooksForBulk.clear();
    }
  }

  toggleBookSelection(book: LibraryBook): void {
    if (!this.bulkEditMode) return;

    if (this.selectedBooksForBulk.has(book.fileName)) {
      this.selectedBooksForBulk.delete(book.fileName);
    } else {
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

    dialogRef.afterClosed().subscribe((result: BookBulkEditDialogResult | undefined) => {
      if (!result) return;

      // Update all selected books with the new metadata
      for (const book of selectedBooks) {
        if (result.authors) {
          book.authors = result.authors;
        }
        if (result.primaryGenre !== undefined) {
          book.primaryGenre = result.primaryGenre;
        }
        if (result.tags) {
          book.tags = result.tags;
        }
        if (result.series !== undefined) {
          book.series = result.series;
        }

        // Update on backend
        this.api.updateLibraryBookMetadata(book.fileName, {
          primaryGenre: result.primaryGenre ?? book.primaryGenre ?? 'Uncategorized',
          tags: result.tags ?? book.tags ?? [],
          series: result.series ?? book.series ?? null,
          title: book.title,
          authors: result.authors ?? book.authors
        }).subscribe({
          next: () => {
            console.log('[library] Updated book metadata:', book.fileName);
          },
          error: (err) => {
            console.error('[library] Failed to update book metadata:', err);
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

      // Wait 2 seconds between sends (except after last book)
      if (i < selectedBooks.length - 1) {
        await this.delay(2000);
      }
    }

    // Exit bulk edit mode after completion
    this.bulkEditMode = false;
    this.selectedBooksForBulk.clear();
  }

  private delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }
}

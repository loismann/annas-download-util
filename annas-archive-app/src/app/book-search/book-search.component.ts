import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule }     from '@angular/material/input';
import { MatCheckboxModule }  from '@angular/material/checkbox';
import { MatSelectModule }    from '@angular/material/select';
import { MatButtonModule }    from '@angular/material/button';
import { MatCardModule }      from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import {
  AnnaArchiveApiService,
  DownloadMemberResponse,
  SendToBooxResponse,
  AuthorSuggestion
} from '../services/anna-archive-api.service';

import { AuthService } from '../services/auth.service';
import { BookDto } from '../models/book-dto.model';
import { VERSION } from '../version';
import { Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged, takeUntil } from 'rxjs/operators';
import { RelatedBooksModalComponent } from '../related-books-modal/related-books-modal.component';

@Component({
  selector: 'app-book-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatSelectModule,
    MatButtonModule,
    MatCardModule,
    MatProgressSpinnerModule,
    MatDialogModule,
    MatIconModule,
  ],
  templateUrl: './book-search.component.html',
  styleUrls: ['./book-search.component.css'],
})
export class BookSearchComponent implements OnDestroy {
  placeholderUrl = '/assets/placeholder.jpg';
  buildTime = VERSION.buildTime;

  /* ───────── search form state ───────── */
  searchTerm = '';

  /* ───────── ui state ───────── */
  loading = false;
  error: string | null = null;
  searchPerformed = false;
  searchPanelCollapsed = false;

  books: BookDto[] = [];
  selectedFormat = '';

  downloadsLeft: number | null = null;
  downloadsPerDay: number | null = null;

  /* ───────── author suggestion state ───────── */
  authorSuggestions: AuthorSuggestion[] = [];
  selectedAuthor = '';
  loadingAuthors = false;
  private searchTermSubject = new Subject<string>();
  private destroy$ = new Subject<void>();
  private latestAuthorQuery = '';
  private coverLookupsInFlight = new Set<string>();

  constructor(
    private api: AnnaArchiveApiService,
    public authService: AuthService,
    private dialog: MatDialog
  ) {
    // Set up debounced author fetching
    this.searchTermSubject.pipe(
      debounceTime(500),
      distinctUntilChanged(),
      takeUntil(this.destroy$)
    ).subscribe(term => {
      this.fetchAuthorSuggestions(term);
    });

    // Fetch counter from server only
    if (this.authService.isAuthenticated()) {
      this.fetchDownloadStatus();
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  /* ───────── download counter management ───────── */
  private fetchDownloadStatus(): void {
    this.api.getDownloadStatus().subscribe({
      next: (resp) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
      },
      error: (err) => {
        console.error('[download-counter] Failed to fetch status', err);
      }
    });
  }

  private updateFromServer(serverLeft: number, serverPerDay: number): void {
    this.downloadsLeft = serverLeft;
    this.downloadsPerDay = serverPerDay;
    console.log('[download-counter] Updated from server', {
      downloadsLeft: this.downloadsLeft,
      downloadsPerDay: this.downloadsPerDay
    });
  }

  get downloadWarningLevel(): 'none' | 'yellow' | 'orange' | 'red' {
    if (this.downloadsLeft === null) return 'none';
    if (this.downloadsLeft <= 10) return 'red';
    if (this.downloadsLeft <= 20) return 'orange';
    if (this.downloadsLeft <= 30) return 'yellow';
    return 'none'; // Blue/default
  }

  /* ───────── helpers for template ───────── */
  get availableFormats(): string[] {
    // Return static list of common formats so users can filter before searching
    return ['EPUB', 'MOBI', 'PDF', 'AZW3', 'FB2', 'TXT'];
  }

  get filteredBooks(): BookDto[] {
    let filtered = this.books;

    // Filter by format
    if (this.selectedFormat) {
      filtered = filtered.filter(b => b.format === this.selectedFormat);
    }

    // Filter by author (fuzzy match)
    if (this.selectedAuthor) {
      filtered = filtered.filter(b =>
        b.authors.some(author =>
          author.toLowerCase().includes(this.selectedAuthor.toLowerCase())
        )
      );
    }

    return filtered;
  }

  /* ───────── search submit ───────── */
  onSearch(): void {
    this.error = null;
    if (!this.searchTerm.trim()) {
      this.error = 'Please enter a search term.';
      return;
    }

    // Build search query: if author is selected, include it in the search
    let searchQuery = this.searchTerm.trim();
    if (this.selectedAuthor) {
      searchQuery = `${searchQuery} ${this.selectedAuthor}`;
    }

    console.log('[book-search] submit', {
      term: this.searchTerm.trim(),
      author: this.selectedAuthor,
      searchQuery,
      selectedFormat: this.selectedFormat,
    });

    this.loading = true;
    this.searchPerformed = true;
    // Keep selectedFormat so it persists across searches

    this.api.searchBooks(searchQuery, false).subscribe({
      next: books => {
        this.books = books;
        this.books.forEach(b => {
          b.sendState = 'idle';
          b.dadsKindleState = 'idle';
          b.momsKindleState = 'idle';
        });
        this.loading = false;
        this.queueCoverLookups();
      },
      error: err => {
        this.error = 'Error fetching books.';
        console.error(err);
        this.loading = false;
      },
    });
  }

  /* ───────── download button ───────── */
  download(book: BookDto): void {
    // Get the cover URL if available
    const coverUrl = book.coverCandidates && book.coverCandidates.length > 0
      ? book.coverCandidates[0]
      : undefined;

    this.api.downloadMember(book.md5, book.title, coverUrl).subscribe({
      next: (blob: Blob) => {
        // Create a download link and trigger it
        const url = window.URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        // Sanitize filename to remove invalid characters
        const sanitizedTitle = book.title.replace(/[<>:"/\\|?*]/g, '_');
        link.download = `${sanitizedTitle}.${book.format.toLowerCase()}`;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        window.URL.revokeObjectURL(url);
      },
      error: err => {
        console.error('Download failed', err);
        this.error = 'Download failed.';
      }
    });
  }

  /* ───────── send-to-boox button (via Dropbox) ───────── */
  sendToBoox(book: BookDto): void {
    if (book.sendState === 'sending') return;  // guard double-click
    book.sendState = 'sending';

    // Get the cover URL if available
    const coverUrl = book.coverCandidates && book.coverCandidates.length > 0
      ? book.coverCandidates[0]
      : undefined;

    this.api.sendToBoox(book.md5, book.title, coverUrl).subscribe({
      next: (resp: SendToBooxResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.sendState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Boox failed', err);
        book.sendState = 'error';
      }
    });
  }

  /* ───────── send-to-dad's-kindle button ───────── */
  sendToDadsKindle(book: BookDto): void {
    if (book.dadsKindleState === 'sending') return;  // guard double-click
    book.dadsKindleState = 'sending';

    // Get the cover URL if available
    const coverUrl = book.coverCandidates && book.coverCandidates.length > 0
      ? book.coverCandidates[0]
      : undefined;

    this.api.sendToKindle(book.md5, book.title, 'dad', coverUrl).subscribe({
      next: (resp: SendToBooxResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.dadsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Dad\'s-Kindle failed', err);
        book.dadsKindleState = 'error';
      }
    });
  }

  /* ───────── send-to-mom's-kindle button ───────── */
  sendToMomsKindle(book: BookDto): void {
    if (book.momsKindleState === 'sending') return;  // guard double-click
    book.momsKindleState = 'sending';

    // Get the cover URL if available
    const coverUrl = book.coverCandidates && book.coverCandidates.length > 0
      ? book.coverCandidates[0]
      : undefined;

    this.api.sendToKindle(book.md5, book.title, 'mom', coverUrl).subscribe({
      next: (resp: SendToBooxResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.momsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Mom\'s-Kindle failed', err);
        book.momsKindleState = 'error';
      }
    });
  }

  onCoverError(book: BookDto, evt: Event): void {
      const img = evt.target as HTMLImageElement;

      // if we're already showing the placeholder, do nothing
      if (img.src.endsWith(this.placeholderUrl)) {
        return;
      }

      // if there are more candidates, try the next
      if (book.coverCandidates.length > 1) {
        book.coverCandidates.shift();
        img.src = book.coverCandidates[0];
      } else {
        // no more external covers → fall back
        book.coverCandidates = [];
        img.src = this.placeholderUrl;
        this.lookupCoverForBook(book);
      }
    }

  private queueCoverLookups(): void {
    const missing = this.books.filter(b => !b.coverCandidates || b.coverCandidates.length === 0);
    missing.slice(0, 50).forEach((book, index) => {
      setTimeout(() => this.lookupCoverForBook(book), index * 200);
    });
  }

  private lookupCoverForBook(book: BookDto): void {
    if (book.coverCandidates && book.coverCandidates.length > 0) {
      return;
    }

    if (this.coverLookupsInFlight.has(book.md5)) {
      return;
    }

    this.coverLookupsInFlight.add(book.md5);
    const author = book.authors?.[0];
    this.api.fetchCover(book.title, author).subscribe({
      next: (resp) => {
        if (resp.coverUrl) {
          if (!book.coverCandidates) {
            book.coverCandidates = [];
          }
          book.coverCandidates.unshift(resp.coverUrl);
        }
        this.coverLookupsInFlight.delete(book.md5);
      },
      error: () => {
        this.coverLookupsInFlight.delete(book.md5);
        // no-op
      }
    });
  }

  /* ───────── author suggestion methods ───────── */
  onSearchTermChange(newTerm: string): void {
    this.searchTerm = newTerm;

    // Clear author suggestions if search term is too short
    if (newTerm.trim().length < 3) {
      this.authorSuggestions = [];
      this.selectedAuthor = '';
      return;
    }

    // Trigger debounced author fetch
    this.searchTermSubject.next(newTerm.trim());
  }

  private fetchAuthorSuggestions(bookTitle: string): void {
    if (!bookTitle || bookTitle.length < 3) {
      this.authorSuggestions = [];
      return;
    }

    this.latestAuthorQuery = bookTitle;
    this.loadingAuthors = true;
    this.api.suggestAuthors(bookTitle).subscribe({
      next: (resp) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        this.authorSuggestions = resp.authors;
        this.loadingAuthors = false;
        console.log('[author-suggestions]', { bookTitle, authors: resp.authors });
      },
      error: (err) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        console.error('Failed to fetch author suggestions', err);
        this.authorSuggestions = [];
        this.loadingAuthors = false;
      }
    });
  }

  /* ───────── related books modal ───────── */
  openRelatedBooksModal(): void {
    if (!this.searchTerm.trim() || !this.selectedAuthor) {
      return;
    }

    const dialogRef = this.dialog.open(RelatedBooksModalComponent, {
      width: '1100px',
      maxWidth: '90vw',
      data: {
        bookTitle: this.searchTerm.trim(),
        author: this.selectedAuthor,
        sameSeries: [],
        otherSeries: [],
        seriesSummary: null,
        loading: true
      }
    });

    dialogRef.componentInstance.clearStatus();
    dialogRef.componentInstance.addStatus('Requesting related books...');

    // Fetch related books
    this.api.getRelatedBooks(this.searchTerm.trim(), this.selectedAuthor).subscribe({
      next: (resp) => {
        dialogRef.componentInstance.data.sameSeries = resp.sameSeries;
        dialogRef.componentInstance.data.otherSeries = resp.otherSeries;
        dialogRef.componentInstance.data.seriesSummary = resp.seriesSummary;
        dialogRef.componentInstance.addStatus(
          `Found ${resp.sameSeries.length} book${resp.sameSeries.length === 1 ? '' : 's'} in series` +
          `${resp.otherSeries.length ? ` and ${resp.otherSeries.length} other series` : ''}.`
        );
        // queueCoverLookups will set loading = false when all lookups complete
        dialogRef.componentInstance.queueCoverLookups();
        console.log('[related-books]', resp);
      },
      error: (err) => {
        console.error('Failed to fetch related books', err);
        dialogRef.componentInstance.data.loading = false;
        dialogRef.componentInstance.addStatus('Failed to fetch related books.');
      }
    });

    // Handle modal close
    dialogRef.afterClosed().subscribe(result => {
      if (result && result.searchBook) {
        // User clicked a book/series to search
        this.searchTerm = result.searchBook;
        if (result.author) {
          this.selectedAuthor = result.author;
        }
        this.onSearch();
      }
    });
  }

  /* ───────── mobile search panel toggle ───────── */
  toggleSearchPanel(): void {
    this.searchPanelCollapsed = !this.searchPanelCollapsed;
  }
}

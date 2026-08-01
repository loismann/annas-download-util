import { Component, HostListener, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule }     from '@angular/material/input';
import { MatCheckboxModule }  from '@angular/material/checkbox';
import { MatSelectModule }    from '@angular/material/select';
import { MatButtonModule }    from '@angular/material/button';
import { MatCardModule }      from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';

import {
  AiApiService,
  AuthorSuggestion,
  AiBookSearchResult,
  GroupableBook
} from '../services/ai-api.service';
import {
  BookSearchApiService,
  SendToTargetResponse
} from '../services/book-search-api.service';

import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { BookDto } from '../models/book-dto.model';
import { BookGroup } from '../models/book-group.model';
import { BookSummaryModalComponent } from '../components/book-summary-modal/book-summary-modal.component';
import { DISPLAYABLE_BOOK_FORMATS } from '../constants/book-formats';
import { Observable, Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged, takeUntil } from 'rxjs/operators';
import { RelatedBooksModalComponent } from '../related-books-modal/related-books-modal.component';
import { SearchFormComponent, DomainHealth, SearchFormSubmitEvent } from '../components/search-form/search-form.component';
import { applyMirrorHealth, applySlumHealth } from './domain-health';
import { BookCoverLookupService } from './book-cover-lookup.service';
import { BookDescriptionLookupService } from './book-description-lookup.service';
import {
  SearchResultsComponent,
  DisplayGroup,
  VariantSelectedEvent,
  SendToLibraryEvent,
  SendToDropboxEvent,
  SendToKindleEvent,
  FetchDescriptionEvent,
  CoverErrorEvent
} from '../components/search-results/search-results.component';

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
    MatSlideToggleModule,
    MatTooltipModule,
    SearchFormComponent,
    SearchResultsComponent,
  ],
  templateUrl: './book-search.component.html',
  styleUrls: ['./book-search.component.css'],
  // Component-scoped, not root: the lookup queue owns setTimeout handles and
  // must be torn down with the page, not shared across it.
  providers: [BookCoverLookupService, BookDescriptionLookupService],
})
export class BookSearchComponent implements OnInit, OnDestroy {
  placeholderUrl = '/assets/placeholder.jpg';
  /* ───────── search form state ───────── */
  searchTerm = '';
  aiSearchQuery = '';
  aiSearchExpanded = false;

  /* ───────── ui state ───────── */
  loading = false;
  error: string | null = null;
  searchPerformed = false;
  /* Secondary search options start collapsed on every screen size — see ngOnInit. */
  searchPanelCollapsed = true;
  useLibGen = false; // Toggle between Anna's Archive and LibGen
  relatedBooksModalOpen = false; // Track if related books modal is open for matching

  books: BookDto[] = [];
  selectedFormat = '';
  expandedCards = new Set<string>(); // Track which book cards are expanded by md5

  /* ───────── result grouping (collapse duplicate uploads/formats) ───────── */
  bookGroups: BookGroup[] = [];
  groupingInProgress = false;
  /** groupKey -> md5 of the variant the user explicitly picked within that
   *  group (e.g. one of several EPUB uploads once filtered to EPUB-only) —
   *  defaults to the group's first book when unset. */
  private groupSelection = new Map<string, string>();

  downloadsLeft: number | null = null;
  downloadsPerDay: number | null = null;

  /* ───────── Anna's Archive domain health ───────── */
  annaDomains: DomainHealth[] = [
    { name: "Anna's Archive GL", extension: 'gl', health: null, certExpDays: null },
    { name: "Anna's Archive PK", extension: 'pk', health: null, certExpDays: null },
    { name: "Anna's Archive GD", extension: 'gd', health: null, certExpDays: null }
  ];

  /* ───────── author suggestion state ───────── */
  authorSuggestions: AuthorSuggestion[] = [];
  selectedAuthor = '';
  loadingAuthors = false;
  private searchTermSubject = new Subject<string>();
  private destroy$ = new Subject<void>();
  private latestAuthorQuery = '';

  constructor(
    private aiApi: AiApiService,
    private bookSearchApi: BookSearchApiService,
    public authService: AuthService,
    private dialog: MatDialog,
    private http: HttpClient,
    private logger: LoggerService,
    private coverLookup: BookCoverLookupService,
    private descriptionLookup: BookDescriptionLookupService
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

  @HostListener('document:keydown.enter', ['$event'])
  handleEnterKey(event: KeyboardEvent): void {
    if (this.dialog.openDialogs.length > 0) return;
    const target = event.target as HTMLElement | null;
    if (!target || !target.closest('.search-form')) return;
    if (this.aiSearchExpanded && event.shiftKey) return;
    event.preventDefault();
    this.onSearch();
  }

  ngOnInit(): void {
    // Fetch domain health status once on page load
    this.fetchDomainHealth();
    this.fetchMirrorHealth();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  /* ───────── domain health management ───────── */
  private fetchDomainHealth(): void {
    this.bookSearchApi.getSlumHealth().subscribe({
      next: data => applySlumHealth(this.annaDomains, data),
      error: err => this.logger.error('[domain-health] Failed to fetch SLUM data', err)
    });
  }

  private fetchMirrorHealth(): void {
    this.bookSearchApi.getMirrorHealth().subscribe({
      next: data => applyMirrorHealth(this.annaDomains, data),
      error: err => this.logger.error('[domain-health] Failed to fetch mirror health data', err)
    });
  }

  /* ───────── download counter management ───────── */
  private fetchDownloadStatus(): void {
    this.bookSearchApi.getDownloadStatus().subscribe({
      next: (resp) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
      },
      error: (err) => {
        this.logger.error('[download-counter] Failed to fetch status', err);
      }
    });
  }

  private updateFromServer(serverLeft: number, serverPerDay: number): void {
    this.downloadsLeft = serverLeft;
    this.downloadsPerDay = serverPerDay;
    this.logger.log('[download-counter] Updated from server', {
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
    // Only the formats every household device can actually open — matches
    // what the result cards' format badges show (see DISPLAYABLE_BOOK_FORMATS).
    return [...DISPLAYABLE_BOOK_FORMATS];
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
        b.authors.some(author => this.authorMatches(author, this.selectedAuthor))
      );
    }

    return filtered;
  }

  /** Grouped, filtered view the results grid actually renders — same author/
   *  format predicates as filteredBooks, applied per-group so a format filter
   *  narrows which books within a group are eligible without dropping the
   *  whole group (see activeBookFor for how the "displayed" book is picked
   *  when a group still has more than one match). */
  get filteredGroups(): BookGroup[] {
    return this.bookGroups
      .map(group => {
        let books = group.books;

        if (this.selectedAuthor) {
          books = books.filter(b =>
            b.authors.some(author => this.authorMatches(author, this.selectedAuthor))
          );
        }

        if (this.selectedFormat) {
          books = books.filter(b => b.format === this.selectedFormat);
        }

        return books.length > 0 ? { key: group.key, books } : null;
      })
      .filter((g): g is BookGroup => g !== null);
  }

  /** Which book within a (possibly filtered) group is currently shown on its
   *  card / acted on by the send-to buttons — the user's explicit pick if
   *  they made one and it's still in the filtered set; failing that, the
   *  first book in DISPLAYABLE_BOOK_FORMATS order (EPUB over PDF over MOBI)
   *  so a card never defaults to showing some other format (AZW3, say) when
   *  a standard one is sitting right there in the same group; failing even
   *  that (no standard-format copy exists at all), just the first book. */
  activeBookFor(group: BookGroup): BookDto {
    const selectedMd5 = this.groupSelection.get(group.key);
    const selected = selectedMd5 ? group.books.find(b => b.md5 === selectedMd5) : undefined;
    if (selected) return selected;

    for (const format of DISPLAYABLE_BOOK_FORMATS) {
      const preferred = group.books.find(b => b.format === format);
      if (preferred) return preferred;
    }
    return group.books[0];
  }

  selectVariant(group: BookGroup, book: BookDto): void {
    this.groupSelection.set(group.key, book.md5);
  }

  /** What the results grid actually renders — each filtered group paired
   *  with whichever book is currently "active" for it (see activeBookFor). */
  get displayGroups(): DisplayGroup[] {
    return this.filteredGroups.map(group => ({ group, active: this.activeBookFor(group) }));
  }

  onVariantSelected(event: VariantSelectedEvent): void {
    this.selectVariant(event.group, event.book);
  }

  openSummaryModal(book: BookDto): void {
    this.dialog.open(BookSummaryModalComponent, {
      width: '700px',
      maxWidth: '90vw',
      data: { book, placeholderUrl: this.placeholderUrl }
    });

    if (!book.description) {
      this.fetchDescriptionOnDemand(book);
    }
  }

  /** Sends the current (possibly page-1-only, possibly full) result set to
   *  the AI grouping endpoint and rebuilds bookGroups from the response.
   *  Safe to call again once page 2 lands — the old groups stay on screen
   *  until the new response replaces them, so there's no flash-to-empty. */
  private regroupBooks(): void {
    if (this.books.length === 0) {
      this.bookGroups = [];
      return;
    }

    this.groupingInProgress = true;
    const payload: GroupableBook[] = this.books.map(b => ({
      md5: b.md5,
      title: b.title,
      authors: b.authors,
      format: b.format,
      year: b.year
    }));

    this.aiApi.groupSearchResults(payload).subscribe({
      next: (resp) => {
        const byMd5 = new Map(this.books.map(b => [b.md5, b]));
        this.bookGroups = resp.groups
          .map(md5s => {
            const groupBooks = md5s.map(md5 => byMd5.get(md5)).filter((b): b is BookDto => !!b);
            return groupBooks.length > 0 ? { key: groupBooks[0].md5, books: groupBooks } : null;
          })
          .filter((g): g is BookGroup => g !== null);
        this.groupingInProgress = false;
      },
      error: (err) => {
        this.logger.error('[book-search] Grouping failed, showing ungrouped results', err);
        // Degrade to "every book is its own group" rather than showing
        // nothing — duplicates stay uncollapsed, but nothing disappears.
        this.bookGroups = this.books.map(b => ({ key: b.md5, books: [b] }));
        this.groupingInProgress = false;
      }
    });
  }

  /* ───────── search form handler ───────── */
  onSearchFormSubmit(event: SearchFormSubmitEvent): void {
    this.searchTerm = event.searchTerm;
    this.selectedAuthor = event.selectedAuthor;
    this.useLibGen = event.useLibGen;
    // selectedFormat isn't part of the submit event — the format selector
    // now lives above the results grid (book-search.component.html), driven
    // directly by this.selectedFormat/onFormatChange, independent of search
    // submission — see search-form.component.ts.

    if (event.isAiSearch && event.aiSearchQuery) {
      this.aiSearchQuery = event.aiSearchQuery;
      this.aiSearchExpanded = true;
      this.runAiSearch();
    } else {
      this.aiSearchExpanded = false;
      this.onSearch();
    }
  }

  onOpenRelatedBooks(event: { searchTerm: string; author: string }): void {
    this.searchTerm = event.searchTerm;
    this.selectedAuthor = event.author;
    this.openRelatedBooksModal();
  }

  /* ───────── search results handlers ───────── */
  onResultSendToLibrary(event: SendToLibraryEvent): void {
    this.sendToLibrary(event.book);
  }

  onResultSendToDropbox(event: SendToDropboxEvent): void {
    this.sendToBoox(event.book);
  }

  onResultSendToKindle(event: SendToKindleEvent): void {
    if (event.target === 'dad') {
      this.sendToDadsKindle(event.book);
    } else {
      this.sendToMomsKindle(event.book);
    }
  }

  onResultFetchDescription(event: FetchDescriptionEvent): void {
    this.fetchDescriptionOnDemand(event.book);
  }

  onResultCoverError(event: CoverErrorEvent): void {
    this.onCoverError(event.book, event.event);
  }

  /* ───────── search submit ───────── */
  onSearch(): void {
    if (this.aiSearchExpanded) {
      this.runAiSearch();
      return;
    }
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

    this.logger.log('[book-search] submit', {
      term: this.searchTerm.trim(),
      author: this.selectedAuthor,
      searchQuery,
      selectedFormat: this.selectedFormat,
    });

    this.loading = true;
    this.searchPerformed = true;
    // Keep selectedFormat so it persists across searches
    // Stale groups from a previous search shouldn't linger while this one loads.
    this.bookGroups = [];
    this.groupSelection.clear();

    const initIdleState = (b: BookDto) => {
      b.sendState = 'idle';
      b.libraryState = 'idle';
      b.dadsKindleState = 'idle';
      b.momsKindleState = 'idle';
    };

    if (this.useLibGen) {
      // LibGen's search doesn't paginate the same way (general vs. fiction
      // search, not simple page accumulation) — single-shot for now.
      this.bookSearchApi.searchBooksLibGen(searchQuery, false).subscribe({
        next: books => {
          this.books = books;
          this.books.forEach(initIdleState);
          this.loading = false;
          this.coverLookup.queueForBooks(this.books, this.useLibGen);
          this.descriptionLookup.queueForBooks(this.books);
          this.regroupBooks();
        },
        error: err => this.handleSearchError(err),
      });
      return;
    }

    // Page 1 renders as soon as it arrives instead of waiting for the full
    // ~50-result budget — page 2 is fetched in the background afterward and
    // appended when it lands, so the user sees results immediately instead
    // of staring at a spinner for two sequential Anna's Archive page fetches.
    this.bookSearchApi.searchBooks(searchQuery, false, 1).subscribe({
      next: books => {
        this.books = books;
        this.books.forEach(initIdleState);
        this.loading = false;
        this.coverLookup.queueForBooks(this.books, this.useLibGen);
        this.descriptionLookup.queueForBooks(this.books);
        this.regroupBooks();

        this.bookSearchApi.searchBooks(searchQuery, false, 2).subscribe({
          next: more => {
            more.forEach(initIdleState);
            this.books = [...this.books, ...more];
            // queueCoverLookups() re-filters this.books for whatever still
            // needs a lookup, so it naturally picks up page 2's additions.
            // The description lookup isn't re-queued here — it always
            // targets the first AUTO_DESCRIPTION_FETCH_LIMIT books by index,
            // which page 1 alone already covers, and calling it again would
            // risk double-firing an in-flight fetch for a page-1 book that
            // hasn't resolved yet (no in-flight guard on that path).
            this.coverLookup.queueForBooks(this.books, this.useLibGen);
            // Re-group over the combined set — page 2 may add more
            // duplicates of page-1 books, or entirely new ones. The old
            // groups stay on screen until this response replaces them.
            this.regroupBooks();
          },
          error: err => {
            // A 404 here just means there's no page 2 — not a real error,
            // don't surface it to the user who already has page 1 results.
            if (err.status !== 404) {
              this.logger.error('[Book Search] Page 2 fetch failed:', err);
            }
          },
        });
      },
      error: err => this.handleSearchError(err),
    });
  }

  private handleSearchError(err: any): void {
    this.logger.error('[Book Search] Error:', err);
    if (err.name === 'TimeoutError') {
      this.error = `Search timed out. ${this.useLibGen ? 'LibGen' : "Anna's Archive"} may be slow or unavailable.`;
    } else if (err.status === 404) {
      this.error = 'No books found.';
    } else if (err.status === 0) {
      this.error = 'Cannot connect to server. Please check your connection.';
    } else {
      this.error = `Error fetching books from ${this.useLibGen ? 'LibGen' : "Anna's Archive"}: ${err.message || err.statusText || 'Unknown error'}`;
    }
    this.logger.error(err);
    this.loading = false;
  }

  /* ───────── download button ───────── */
  /* ───────── send-to-target buttons ───────── */

  /** LibGen and Anna's Archive take an identical argument list here — only the
   *  endpoint differs, so the choice is which function to call, not what to
   *  pass it. */
  private libraryRequest(book: BookDto, coverUrl?: string) {
    const send = this.useLibGen
      ? this.bookSearchApi.sendToLibraryLibGen.bind(this.bookSearchApi)
      : this.bookSearchApi.sendToLibrary.bind(this.bookSearchApi);

    return send(
      book.md5,
      book.title,
      coverUrl,
      book.authors?.join(';'),
      book.format,
      book.fileSize,
      book.source,
      book.description ?? undefined
    );
  }

  /** `surfaceError` is the only difference between the user pressing "Save to
   *  library" and the copy every send-to-device button makes on the way past:
   *  the explicit action reports a failure, the incidental one stays quiet. */
  private saveToLibrary(book: BookDto, coverUrl: string | undefined, surfaceError: boolean): void {
    this.libraryRequest(book, coverUrl).subscribe({
      next: () => {
        book.libraryState = 'success';
      },
      error: err => {
        this.logger.error('Send-to-library failed', err);
        book.libraryState = 'error';
        if (surfaceError) {
          this.error = 'Send to library failed.';
        }
      }
    });
  }

  sendToLibrary(book: BookDto): void {
    if (book.libraryState === 'sending') return;
    book.libraryState = 'sending';
    this.saveToLibrary(book, this.coverUrlFor(book), true);
  }

  sendToBoox(book: BookDto): void {
    this.sendToDevice(book, 'sendState', 'Send-to-Boox',
      (md5, title, cover) => this.bookSearchApi.sendToBoox(md5, title, cover));
  }

  sendToDadsKindle(book: BookDto): void {
    this.sendToDevice(book, 'dadsKindleState', "Send-to-Dad's-Kindle",
      (md5, title, cover) => this.bookSearchApi.sendToKindle(md5, title, 'dad', cover));
  }

  sendToMomsKindle(book: BookDto): void {
    this.sendToDevice(book, 'momsKindleState', "Send-to-Mom's-Kindle",
      (md5, title, cover) => this.bookSearchApi.sendToKindle(md5, title, 'mom', cover));
  }

  /**
   * Every send-to-device button does the same five things: guard the
   * double-click, mark itself sending, quietly save a copy to the library, fire
   * the device request, and fold the response's download counter back in. Only
   * the per-book state field, the log label and the request itself differ.
   */
  private sendToDevice(
    book: BookDto,
    stateKey: 'sendState' | 'dadsKindleState' | 'momsKindleState',
    label: string,
    send: (md5: string, title: string, coverUrl?: string) => Observable<SendToTargetResponse>
  ): void {
    if (book[stateKey] === 'sending') return;  // guard double-click
    book[stateKey] = 'sending';

    const coverUrl = this.coverUrlFor(book);
    this.saveToLibrary(book, coverUrl, false);

    send(book.md5, book.title, coverUrl).subscribe({
      next: (resp: SendToTargetResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book[stateKey] = resp.success ? 'success' : 'error';
      },
      error: err => {
        this.logger.error(`${label} failed`, err);
        book[stateKey] = 'error';
      }
    });
  }

  private coverUrlFor(book: BookDto): string | undefined {
    return book.coverCandidates?.[0];
  }

  /* ───────── remove book from results ───────── */
  removeBook(book: BookDto): void {
    const index = this.books.indexOf(book);
    if (index > -1) {
      this.books.splice(index, 1);
    }
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
        this.coverLookup.enqueue(book, this.useLibGen);
      }
    }

  /* ───────── description card expansion ───────── */
  toggleCardExpansion(bookMd5: string): void {
    if (this.expandedCards.has(bookMd5)) {
      this.expandedCards.delete(bookMd5);
    } else {
      this.expandedCards.add(bookMd5);
    }
  }

  isCardExpanded(bookMd5: string): boolean {
    return this.expandedCards.has(bookMd5);
  }

  needsExpansion(description: string): boolean {
    // Rough estimate: if description is longer than ~150 characters, it likely needs expansion
    // This accounts for 3 lines at ~50 characters per line
    return !!description && description.length > 150;
  }

  fetchDescriptionOnDemand(book: BookDto): void {
    this.descriptionLookup.fetchOnDemand(book);
  }

  onSearchTermChange(newTerm: string): void {
    if (this.aiSearchExpanded) {
      return;
    }
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
    this.aiApi.suggestAuthors(bookTitle).subscribe({
      next: (resp) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        this.authorSuggestions = resp.authors;
        this.loadingAuthors = false;
        this.logger.log('[author-suggestions]', { bookTitle, authors: resp.authors });
      },
      error: (err) => {
        if (bookTitle !== this.latestAuthorQuery) {
          return;
        }
        this.logger.error('Failed to fetch author suggestions', err);
        this.authorSuggestions = [];
        this.loadingAuthors = false;
      }
    });
  }

  private authorMatches(author: string, selectedAuthor: string): boolean {
    const normalizedAuthor = this.normalizeName(author);
    const normalizedSelected = this.normalizeName(selectedAuthor);

    if (!normalizedAuthor || !normalizedSelected) {
      return false;
    }

    if (normalizedAuthor.includes(normalizedSelected)) {
      return true;
    }

    const authorTokens = normalizedAuthor.split(' ').filter(Boolean);
    const selectedTokens = normalizedSelected.split(' ').filter(Boolean);

    return selectedTokens.every(token => authorTokens.includes(token));
  }

  private normalizeName(value: string): string {
    return value
      .toLowerCase()
      .replace(/[^a-z0-9 ]/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  /* ───────── related books modal ───────── */
  openRelatedBooksModal(): void {
    if (!this.searchTerm.trim() || !this.selectedAuthor) {
      return;
    }

    // Lock format dropdown while matching is in progress
    this.relatedBooksModalOpen = true;

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
    this.aiApi.getRelatedBooks(this.searchTerm.trim(), this.selectedAuthor).subscribe({
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
        this.logger.log('[related-books]', resp);
      },
      error: (err) => {
        this.logger.error('Failed to fetch related books', err);
        dialogRef.componentInstance.data.loading = false;
        dialogRef.componentInstance.addStatus('Failed to fetch related books.');
      }
    });

    // Handle modal close
    dialogRef.afterClosed().subscribe(result => {
      // Unlock format dropdown when modal closes
      this.relatedBooksModalOpen = false;

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

  toggleAiSearch(): void {
    this.aiSearchExpanded = !this.aiSearchExpanded;
    if (this.aiSearchExpanded) {
      this.aiSearchQuery = this.searchTerm.trim();
    } else {
      this.aiSearchQuery = '';
      this.error = null;
    }
  }

  private runAiSearch(): void {
    this.error = null;
    const query = this.aiSearchQuery.trim();
    if (!query) {
      this.error = 'Ask a book-related question to start AI search.';
      return;
    }

    this.loading = true;
    this.searchPerformed = true;

    const dialogRef = this.dialog.open(RelatedBooksModalComponent, {
      width: '1100px',
      maxWidth: '90vw',
      data: {
        bookTitle: query,
        author: 'AI Search',
        sameSeries: [],
        otherSeries: [],
        seriesSummary: null,
        loading: true,
        mode: 'ai',
        query
      }
    });

    dialogRef.componentInstance.clearStatus();
    dialogRef.componentInstance.addStatus('Thinking…');

    this.aiApi.aiBookSearch(query).subscribe({
      next: (resp: AiBookSearchResult) => {
        const results = (resp.books ?? []).map((book, index) => ({
          title: book.title,
          author: book.author,
          order: index + 1,
          description: [book.summary, book.importance].filter(Boolean).join(' • '),
          coverUrl: book.coverUrl || undefined,
          descriptionSource: book.descriptionSource || null
        }));

        dialogRef.componentInstance.data.sameSeries = results;
        dialogRef.componentInstance.data.otherSeries = [];
        dialogRef.componentInstance.data.seriesSummary = resp.summary ?? null;
        dialogRef.componentInstance.data.loading = false;
        dialogRef.componentInstance.clearStatus();
        dialogRef.componentInstance.addStatus(`Found ${results.length} book${results.length === 1 ? '' : 's'}.`);

        this.loading = false;

        // Covers are fetched lazily here, one at a time, rather than by the
        // backend up front — the AI response now returns almost instantly
        // instead of waiting on per-book description/cover lookups against
        // OpenLibrary/Google Books for every result.
        this.coverLookup.queueAiResults(results);
      },
      error: (err) => {
        this.loading = false;
        dialogRef.componentInstance.data.loading = false;
        const message = err?.error?.error || 'AI search failed.';
        dialogRef.componentInstance.addStatus(message);
        this.error = message;
      }
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result && result.searchBook) {
        this.searchTerm = result.searchBook;
        this.onSearch();
      }
    });
  }

  /* ───────── mobile search panel toggle ───────── */
  toggleSearchPanel(): void {
    this.searchPanelCollapsed = !this.searchPanelCollapsed;
  }
}

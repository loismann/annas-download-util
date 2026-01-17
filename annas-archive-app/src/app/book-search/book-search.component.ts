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
  AiBookSearchResult
} from '../services/ai-api.service';
import {
  BookSearchApiService,
  SendToTargetResponse
} from '../services/book-search-api.service';

import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { BookDto } from '../models/book-dto.model';
import { SlumHealthEntry, MirrorHealthEntry, SlumHealthResponse, MirrorHealthResponse } from '../models/health-check.model';
import {
  AUTO_COVER_FETCH_LIMIT,
  AUTO_DESCRIPTION_FETCH_LIMIT,
  COVER_LOOKUP_STAGGER_MS,
  DESCRIPTION_FETCH_STAGGER_MS
} from '../constants';
import { Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged, takeUntil, tap } from 'rxjs/operators';
import { RelatedBooksModalComponent } from '../related-books-modal/related-books-modal.component';
import { SearchFormComponent, DomainHealth, SearchFormSubmitEvent } from '../components/search-form/search-form.component';
import {
  SearchResultsComponent,
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
  searchPanelCollapsed = false;
  useLibGen = false; // Toggle between Anna's Archive and LibGen
  relatedBooksModalOpen = false; // Track if related books modal is open for matching

  books: BookDto[] = [];
  selectedFormat = '';
  expandedCards = new Set<string>(); // Track which book cards are expanded by md5

  downloadsLeft: number | null = null;
  downloadsPerDay: number | null = null;

  /* ───────── Anna's Archive domain health ───────── */
  annaDomains: DomainHealth[] = [
    { name: "Anna's Archive ORG", extension: 'org', health: null, certExpDays: null },
    { name: "Anna's Archive SE", extension: 'se', health: null, certExpDays: null },
    { name: "Anna's Archive LI", extension: 'li', health: null, certExpDays: null },
    { name: "Anna's Archive PM", extension: 'pm', health: null, certExpDays: null },
    { name: "Anna's Archive IN", extension: 'in', health: null, certExpDays: null }
  ];

  /* ───────── author suggestion state ───────── */
  authorSuggestions: AuthorSuggestion[] = [];
  selectedAuthor = '';
  loadingAuthors = false;
  private searchTermSubject = new Subject<string>();
  private destroy$ = new Subject<void>();
  private latestAuthorQuery = '';
  private coverLookupsInFlight = new Set<string>();

  constructor(
    private aiApi: AiApiService,
    private bookSearchApi: BookSearchApiService,
    public authService: AuthService,
    private dialog: MatDialog,
    private http: HttpClient,
    private logger: LoggerService
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
    // Start with search panel collapsed on mobile for better UX
    if (window.innerWidth <= 480) {
      this.searchPanelCollapsed = true;
    }

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
      next: (data) => {
        this.parseDomainHealth(data);
      },
      error: (err) => {
        this.logger.error('[domain-health] Failed to fetch SLUM data', err);
      }
    });
  }

  private fetchDomainHealthObservable() {
    return this.bookSearchApi.getSlumHealth().pipe(
      tap((data: SlumHealthResponse) => this.parseDomainHealth(data))
    );
  }

  private fetchMirrorHealth(): void {
    this.bookSearchApi.getMirrorHealth().subscribe({
      next: (data) => {
        this.parseMirrorHealth(data);
      },
      error: (err) => {
        this.logger.error('[domain-health] Failed to fetch mirror health data', err);
      }
    });
  }

  private parseMirrorHealth(data: MirrorHealthResponse): void {
    if (!data || !Array.isArray(data)) return;

    data.forEach((entry: MirrorHealthEntry) => {
      if (!entry?.extension) return;
      const domain = this.annaDomains.find(d => d.extension === entry.extension);
      if (!domain) return;

      if (domain.health === null || entry.extension === 'pm' || entry.extension === 'in') {
        domain.health = typeof entry.health === 'number' ? entry.health : null;
      }
    });
  }

  private parseDomainHealth(data: SlumHealthResponse): void {
    if (!data || !Array.isArray(data)) return;

    // Find the entries for Anna's Archive domains
    const orgEntry = data.find((entry: SlumHealthEntry) => entry.name === "Anna's Archive ORG");
    const seEntry = data.find((entry: SlumHealthEntry) => entry.name === "Anna's Archive SE");
    const liEntry = data.find((entry: SlumHealthEntry) => entry.name === "Anna's Archive LI");

    if (orgEntry) {
      this.updateDomainHealth('org', orgEntry);
    }

    if (seEntry) {
      this.updateDomainHealth('se', seEntry);
    }

    if (liEntry) {
      this.updateDomainHealth('li', liEntry);
    }
  }

  private parseHealthPercentage(healthString: string): number | null {
    if (!healthString) return null;
    const match = healthString.match(/(\d+\.?\d*)%/);
    return match ? parseFloat(match[1]) : null;
  }

  private parseCertExpiry(certExp: string): number | null {
    if (!certExp) return null;
    const match = certExp.match(/(\d+)\s*days?/);
    return match ? parseInt(match[1], 10) : null;
  }

  private updateDomainHealth(extension: string, entry: SlumHealthEntry): void {
    const domain = this.annaDomains.find(d => d.extension === extension);
    if (!domain) return;

    domain.health = this.parseHealthPercentage(entry.health);
    domain.certExpDays = this.parseCertExpiry(entry.cert_exp);
  }

  getHealthColorClass(health: number | null): string {
    if (health === null) return 'health-unknown';
    if (health >= 90) return 'health-green';
    if (health >= 70) return 'health-yellow';
    if (health >= 50) return 'health-orange';
    return 'health-red';
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
    // Return static list of common formats so users can filter before searching
    return ['EPUB', 'MOBI', 'PDF', 'AZW3', 'FB2', 'TXT'];
  }

  onFormatChange(format: string): void {
    this.selectedFormat = format;
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

  /* ───────── search form handler ───────── */
  onSearchFormSubmit(event: SearchFormSubmitEvent): void {
    this.searchTerm = event.searchTerm;
    this.selectedAuthor = event.selectedAuthor;
    this.selectedFormat = event.selectedFormat;
    this.useLibGen = event.useLibGen;

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

    const searchObservable = this.useLibGen
      ? this.bookSearchApi.searchBooksLibGen(searchQuery, false)
      : this.bookSearchApi.searchBooks(searchQuery, false);

    searchObservable.subscribe({
      next: books => {
        this.books = books;
        this.books.forEach(b => {
          b.sendState = 'idle';
          b.libraryState = 'idle';
          b.dadsKindleState = 'idle';
          b.momsKindleState = 'idle';
        });
        this.loading = false;
        this.queueCoverLookups();
        this.fetchBookDescriptions();
      },
      error: err => {
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
      },
    });
  }

  /* ───────── download button ───────── */
  sendToLibrary(book: BookDto): void {
    if (book.libraryState === 'sending') return;
    book.libraryState = 'sending';

    // Get the cover URL if available
    const coverUrl = book.coverCandidates && book.coverCandidates.length > 0
      ? book.coverCandidates[0]
      : undefined;

    const authorString = book.authors?.join(';');

    const libraryObservable = this.useLibGen
      ? this.bookSearchApi.sendToLibraryLibGen(
          book.md5,
          book.title,
          coverUrl,
          authorString,
          book.format,
          book.fileSize,
          book.source,
          book.description ?? undefined
        )
      : this.bookSearchApi.sendToLibrary(
          book.md5,
          book.title,
          coverUrl,
          authorString,
          book.format,
          book.fileSize,
          book.source,
          book.description ?? undefined
        );

    libraryObservable.subscribe({
      next: () => {
        book.libraryState = 'success';
      },
      error: err => {
        this.logger.error('Send-to-library failed', err);
        book.libraryState = 'error';
        this.error = 'Send to library failed.';
      }
    });
  }

  private sendToLibrarySilently(book: BookDto, coverUrl?: string): void {
    const authorString = book.authors?.join(';');
    const libraryObservable = this.useLibGen
      ? this.bookSearchApi.sendToLibraryLibGen(
          book.md5,
          book.title,
          coverUrl,
          authorString,
          book.format,
          book.fileSize,
          book.source,
          book.description ?? undefined
        )
      : this.bookSearchApi.sendToLibrary(
          book.md5,
          book.title,
          coverUrl,
          authorString,
          book.format,
          book.fileSize,
          book.source,
          book.description ?? undefined
        );

    libraryObservable.subscribe({
      next: () => {
        book.libraryState = 'success';
      },
      error: err => {
        this.logger.error('Send-to-library failed', err);
        book.libraryState = 'error';
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

    this.sendToLibrarySilently(book, coverUrl);

    this.bookSearchApi.sendToBoox(book.md5, book.title, coverUrl).subscribe({
      next: (resp: SendToTargetResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.sendState = resp.success ? 'success' : 'error';
      },
      error: err => {
        this.logger.error('Send-to-Boox failed', err);
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

    this.sendToLibrarySilently(book, coverUrl);

    this.bookSearchApi.sendToKindle(book.md5, book.title, 'dad', coverUrl).subscribe({
      next: (resp: SendToTargetResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.dadsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        this.logger.error('Send-to-Dad\'s-Kindle failed', err);
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

    this.sendToLibrarySilently(book, coverUrl);

    this.bookSearchApi.sendToKindle(book.md5, book.title, 'mom', coverUrl).subscribe({
      next: (resp: SendToTargetResponse) => {
        if (resp.accountFastInfo) {
          this.updateFromServer(resp.accountFastInfo.downloadsLeft, resp.accountFastInfo.downloadsPerDay);
        }
        book.momsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        this.logger.error('Send-to-Mom\'s-Kindle failed', err);
        book.momsKindleState = 'error';
      }
    });
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
        this.lookupCoverForBook(book);
      }
    }

  private queueCoverLookups(): void {
    const missing = this.books.filter(b => this.needsExternalCoverLookup(b));
    missing.slice(0, AUTO_COVER_FETCH_LIMIT).forEach((book, index) => {
      setTimeout(() => this.lookupCoverForBook(book), index * COVER_LOOKUP_STAGGER_MS);
    });
  }

  private lookupCoverForBook(book: BookDto): void {
    if (!this.needsExternalCoverLookup(book)) {
      return;
    }

    if (this.coverLookupsInFlight.has(book.md5)) {
      return;
    }

    this.coverLookupsInFlight.add(book.md5);
    const author = book.authors?.[0];
    this.bookSearchApi.fetchCover(book.title, author).subscribe({
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

  private needsExternalCoverLookup(book: BookDto): boolean {
    if (!book.coverCandidates || book.coverCandidates.length === 0) {
      return true;
    }

    if (!this.useLibGen && !book.source?.startsWith('libgen')) {
      return false;
    }

    const hasNonLibGenCover = book.coverCandidates.some(url => !this.isLibGenCoverUrl(url));
    return !hasNonLibGenCover;
  }

  private isLibGenCoverUrl(url: string): boolean {
    const normalized = url.toLowerCase();
    return normalized.includes('libgen.') && normalized.includes('/covers');
  }

  /* ───────── book description fetching ───────── */
  private fetchBookDescriptions(): void {
    // Fetch descriptions for first N books automatically
    const booksToFetch = this.books.slice(0, AUTO_DESCRIPTION_FETCH_LIMIT);

    booksToFetch.forEach((book, index) => {
      // Stagger requests slightly to avoid overwhelming the APIs
      setTimeout(() => this.fetchDescriptionForBook(book), index * DESCRIPTION_FETCH_STAGGER_MS);
    });
  }

  fetchDescriptionOnDemand(book: BookDto): void {
    if (book.description) {
      return; // Already has description
    }
    this.fetchDescriptionForBook(book);
  }

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

  private fetchDescriptionForBook(book: BookDto): void {
    if (book.description) {
      return; // Already has a description
    }

    const author = book.authors?.[0];

    // Try Google Books first
    this.bookSearchApi.fetchDescriptionFromGoogleBooks(book.title, author).subscribe({
      next: (resp) => {
        if (resp.description) {
          book.description = resp.description;
          book.descriptionSource = 'googlebooks';
        } else {
          // If Google Books fails, try OpenLibrary
          this.tryOpenLibrary(book);
        }
      },
      error: () => {
        // If Google Books fails, try OpenLibrary
        this.tryOpenLibrary(book);
      }
    });
  }

  private tryOpenLibrary(book: BookDto): void {
    const author = book.authors?.[0];

    this.bookSearchApi.fetchDescriptionFromOpenLibrary(book.title, author).subscribe({
      next: (resp) => {
        if (resp.description) {
          book.description = resp.description;
          book.descriptionSource = 'openlibrary';
        } else {
          // If OpenLibrary fails, try GPT-4
          this.tryGPT4(book);
        }
      },
      error: () => {
        // If OpenLibrary fails, try GPT-4
        this.tryGPT4(book);
      }
    });
  }

  private tryGPT4(book: BookDto): void {
    const author = book.authors?.[0];

    this.bookSearchApi.fetchDescriptionFromGPT4(book.title, author).subscribe({
      next: (resp) => {
        if (resp.description) {
          book.description = resp.description;
          book.descriptionSource = 'gpt';
        }
      },
      error: (err) => {
        this.logger.error('Failed to fetch description from GPT-4', err);
        // No more fallbacks - book will remain without description
      }
    });
  }

  /* ───────── author suggestion methods ───────── */
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

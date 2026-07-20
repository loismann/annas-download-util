import { Component, Inject, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { MatDividerModule } from '@angular/material/divider';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  AiApiService,
  SeriesBook,
  AuthorSeriesInfo,
  BookWithCandidates,
  CandidateBook
} from '../services/ai-api.service';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { BookDto } from '../models/book-dto.model';
import { firstValueFrom } from 'rxjs';
import { LoggerService } from '../services/logger.service';
import { RELATED_BOOKS_STAGGER_MS } from '../constants/timeouts';

// How many per-book candidate searches run at once during "Review
// Selections". Kept deliberately small — see the comment at its call site
// in reviewSelections() for why unbounded concurrency here backfired.
const MATCH_SEARCH_CONCURRENCY = 2;

// Runs `fn` over `items` with at most `limit` calls in flight at once,
// preserving input order in the returned array.
async function runWithConcurrencyLimit<T, R>(
  items: T[],
  limit: number,
  fn: (item: T, index: number) => Promise<R>
): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let nextIndex = 0;

  async function worker(): Promise<void> {
    while (nextIndex < items.length) {
      const index = nextIndex++;
      results[index] = await fn(items[index], index);
    }
  }

  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, () => worker()));
  return results;
}

export interface RelatedBooksModalData {
  bookTitle: string;
  author: string;
  sameSeries: SeriesBook[];
  otherSeries: AuthorSeriesInfo[];
  seriesSummary: string | null;
  loading: boolean;
  mode?: 'related' | 'ai';
  query?: string;
}

type MatchStatus = 'pending' | 'matched' | 'ambiguous' | 'missing';

interface MatchResult {
  key: string;
  title: string;
  order?: number;
  status: MatchStatus;
  candidates: BookDto[];
  selected?: BookDto;
  reason?: string;
}

@Component({
  selector: 'app-related-books-modal',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatListModule,
    MatProgressSpinnerModule,
    MatIconModule,
    MatDividerModule,
    MatCheckboxModule,
    MatSelectModule,
    MatFormFieldModule,
    MatTooltipModule,
  ],
  templateUrl: './related-books-modal.component.html',
  styleUrl: './related-books-modal.component.scss'
})
export class RelatedBooksModalComponent implements OnDestroy {
  private destroy$ = new Subject<void>();

  selectedKeys = new Set<string>();
  selectedOtherSeriesKeys = new Set<string>();  // For tracking selections in other series
  expandedSeries = new Set<string>();  // Track which series are expanded
  matchResults: MatchResult[] = [];
  preparingMatches = false;
  sending = false;
  sendLog: string[] = [];
  selectedFormat = 'EPUB';
  statusUpdates: string[] = [];
  coverLookupsInFlight = new Set<string>();

  constructor(
    public dialogRef: MatDialogRef<RelatedBooksModalComponent>,
    @Inject(MAT_DIALOG_DATA) public data: RelatedBooksModalData,
    private aiApi: AiApiService,
    private annaApi: AnnaArchiveApiService,
    private logger: LoggerService
  ) {}

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get selectedBooks(): SeriesBook[] {
    const sameSeriesBooks = this.data.sameSeries.filter(book => this.selectedKeys.has(this.bookKey(book)));

    // Also include books selected from other series
    const otherSeriesBooks: SeriesBook[] = [];
    for (const series of this.data.otherSeries) {
      for (const book of series.books) {
        if (this.selectedOtherSeriesKeys.has(this.otherSeriesBookKey(series.seriesName, book))) {
          otherSeriesBooks.push(book);
        }
      }
    }

    return [...sameSeriesBooks, ...otherSeriesBooks];
  }

  get canReview(): boolean {
    const hasSelections = this.selectedKeys.size > 0 || this.selectedOtherSeriesKeys.size > 0;
    return hasSelections && !this.preparingMatches && !this.sending;
  }

  get hasAmbiguity(): boolean {
    return this.matchResults.some(result => result.status === 'ambiguous');
  }

  get canSend(): boolean {
    if (this.preparingMatches || this.sending || this.matchResults.length === 0) return false;
    return !this.matchResults.some(result => result.status === 'ambiguous' && !result.selected);
  }

  addStatus(message: string): void {
    this.statusUpdates = [...this.statusUpdates, message];
  }

  clearStatus(): void {
    this.statusUpdates = [];
  }

  queueCoverLookups(): void {
    const booksNeedingCovers = this.data.sameSeries.filter(book => !book.coverUrl);
    if (booksNeedingCovers.length === 0) {
      // No covers to look up, finish loading
      this.data.loading = false;
      return;
    }

    this.addStatus(`Looking up covers for ${booksNeedingCovers.length} book${booksNeedingCovers.length === 1 ? '' : 's'}...`);

    booksNeedingCovers.forEach((book, index) => {
      const key = this.bookKey(book);
      if (this.coverLookupsInFlight.has(key)) return;
      this.coverLookupsInFlight.add(key);
      window.setTimeout(() => this.lookupCoverForBook(book), index * RELATED_BOOKS_STAGGER_MS);
    });
  }

  toggleSelected(book: SeriesBook): void {
    const key = this.bookKey(book);
    if (this.selectedKeys.has(key)) {
      this.selectedKeys.delete(key);
    } else {
      this.selectedKeys.add(key);
    }
    this.matchResults = [];
    this.sendLog = [];
  }

  onClose(): void {
    this.dialogRef.close();
  }

  onSearchBook(bookTitle: string): void {
    this.dialogRef.close({ searchBook: bookTitle, author: this.data.author });
  }

  onSearchSeries(seriesName: string, firstBook: string): void {
    this.dialogRef.close({ searchBook: firstBook, author: this.data.author, seriesName });
  }

  async reviewSelections(): Promise<void> {
    if (!this.canReview) return;

    this.preparingMatches = true;
    this.sendLog = [];
    this.matchResults = [];

    const format = this.selectedFormat;
    const author = this.data.mode === 'ai' ? 'Unknown' : this.data.author;

    // Step 1: Search for all books and collect candidates — a *capped*
    // amount of concurrency (MATCH_SEARCH_CONCURRENCY at a time), not fully
    // sequential and not unbounded. Firing every book's search at once
    // (unbounded Promise.all) turned out to backfire badly in practice: a
    // burst of simultaneous requests against Anna's Archive made Cloudflare
    // slow down every single one of them (individual fetches went from a
    // few seconds to 10s-60s+, some timing out outright), which is worse
    // than doing them one at a time. A small fixed batch size gets some
    // overlap without looking like a burst to Cloudflare's anti-bot checks.
    const booksWithCandidates: BookWithCandidates[] = await runWithConcurrencyLimit(
      this.selectedBooks,
      MATCH_SEARCH_CONCURRENCY,
      async (book): Promise<BookWithCandidates> => {
        try {
          const searchResults = await firstValueFrom(this.annaApi.searchBooks(book.title, false));

          // Convert BookDto[] to CandidateBook[]
          const candidates: CandidateBook[] = (searchResults ?? []).map(result => ({
            md5: result.md5,
            title: result.title,
            authors: result.authors,
            format: result.format,
            fileSize: result.fileSize
          }));

          return { title: book.title, order: book.order, candidates };
        } catch (err) {
          this.logger.error(`Failed to search for "${book.title}"`, err);
          return { title: book.title, order: book.order, candidates: [] };
        }
      }
    );

    // Step 2: Use GPT to intelligently match books
    try {
      const matchResponse = await firstValueFrom(
        this.aiApi.matchSeriesBooks({
          seriesName: undefined,  // Could extract from data if available
          author: author,
          preferredFormat: format === 'ALL' ? undefined : format,
          books: booksWithCandidates
        })
      );

      // Step 3: Convert GPT matches to MatchResult format for UI
      for (const match of matchResponse.matches) {
        const bookData = booksWithCandidates.find(b => b.title === match.bookTitle);
        const selectedCandidate = bookData?.candidates.find(c => c.md5 === match.selectedMd5);
        // Find the original SeriesBook to get its description
        const originalBook = this.selectedBooks.find(b => b.title === match.bookTitle);

        const result: MatchResult = {
          key: `${match.order}:${match.bookTitle}`.toLowerCase(),
          title: match.bookTitle,
          order: match.order,
          status: match.status as MatchStatus,
          candidates: bookData?.candidates.map(c => ({
            md5: c.md5,
            title: c.title,
            authors: c.authors,
            format: c.format,
            fileSize: c.fileSize,
            language: '',
            source: '',
            bookType: '',
            publisher: '',
            year: null,
            isbn: null,
            sendState: 'idle',
            dadsKindleState: 'idle',
            momsKindleState: 'idle',
            coverCandidates: [],
            description: originalBook?.description,
            descriptionSource: originalBook?.descriptionSource
          } as BookDto)) ?? [],
          selected: selectedCandidate ? {
            md5: selectedCandidate.md5,
            title: selectedCandidate.title,
            authors: selectedCandidate.authors,
            format: selectedCandidate.format,
            fileSize: selectedCandidate.fileSize,
            language: '',
            source: '',
            bookType: '',
            publisher: '',
            year: null,
            isbn: null,
            sendState: 'idle',
            dadsKindleState: 'idle',
            momsKindleState: 'idle',
            coverCandidates: [],
            description: originalBook?.description,
            descriptionSource: originalBook?.descriptionSource
          } as BookDto : undefined,
          reason: `${match.confidence.toUpperCase()}: ${match.reason}`
        };

        this.matchResults.push(result);
      }
    } catch (err) {
      this.logger.error('GPT matching failed, falling back to simple matching', err);

      // Fallback: Use simple client-side matching
      for (const bookData of booksWithCandidates) {
        // Find the original SeriesBook to get its description
        const originalBook = this.selectedBooks.find(b => b.title === bookData.title);

        const result: MatchResult = {
          key: `${bookData.order}:${bookData.title}`.toLowerCase(),
          title: bookData.title,
          order: bookData.order,
          status: 'ambiguous',
          candidates: bookData.candidates.map(c => ({
            md5: c.md5,
            title: c.title,
            authors: c.authors,
            format: c.format,
            fileSize: c.fileSize,
            language: '',
            source: '',
            bookType: '',
            publisher: '',
            year: null,
            isbn: null,
            sendState: 'idle',
            dadsKindleState: 'idle',
            momsKindleState: 'idle',
            coverCandidates: [],
            description: originalBook?.description,
            descriptionSource: originalBook?.descriptionSource
          } as BookDto)),
          reason: 'AI matching failed - please select manually'
        };

        this.matchResults.push(result);
      }
    }

    this.preparingMatches = false;
  }

  async sendSelected(action: 'library' | 'dropbox' | 'kindle-dad' | 'kindle-mom'): Promise<void> {
    if (this.sending) return;
    const ready = this.matchResults.filter(result => result.selected);
    if (ready.length === 0) return;

    this.sending = true;
    this.sendLog = [];

    for (let i = 0; i < ready.length; i++) {
      const result = ready[i];
      const selected = result.selected!;

      try {
        this.sendLog.push(`Processing ${i + 1}/${ready.length}: ${selected.title}...`);

        // Get the cover URL if available
        const coverUrl = selected.coverCandidates && selected.coverCandidates.length > 0
          ? selected.coverCandidates[0]
          : undefined;

        const authorString = selected.authors?.join(';');

        const trySaveToLibrary = async (): Promise<boolean> => {
          try {
            await firstValueFrom(
              this.annaApi.sendToLibrary(
                selected.md5,
                selected.title,
                coverUrl,
                authorString,
                selected.format,
                selected.fileSize,
                selected.source,
                selected.description ?? undefined
              )
            );
            return true;
          } catch {
            return false;
          }
        };

        if (action === 'library') {
          const saved = await trySaveToLibrary();
          this.sendLog[this.sendLog.length - 1] = saved
            ? `✓ Saved to Library: ${selected.title}`
            : `✗ Library failed: ${selected.title}`;
        } else if (action === 'dropbox') {
          await trySaveToLibrary();
          const resp = await firstValueFrom(this.annaApi.sendToBoox(selected.md5, selected.title, coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Dropbox: ${selected.title}`
            : `✗ Dropbox failed: ${selected.title}`;
        } else if (action === 'kindle-dad') {
          await trySaveToLibrary();
          const resp = await firstValueFrom(this.annaApi.sendToKindle(selected.md5, selected.title, 'dad', coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Dad's Kindle: ${selected.title}`
            : `✗ Dad's Kindle failed: ${selected.title} - ${resp?.message || 'Unknown error'}`;
        } else {
          await trySaveToLibrary();
          const resp = await firstValueFrom(this.annaApi.sendToKindle(selected.md5, selected.title, 'mom', coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Mom's Kindle: ${selected.title}`
            : `✗ Mom's Kindle failed: ${selected.title} - ${resp?.message || 'Unknown error'}`;
        }
      } catch (err) {
        this.sendLog[this.sendLog.length - 1] = `✗ Failed: ${selected.title}`;
      }

      // Add delay between sends to avoid rate limiting (except after last book)
      if (i < ready.length - 1) {
        this.sendLog.push(`⏱️ Waiting 5 seconds to avoid rate limiting...`);
        await this.delay(5000);
        this.sendLog.pop(); // Remove the waiting message
      }
    }

    this.sendLog.push(`✓ Complete! Processed ${ready.length} book${ready.length !== 1 ? 's' : ''}.`);
    this.sending = false;
  }

  setSelectedCandidate(result: MatchResult, md5: string): void {
    const selected = result.candidates.find(candidate => candidate.md5 === md5);
    if (selected) {
      result.selected = selected;
      result.status = 'matched';
    }
  }

  removeMatchResult(result: MatchResult): void {
    const index = this.matchResults.indexOf(result);
    if (index > -1) {
      this.matchResults.splice(index, 1);
    }
  }

  bookKey(book: SeriesBook): string {
    return `${book.order ?? 0}:${book.title}`.toLowerCase();
  }

  otherSeriesBookKey(seriesName: string, book: SeriesBook): string {
    return `${seriesName}:${book.order ?? 0}:${book.title}`.toLowerCase();
  }

  toggleSeriesExpansion(seriesName: string): void {
    if (this.expandedSeries.has(seriesName)) {
      this.expandedSeries.delete(seriesName);
    } else {
      this.expandedSeries.add(seriesName);
      // Queue cover lookups for this series if not already done
      this.queueCoverLookupsForSeries(seriesName);
    }
  }

  isSeriesExpanded(seriesName: string): boolean {
    return this.expandedSeries.has(seriesName);
  }

  toggleOtherSeriesBook(seriesName: string, book: SeriesBook): void {
    const key = this.otherSeriesBookKey(seriesName, book);
    if (this.selectedOtherSeriesKeys.has(key)) {
      this.selectedOtherSeriesKeys.delete(key);
    } else {
      this.selectedOtherSeriesKeys.add(key);
    }
    this.matchResults = [];
    this.sendLog = [];
  }

  getFilteredCandidates(result: MatchResult): BookDto[] {
    const preferredFormat = this.selectedFormat?.toUpperCase();
    const normalizedAuthor = this.getNormalizedAuthorQuery();

    let candidates = result.candidates;
    if (preferredFormat && preferredFormat !== 'ALL') {
      candidates = candidates.filter(candidate => candidate.format?.toUpperCase() === preferredFormat);
    }

    if (normalizedAuthor) {
      const authorMatches = candidates.filter(candidate =>
        candidate.authors?.some(author => this.normalizeText(author).includes(normalizedAuthor))
      );
      if (authorMatches.length > 0) {
        candidates = authorMatches;
      }
    }

    const targetTitle = result.title;
    return [...candidates].sort((a, b) =>
      this.scoreCandidate(targetTitle, b, normalizedAuthor) - this.scoreCandidate(targetTitle, a, normalizedAuthor)
    );
  }

  private scoreCandidate(targetTitle: string, candidate: BookDto, normalizedAuthor: string): number {
    const candidateTitle = this.normalizeText(candidate.title || '');
    const target = this.normalizeText(targetTitle || '');
    let score = 0;

    if (candidateTitle === target) score += 50;
    if (candidateTitle.startsWith(target) || target.startsWith(candidateTitle)) score += 25;
    if (candidateTitle.includes(target) || target.includes(candidateTitle)) score += 20;

    if (normalizedAuthor) {
      const hasAuthorMatch = candidate.authors?.some(author =>
        this.normalizeText(author).includes(normalizedAuthor)
      );
      if (hasAuthorMatch) score += 30;
    }

    return score;
  }

  private getNormalizedAuthorQuery(): string {
    const authorQuery = (this.data?.author || '').trim();
    if (!authorQuery || authorQuery.toLowerCase() === 'ai search') return '';
    return this.normalizeText(authorQuery);
  }

  private normalizeText(value: string): string {
    return value
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, ' ')
      .trim();
  }

  queueCoverLookupsForSeries(seriesName: string): void {
    const series = this.data.otherSeries.find(s => s.seriesName === seriesName);
    if (!series) return;

    const booksNeedingCovers = series.books.filter(book => !book.coverUrl);
    if (booksNeedingCovers.length === 0) return;

    booksNeedingCovers.forEach((book, index) => {
      const key = this.otherSeriesBookKey(seriesName, book);
      if (this.coverLookupsInFlight.has(key)) return;
      this.coverLookupsInFlight.add(key);
      window.setTimeout(() => this.lookupCoverForOtherSeriesBook(series, book), index * RELATED_BOOKS_STAGGER_MS);
    });
  }

  private lookupCoverForOtherSeriesBook(series: AuthorSeriesInfo, book: SeriesBook): void {
    const title = book.title?.trim();
    const key = this.otherSeriesBookKey(series.seriesName, book);

    if (!title) {
      this.coverLookupsInFlight.delete(key);
      return;
    }

    this.annaApi.fetchCover(title, this.data.author).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        if (resp?.coverUrl) {
          book.coverUrl = resp.coverUrl;
        }
        this.coverLookupsInFlight.delete(key);
      },
      error: () => {
        this.coverLookupsInFlight.delete(key);
      }
    });
  }

  private lookupCoverForBook(book: SeriesBook): void {
    const title = book.title?.trim();
    if (!title) {
      this.coverLookupsInFlight.delete(this.bookKey(book));
      this.checkIfAllLookupsComplete();
      return;
    }

    this.annaApi.fetchCover(title, this.data.author).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        if (resp?.coverUrl) {
          book.coverUrl = resp.coverUrl;
          this.addStatus(`✓ ${book.title}`);
        } else {
          this.addStatus(`✗ ${book.title}`);
        }
        this.coverLookupsInFlight.delete(this.bookKey(book));
        this.checkIfAllLookupsComplete();
      },
      error: () => {
        this.addStatus(`✗ ${book.title} (error)`);
        this.coverLookupsInFlight.delete(this.bookKey(book));
        this.checkIfAllLookupsComplete();
      }
    });
  }

  private checkIfAllLookupsComplete(): void {
    if (this.coverLookupsInFlight.size === 0 && this.data.loading) {
      this.data.loading = false;
      this.clearStatus();
    }
  }

  private delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }
}

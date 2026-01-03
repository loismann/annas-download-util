import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
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
  SeriesBook,
  AuthorSeriesInfo,
  AnnaArchiveApiService,
  BookWithCandidates,
  CandidateBook
} from '../services/anna-archive-api.service';
import { BookDto } from '../models/book-dto.model';
import { firstValueFrom } from 'rxjs';

export interface RelatedBooksModalData {
  bookTitle: string;
  author: string;
  sameSeries: SeriesBook[];
  otherSeries: AuthorSeriesInfo[];
  seriesSummary: string | null;
  loading: boolean;
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
export class RelatedBooksModalComponent {
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
    private api: AnnaArchiveApiService
  ) {}

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
      window.setTimeout(() => this.lookupCoverForBook(book), index * 120);
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
    const author = this.data.author;

    // Step 1: Search for all books and collect candidates
    const booksWithCandidates: BookWithCandidates[] = [];

    for (const book of this.selectedBooks) {
      try {
        const searchResults = await firstValueFrom(this.api.searchBooks(book.title, false));

        // Convert BookDto[] to CandidateBook[]
        const candidates: CandidateBook[] = (searchResults ?? []).map(result => ({
          md5: result.md5,
          title: result.title,
          authors: result.authors,
          format: result.format,
          fileSize: result.fileSize
        }));

        booksWithCandidates.push({
          title: book.title,
          order: book.order,
          candidates
        });
      } catch (err) {
        console.error(`Failed to search for "${book.title}"`, err);
        booksWithCandidates.push({
          title: book.title,
          order: book.order,
          candidates: []
        });
      }
    }

    // Step 2: Use GPT to intelligently match books
    try {
      const matchResponse = await firstValueFrom(
        this.api.matchSeriesBooks({
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
            coverCandidates: []
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
            coverCandidates: []
          } as BookDto : undefined,
          reason: `${match.confidence.toUpperCase()}: ${match.reason}`
        };

        this.matchResults.push(result);
      }
    } catch (err) {
      console.error('GPT matching failed, falling back to simple matching', err);

      // Fallback: Use simple client-side matching
      for (const bookData of booksWithCandidates) {
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
            coverCandidates: []
          } as BookDto)),
          reason: 'AI matching failed - please select manually'
        };

        this.matchResults.push(result);
      }
    }

    this.preparingMatches = false;
  }

  async sendSelected(action: 'download' | 'dropbox' | 'kindle-dad' | 'kindle-mom'): Promise<void> {
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

        if (action === 'download') {
          const blob = await firstValueFrom(this.api.downloadMember(selected.md5, selected.title, coverUrl));
          // Create a download link and trigger it
          const url = window.URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = url;
          // Sanitize filename to remove invalid characters
          const sanitizedTitle = selected.title.replace(/[<>:"/\\|?*]/g, '_');
          link.download = `${sanitizedTitle}.${selected.format.toLowerCase()}`;
          document.body.appendChild(link);
          link.click();
          document.body.removeChild(link);
          window.URL.revokeObjectURL(url);
          this.sendLog[this.sendLog.length - 1] = `✓ Downloaded: ${selected.title}`;
        } else if (action === 'dropbox') {
          const resp = await firstValueFrom(this.api.sendToBoox(selected.md5, selected.title, coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Dropbox: ${selected.title}`
            : `✗ Dropbox failed: ${selected.title}`;
        } else if (action === 'kindle-dad') {
          const resp = await firstValueFrom(this.api.sendToKindle(selected.md5, selected.title, 'dad', coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Dad's Kindle: ${selected.title}`
            : `✗ Dad's Kindle failed: ${selected.title} - ${resp?.message || 'Unknown error'}`;
        } else {
          const resp = await firstValueFrom(this.api.sendToKindle(selected.md5, selected.title, 'mom', coverUrl));
          this.sendLog[this.sendLog.length - 1] = resp?.success
            ? `✓ Sent to Mom's Kindle: ${selected.title}`
            : `✗ Mom's Kindle failed: ${selected.title} - ${resp?.message || 'Unknown error'}`;
        }
      } catch (err) {
        this.sendLog[this.sendLog.length - 1] = `✗ Failed: ${selected.title}`;
      }

      // Add delay between sends to avoid rate limiting (except after last book)
      if (i < ready.length - 1) {
        this.sendLog.push(`⏱️ Waiting 15 seconds to avoid rate limiting...`);
        await this.delay(15000);
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

  queueCoverLookupsForSeries(seriesName: string): void {
    const series = this.data.otherSeries.find(s => s.seriesName === seriesName);
    if (!series) return;

    const booksNeedingCovers = series.books.filter(book => !book.coverUrl);
    if (booksNeedingCovers.length === 0) return;

    booksNeedingCovers.forEach((book, index) => {
      const key = this.otherSeriesBookKey(seriesName, book);
      if (this.coverLookupsInFlight.has(key)) return;
      this.coverLookupsInFlight.add(key);
      window.setTimeout(() => this.lookupCoverForOtherSeriesBook(series, book), index * 120);
    });
  }

  private lookupCoverForOtherSeriesBook(series: AuthorSeriesInfo, book: SeriesBook): void {
    const title = book.title?.trim();
    const key = this.otherSeriesBookKey(series.seriesName, book);

    if (!title) {
      this.coverLookupsInFlight.delete(key);
      return;
    }

    this.api.fetchCover(title, this.data.author).subscribe({
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

    this.api.fetchCover(title, this.data.author).subscribe({
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

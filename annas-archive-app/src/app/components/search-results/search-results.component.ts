import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { BookDto } from '../../models/book-dto.model';
import { AUTO_DESCRIPTION_FETCH_LIMIT } from '../../constants/limits';

export interface SendToLibraryEvent {
  book: BookDto;
}

export interface SendToDropboxEvent {
  book: BookDto;
}

export interface SendToKindleEvent {
  book: BookDto;
  target: 'dad' | 'mom';
}

export interface FetchDescriptionEvent {
  book: BookDto;
}

export interface CoverErrorEvent {
  book: BookDto;
  event: Event;
}

@Component({
  selector: 'app-search-results',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule
  ],
  templateUrl: './search-results.component.html',
  styleUrls: ['./search-results.component.css']
})
export class SearchResultsComponent {
  // Exposed for the template — the manual "Retrieve Summary" button should
  // only appear for books past whatever range book-search.component.ts's
  // fetchBookDescriptions() already auto-fetches. Was hardcoded as a
  // literal `10` in the template, which silently went stale when
  // AUTO_DESCRIPTION_FETCH_LIMIT was turned down to 0 (auto-fetch disabled
  // entirely) — every book was then in the auto-fetch limit's "index < 10"
  // dead zone with no description and no way to request one. Binding to
  // the real constant keeps these two in sync going forward.
  readonly autoDescriptionFetchLimit = AUTO_DESCRIPTION_FETCH_LIMIT;

  @Input() books: BookDto[] = [];
  @Input() loading = false;
  @Input() searchPerformed = false;
  @Input() placeholderUrl = '/assets/placeholder.jpg';

  @Output() sendToLibrary = new EventEmitter<SendToLibraryEvent>();
  @Output() sendToDropbox = new EventEmitter<SendToDropboxEvent>();
  @Output() sendToKindle = new EventEmitter<SendToKindleEvent>();
  @Output() fetchDescription = new EventEmitter<FetchDescriptionEvent>();
  @Output() coverError = new EventEmitter<CoverErrorEvent>();

  // Track expanded cards by md5
  private expandedCards = new Set<string>();

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
    return !!description && description.length > 150;
  }

  onCoverError(book: BookDto, event: Event): void {
    this.coverError.emit({ book, event });
  }

  onSendToLibrary(book: BookDto): void {
    this.sendToLibrary.emit({ book });
  }

  onSendToDropbox(book: BookDto): void {
    this.sendToDropbox.emit({ book });
  }

  onSendToDadsKindle(book: BookDto): void {
    this.sendToKindle.emit({ book, target: 'dad' });
  }

  onSendToMomsKindle(book: BookDto): void {
    this.sendToKindle.emit({ book, target: 'mom' });
  }

  onFetchDescription(book: BookDto): void {
    this.fetchDescription.emit({ book });
  }

  getCoverUrl(book: BookDto): string {
    return book.coverCandidates?.length ? book.coverCandidates[0] : this.placeholderUrl;
  }
}

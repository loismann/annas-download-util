import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { BookDto } from '../../models/book-dto.model';
import { BookGroup } from '../../models/book-group.model';

/** One card in the results grid — a group plus whichever book within it is
 *  currently "active" (shown on the card / acted on by the send buttons).
 *  Computed by the parent (BookSearchComponent.displayGroups) since that's
 *  where the per-group variant selection state (groupSelection) lives. */
export interface DisplayGroup {
  group: BookGroup;
  active: BookDto;
}

export interface VariantSelectedEvent {
  group: BookGroup;
  book: BookDto;
}

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
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatFormFieldModule,
    MatSelectModule
  ],
  templateUrl: './search-results.component.html',
  styleUrls: ['./search-results.component.css']
})
export class SearchResultsComponent {
  @Input() groups: DisplayGroup[] = [];
  @Input() groupingInProgress = false;
  @Input() loading = false;
  @Input() searchPerformed = false;
  @Input() placeholderUrl = '/assets/placeholder.jpg';

  @Output() variantSelected = new EventEmitter<VariantSelectedEvent>();
  @Output() sendToLibrary = new EventEmitter<SendToLibraryEvent>();
  @Output() sendToDropbox = new EventEmitter<SendToDropboxEvent>();
  @Output() sendToKindle = new EventEmitter<SendToKindleEvent>();
  @Output() fetchDescription = new EventEmitter<FetchDescriptionEvent>();
  @Output() coverError = new EventEmitter<CoverErrorEvent>();
  @Output() openSummary = new EventEmitter<BookDto>();

  trackByGroupKey(_index: number, dg: DisplayGroup): string {
    return dg.group.key;
  }

  /** Distinct formats anywhere in the group — the card's format badges,
   *  independent of which one happens to be "active" right now. */
  formatsOf(group: BookGroup): string[] {
    return Array.from(new Set(group.books.map(b => b.format)));
  }

  /** Other uploads sharing the active book's format — when there's more
   *  than one, the card offers a version picker (distinguished only by
   *  file size/source, since title/author/format are otherwise identical). */
  sameFormatSiblings(group: BookGroup, active: BookDto): BookDto[] {
    return group.books.filter(b => b.format === active.format);
  }

  onPickFormat(dg: DisplayGroup, format: string): void {
    if (format === dg.active.format) return;
    const book = dg.group.books.find(b => b.format === format);
    if (book) this.variantSelected.emit({ group: dg.group, book });
  }

  onPickVariant(dg: DisplayGroup, md5: string): void {
    if (md5 === dg.active.md5) return;
    const book = dg.group.books.find(b => b.md5 === md5);
    if (book) this.variantSelected.emit({ group: dg.group, book });
  }

  onTileClick(book: BookDto): void {
    this.openSummary.emit(book);
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

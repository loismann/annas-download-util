import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';

import { BookDto } from '../../models/book-dto.model';
import { BookGroup } from '../../models/book-group.model';
import { DISPLAYABLE_BOOK_FORMATS } from '../../constants/book-formats';

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

/** Parses a "1.1MB"/"850KB"/"2.3GB"-style label into a comparable magnitude
 *  (KB) — fileSize is a display string, not a number, so sorting the
 *  version picker by size needs this first. Unrecognized formats sort last
 *  (treated as 0) rather than throwing. */
function parseFileSizeKb(fileSize: string): number {
  const match = /([\d.]+)\s*(KB|MB|GB)/i.exec(fileSize ?? '');
  if (!match) return 0;
  const value = parseFloat(match[1]);
  const unit = match[2].toUpperCase();
  const multiplier = unit === 'GB' ? 1024 * 1024 : unit === 'MB' ? 1024 : 1;
  return value * multiplier;
}

@Component({
  selector: 'app-search-results',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatMenuModule
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

  /** Distinct formats anywhere in the group, restricted and ordered per
   *  DISPLAYABLE_BOOK_FORMATS — the card's format badges, independent of
   *  which one happens to be "active" right now. A group whose only files
   *  are outside that set (AZW3-only, say) just shows no badges; its real
   *  format still shows in the plain meta line below. */
  formatsOf(group: BookGroup): string[] {
    const present = new Set(group.books.map(b => b.format));
    return DISPLAYABLE_BOOK_FORMATS.filter(f => present.has(f));
  }

  /** Other uploads sharing the active book's format — when there's more
   *  than one, the card offers a version picker (distinguished only by
   *  file size/source, since title/author/format are otherwise identical).
   *  Largest first — usually the more complete/higher-quality scan. */
  sameFormatSiblings(group: BookGroup, active: BookDto): BookDto[] {
    return group.books
      .filter(b => b.format === active.format)
      .sort((a, b) => parseFileSizeKb(b.fileSize) - parseFileSizeKb(a.fileSize));
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

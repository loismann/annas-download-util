import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';

export interface LibraryBook {
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
  description?: string | null;
  dadsKindleState?: 'idle' | 'sending' | 'success' | 'error';
  momsKindleState?: 'idle' | 'sending' | 'success' | 'error';
}

@Component({
  selector: 'app-book-card',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule
  ],
  templateUrl: './book-card.component.html',
  styleUrls: ['./book-card.component.css']
})
export class BookCardComponent {
  @Input() book!: LibraryBook;
  @Input() tileSize: 'small' | 'medium' | 'large' = 'medium';
  @Input() bulkEditMode = false;
  @Input() isSelected = false;
  @Input() canSendToKindle = false;
  @Input() placeholderUrl = '/assets/placeholder.jpg';

  @Output() coverClick = new EventEmitter<LibraryBook>();
  @Output() ratingChange = new EventEmitter<{ book: LibraryBook; rating: number }>();
  @Output() sendToKindle = new EventEmitter<{ book: LibraryBook; target: 'dad' | 'mom' }>();
  @Output() selectionToggle = new EventEmitter<LibraryBook>();
  @Output() coverError = new EventEmitter<Event>();

  readonly starRange = [1, 2, 3, 4, 5];

  onCoverClick(): void {
    this.coverClick.emit(this.book);
  }

  onCoverError(event: Event): void {
    const img = event.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
    this.coverError.emit(event);
  }

  setPersonalRating(rating: number): void {
    this.ratingChange.emit({ book: this.book, rating });
  }

  onSendToKindle(target: 'dad' | 'mom'): void {
    this.sendToKindle.emit({ book: this.book, target });
  }

  onSelectionToggle(): void {
    this.selectionToggle.emit(this.book);
  }
}

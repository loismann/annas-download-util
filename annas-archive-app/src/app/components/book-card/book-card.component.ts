import { Component, EventEmitter, Input, Output, OnInit, OnDestroy, ElementRef, ViewChild, AfterViewInit } from '@angular/core';
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
  bookmarked?: boolean | null;
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
export class BookCardComponent implements AfterViewInit, OnDestroy {
  @Input() book!: LibraryBook;
  @Input() tileSize: 'small' | 'medium' | 'large' = 'medium';
  @Input() bulkEditMode = false;
  @Input() isSelected = false;
  @Input() canSendToKindle = false;
  @Input() placeholderUrl = '/assets/placeholder.jpg';

  @Output() coverClick = new EventEmitter<LibraryBook>();
  @Output() ratingChange = new EventEmitter<{ book: LibraryBook; rating: number }>();
  @Output() bookmarkToggle = new EventEmitter<LibraryBook>();
  @Output() sendToKindle = new EventEmitter<{ book: LibraryBook; target: 'dad' | 'mom' }>();
  @Output() selectionToggle = new EventEmitter<LibraryBook>();
  @Output() coverError = new EventEmitter<Event>();

  @ViewChild('coverImage') coverImageRef?: ElementRef<HTMLImageElement>;

  readonly starRange = [1, 2, 3, 4, 5];

  /** Track if the image has been loaded via IntersectionObserver */
  imageLoaded = false;

  /** Shared IntersectionObserver for all book cards (more efficient than per-card observers) */
  private static observer: IntersectionObserver | null = null;
  private static observedElements = new Map<HTMLElement, BookCardComponent>();

  private static getOrCreateObserver(): IntersectionObserver {
    if (!BookCardComponent.observer) {
      BookCardComponent.observer = new IntersectionObserver(
        (entries) => {
          entries.forEach(entry => {
            if (entry.isIntersecting) {
              const component = BookCardComponent.observedElements.get(entry.target as HTMLElement);
              if (component && !component.imageLoaded) {
                component.loadImage();
                // Once loaded, stop observing
                BookCardComponent.observer?.unobserve(entry.target);
                BookCardComponent.observedElements.delete(entry.target as HTMLElement);
              }
            }
          });
        },
        {
          rootMargin: '200px 0px', // Start loading 200px before entering viewport
          threshold: 0
        }
      );
    }
    return BookCardComponent.observer;
  }

  ngAfterViewInit(): void {
    // If no cover URL, no need to observe
    if (!this.book?.coverUrl || !this.coverImageRef?.nativeElement) {
      this.imageLoaded = true; // Show placeholder immediately
      return;
    }

    const observer = BookCardComponent.getOrCreateObserver();
    const element = this.coverImageRef.nativeElement;
    BookCardComponent.observedElements.set(element, this);
    observer.observe(element);
  }

  ngOnDestroy(): void {
    if (this.coverImageRef?.nativeElement) {
      BookCardComponent.observer?.unobserve(this.coverImageRef.nativeElement);
      BookCardComponent.observedElements.delete(this.coverImageRef.nativeElement);
    }
  }

  /** Load the actual image (called by IntersectionObserver) */
  private loadImage(): void {
    this.imageLoaded = true;
  }

  /** Get the current image source - placeholder until visible */
  get currentCoverUrl(): string {
    if (!this.imageLoaded) {
      return this.placeholderUrl;
    }
    return this.book?.coverUrl || this.placeholderUrl;
  }

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

  onBookmarkToggle(): void {
    this.bookmarkToggle.emit(this.book);
  }

  onSendToKindle(target: 'dad' | 'mom'): void {
    this.sendToKindle.emit({ book: this.book, target });
  }

  onSelectionToggle(): void {
    this.selectionToggle.emit(this.book);
  }
}

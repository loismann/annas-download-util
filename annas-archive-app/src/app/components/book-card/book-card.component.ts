import { Component, EventEmitter, Input, Output, OnDestroy, OnChanges, SimpleChanges, ElementRef, ViewChild, AfterViewInit } from '@angular/core';
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
  /** Names of household members who have favorited this book — per-owner, not a shared flag. */
  favoritedBy?: string[];
  readerEnabled?: boolean | null;
  description?: string | null;
  dadsKindleState?: 'idle' | 'sending' | 'success' | 'error';
  momsKindleState?: 'idle' | 'sending' | 'success' | 'error';
  dropboxState?: 'idle' | 'sending' | 'success' | 'error';
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
export class BookCardComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input() book!: LibraryBook;
  @Input() tileSize: 'small' | 'medium' | 'large' = 'medium';
  @Input() bulkEditMode = false;
  @Input() isSelected = false;
  @Input() canSendToKindle = false;
  @Input() isAdmin = false;
  @Input() placeholderUrl = '/assets/placeholder.jpg';
  /** Whoever's currently logged in ("Paul"/"Mom"/"Dad") — determines which owner's favorite
   *  star this card shows/toggles, since favoriting is per-user, not a shared flag. */
  @Input() currentOwnerName: string | null = null;

  @Output() coverClick = new EventEmitter<LibraryBook>();
  @Output() ratingChange = new EventEmitter<{ book: LibraryBook; rating: number }>();
  @Output() favoriteToggle = new EventEmitter<LibraryBook>();
  @Output() sendToKindle = new EventEmitter<{ book: LibraryBook; target: 'dad' | 'mom' }>();
  @Output() sendToDropbox = new EventEmitter<LibraryBook>();
  @Output() readBook = new EventEmitter<LibraryBook>();
  @Output() selectionToggle = new EventEmitter<LibraryBook>();
  @Output() coverError = new EventEmitter<Event>();

  @ViewChild('coverImage') coverImageRef?: ElementRef<HTMLImageElement>;

  readonly starRange = [1, 2, 3, 4, 5];

  /** Track if the image has been loaded via IntersectionObserver */
  imageLoaded = false;

  /**
   * Which book's cover this card is currently registered/loaded for. cdk-virtual-scroll
   * recycles BookCardComponent instances (and their <img> element) across many different
   * books as you scroll, rebinding `book` via ngOnChanges rather than recreating the
   * component — so ngAfterViewInit alone only sets up lazy-loading for the first book a
   * given recycled slot ever shows. This tracks that so ngOnChanges can detect the swap.
   */
  private observedFileName: string | null = null;

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
    this.observedFileName = this.book?.fileName ?? null;
    this.registerForLazyLoad();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['book']) return;
    // On the very first binding (before the view exists), coverImageRef isn't
    // set yet — let ngAfterViewInit handle that initial setup as before. This
    // branch only matters for a card being recycled to a different book after
    // the view already exists.
    if (!this.coverImageRef?.nativeElement) return;
    // Also skip in-place mutations of the same book (e.g. rating/favorite
    // updates) — only a genuine book swap should re-arm lazy-loading.
    if (this.book?.fileName === this.observedFileName) return;
    this.observedFileName = this.book?.fileName ?? null;
    this.imageLoaded = false;
    this.registerForLazyLoad();
  }

  ngOnDestroy(): void {
    if (this.coverImageRef?.nativeElement) {
      BookCardComponent.observer?.unobserve(this.coverImageRef.nativeElement);
      BookCardComponent.observedElements.delete(this.coverImageRef.nativeElement);
    }
  }

  /** (Re-)register this card's <img> element with the shared IntersectionObserver. */
  private registerForLazyLoad(): void {
    const element = this.coverImageRef?.nativeElement;
    if (!this.book?.coverUrl || !element) {
      this.imageLoaded = true; // Show placeholder immediately
      return;
    }

    const observer = BookCardComponent.getOrCreateObserver();
    // A recycled element may already be registered (or already fired and been
    // unobserved) for a previous book — clear that out before re-observing so
    // it isn't silently ignored or double-registered.
    observer.unobserve(element);
    BookCardComponent.observedElements.set(element, this);
    observer.observe(element);
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
    // On a recycled card, rapid scrolling can rebind `src` several times in
    // quick succession, aborting whatever request was still in flight for a
    // book this card has already moved on from. Only treat the error as real
    // if it's still for the book currently bound — otherwise ignore it, since
    // Angular's [src] binding won't re-write an unchanged value, and this
    // stale error would permanently stick the placeholder in place.
    const expectedSrc = new URL(this.currentCoverUrl, document.baseURI).href;
    if (img.src !== expectedSrc) {
      return;
    }
    img.src = this.placeholderUrl;
    this.coverError.emit(event);
  }

  setPersonalRating(rating: number): void {
    this.ratingChange.emit({ book: this.book, rating });
  }

  get isPdf(): boolean {
    return (this.book?.format ?? '').toUpperCase() === 'PDF';
  }

  get isFavorited(): boolean {
    return !!this.currentOwnerName && (this.book?.favoritedBy ?? []).includes(this.currentOwnerName);
  }

  onFavoriteToggle(): void {
    this.favoriteToggle.emit(this.book);
  }

  onSendToKindle(target: 'dad' | 'mom'): void {
    this.sendToKindle.emit({ book: this.book, target });
  }

  onSendToDropbox(): void {
    this.sendToDropbox.emit(this.book);
  }

  onReadClick(): void {
    this.readBook.emit(this.book);
  }

  onSelectionToggle(): void {
    this.selectionToggle.emit(this.book);
  }
}

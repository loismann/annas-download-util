import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Book, Lens } from '../reader2.models';

/**
 * The books enrolled in Reader II, most recently opened first.
 *
 * <p>A book whose file has gone missing is shown, greyed, with what happened —
 * not hidden and not an error. Its artifacts are still there, and the file
 * usually comes back (a rename, a moved drive), at which point the content hash
 * re-locates it and everything the reader paid for is still attached.</p>
 */
@Component({
  selector: 'app-reader2-book-shelf',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="shelf">
      <li *ngFor="let book of books" [class.unavailable]="!book.isAvailable">
        <button
          type="button"
          class="book"
          [class.current]="book.bookId === currentBookId"
          [disabled]="!book.isAvailable"
          [attr.aria-current]="book.bookId === currentBookId ? 'true' : null"
          (click)="open.emit(book.bookId)">
          <span class="title">{{ book.title }}</span>
          <span class="authors" *ngIf="book.authors.length > 0">{{ book.authors.join(', ') }}</span>
          <span class="type">{{ lensName(book.lensKey) }}</span>
          <span class="missing" *ngIf="!book.isAvailable">
            <mat-icon aria-hidden="true">error_outline</mat-icon>
            The file is missing. Its work is kept.
          </span>
        </button>

        <button
          type="button"
          class="remove"
          [attr.aria-label]="'Remove ' + book.title + ' from Reader II'"
          (click)="remove.emit(book.bookId)">
          <mat-icon>close</mat-icon>
        </button>
      </li>
    </ul>

    <p class="empty" *ngIf="books.length === 0">
      No books yet. Add one from your library.
    </p>
  `,
  styleUrl: './book-shelf.component.scss'
})
export class BookShelfComponent {
  @Input() books: Book[] = [];
  @Input() lenses: Lens[] = [];
  @Input() currentBookId: string | null = null;

  @Output() open = new EventEmitter<string>();
  @Output() remove = new EventEmitter<string>();

  /**
   * The server's display name for a book type, never a name of our own — a book
   * filed under a type this build no longer has still says something truthful.
   */
  protected lensName(lensKey: string): string {
    return this.lenses.find(l => l.key === lensKey)?.displayName ?? lensKey;
  }
}

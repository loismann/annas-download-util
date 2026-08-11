import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { Book, Lens } from '../reader2.models';

/**
 * The books enrolled in Reader II, most recently opened first.
 *
 * <p>A book whose file has gone missing is shown, greyed, with what happened —
 * not hidden and not an error. Its artifacts are still there, and the file
 * usually comes back (a rename, a moved drive), at which point the content hash
 * re-locates it and everything the reader paid for is still attached.</p>
 *
 * <p>Covers come from the library, which is the only thing that knows where a
 * given book's picture is. A book without one gets the same rectangle with a
 * spine drawn in it rather than nothing, so the titles stay in one column —
 * ragged rows are harder to read down than a few plain tiles.</p>
 */
@Component({
  selector: 'app-reader2-book-shelf',
  standalone: true,
  imports: [CommonModule, MatIconModule, RouterLink],
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
          <!--
            Decorative: the title is right beside it, so an alt text would have a
            screen reader read every book twice. A cover that 404s hides itself
            rather than showing a broken-image glyph — the library's index can
            name a file that has since been tidied away.
          -->
          <img
            class="cover"
            *ngIf="coverOf(book) as cover; else noCover"
            [src]="cover"
            alt=""
            loading="lazy"
            (error)="lost(book.bookId)" />
          <ng-template #noCover>
            <span class="cover placeholder" aria-hidden="true">
              <mat-icon>menu_book</mat-icon>
            </span>
          </ng-template>

          <span class="about">
            <span class="title">{{ book.title }}</span>
            <span class="authors" *ngIf="book.authors.length > 0">{{ book.authors.join(', ') }}</span>
            <span class="type">{{ lensName(book.lensKey) }}</span>
            <span class="missing" *ngIf="!book.isAvailable">
              <mat-icon aria-hidden="true">error_outline</mat-icon>
              The file is missing. Its work is kept.
            </span>
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

    <!--
      Always offered, not only when the shelf is empty: enrolling happens in the
      library, so "where do I get another one" is the question this page raises
      every time it is opened, whether it has three books on it or none.

      An anchor with a routerLink rather than a button the shell routes for: this
      is a link to a fixed place, so it should middle-click and open in a new tab
      like every other one. The shell would be the natural owner of navigation,
      but it is at its 200-line limit and this is not a which-book question.
    -->
    <a class="browse" routerLink="/library">
      <mat-icon>local_library</mat-icon> Your ebook library
    </a>
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

  /** The cover to draw, or nothing once this one has been shown not to load. */
  protected coverOf(book: Book): string | null {
    return this.broken.has(book.bookId) ? null : book.coverUrl;
  }

  /**
   * A cover the browser could not fetch falls back to the placeholder.
   *
   * <p>The library's index outlives the files it names — a cover tidied away or
   * a host that has moved leaves a URL that 404s, and the browser's own answer to
   * that is a broken-image glyph in every row. Recorded per book rather than
   * clearing the input, because the input is the server's answer and will be
   * again on the next load.</p>
   */
  protected lost(bookId: string): void {
    this.broken.add(bookId);
  }

  private readonly broken = new Set<string>();
}

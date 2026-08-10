import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Bookmark, ChapterInfo } from '../reader2.models';

/**
 * The bookmark toggle and the list behind it.
 *
 * <p>One button, not two: whether pressing it marks or unmarks depends on
 * whether there is already a mark where the reader is, and that decision is the
 * store's — the template only renders which of the two it currently is. Two
 * buttons would need the same decision made twice, in a place with no state to
 * make it from.</p>
 */
@Component({
  selector: 'app-reader2-bookmark-bar',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar">
      <button
        type="button"
        class="toggle"
        [class.marked]="!!markHere"
        [disabled]="disabled"
        [attr.aria-pressed]="!!markHere"
        [title]="markHere ? 'Remove this bookmark' : 'Bookmark this page'"
        (click)="toggle.emit()">
        <mat-icon>{{ markHere ? 'bookmark' : 'bookmark_border' }}</mat-icon>
      </button>

      <button
        type="button"
        class="count"
        [disabled]="bookmarks.length === 0"
        [attr.aria-expanded]="open"
        (click)="openChange.emit(!open)">
        {{ bookmarks.length }}
        <span class="visually-hidden">bookmarks</span>
      </button>
    </div>

    <ul class="list" *ngIf="open && bookmarks.length > 0">
      <li *ngFor="let mark of bookmarks">
        <button type="button" class="jump" (click)="jump.emit(mark)">
          <span class="where">{{ chapterTitle(mark.chapter) }}</span>
          <span class="label" *ngIf="mark.label">{{ mark.label }}</span>
        </button>
        <button
          type="button"
          class="remove"
          [attr.aria-label]="'Remove bookmark in ' + chapterTitle(mark.chapter)"
          (click)="remove.emit(mark.id)">
          <mat-icon>close</mat-icon>
        </button>
      </li>
    </ul>
  `,
  styleUrl: './bookmark-bar.component.scss'
})
export class BookmarkBarComponent {
  @Input() bookmarks: Bookmark[] = [];

  /** The mark at the reader's current place, when there is one. */
  @Input() markHere: Bookmark | null = null;

  @Input() chapters: ChapterInfo[] = [];
  @Input() open = false;
  @Input() disabled = false;

  @Output() toggle = new EventEmitter<void>();
  @Output() jump = new EventEmitter<Bookmark>();
  @Output() remove = new EventEmitter<string>();
  @Output() openChange = new EventEmitter<boolean>();

  /**
   * Chapters are indexed by position in the list, which is the same number the
   * bookmark stores — so a title is a lookup, and a missing one still names
   * something rather than rendering blank.
   */
  protected chapterTitle(chapter: number): string {
    return this.chapters[chapter]?.title ?? `Chapter ${chapter + 1}`;
  }
}

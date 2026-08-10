import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChapterInfo } from '../reader2.models';

/**
 * The contents list. Nesting comes from the TOC's own depth, so a book with
 * parts and chapters reads as one.
 *
 * <p>Chapters already summarised are marked. A reader who cannot see what they
 * have already paid for pays for it again, and the mark is the cheapest possible
 * way to stop that — the server sends it with the list that was being fetched
 * anyway.</p>
 */
@Component({
  selector: 'app-reader2-chapter-list',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="chapters" aria-label="Contents">
      <button
        *ngFor="let chapter of chapters; let i = index"
        type="button"
        class="chapter"
        [class.current]="i === currentIndex"
        [style.padding-left.rem]="0.75 + chapter.level"
        [attr.aria-current]="i === currentIndex ? 'true' : null"
        (click)="select.emit(i)">
        <span class="title">{{ chapter.title }}</span>
        <span
          class="summarised"
          *ngIf="chapter.hasSummary"
          aria-label="Already summarised"
          title="Already summarised">&#10003;</span>
        <span class="words">{{ chapter.wordCount | number }}</span>
      </button>

      <p class="empty" *ngIf="chapters.length === 0">Nothing indexed yet.</p>
    </nav>
  `,
  styleUrl: './chapter-list.component.scss'
})
export class ChapterListComponent {
  @Input() chapters: ChapterInfo[] = [];
  @Input() currentIndex = 0;

  @Output() select = new EventEmitter<number>();
}

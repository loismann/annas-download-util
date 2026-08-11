import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChapterInfo } from '../reader2.models';
import { ChapterNamePipe } from '../chapter-name.pipe';

/** One thing that happened, and the chapter it happened in. */
export interface ChapterEntry {
  chapter: number;
  what: string;
}

/**
 * A chapter-by-chapter list: an arc, a thread's beats, a relationship's history.
 *
 * <p>One component because all three are the same list — a chapter and a
 * sentence — and they were drawn three times with three copies of the same
 * hanging-indent CSS. The chapter <i>name</i> is the reason to collapse them
 * now: naming a chapter properly instead of numbering it is a change that would
 * otherwise have had to be made, and got right, in three places.</p>
 *
 * <p>Names run inline rather than in a fixed left gutter. The gutter was three
 * rems wide because it only ever held "Ch 14"; a real chapter title is any
 * length, and the first long one would have overflowed it.</p>
 */
@Component({
  selector: 'app-reader2-chapter-log',
  standalone: true,
  imports: [CommonModule, ChapterNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="log">
      <!-- The dash sits inside the span so the text reads as one sentence when
           it is copied or read aloud, rather than relying on a CSS margin. -->
      <li *ngFor="let entry of entries; trackBy: at">
        <span class="when">{{ entry.chapter | chapterName: chapters }} —</span>
        {{ entry.what }}
      </li>
    </ol>
  `,
  styleUrl: './chapter-log.component.scss'
})
export class ChapterLogComponent {
  @Input({ required: true }) entries: ChapterEntry[] = [];

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  protected at(index: number): number {
    return index;
  }
}

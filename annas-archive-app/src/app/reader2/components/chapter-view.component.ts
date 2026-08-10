import {
  ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, Output, ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { DEFAULT_PREFERENCES, PassageSelection, ReadingPreferences } from '../reader2.models';

/**
 * The reading surface: one page of text, and the selection that drives passage
 * analysis.
 *
 * <p>Presentational only — it is handed the words to show and reports what was
 * selected. It does not know what a page is, which is what keeps the paging
 * arithmetic testable without a browser.</p>
 */
@Component({
  selector: 'app-reader2-chapter-view',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article
      #surface
      class="surface"
      tabindex="0"
      [attr.aria-label]="title"
      [class.theme-dark]="preferences.theme === 'dark'"
      [class.theme-sepia]="preferences.theme === 'sepia'"
      [style.font-family]="fontStack"
      [style.font-size.px]="preferences.fontSize"
      (mouseup)="reportSelection()"
      (touchend)="reportSelection()"
      (keydown.arrowright)="forward.emit()"
      (keydown.arrowleft)="back.emit()"
      (keydown.pagedown)="forward.emit()"
      (keydown.pageup)="back.emit()">
      <h2 class="chapter-title">{{ title }}</h2>
      <p class="body">{{ text }}</p>
    </article>

    <footer class="pager">
      <button type="button" (click)="back.emit()" [disabled]="!canBack" aria-label="Previous page">‹</button>
      <span class="position" aria-live="polite">Page {{ page + 1 }} of {{ pageTotal }}</span>
      <button type="button" (click)="forward.emit()" [disabled]="!canForward" aria-label="Next page">›</button>
    </footer>
  `,
  styleUrl: './chapter-view.component.scss'
})
export class ChapterViewComponent {
  @Input() title = '';
  @Input() text = '';
  @Input() page = 0;
  @Input() pageTotal = 1;
  @Input() pageStartWord = 0;
  @Input() canBack = false;
  @Input() canForward = false;
  @Input() preferences: ReadingPreferences = DEFAULT_PREFERENCES;

  @Output() forward = new EventEmitter<void>();
  @Output() back = new EventEmitter<void>();
  @Output() selected = new EventEmitter<PassageSelection>();

  @ViewChild('surface') surface?: ElementRef<HTMLElement>;

  get fontStack(): string {
    return FONT_STACKS[this.preferences.fontFamily] ?? FONT_STACKS['serif'];
  }

  /**
   * Turns a browser selection into a chapter word offset.
   *
   * <p>Counted from the start of the *page* and added to the page's own offset,
   * because that is the one arithmetic the server also does — an offset computed
   * any other way would not line up with a bookmark or a search hit.</p>
   */
  reportSelection(): void {
    const selection = window.getSelection();
    const text = selection?.toString().trim() ?? '';

    if (text.length === 0 || !selection || selection.rangeCount === 0) return;

    this.selected.emit({ text, wordOffset: this.pageStartWord + this.wordsBefore(selection) });
  }

  private wordsBefore(selection: Selection): number {
    const surface = this.surface?.nativeElement;
    if (!surface) return 0;

    const before = selection.getRangeAt(0).cloneRange();
    before.selectNodeContents(surface);
    before.setEnd(selection.getRangeAt(0).startContainer, selection.getRangeAt(0).startOffset);

    const preceding = before.toString().trim();
    return preceding.length === 0 ? 0 : preceding.split(/\s+/).length;
  }
}

const FONT_STACKS: Record<string, string> = {
  serif: 'Georgia, "Iowan Old Style", "Times New Roman", serif',
  sans: '"Inter", "Helvetica Neue", Arial, sans-serif',
  mono: '"SF Mono", "Cascadia Mono", Menlo, monospace'
};

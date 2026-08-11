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
      [style.font-family]="fontStack"
      [style.font-size.px]="preferences.fontSize"
      (mouseup)="reportSelection()"
      (touchend)="reportSelection()"
      (keydown.arrowright)="forward.emit()"
      (keydown.arrowleft)="back.emit()"
      (keydown.pagedown)="forward.emit()"
      (keydown.pageup)="back.emit()">
      <h2 class="chapter-title">{{ title }}</h2>

      <!-- One element per paragraph, not one per page. The prose arrives with
           its breaks intact and this is the last place they could be lost. -->
      <p class="body" *ngFor="let paragraph of paragraphs">{{ paragraph }}</p>
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

  /**
   * The page, as its paragraphs.
   *
   * <p>Paragraphs rather than one string: the reading surface is the only thing
   * that knows how a paragraph should look, and a page handed over as a single
   * blob has already thrown away the one fact needed to draw it. A page begins
   * and ends wherever the measurement said, so the first and last of these are
   * routinely partial paragraphs — that is correct, not a rounding error.</p>
   */
  @Input() paragraphs: readonly string[] = [];

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

  /**
   * Words on this page before the selection begins, counted a paragraph at a
   * time.
   *
   * <p>Not one range over the whole surface, for two reasons that both silently
   * shift every offset. <c>Range.toString()</c> concatenates with no separator,
   * so the last word of one paragraph and the first of the next arrive as one
   * word and the count comes up short. And the surface's first child is the
   * chapter heading, whose words are not in the chapter text at all — counting
   * them pushed every offset along by the length of the title.</p>
   *
   * <p>Both meant a passage was analysed with the wrong words around it, and
   * neither is visible from the reader's side: the analysis is of real prose
   * from the right chapter, just not quite the prose that was highlighted.</p>
   */
  private wordsBefore(selection: Selection): number {
    const surface = this.surface?.nativeElement;
    if (!surface) return 0;

    const { startContainer, startOffset } = selection.getRangeAt(0);
    let words = 0;

    for (const paragraph of Array.from(surface.querySelectorAll('.body'))) {
      const upTo = document.createRange();
      upTo.selectNodeContents(paragraph);

      const where = upTo.comparePoint(startContainer, startOffset);

      // The selection starts before this paragraph: nothing further counts.
      if (where < 0) break;

      // Inside it: count up to the selection and stop.
      if (where === 0) upTo.setEnd(startContainer, startOffset);

      words += countWords(upTo.toString());

      if (where === 0) break;
    }

    return words;
  }
}

/** Whitespace-separated, the definition the server counts by. */
function countWords(text: string): number {
  const trimmed = text.trim();

  return trimmed.length === 0 ? 0 : trimmed.split(/\s+/).length;
}

const FONT_STACKS: Record<string, string> = {
  serif: 'Georgia, "Iowan Old Style", "Times New Roman", serif',
  sans: '"Inter", "Helvetica Neue", Arial, sans-serif',
  mono: '"SF Mono", "Cascadia Mono", Menlo, monospace'
};

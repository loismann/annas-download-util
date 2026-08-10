import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { SectionInfo } from '../reader2.models';
import { sectionAt } from '../services/pagination';
import { ProsePipe } from '../prose.pipe';

/** Which section the reader asked about, and whether to pay again. */
export interface SectionRequest {
  index: number;
  force: boolean;
}

/**
 * A chapter's sections, and the summary of whichever one is open.
 *
 * <p>Sections exist so a reader can ask about the part they are actually in
 * rather than paying for a whole chapter. The component marks the section
 * containing the reader's word offset, because "which of these am I in" is
 * otherwise a calculation the reader has to do themselves.</p>
 */
@Component({
  selector: 'app-reader2-section-summary',
  standalone: true,
  imports: [CommonModule, MatIconModule, ProsePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="sections" aria-label="Sections">
      <button
        *ngFor="let section of sections; let i = index"
        type="button"
        class="section"
        [class.current]="i === openIndex"
        [class.here]="i === sectionHere"
        [attr.aria-current]="i === openIndex ? 'true' : null"
        title="Show this section's summary — written the first time you ask"
        (click)="open.emit({ index: i, force: false })">
        Section {{ i + 1 }}
        <span class="here-marker" *ngIf="i === sectionHere" title="You are reading here">•</span>
      </button>

      <p class="empty" *ngIf="sections.length === 0">This chapter has no sections yet.</p>
    </nav>

    <section class="summary" aria-live="polite" *ngIf="openIndex >= 0">
      <header>
        <h3>Section {{ openIndex + 1 }}</h3>
        <button
          type="button"
          class="regenerate"
          *ngIf="markdown && !busy"
          (click)="open.emit({ index: openIndex, force: true })"
          title="Summarise this again and pay for it again">
          <mat-icon>refresh</mat-icon>
        </button>
      </header>

      <div class="prose" *ngIf="markdown" [innerHTML]="markdown | prose"></div>

      <p class="idle" *ngIf="!markdown && !busy">
        Nothing summarised for this section yet.
      </p>
    </section>
  `,
  styleUrl: './section-summary.component.scss'
})
export class SectionSummaryComponent {
  @Input() sections: SectionInfo[] = [];

  /** The section being shown, or -1 when none is open. */
  @Input() openIndex = -1;

  @Input() markdown: string | null = null;
  @Input() busy = false;

  /** Where the reader is, in words from the start of the chapter. */
  @Input() wordOffset = 0;

  @Output() open = new EventEmitter<SectionRequest>();

  /** The section the reader's offset falls in, or -1 when it falls in none. */
  get sectionHere(): number {
    return sectionAt(this.sections, this.wordOffset);
  }
}

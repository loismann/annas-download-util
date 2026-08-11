import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Busy } from '../services/reader-tasks';
import { AnalysisKind } from '../services/analysis-store';
import { PassageSelection } from '../reader2.models';
import { ProsePipe } from '../prose.pipe';

/**
 * The right-hand pane: whatever was last generated, and the buttons that
 * generate.
 *
 * <p>Every button here spends money, so every one is an explicit control with a
 * name that says what it does. Nothing on this panel fires on open, on scroll,
 * or because the reader turned a page.</p>
 */
@Component({
  selector: 'app-reader2-analysis-panel',
  standalone: true,
  imports: [CommonModule, MatIconModule, ProsePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!--
      A selection buys nothing on its own. Reader I sent the passage the moment
      the mouse came up, so a stray drag while reading was a billed request; here
      the reader is offered two named choices and may take neither.
    -->
    <section class="selection" *ngIf="selection">
      <p class="quoted">“{{ selection.text }}”</p>
      <div class="selection-actions">
        <button
          type="button"
          [disabled]="!!busy || !isPassage"
          [title]="isPassage ? '' : 'Select a phrase — one word is a word, not a passage'"
          (click)="analyseSelection.emit(selection)">
          <mat-icon>psychology_alt</mat-icon> Explain this passage
        </button>
        <button type="button" (click)="fileSelection.emit(selection)">
          <mat-icon>bookmark_add</mat-icon> Add to vocabulary
        </button>
        <button type="button" class="dismiss" aria-label="Dismiss" (click)="dismissSelection.emit()">
          <mat-icon>close</mat-icon>
        </button>
      </div>

      <!-- Said rather than merely greyed out: a disabled button with no reason
           beside it is indistinguishable from a broken one. -->
      <p class="nudge" *ngIf="!isPassage">
        One word is a vocabulary term. Select a phrase to have it explained.
      </p>
    </section>

    <header class="controls">
      <button type="button" (click)="generate.emit('summary')" [disabled]="!!busy">
        <mat-icon>menu_book</mat-icon> Summarise chapter
      </button>

      <!-- Reader I's name, kept: readers know the button by it. -->
      <button type="button" (click)="generate.emit('explain-simply')" [disabled]="!!busy">
        <mat-icon>self_improvement</mat-icon> I'm a Dummy
      </button>

      <button
        type="button"
        class="regenerate"
        *ngIf="markdown && !busy"
        (click)="regenerate.emit(kind)"
        title="Generate this again and pay for it again">
        <mat-icon>refresh</mat-icon>
      </button>
    </header>

    <!--
      Offered, never taken. A summary written under older wording is still a
      summary of prose that has not changed, so it is served as it always was and
      the reader decides whether the newer wording is worth paying for.
    -->
    <p class="stale" *ngIf="stale && markdown && !busy">
      Written under an earlier version of the prompt.
      <button type="button" class="link" (click)="regenerate.emit(kind)">
        Generate it again
      </button>
    </p>

    <section class="output" aria-live="polite">
      <div class="busy" *ngIf="busy">
        <p>{{ busy.what }}…</p>
        <p class="step" *ngIf="busy.step">
          {{ busy.step.message }}
          <span *ngIf="busy.step.totalSteps > 1">
            ({{ busy.step.stepNumber }}/{{ busy.step.totalSteps }})
          </span>
        </p>
      </div>

      <p class="failed" *ngIf="error">{{ error }}</p>

      <div class="prose" *ngIf="markdown && !busy" [innerHTML]="markdown | prose"></div>

      <p class="idle" *ngIf="!markdown && !busy && !error">
        Nothing generated for this chapter yet.
      </p>
    </section>
  `,
  styleUrl: './analysis-panel.component.scss'
})
export class AnalysisPanelComponent {
  @Input() kind: AnalysisKind = 'summary';

  /** Whether what is shown predates the current prompt. Decided by the server. */
  @Input() stale = false;
  @Input() markdown: string | null = null;
  @Input() busy: Busy | null = null;
  @Input() error: string | null = null;

  /** What the reader has highlighted, and not yet decided what to do with. */
  @Input() selection: PassageSelection | null = null;

  /**
   * Whether what is highlighted is a passage at all.
   *
   * <p>One word is not. Passage analysis is a paid call that reads a phrase in
   * the context of the paragraph around it, and a single word asked of it comes
   * back as a definition — which is what <i>Add to vocabulary</i> is for, and
   * free. The two buttons sit side by side, so the cheap one has to be the
   * obvious answer to the cheap question.</p>
   *
   * <p>Split on whitespace rather than counted in characters: a long German
   * compound is still one word, and "on the" is still a phrase.</p>
   */
  protected get isPassage(): boolean {
    const words = this.selection?.text.trim().split(/\s+/).filter(Boolean) ?? [];

    return words.length >= MIN_PASSAGE_WORDS;
  }

  @Output() generate = new EventEmitter<AnalysisKind>();
  @Output() regenerate = new EventEmitter<AnalysisKind>();
  @Output() analyseSelection = new EventEmitter<PassageSelection>();
  @Output() fileSelection = new EventEmitter<PassageSelection>();
  @Output() dismissSelection = new EventEmitter<void>();
}

/** Two: the smallest selection that is a phrase rather than a term. */
const MIN_PASSAGE_WORDS = 2;

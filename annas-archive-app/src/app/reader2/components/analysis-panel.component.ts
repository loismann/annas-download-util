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
        <button type="button" [disabled]="!!busy" (click)="analyseSelection.emit(selection)">
          <mat-icon>psychology_alt</mat-icon> Explain this passage
        </button>
        <button type="button" (click)="fileSelection.emit(selection)">
          <mat-icon>bookmark_add</mat-icon> Add to vocabulary
        </button>
        <button type="button" class="dismiss" aria-label="Dismiss" (click)="dismissSelection.emit()">
          <mat-icon>close</mat-icon>
        </button>
      </div>
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
  @Input() markdown: string | null = null;
  @Input() busy: Busy | null = null;
  @Input() error: string | null = null;

  /** What the reader has highlighted, and not yet decided what to do with. */
  @Input() selection: PassageSelection | null = null;

  @Output() generate = new EventEmitter<AnalysisKind>();
  @Output() regenerate = new EventEmitter<AnalysisKind>();
  @Output() analyseSelection = new EventEmitter<PassageSelection>();
  @Output() fileSelection = new EventEmitter<PassageSelection>();
  @Output() dismissSelection = new EventEmitter<void>();
}

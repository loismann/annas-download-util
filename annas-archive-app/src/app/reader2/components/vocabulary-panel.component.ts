import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Definition, TermState, VocabularyTerm } from '../reader2.models';

/** Filing a term, or moving one between the two lists. */
export interface TermChange {
  term: string;
  state: TermState;
  definition?: string;
}

/** A card the reader asked for, made from a definition already on screen. */
export interface NewCard {
  term: string;
  definition: string;
}

/** The open deep dive: which term, and the HTML the server wrote. */
export interface OpenDive {
  term: string;
  html: string;
}

/**
 * The hard words in this passage, and the reader's own two lists.
 *
 * <p>The passage list is generated and costs a click; the reader's lists are
 * rows and cost nothing. Both are here because the whole point is moving a word
 * from the first to the second — a word marked known stops being offered, which
 * is the only reason marking it is worth doing.</p>
 *
 * <p>Deep-dive HTML comes from the server and is rendered with
 * <c>innerHTML</c>. Angular sanitises it, which is what makes that safe; the
 * prompt's image rules are what make it useful.</p>
 */
@Component({
  selector: 'app-reader2-vocabulary-panel',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="controls">
      <button type="button" [disabled]="busy" (click)="generate.emit(false)">
        <mat-icon>spellcheck</mat-icon> This section
      </button>
      <!-- One call per section, so it is named for the chapter it will bill for. -->
      <button type="button" [disabled]="busy" (click)="generateChapter.emit(false)">
        <mat-icon>menu_book</mat-icon> Whole chapter
      </button>
      <button
        type="button"
        class="regenerate"
        *ngIf="terms.length > 0 && !busy"
        title="Find them again and pay for it again"
        (click)="generate.emit(true)">
        <mat-icon>refresh</mat-icon>
      </button>
    </header>

    <ul class="found" *ngIf="terms.length > 0">
      <li *ngFor="let definition of terms">
        <div class="term">
          <strong>{{ definition.term }}</strong>
          <span class="actions">
            <button type="button" title="I know this word"
              (click)="file.emit({ term: definition.term, state: 'Known', definition: definition.meaning })">
              <mat-icon>check</mat-icon>
            </button>
            <button type="button" title="Keep studying this word"
              (click)="file.emit({ term: definition.term, state: 'Studying', definition: definition.meaning })">
              <mat-icon>bookmark_add</mat-icon>
            </button>
            <button type="button" title="Tell me more" (click)="learnMore.emit(definition.term)">
              <mat-icon>travel_explore</mat-icon>
            </button>
            <button type="button" title="Make a flashcard"
              (click)="makeCard.emit({ term: definition.term, definition: definition.meaning })">
              <mat-icon>style</mat-icon>
            </button>
          </span>
        </div>
        <p class="meaning">{{ definition.meaning }}</p>
      </li>
    </ul>

    <p class="idle" *ngIf="terms.length === 0 && !busy">
      Nothing found for this passage yet.
    </p>

    <section class="dive" *ngIf="dive" aria-live="polite">
      <header>
        <h3>{{ dive.term }}</h3>
        <button type="button" aria-label="Close" (click)="closeDive.emit()">
          <mat-icon>close</mat-icon>
        </button>
      </header>
      <div class="dive-body" [innerHTML]="dive.html"></div>
    </section>

    <section class="filed">
      <button type="button" class="forget-book" (click)="forgetBook.emit()">
        <mat-icon>delete_outline</mat-icon> Forget this book’s vocabulary
      </button>
      <details *ngFor="let list of lists">
        <summary>
          {{ list.name }} <span class="count">{{ list.terms.length }}</span>
          <button
            type="button"
            class="clear"
            *ngIf="list.terms.length > 0"
            [title]="'Forget every ' + list.name.toLowerCase() + ' word'"
            (click)="clear.emit(list.state); $event.preventDefault()">
            <mat-icon>delete_sweep</mat-icon>
          </button>
        </summary>
        <ul>
          <li *ngFor="let filed of list.terms">
            <span>{{ filed.term }}</span>
            <button type="button" aria-label="Forget this word" (click)="forget.emit(filed.term)">
              <mat-icon>close</mat-icon>
            </button>
          </li>
        </ul>
      </details>
    </section>
  `,
  styleUrl: './vocabulary-panel.component.scss'
})
export class VocabularyPanelComponent {
  @Input() terms: Definition[] = [];
  @Input() known: VocabularyTerm[] = [];
  @Input() studying: VocabularyTerm[] = [];
  @Input() dive: OpenDive | null = null;
  @Input() busy = false;

  @Output() generate = new EventEmitter<boolean>();
  @Output() generateChapter = new EventEmitter<boolean>();
  @Output() makeCard = new EventEmitter<NewCard>();
  @Output() forgetBook = new EventEmitter<void>();
  @Output() file = new EventEmitter<TermChange>();
  @Output() forget = new EventEmitter<string>();
  @Output() clear = new EventEmitter<TermState>();
  @Output() learnMore = new EventEmitter<string>();
  @Output() closeDive = new EventEmitter<void>();

  /** Both filed lists render identically, so they are data rather than markup. */
  protected get lists(): { name: string; state: TermState; terms: VocabularyTerm[] }[] {
    return [
      { name: 'Studying', state: 'Studying', terms: this.studying },
      { name: 'Known', state: 'Known', terms: this.known }
    ];
  }
}

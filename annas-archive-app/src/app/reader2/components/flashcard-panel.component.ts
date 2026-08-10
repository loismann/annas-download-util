import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Flashcard } from '../reader2.models';

/**
 * This book's deck.
 *
 * <p>Cards are made from words the reader already met, so nothing here reaches
 * a model — the definition was paid for once, when the word was looked up.</p>
 *
 * <p>Which card is face-up is local state and stays local: it is not worth a
 * round trip, and it should not survive a reload, because a reader coming back
 * to a deck wants to be tested rather than shown the answer they left on
 * screen.</p>
 */
@Component({
  selector: 'app-reader2-flashcard-panel',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="controls">
      <h3>Cards <span class="count">{{ cards.length }}</span></h3>
      <button
        type="button"
        class="clear"
        *ngIf="cards.length > 0"
        title="Clear the whole deck"
        (click)="clear.emit()">
        <mat-icon>delete_sweep</mat-icon>
      </button>
    </header>

    <ul class="deck" *ngIf="cards.length > 0">
      <li *ngFor="let card of cards">
        <button
          type="button"
          class="card"
          [class.turned]="turned() === card.norm"
          [attr.aria-expanded]="turned() === card.norm"
          (click)="turn(card.norm)">
          <span class="face">{{ card.term }}</span>
          <span class="back" *ngIf="turned() === card.norm">{{ card.definition }}</span>
          <span class="prompt" *ngIf="turned() !== card.norm">Show the definition</span>
        </button>
        <button type="button" class="remove" aria-label="Remove this card" (click)="remove.emit(card.term)">
          <mat-icon>close</mat-icon>
        </button>
      </li>
    </ul>

    <p class="idle" *ngIf="cards.length === 0">
      No cards yet. Add one from a word you have looked up.
    </p>
  `,
  styleUrl: './flashcard-panel.component.scss'
})
export class FlashcardPanelComponent {
  @Input() cards: Flashcard[] = [];

  @Output() remove = new EventEmitter<string>();
  @Output() clear = new EventEmitter<void>();

  /** The normalised term of the card currently face-up, if any. */
  protected readonly turned = signal<string | null>(null);

  protected turn(norm: string): void {
    this.turned.update(current => (current === norm ? null : norm));
  }
}

import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

/**
 * The full-width "Add to Favorites" button used by every media edit dialog.
 * Purely presentational — the parent owns the favoritedBy state and whatever
 * API call (books vs movies vs TV vs audiobooks differ) persists the toggle,
 * including any optimistic-update/revert handling.
 */
@Component({
  selector: 'app-favorite-toggle',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  template: `
    <button
      type="button"
      class="favorite-toggle-btn"
      [class.favorited]="favorited"
      (click)="toggled.emit(!favorited)"
    >
      <mat-icon>{{ favorited ? 'favorite' : 'favorite_border' }}</mat-icon>
      {{ favorited ? 'Favorited' : 'Add to Favorites' }}
    </button>
  `,
  styles: [`
    .favorite-toggle-btn {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      width: 100%;
      border: 1px solid #fda4af;
      background: #ffffff;
      color: #e11d48;
      padding: 8px 16px;
      border-radius: 8px;
      font-size: 0.9rem;
      font-weight: 600;
      cursor: pointer;
      transition: all 0.15s ease;
      margin-bottom: 16px;
    }
    .favorite-toggle-btn:hover {
      background: #fff1f2;
    }
    .favorite-toggle-btn.favorited {
      background: #e11d48;
      color: #ffffff;
      border-color: #e11d48;
    }
  `]
})
export class FavoriteToggleComponent {
  @Input() favorited = false;
  /** Emits the desired new state (true = favorite, false = unfavorite). */
  @Output() toggled = new EventEmitter<boolean>();
}

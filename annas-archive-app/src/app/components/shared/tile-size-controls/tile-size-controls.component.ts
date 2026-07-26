import { Component, EventEmitter, Input, Output, ViewEncapsulation } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export type TileSize = 'small' | 'medium' | 'large';

/**
 * The small/medium/large tile-size buttons plus optional favorites filter —
 * previously copy-pasted across the library, video-library, media-library,
 * and audiobooks pages. Encapsulation off + unchanged class names
 * (.tile-size-controls/.tile-size-btn/.favorites-filter-btn) so each page's
 * existing CSS keeps styling it exactly as before.
 */
@Component({
  selector: 'app-tile-size-controls',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule],
  encapsulation: ViewEncapsulation.None,
  host: { style: 'display: contents' },
  template: `
    <div class="tile-size-controls">
      <button
        mat-icon-button
        type="button"
        class="tile-size-btn"
        [class.active]="tileSize === 'small'"
        (click)="tileSizeChange.emit('small')"
        aria-label="Small tiles"
      >
        <mat-icon>view_comfy</mat-icon>
      </button>
      <button
        mat-icon-button
        type="button"
        class="tile-size-btn"
        [class.active]="tileSize === 'medium'"
        (click)="tileSizeChange.emit('medium')"
        aria-label="Medium tiles"
      >
        <mat-icon>view_module</mat-icon>
      </button>
      <button
        mat-icon-button
        type="button"
        class="tile-size-btn"
        [class.active]="tileSize === 'large'"
        (click)="tileSizeChange.emit('large')"
        aria-label="Large tiles"
      >
        <mat-icon>view_agenda</mat-icon>
      </button>
      <button
        *ngIf="showFilter"
        mat-icon-button
        type="button"
        [class]="filterStyle === 'bookmark' ? 'bookmark-filter-btn' : 'favorites-filter-btn'"
        [class.active]="filterActive"
        (click)="filterToggle.emit()"
        [attr.aria-label]="filterAriaLabel"
      >
        <mat-icon>{{ filterIcon }}</mat-icon>
      </button>
    </div>
  `
})
export class TileSizeControlsComponent {
  @Input() tileSize: TileSize = 'medium';
  /** Optional trailing filter toggle — 'favorite' (heart) or 'bookmark' variant. */
  @Input() showFilter = false;
  @Input() filterStyle: 'favorite' | 'bookmark' = 'favorite';
  @Input() filterActive = false;
  @Input() filterAriaLabel = 'Filter favorites';
  @Output() tileSizeChange = new EventEmitter<TileSize>();
  @Output() filterToggle = new EventEmitter<void>();

  get filterIcon(): string {
    return this.filterStyle === 'bookmark'
      ? (this.filterActive ? 'bookmark' : 'bookmark_border')
      : (this.filterActive ? 'favorite' : 'favorite_border');
  }
}

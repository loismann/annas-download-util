import { Component, EventEmitter, Input, Output, ViewEncapsulation } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * The grid tile shared by the media library (TV + movies) and audiobooks —
 * poster, title, overlay edit/favorite/delete buttons, bulk-select checkbox,
 * and owner badge. The status area between title and badge varies per media
 * type (progress bars, meta lines, release-picker links), so it's projected
 * content rather than configuration.
 *
 * Encapsulation is deliberately off and the class names are unchanged
 * (.tile/.poster/.tile-title/...): the host pages already carry the tile CSS,
 * including grid-size modifiers like `.tiles-grid.small .tile-title`, and this
 * component slots into those selectors as-is. Consumers put `class="tile"`,
 * size/selection classes, and the tile click handler on the host element.
 */
@Component({
  selector: 'app-media-tile',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule],
  encapsulation: ViewEncapsulation.None,
  // Custom elements default to display:inline; the tile was previously a div.
  // Grid layout blockifies children anyway, but pin it for robustness.
  host: { style: 'display: block' },
  template: `
    <div *ngIf="bulkMode" class="bulk-checkbox-wrapper">
      <mat-checkbox
        [checked]="bulkSelected"
        (change)="bulkToggle.emit()"
        (click)="$event.stopPropagation()"
      ></mat-checkbox>
    </div>
    <button
      *ngIf="!bulkMode && showEdit"
      mat-icon-button
      class="edit-btn"
      matTooltip="Edit genres &amp; owners"
      (click)="emitStopped(edit, $event)"
    >
      <mat-icon>edit</mat-icon>
    </button>
    <button
      *ngIf="!bulkMode && showFavorite"
      mat-icon-button
      class="favorite-btn"
      [class.favorited]="favorited"
      [matTooltip]="favorited ? 'Remove from favorites' : 'Add to favorites'"
      (click)="emitStopped(favoriteToggle, $event)"
    >
      <mat-icon>{{ favorited ? 'favorite' : 'favorite_border' }}</mat-icon>
    </button>
    <button
      *ngIf="!bulkMode && showDelete"
      mat-icon-button
      class="delete-btn"
      [matTooltip]="deleteTooltip"
      (click)="emitStopped(remove, $event)"
    >
      <mat-icon>delete</mat-icon>
    </button>
    <img class="poster" [src]="posterUrl" [alt]="title" />
    <div class="tile-title">{{ title }}</div>
    <div class="tile-status">
      <ng-content></ng-content>
    </div>
    <ng-content select="[tile-extra]"></ng-content>
    <div class="owner-badge" *ngIf="ownerLabel">{{ ownerLabel }}</div>
  `
})
export class MediaTileComponent {
  @Input() title = '';
  @Input() posterUrl = '';
  @Input() favorited = false;
  @Input() showEdit = true;
  @Input() showFavorite = true;
  @Input() showDelete = false;
  @Input() deleteTooltip = 'Delete';
  @Input() bulkMode = false;
  @Input() bulkSelected = false;
  /** Rendered as the corner badge when non-empty; pass null/'' to omit. */
  @Input() ownerLabel: string | null = null;

  @Output() edit = new EventEmitter<Event>();
  @Output() favoriteToggle = new EventEmitter<Event>();
  @Output() remove = new EventEmitter<Event>();
  @Output() bulkToggle = new EventEmitter<void>();

  /** Overlay buttons must never trigger the tile's own click (open/play). */
  emitStopped(emitter: EventEmitter<Event>, event: Event): void {
    event.stopPropagation();
    emitter.emit(event);
  }
}

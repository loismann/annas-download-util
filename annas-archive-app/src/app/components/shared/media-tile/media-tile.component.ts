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
 * Owns all tile styling below. The page's emulated-encapsulation CSS cannot
 * reach into this template (its selectors are compiled against the page's own
 * elements), so every rule for .poster/.tile-title/etc. must live here —
 * encapsulation is off and each selector is prefixed with `app-media-tile` so
 * the styles apply globally to this element only. Projected content (progress
 * bars, meta lines) is authored in the page template and stays styled by the
 * page. Consumers put `class="tile"`, state classes (.not-ready,
 * .bulk-selected), and the tile click handler on the host element.
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
    <!-- loading="lazy": grids can hold hundreds of tiles (991 audiobooks) and
         eagerly fetching every proxied cover at once stampedes the backend —
         the browser only fetches covers as they near the viewport. -->
    <img class="poster" [src]="posterUrl" [alt]="title" loading="lazy" decoding="async" />
    <div class="tile-title">{{ title }}</div>
    <div class="tile-status">
      <ng-content></ng-content>
    </div>
    <ng-content select="[tile-extra]"></ng-content>
    <div class="owner-badge" *ngIf="ownerLabel">{{ ownerLabel }}</div>
  `,
  styles: [`
    app-media-tile {
      position: relative;
      cursor: pointer;
    }

    app-media-tile.not-ready {
      cursor: default;
    }

    app-media-tile.not-ready .poster {
      opacity: 0.5;
    }

    app-media-tile.bulk-selected {
      outline: 3px solid #3f51b5;
      outline-offset: 2px;
      border-radius: 4px;
    }

    app-media-tile .bulk-checkbox-wrapper {
      position: absolute;
      top: 8px;
      right: 8px;
      z-index: 10;
      background: #ffffff;
      border-radius: 4px;
      padding: 4px;
      box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
    }

    app-media-tile .poster {
      width: 100%;
      aspect-ratio: 2 / 3;
      object-fit: cover;
      border-radius: 4px;
      display: block;
      background: #f1f5f9;
    }

    app-media-tile .tile-title {
      margin-top: 0.5rem;
      font-weight: 500;
      font-size: 0.95rem;
      line-height: 1.3;
      /* Always reserves 2 lines, even for a short one-line title, so every
         tile's status/owner-badge row starts at the same Y position. */
      min-height: calc(1.3em * 2);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }

    app-media-tile .tile-status {
      /* Reserves the same height whether it holds one line of text or a
         progress bar + label, keeping owner badges aligned across a row. */
      min-height: 2.3em;
      display: flex;
      flex-direction: column;
      justify-content: center;
      gap: 3px;
    }

    app-media-tile .delete-btn,
    app-media-tile .edit-btn,
    app-media-tile .favorite-btn {
      position: absolute;
      top: 4px;
      z-index: 1;
      background: rgba(0, 0, 0, 0.55);
      color: #fff;
      opacity: 0;
      transition: opacity 0.15s ease;
    }

    app-media-tile .delete-btn {
      right: 4px;
    }

    app-media-tile .edit-btn {
      left: 4px;
    }

    app-media-tile .favorite-btn {
      left: 48px;
    }

    app-media-tile:hover .delete-btn,
    app-media-tile:hover .edit-btn,
    app-media-tile:hover .favorite-btn {
      opacity: 1;
    }

    app-media-tile .favorite-btn.favorited {
      color: #f87171;
    }

    app-media-tile .delete-btn:hover {
      background: rgba(211, 47, 47, 0.85);
    }

    app-media-tile .edit-btn:hover {
      background: rgba(63, 81, 181, 0.85);
    }

    app-media-tile .favorite-btn:hover {
      background: rgba(190, 24, 93, 0.85);
    }

    app-media-tile .owner-badge {
      display: inline-block;
      margin-top: 0.4rem;
      border: 1px solid #cbd5f5;
      background: #eef2ff;
      color: #3f51b5;
      padding: 2px 10px;
      border-radius: 999px;
      font-size: 0.72rem;
    }
  `]
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

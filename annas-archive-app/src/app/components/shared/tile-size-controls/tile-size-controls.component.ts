import { Component, EventEmitter, Input, Output, ViewEncapsulation } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export type TileSize = 'small' | 'medium' | 'large';

/**
 * The small/medium/large tile-size buttons plus optional favorites filter —
 * previously copy-pasted across the library, video-library, media-library,
 * and audiobooks pages.
 *
 * Owns its styling: page CSS (emulated encapsulation) cannot reach into this
 * template, so the rules live here with encapsulation off, every selector
 * prefixed with `app-tile-size-controls` to avoid leaking. Default look is the
 * tall 44x56 buttons used by the library/video pages; add `class="compact"`
 * on the host for the 40x40 flavor used by media-library/audiobooks.
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
  `,
  styles: [`
    app-tile-size-controls .tile-size-controls {
      display: flex;
      align-items: center;
      gap: 4px;
      margin-right: 12px;
    }

    app-tile-size-controls .tile-size-btn,
    app-tile-size-controls .favorites-filter-btn,
    app-tile-size-controls .bookmark-filter-btn {
      width: 44px;
      height: 56px;
      /* mat-icon-button's hover/focus state layer defaults to Material's own
         size token, independent of the button's box — without this override
         the layer mismatches our custom size and can bleed past the button's
         edge into ancestors that clip overflow. */
      --mdc-icon-button-state-layer-size: 44px;
      border: 1px solid #cbd5f5;
      border-radius: 10px;
      color: #64748b;
      background: #ffffff;
      transition: all 0.15s ease;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 0;
    }

    app-tile-size-controls .favorites-filter-btn,
    app-tile-size-controls .bookmark-filter-btn {
      margin-left: 8px;
    }

    app-tile-size-controls .tile-size-btn:hover,
    app-tile-size-controls .favorites-filter-btn:hover,
    app-tile-size-controls .bookmark-filter-btn:hover {
      background: #eef2ff;
      color: #3f51b5;
    }

    app-tile-size-controls .tile-size-btn.active,
    app-tile-size-controls .favorites-filter-btn.active,
    app-tile-size-controls .bookmark-filter-btn.active {
      background: #3f51b5;
      color: #ffffff;
      border-color: #3f51b5;
    }

    app-tile-size-controls .tile-size-btn mat-icon,
    app-tile-size-controls .favorites-filter-btn mat-icon,
    app-tile-size-controls .bookmark-filter-btn mat-icon {
      font-size: 22px;
      width: 22px;
      height: 22px;
      line-height: 22px;
      /* The outline heart overflows this box and used to be clipped by
         mat-icon's own overflow:hidden. Unclipped globally now — see the rule
         in styles.scss, which explains why the same glyph is fine at one size
         and cut at another. */
    }

    /* 40x40 flavor (media-library / audiobooks toolbars) */
    app-tile-size-controls.compact .tile-size-controls {
      margin-right: 0;
    }

    app-tile-size-controls.compact .tile-size-btn,
    app-tile-size-controls.compact .favorites-filter-btn,
    app-tile-size-controls.compact .bookmark-filter-btn {
      width: 40px;
      height: 40px;
      --mdc-icon-button-state-layer-size: 40px;
      margin-left: 0;
    }

    app-tile-size-controls.compact .tile-size-btn mat-icon,
    app-tile-size-controls.compact .favorites-filter-btn mat-icon,
    app-tile-size-controls.compact .bookmark-filter-btn mat-icon {
      font-size: 24px;
      width: 24px;
      height: 24px;
      line-height: 24px;
    }
  `]
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

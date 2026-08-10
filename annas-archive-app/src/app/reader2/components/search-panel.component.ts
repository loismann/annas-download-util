import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { SearchHit } from '../reader2.models';

/** Where a hit sends the reader: a chapter and a word offset within it. */
export interface HitTarget {
  chapter: number;
  wordOffset: number;
}

/**
 * Full-text search over the extracted book.
 *
 * <p>No model, no spend, no debounce-on-keystroke — the reader presses enter.
 * Reader I's search fired per keystroke against an endpoint that also summarised,
 * which is how a search could cost money.</p>
 */
@Component({
  selector: 'app-reader2-search-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="search" (ngSubmit)="submit()">
      <label class="field">
        <mat-icon aria-hidden="true">search</mat-icon>
        <input
          type="search"
          name="query"
          [(ngModel)]="query"
          [ngModelOptions]="{ standalone: true }"
          [attr.minlength]="minLength"
          [attr.maxlength]="maxLength"
          placeholder="Search this book"
          aria-label="Search this book" />
      </label>
      <button type="submit" [disabled]="busy || query.trim().length < minLength">Search</button>
    </form>

    <p class="hint" *ngIf="query.trim().length > 0 && query.trim().length < minLength">
      At least {{ minLength }} characters.
    </p>

    <ul class="hits" *ngIf="hits.length > 0">
      <li *ngFor="let hit of hits">
        <button type="button" (click)="jump.emit({ chapter: hit.chapterId, wordOffset: hit.firstWordOffset })">
          <span class="where">
            {{ hit.chapterTitle }}
            <span class="count">{{ hit.matchCount }}</span>
          </span>
          <span class="snippet">{{ hit.snippet }}</span>
        </button>
      </li>
    </ul>

    <p class="empty" *ngIf="searched && hits.length === 0 && !busy">
      Nothing found for “{{ searched }}”.
    </p>
  `,
  styleUrl: './search-panel.component.scss'
})
export class SearchPanelComponent {
  @Input() hits: SearchHit[] = [];
  @Input() busy = false;

  /** The last query actually run, so "nothing found" names the right thing. */
  @Input() searched: string | null = null;

  /** Mirrors the server's `Reader2:Search:*` bounds. */
  @Input() minLength = 3;
  @Input() maxLength = 500;

  @Output() search = new EventEmitter<string>();
  @Output() jump = new EventEmitter<HitTarget>();

  protected query = '';

  protected submit(): void {
    const query = this.query.trim();
    if (query.length >= this.minLength) this.search.emit(query);
  }
}

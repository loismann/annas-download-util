import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActorGroup, ActorTier, StoryThread, StoryVocabulary } from '../reader2.models';
import { ALL_TIERS, CastFilter, NO_FILTER, toggleTier } from '../services/cast-filter';

/**
 * The controls over who is shown, above both the cast list and the map.
 *
 * <p>One bar rather than one per view: the filter is a statement about the book,
 * not about the tab, and a reader who narrows to one faction and switches to the
 * map has not asked to see everybody again.</p>
 *
 * <p>A presenter — it holds no state. What is selected lives in the panel,
 * because both views need it.</p>
 */
@Component({
  selector: 'app-reader2-cast-filter',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters">
      <div class="tiers" role="group" attr.aria-label="Filter {{ vocabulary.actors }} by importance">
        <button
          *ngFor="let tier of allTiers"
          type="button"
          class="chip"
          [class.on]="filter.tiers.includes(tier)"
          [attr.aria-pressed]="filter.tiers.includes(tier)"
          (click)="change.emit(toggle(tier))">
          {{ tier }}
        </button>
      </div>

      <label class="pick" *ngIf="groups.length">
        <span class="visually-hidden">{{ vocabulary.groups }}</span>
        <select
          [value]="filter.groupId ?? ''"
          (change)="change.emit(withGroup($any($event.target).value))">
          <option value="">All {{ vocabulary.groups.toLowerCase() }}</option>
          <option *ngFor="let group of groups" [value]="group.id">{{ group.name }}</option>
        </select>
      </label>

      <label class="pick" *ngIf="threads.length">
        <span class="visually-hidden">{{ vocabulary.threads }}</span>
        <select
          [value]="filter.threadId ?? ''"
          (change)="change.emit(withThread($any($event.target).value))">
          <option value="">All {{ vocabulary.threads.toLowerCase() }}</option>
          <option *ngFor="let thread of threads" [value]="thread.id">{{ thread.name }}</option>
        </select>
      </label>

      <button
        type="button"
        class="chip"
        [class.on]="filter.hereOnly"
        [attr.aria-pressed]="filter.hereOnly"
        (click)="change.emit(withHereOnly())">
        In this chapter
      </button>

      <!--
        The door back. Somebody the reader has hidden is off the map and below
        the default tiers, so without this there is no way to reach them again —
        which would make hiding a deletion that lied about being one.
      -->
      <button
        *ngIf="hiddenCount > 0"
        type="button"
        class="chip hidden-chip"
        [class.on]="filter.hiddenOnly"
        [attr.aria-pressed]="filter.hiddenOnly"
        (click)="change.emit(withHiddenOnly())">
        Hidden from map ({{ hiddenCount }})
      </button>
    </div>

    <!--
      The count is the point of the default view: it says there are more, and
      offers the one press that shows them.
    -->
    <p class="hidden-note" *ngIf="notShown > 0 && !filter.hiddenOnly">
      {{ notShown }} not shown.
      <button type="button" class="link" (click)="change.emit(everybody)">Show everybody</button>
    </p>

    <p class="hidden-note" *ngIf="filter.hiddenOnly">
      Showing only what you have hidden from the map. Open one to put it back.
      <button type="button" class="link" (click)="change.emit(withHiddenOnly())">Done</button>
    </p>
  `,
  styleUrl: './cast-filter.component.scss'
})
export class CastFilterComponent {
  @Input({ required: true }) filter!: CastFilter;
  @Input({ required: true }) vocabulary!: StoryVocabulary;
  @Input() groups: ActorGroup[] = [];
  @Input() threads: StoryThread[] = [];

  /** How many the filter is keeping back. The reason the default is safe. */
  @Input() notShown = 0;

  /** How many the reader has hidden from the map — a different thing entirely. */
  @Input() hiddenCount = 0;

  @Output() change = new EventEmitter<CastFilter>();

  protected readonly allTiers = ALL_TIERS;
  protected readonly everybody = NO_FILTER;

  protected toggle(tier: ActorTier): CastFilter {
    return toggleTier(this.filter, tier);
  }

  // The next three build the changed filter here rather than in the template:
  // Angular's expression language has no object spread, and a presenter that
  // emits a whole filter has to construct one somewhere.

  protected withGroup(value: string): CastFilter {
    return { ...this.filter, groupId: pick(value) };
  }

  protected withThread(value: string): CastFilter {
    return { ...this.filter, threadId: pick(value) };
  }

  protected withHereOnly(): CastFilter {
    return { ...this.filter, hereOnly: !this.filter.hereOnly };
  }

  protected withHiddenOnly(): CastFilter {
    return { ...this.filter, hiddenOnly: !this.filter.hiddenOnly };
  }
}

/** Nulls the value the empty option carries, since a select gives back a string. */
function pick(value: string): string | null {
  return value === '' ? null : value;
}

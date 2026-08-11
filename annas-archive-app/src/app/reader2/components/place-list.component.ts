import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChapterInfo, Place } from '../reader2.models';
import { ChapterNamePipe } from '../chapter-name.pipe';
import { PlaceNode, branches, placeTree, visibleRows } from '../services/place-tree';

/**
 * Where the book has been, as the nesting it describes.
 *
 * <p><b>A tree, not a list.</b> A novel names forty places and a flat list of
 * them answers nothing — the question a reader has is "where was that", and the
 * only useful answer is the chain upward: this palace, on this continent, on this
 * world, in this cluster.</p>
 *
 * <p>Every branch closes, because the chain that makes one place findable makes
 * the whole list long. A shut branch says how many it is holding, so closing
 * something is never the same as losing it.</p>
 *
 * <p>Everything here is already stored and already paid for: places are written
 * when a chapter is ingested, on the back of a summary the reader asked for.</p>
 */
@Component({
  selector: 'app-reader2-place-list',
  standalone: true,
  imports: [CommonModule, ChapterNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="empty" *ngIf="!places.length">
      No places recorded yet. They are gathered from chapter summaries as you ask
      for them.
    </p>

    <div class="bar" *ngIf="places.length && collapsible().length">
      <button type="button" class="link" (click)="toggleAll()">
        {{ allShut() ? 'Expand all' : 'Collapse all' }}
      </button>
    </div>

    <ul class="places" *ngIf="places.length">
      <li
        *ngFor="let row of rows(); trackBy: byId"
        class="place"
        [class.branch]="row.children.length"
        [style.padding-left.rem]="row.depth * 1.1">
        <div class="head">
          <!-- The whole row is not the toggle: a place with no children has
               nothing to toggle, and a row that sometimes does nothing when
               clicked teaches the reader not to click it. -->
          <button
            *ngIf="row.children.length"
            type="button"
            class="twist"
            [attr.aria-expanded]="!shut().has(row.place.id)"
            [attr.aria-label]="(shut().has(row.place.id) ? 'Expand ' : 'Collapse ') + row.place.name"
            (click)="toggle(row.place.id)">
            {{ shut().has(row.place.id) ? '▸' : '▾' }}
          </button>

          <span class="twist empty-twist" *ngIf="!row.children.length" aria-hidden="true"></span>

          <span class="name">{{ row.place.name }}</span>
          <span class="kind">{{ row.place.kind.toLowerCase() }}</span>

          <span class="held" *ngIf="row.children.length && shut().has(row.place.id)">
            {{ row.total }} inside
          </span>
        </div>

        <div class="body">
          <p class="aka" *ngIf="row.place.aliases.length">
            also {{ row.place.aliases.join(', ') }}
          </p>

          <p class="what" *ngIf="row.place.description">{{ row.place.description }}</p>

          <p class="meta">
            <span>First seen in {{ row.place.firstSeenChapter | chapterName: chapters }}</span>
            <span>Last seen in {{ row.place.lastSeenChapter | chapterName: chapters }}</span>
          </p>
        </div>
      </li>
    </ul>
  `,
  styleUrl: './place-list.component.scss'
})
export class PlaceListComponent {
  @Input({ required: true }) set places(value: Place[]) {
    this.given.set(value);
  }

  get places(): Place[] {
    return this.given();
  }

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  private readonly given = signal<Place[]>([]);

  /**
   * Shut branches, by id — not open ones. A tree that arrives with a new world
   * in it should show the world, and remembering what was closed is the only
   * spelling of that which does not need every new place to be opted in.
   */
  protected readonly shut = signal<ReadonlySet<string>>(new Set());

  protected readonly tree = computed(() => placeTree(this.given()));
  protected readonly rows = computed(() => visibleRows(this.tree(), this.shut()));
  protected readonly collapsible = computed(() => branches(this.tree()));

  protected readonly allShut = computed(() => {
    const shut = this.shut();

    return this.collapsible().length > 0 && this.collapsible().every(id => shut.has(id));
  });

  protected byId(_at: number, row: PlaceNode): string {
    return row.place.id;
  }

  protected toggle(id: string): void {
    const next = new Set(this.shut());

    if (!next.delete(id)) next.add(id);

    this.shut.set(next);
  }

  protected toggleAll(): void {
    this.shut.set(this.allShut() ? new Set() : new Set(this.collapsible()));
  }
}

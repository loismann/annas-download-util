import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, ActorCorrection, ActorEdge, ActorGroup, ChapterInfo } from '../reader2.models';
import { ActorDossierComponent } from './actor-dossier.component';
import { ChapterEntry, ChapterLogComponent } from './chapter-log.component';
import { ChapterNamePipe } from '../chapter-name.pipe';

/**
 * Who somebody is, or how two people know each other, beside the map.
 *
 * <p><b>Everything here is already loaded and already paid for.</b> A dossier is
 * written when a chapter is ingested and an edge carries its own chapter-tagged
 * history, so answering "who is this" and "how do these two know each other"
 * costs nothing and reaches no model. Generating either on click would have been
 * the reader spending money by pointing at something, which is the behaviour
 * Reader II exists to remove.</p>
 *
 * <p>A presenter: it is given what to show and emits when it is dismissed. The
 * map decides what is selected.</p>
 */
@Component({
  selector: 'app-reader2-map-detail',
  standalone: true,
  imports: [CommonModule, ActorDossierComponent, ChapterLogComponent, ChapterNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="detail" *ngIf="actor || edge">
      <button type="button" class="close" (click)="dismiss.emit()" aria-label="Close">×</button>

      <ng-container *ngIf="actor as who">
        <h3>{{ who.canonicalName }}</h3>
        <p class="aka" *ngIf="who.aliases.length">also {{ who.aliases.join(', ') }}</p>

        <p class="badges">
          <span class="tier" [class]="'t-' + who.tier.toLowerCase()">{{ who.tier }}</span>
          <span *ngIf="who.role">{{ who.role }}</span>
        </p>

        <app-reader2-actor-dossier
          [actor]="who"
          [groups]="groups"
          [chapters]="chapters"
          [others]="others"
          (correct)="correct.emit({ actorId: who.id, correction: $event })"
          (hide)="hide.emit({ actorId: who.id, hidden: $event })" />
      </ng-container>

      <ng-container *ngIf="edge as tie">
        <h3>{{ fromName }} <span class="tie-type">{{ tie.type }}</span> {{ toName }}</h3>

        <p class="badges">
          <span>Since {{ tie.sinceChapter | chapterName: chapters }}</span>
          <span class="ended" *ngIf="tie.endedChapter !== null">
            Ended {{ tie.endedChapter | chapterName: chapters }}
          </span>
        </p>

        <!-- Chapter by chapter, because when something changed between two
             people is most of what "how do they know each other" means. -->
        <app-reader2-chapter-log
          *ngIf="tie.notes.length"
          [entries]="history"
          [chapters]="chapters" />

        <p class="none" *ngIf="!tie.notes.length">
          They are recorded as {{ tie.type }}, but no chapter has said yet what passed
          between them.
        </p>
      </ng-container>
    </aside>
  `,
  styleUrl: './map-detail.component.scss'
})
export class MapDetailComponent {
  @Input() actor: Actor | null = null;

  /** Held rather than read straight through, so the history is mapped once per edge. */
  @Input() set edge(value: ActorEdge | null) {
    this.held = value;
    this.history = (value?.notes ?? []).map(note => ({ chapter: note.chapter, what: note.what }));
  }

  get edge(): ActorEdge | null {
    return this.held;
  }

  private held: ActorEdge | null = null;

  protected history: ChapterEntry[] = [];

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  /** Resolved by the map, which holds the cast; this component looks nothing up. */
  @Input() fromName = '';
  @Input() toName = '';

  @Input() groups: ActorGroup[] = [];

  /** Everybody else, for the "same person as" picker. */
  @Input() others: Actor[] = [];

  @Output() dismiss = new EventEmitter<void>();
  @Output() correct = new EventEmitter<{ actorId: string; correction: ActorCorrection }>();
  @Output() hide = new EventEmitter<{ actorId: string; hidden: boolean }>();
}

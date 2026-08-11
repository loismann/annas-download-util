import {
  ChangeDetectionStrategy, Component, EventEmitter, Input, Output, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Actor, ActorCorrection, ActorGroup, ChapterInfo, StoryVocabulary } from '../reader2.models';
import { ActorDossierComponent } from './actor-dossier.component';

/**
 * The cast, as a list.
 *
 * <p>It filters nothing. Deciding who is shown moved to {@link CastFilterComponent}
 * and `cast-filter.ts` when the map needed the same answer — two components each
 * with their own copy of "who is shown" would drift the moment one gained a
 * control the other did not, and a map of thirty people beside a list of twelve
 * is a map of a different book.</p>
 *
 * <p>What is left here is a row, a dossier, and which row is open.</p>
 */
@Component({
  selector: 'app-reader2-character-table',
  standalone: true,
  imports: [CommonModule, MatIconModule, ActorDossierComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './character-table.component.html',
  styleUrl: './character-table.component.scss'
})
export class CharacterTableComponent {
  /** Already filtered. Nothing here can widen or narrow it. */
  @Input({ required: true }) actors: Actor[] = [];

  @Input() groups: ActorGroup[] = [];

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  /** What this book type calls its people and their groupings. */
  @Input({ required: true }) vocabulary!: StoryVocabulary;

  /**
   * Whether an empty list means "filtered out" or "nobody yet". Decided by the
   * panel, which knows the unfiltered cast — a list of one walk-on under the
   * default tiers is hidden, not absent.
   */
  @Input() anybody = false;

  @Output() correct = new EventEmitter<{ actorId: string; correction: ActorCorrection }>();
  @Output() hide = new EventEmitter<{ actorId: string; hidden: boolean }>();

  protected readonly openId = signal<string | null>(null);

  /** Everybody else, for the "same person as" picker. */
  protected others(actor: Actor): Actor[] {
    return this.actors.filter(a => a.id !== actor.id);
  }

  protected toggleOpen(id: string): void {
    this.openId.set(this.openId() === id ? null : id);
  }
}

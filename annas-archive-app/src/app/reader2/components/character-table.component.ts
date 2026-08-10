import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Actor, ActorGroup, ActorTier, StoryThread, StoryVocabulary } from '../reader2.models';

/** The tiers shown before the reader asks for more. */
const DEFAULT_TIERS: ActorTier[] = ['Major', 'Secondary'];

/**
 * The cast, filtered.
 *
 * <p><b>It opens showing the people who matter, and says how many it is not
 * showing.</b> A long novel's model runs to hundreds of entries, most of them
 * walk-ons the extraction recorded because it was told not to guess — a table
 * that opened with all of them would be the same wall of names the reader opened
 * it to escape. Hiding them silently would be worse: the count is what tells
 * somebody looking for a minor character that there is a control to press.</p>
 *
 * <p>Every filter narrows a list the server has already cut to the reader's
 * position. Nothing here can widen it.</p>
 */
@Component({
  selector: 'app-reader2-character-table',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './character-table.component.html',
  styleUrl: './character-table.component.scss'
})
export class CharacterTableComponent {
  private readonly cast = signal<Actor[]>([]);
  private readonly chapter = signal(0);

  @Input({ required: true }) set actors(value: Actor[]) {
    this.cast.set(value);
  }

  /** Which chapter the reader is in, for the "in this chapter" filter. */
  @Input() set currentChapter(value: number) {
    this.chapter.set(value);
  }

  @Input() groups: ActorGroup[] = [];

  /** A signal because {@link shown} filters on it; a plain input would go stale. */
  private readonly threadList = signal<StoryThread[]>([]);

  @Input() set threads(value: StoryThread[]) {
    this.threadList.set(value);
  }

  protected get threadChoices(): StoryThread[] {
    return this.threadList();
  }

  /** What this book type calls its people and their groupings. */
  @Input({ required: true }) vocabulary!: StoryVocabulary;

  protected readonly tiers = signal<ActorTier[]>(DEFAULT_TIERS);
  protected readonly groupId = signal<string | null>(null);
  protected readonly threadId = signal<string | null>(null);
  protected readonly hereOnly = signal(false);
  protected readonly openId = signal<string | null>(null);

  protected readonly allTiers: ActorTier[] = ['Major', 'Secondary', 'Minor', 'Mentioned'];

  protected readonly shown = computed(() => {
    const tiers = this.tiers();
    const group = this.groupId();
    const thread = this.threadId();
    const here = this.hereOnly() ? this.chapter() : null;
    const cast = this.threadList().find(t => t.id === thread)?.participantIds;

    return this.cast().filter(actor =>
      tiers.includes(actor.tier)
      && (group === null || actor.groupIds.includes(group))
      && (cast === undefined || cast.includes(actor.id))
      && (here === null || actor.lastSeenChapter === here));
  });

  /** What the filters are keeping back. The reason the default is safe. */
  protected readonly hidden = computed(() => this.cast().length - this.shown().length);

  /**
   * Whether an empty list means "filtered out" or "nobody yet". Decided by the
   * cast, not by which filters are touched — the default tiers are themselves a
   * filter, and a cast of one walk-on under them is hidden, not absent.
   */
  protected readonly filtered = computed(() => this.cast().length > 0);

  protected toggleTier(tier: ActorTier): void {
    const tiers = this.tiers();

    this.tiers.set(tiers.includes(tier) ? tiers.filter(t => t !== tier) : [...tiers, tier]);
  }

  protected showEverybody(): void {
    this.tiers.set(this.allTiers);
    this.groupId.set(null);
    this.threadId.set(null);
    this.hereOnly.set(false);
  }

  protected toggleOpen(id: string): void {
    this.openId.set(this.openId() === id ? null : id);
  }

  protected groupNames(actor: Actor): string {
    return this.groups
      .filter(g => actor.groupIds.includes(g.id))
      .map(g => g.name)
      .join(', ');
  }

  /** Nulls the value the empty option carries, since a select gives back a string. */
  protected pick(value: string): string | null {
    return value === '' ? null : value;
  }
}

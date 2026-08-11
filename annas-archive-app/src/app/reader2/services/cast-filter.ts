import { Actor, ActorTier, StoryThread } from '../reader2.models';

/**
 * Which of the cast the reader is looking at.
 *
 * <p>Lifted out of the table because the map needs the same answer. Two copies
 * of "who is shown" would drift the moment one gained a filter the other did
 * not, and a map showing thirty people beside a list showing twelve is a map of
 * a different book.</p>
 *
 * <p>Pure, so the rule is testable without a component and without a chart.</p>
 */
export interface CastFilter {
  tiers: ActorTier[];
  groupId: string | null;
  threadId: string | null;

  /** Only people last seen in the chapter the reader is in. */
  hereOnly: boolean;

  /**
   * Show the ones the reader has hidden, and only those.
   *
   * <p>How hiding is reviewed and undone. A hidden character with nowhere to be
   * seen would be a deleted one that lied about it — this is the door back.</p>
   */
  hiddenOnly: boolean;
}

export const ALL_TIERS: ActorTier[] = ['Major', 'Secondary', 'Minor', 'Mentioned'];

/**
 * What the cast list opens on.
 *
 * <p>A long novel's model runs to hundreds of entries, most of them walk-ons the
 * extraction recorded because it was told not to guess. Opening on all of them
 * is the wall of names the reader opened the panel to escape — so the default
 * narrows, and the count of what it hides is what says there is a control.</p>
 */
export const DEFAULT_FILTER: CastFilter = {
  tiers: ['Major', 'Secondary'],
  groupId: null,
  threadId: null,
  hereOnly: false,
  hiddenOnly: false
};

export const NO_FILTER: CastFilter = {
  tiers: ALL_TIERS,
  groupId: null,
  threadId: null,
  hereOnly: false,
  hiddenOnly: false
};

export function filterCast(
  actors: Actor[], threads: StoryThread[], filter: CastFilter, chapter: number
): Actor[] {
  const participants = threads.find(t => t.id === filter.threadId)?.participantIds;

  // Reviewing what is hidden means seeing exactly that, so this one filter
  // answers on its own — a hidden walk-on is below the default tiers as well,
  // and asking for both at once would show an empty list and no way back.
  if (filter.hiddenOnly) return actors.filter(actor => actor.hidden);

  return actors.filter(actor =>
    filter.tiers.includes(actor.tier)
    && (filter.groupId === null || actor.groupIds.includes(filter.groupId))
    && (participants === undefined || participants.includes(actor.id))
    && (!filter.hereOnly || actor.lastSeenChapter === chapter));
}

/**
 * Who the map draws: whoever the filter allows, less whoever is hidden.
 *
 * <p>The list and the map take the same filter and differ only here, which is
 * the whole point of hiding: the list is the record and keeps everybody, the map
 * is a picture and a picture with forty walk-ons on it shows nothing.</p>
 */
export function onTheMap(shown: Actor[]): Actor[] {
  return shown.filter(actor => !actor.hidden);
}

/** The filter with one tier turned on or off. */
export function toggleTier(filter: CastFilter, tier: ActorTier): CastFilter {
  return {
    ...filter,
    tiers: filter.tiers.includes(tier)
      ? filter.tiers.filter(t => t !== tier)
      : [...filter.tiers, tier]
  };
}

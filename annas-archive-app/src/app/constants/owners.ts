/**
 * The household members, in display order. Single source for every owner
 * picker/filter — previously hardcoded separately in each edit dialog.
 */
export const HOUSEHOLD_OWNERS = ['Paul', 'Mom', 'Dad'] as const;

export type HouseholdOwner = (typeof HOUSEHOLD_OWNERS)[number];

/**
 * The ebook library stores ownership as tags ("Paul's Books") rather than an
 * owners[] list like the media/audiobook libraries do. Until that storage
 * convention is unified, every conversion between the two shapes goes through
 * these helpers — never hand-built "'s Books" strings.
 */
export function ownerToBookTag(owner: string): string {
  return `${owner}'s Books`;
}

export function bookTagToOwner(tag: string): HouseholdOwner | null {
  const match = /^(.+)'s Books$/.exec(tag);
  const name = match?.[1];
  return name && (HOUSEHOLD_OWNERS as readonly string[]).includes(name) ? (name as HouseholdOwner) : null;
}

export const BOOK_OWNER_TAGS: string[] = HOUSEHOLD_OWNERS.map(ownerToBookTag);

/** Label/value pairs for owner pickers in book flows ("Paul's" → "Paul's Books"). */
export const BOOK_OWNER_OPTIONS = HOUSEHOLD_OWNERS.map(o => ({ value: ownerToBookTag(o), label: `${o}'s` }));

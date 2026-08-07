/**
 * The owner and favourite filter rules shared by every library grid.
 *
 * These were written out twice — once in `media-library.component.ts` and once in
 * `audiobooks.component.ts` — each carrying a comment saying it matched the other,
 * and neither reachable from a test. Both rules are load-bearing and both have
 * already been got wrong once in production:
 *
 * - an owner filter that requires a positive match makes every untagged item
 *   permanently invisible ("Showing 990 of 992"), and owner tags are only ever
 *   written by the reconciler, so anything arriving by another route is untagged;
 * - the favourites filter has to be read *against* the active owner filter, or
 *   "Mom's favourites" silently means "anything anyone favourited".
 *
 * A rule protected by a comment in two places is a rule with no protection.
 */

/** The shape both libraries' items share. Anything with owners and favourites qualifies. */
export interface OwnedItem {
  owners?: string[];
  favorites?: string[];
}

/**
 * Whether an item survives the owner filter.
 *
 * An owner filter answers "whose is this". An **untagged item has no answer, so
 * there is nothing to exclude it on** — it stays visible under every filter. Only
 * an item that is tagged, and tagged with nobody selected, is hidden.
 */
export function matchesOwnerFilter(item: OwnedItem, selectedOwners: ReadonlySet<string>): boolean {
  if (selectedOwners.size === 0) return true;

  const itemOwners = item.owners ?? [];
  if (itemOwners.length === 0) return true;

  return itemOwners.some(owner => selectedOwners.has(owner));
}

/**
 * Whether an item survives the favourites filter.
 *
 * Cross-referenced against whichever owner buttons are active: with an owner
 * filter on, only that person's favourites count; with none, anything favourited
 * by any household member does. Unlike the owner filter, an item favourited by
 * nobody is excluded — "no favourites" is a real answer, not a missing one.
 */
export function matchesFavoritesFilter(item: OwnedItem, selectedOwners: ReadonlySet<string>): boolean {
  const favorites = item.favorites ?? [];
  if (favorites.length === 0) return false;
  if (selectedOwners.size === 0) return true;

  return favorites.some(owner => selectedOwners.has(owner));
}

/**
 * Both filters together, in the order the grids apply them.
 * `favoritesOnly` false skips the favourites rule entirely.
 */
export function matchesOwnerAndFavorites(
  item: OwnedItem,
  selectedOwners: ReadonlySet<string>,
  favoritesOnly: boolean
): boolean {
  if (!matchesOwnerFilter(item, selectedOwners)) return false;
  if (favoritesOnly && !matchesFavoritesFilter(item, selectedOwners)) return false;
  return true;
}

/** Adds or removes `value`, in place — the shared body of every `toggleOwnerFilter`. */
export function toggleInSet<T>(set: Set<T>, value: T): void {
  if (set.has(value)) {
    set.delete(value);
  } else {
    set.add(value);
  }
}

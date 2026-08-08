/**
 * The pieces the three media grids — `media-library`, `audiobooks` and
 * `video-library` — genuinely share.
 *
 * Deliberately *not* a base class. Each grid also declares `searchTerm`,
 * `selectedGenre`, `sortOrder` and `tileSize` as its own fields, and that stays
 * that way: they are four independent pieces of UI state that happen to have the
 * same names, and hoisting them into a shared component would couple three
 * unrelated pages, add a layer of indirection to every template binding, and buy
 * four saved lines each. What is shared here is the logic, which is where the
 * bugs live.
 */

/** The three tile sizes the grids offer. Was declared identically in four files. */
export type TileSize = 'small' | 'medium' | 'large';

/**
 * A human-readable file size.
 *
 * **This unified two functions that disagreed on every value.** `media-library`
 * divided by 1024 and covered B through TB; `audiobooks` divided by 1,000,000,000
 * and only ever produced GB or MB. The same 1.5 GB file read as `1.4 GB` on one
 * page and `1.5 GB` on the other.
 *
 * The 1024 version won, for two reasons: it is what the NAS, the OS and every
 * other tool in this stack report, so sizes here can be compared against those
 * without arithmetic; and it degrades properly for small files, where the
 * audiobooks version rounded a 5 KB file to `0 MB`.
 *
 * The visible consequence is that audiobook sizes now read about 7% smaller than
 * they did. That is the number being right, not a regression — but it is a change
 * on screen, so it is written down here.
 *
 * Units are labelled `KB`/`MB`/`GB` rather than the strictly-correct
 * `KiB`/`MiB`/`GiB`, matching what the rest of the stack shows.
 */
export function formatBytes(bytes: number | null | undefined): string | undefined {
  if (!bytes || bytes <= 0) return undefined;

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = bytes;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }

  return `${size.toFixed(1)} ${units[unitIndex]}`;
}

/**
 * Whether a free-text search term matches the given fields.
 *
 * All three grids did this: lowercase everything, join it, `includes`. What
 * differed — and still does — is *which* fields each one searches, so that stays
 * with the caller. Only the matching rule is shared.
 *
 * The fields are joined into **one** haystack rather than tested individually,
 * which is what `audiobooks` and `video-library` already did. It matters: it is
 * what lets "dune herbert" match a book whose title and author are separate
 * fields, and typing title-then-author is the obvious thing to do in a single
 * search box. Testing each field on its own would silently break that.
 *
 * An empty or whitespace-only term matches everything: a blank search box is not
 * a filter.
 */
export function matchesSearchTerm(
  term: string | null | undefined,
  ...fields: (string | null | undefined)[]
): boolean {
  const needle = (term ?? '').trim().toLowerCase();
  if (!needle) return true;

  const haystack = fields
    .filter((f): f is string => typeof f === 'string' && f.length > 0)
    .join(' ')
    .toLowerCase();

  return haystack.includes(needle);
}

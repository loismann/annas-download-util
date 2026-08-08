import { MediaLookupResult } from '../services/media-search-api.service';

/** Shown in place of an owner list when nobody has claimed an item. */
export const UNASSIGNED = 'Unassigned';

/** Shown when Sonarr/Radarr offers no poster at all. */
export const PLACEHOLDER_POSTER = '/assets/placeholder.jpg';

export type MediaSortOrder = 'title' | 'year' | 'recent';

/**
 * The sorting, poster-picking and bulk-merge decisions behind the media library grid.
 *
 * These were file-local functions and private methods in a 745-line component, so none
 * of them had ever been run without a TestBed. The bulk merge in particular decides
 * whether an edit *adds to* or *replaces* what is already on an item, across a
 * multi-selection — getting it backwards silently discards owners.
 */

/**
 * Sonarr/Radarr's images array is not guaranteed poster-first — it can lead with a
 * banner or fanart. Taking `images[0]` blindly (the original bug here) crops oddly
 * when forced into a portrait frame.
 */
export function posterUrlFor(result: MediaLookupResult): string {
  const poster = result.images?.find((i: { coverType: string }) => i.coverType === 'poster');
  return poster?.remoteUrl || poster?.url || PLACEHOLDER_POSTER;
}

/**
 * This app's own genres, not Sonarr/Radarr's. Their `genres` field is read-only
 * metadata from TMDB; `customGenres` is what the household actually filters by.
 */
export function genresOf(result: MediaLookupResult): string[] {
  return result.customGenres ?? [];
}

/** When the item entered the library, or 0 if that is unknown or unparseable. */
export function addedTimestamp(result: MediaLookupResult): number {
  return Date.parse((result['added'] as string) ?? '') || 0;
}

/** The owners line, or a placeholder — an empty string here would read as a bug. */
export function ownerLabel(result: MediaLookupResult): string {
  return result.owners && result.owners.length > 0 ? result.owners.join(', ') : UNASSIGNED;
}

/**
 * Grid ordering. Title ascending, but year and recency descending — for those two the
 * interesting end is the newest, and a library sorted oldest-first opens on things
 * nobody is looking for.
 */
export function compareMedia(
  a: MediaLookupResult, b: MediaLookupResult, sortOrder: MediaSortOrder
): number {
  switch (sortOrder) {
    case 'title':
      return (a.title || '').localeCompare(b.title || '');
    case 'year':
      return (b.year || 0) - (a.year || 0);
    case 'recent':
    default:
      return addedTimestamp(b) - addedTimestamp(a);
  }
}

/**
 * What one item's owners and genres become after a bulk edit.
 *
 * An empty incoming list means "this field was not part of the edit" and leaves the
 * existing value alone — which is what lets someone set genres across a selection
 * without wiping each item's owners. Only a non-empty list in `replace` mode
 * overwrites.
 */
export function mergeBulkResult(
  item: { owners?: string[]; customGenres?: string[] },
  result: { mode: 'append' | 'replace'; owners: string[]; genres: string[] }
): { owners: string[]; genres: string[] } {
  const merge = (existing: string[] | undefined, incoming: string[]): string[] => {
    if (incoming.length === 0) return existing ?? [];
    if (result.mode === 'replace') return incoming;
    return Array.from(new Set([...(existing ?? []), ...incoming]));
  };

  return {
    owners: merge(item.owners, result.owners),
    genres: merge(item.customGenres, result.genres)
  };
}

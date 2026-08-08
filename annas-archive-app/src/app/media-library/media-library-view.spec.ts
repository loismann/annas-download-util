import { MediaLookupResult } from '../services/media-search-api.service';
import {
  PLACEHOLDER_POSTER, UNASSIGNED, addedTimestamp, compareMedia, genresOf, mergeBulkResult,
  ownerLabel, posterUrlFor
} from './media-library-view';

/**
 * File-local functions and private methods in a 745-line component, so none had run
 * outside a TestBed. The bulk merge matters most: it decides whether an edit adds to or
 * replaces what is already on an item, across a whole selection, and getting it
 * backwards silently discards owners nobody asked to remove.
 */
describe('media-library-view', () => {
  const item = (over: Partial<MediaLookupResult> = {}): MediaLookupResult =>
    ({ title: 'A Title', ...over }) as MediaLookupResult;

  describe('posterUrlFor', () => {
    /**
     * The original bug: the images array is not poster-first, so taking images[0]
     * picked up a banner, which crops badly in a portrait frame.
     */
    it('picks the poster even when it is not the first image', () => {
      const result = item({
        images: [
          { coverType: 'banner', remoteUrl: 'https://banner' },
          { coverType: 'poster', remoteUrl: 'https://poster' }
        ]
      } as Partial<MediaLookupResult>);

      expect(posterUrlFor(result)).toBe('https://poster');
    });

    it('prefers the remote URL over the local one', () => {
      const result = item({
        images: [{ coverType: 'poster', remoteUrl: 'https://remote', url: '/local.jpg' }]
      } as Partial<MediaLookupResult>);

      expect(posterUrlFor(result)).toBe('https://remote');
    });

    it('falls back to the local URL when there is no remote one', () => {
      const result = item({
        images: [{ coverType: 'poster', url: '/local.jpg' }]
      } as Partial<MediaLookupResult>);

      expect(posterUrlFor(result)).toBe('/local.jpg');
    });

    it('falls back to the placeholder when there is no poster at all', () => {
      expect(posterUrlFor(item({ images: [{ coverType: 'fanart', remoteUrl: 'https://f' }] } as Partial<MediaLookupResult>)))
        .toBe(PLACEHOLDER_POSTER);
      expect(posterUrlFor(item())).toBe(PLACEHOLDER_POSTER);
    });
  });

  describe('genresOf', () => {
    /**
     * This app's own genres, not Sonarr/Radarr's. Their `genres` field is read-only
     * TMDB metadata; `customGenres` is what the household actually filters by, and
     * reading the wrong one makes the filter list look populated but match nothing.
     */
    it('reads the household\'s own genres, not the ones Radarr supplies', () => {
      const result = item({ customGenres: ['Cosy'], genres: ['Drama'] } as Partial<MediaLookupResult>);

      expect(genresOf(result)).toEqual(['Cosy']);
    });

    it('is empty when nothing has been assigned', () => {
      expect(genresOf(item())).toEqual([]);
    });
  });

  describe('addedTimestamp', () => {
    it('parses the added date into a sortable number', () => {
      expect(addedTimestamp(item({ added: '2026-07-27T00:00:00Z' } as Partial<MediaLookupResult>)))
        .toBe(Date.parse('2026-07-27T00:00:00Z'));
    });

    /** Sorting must not produce NaN, which makes a comparator order things at random. */
    it('is zero rather than NaN when the date is missing or unparseable', () => {
      expect(addedTimestamp(item())).toBe(0);
      expect(addedTimestamp(item({ added: 'not a date' } as Partial<MediaLookupResult>))).toBe(0);
    });
  });

  describe('ownerLabel', () => {
    it('lists the owners', () => {
      expect(ownerLabel(item({ owners: ['Mom', 'Dad'] }))).toBe('Mom, Dad');
    });

    /** An empty string here would read as a rendering bug rather than "nobody". */
    it('says Unassigned rather than nothing', () => {
      expect(ownerLabel(item({ owners: [] }))).toBe(UNASSIGNED);
      expect(ownerLabel(item())).toBe(UNASSIGNED);
    });
  });

  describe('compareMedia', () => {
    it('sorts titles ascending', () => {
      expect(compareMedia(item({ title: 'Alpha' }), item({ title: 'Zulu' }), 'title')).toBeLessThan(0);
    });

    /** Newest first for year and recency — an oldest-first library opens on nothing anyone wants. */
    it('sorts years newest first', () => {
      expect(compareMedia(item({ year: 2020 }), item({ year: 2024 }), 'year')).toBeGreaterThan(0);
    });

    it('sorts recency newest first', () => {
      const older = item({ added: '2020-01-01T00:00:00Z' } as Partial<MediaLookupResult>);
      const newer = item({ added: '2026-01-01T00:00:00Z' } as Partial<MediaLookupResult>);

      expect(compareMedia(older, newer, 'recent')).toBeGreaterThan(0);
    });

    it('does not throw on items missing the field being sorted by', () => {
      expect(() => compareMedia(item({ title: undefined }), item(), 'title')).not.toThrow();
      expect(compareMedia(item(), item(), 'year')).toBe(0);
    });
  });

  describe('mergeBulkResult', () => {
    const existing = { owners: ['Mom'], customGenres: ['Cosy'] };

    it('adds to what is already there in append mode', () => {
      const merged = mergeBulkResult(existing, { mode: 'append', owners: ['Dad'], genres: [] });

      expect(merged.owners).toEqual(['Mom', 'Dad']);
    });

    it('does not duplicate a value already present', () => {
      const merged = mergeBulkResult(existing, { mode: 'append', owners: ['Mom', 'Dad'], genres: [] });

      expect(merged.owners).toEqual(['Mom', 'Dad']);
    });

    it('overwrites in replace mode', () => {
      const merged = mergeBulkResult(existing, { mode: 'replace', owners: ['Dad'], genres: [] });

      expect(merged.owners).toEqual(['Dad']);
    });

    /**
     * The case that makes bulk editing usable at all. An empty list means the field
     * was not part of this edit — without it, setting genres across a selection would
     * clear every item's owners in replace mode.
     */
    it('leaves a field alone when the edit did not include it, even in replace mode', () => {
      const merged = mergeBulkResult(existing, { mode: 'replace', owners: [], genres: ['Noir'] });

      expect(merged.owners).toEqual(['Mom']);
      expect(merged.genres).toEqual(['Noir']);
    });

    it('handles an item that has nothing set yet', () => {
      const merged = mergeBulkResult({}, { mode: 'append', owners: ['Mom'], genres: ['Cosy'] });

      expect(merged).toEqual({ owners: ['Mom'], genres: ['Cosy'] });
    });

    it('is a no-op when neither field was part of the edit', () => {
      const merged = mergeBulkResult(existing, { mode: 'replace', owners: [], genres: [] });

      expect(merged).toEqual({ owners: ['Mom'], genres: ['Cosy'] });
    });
  });
});

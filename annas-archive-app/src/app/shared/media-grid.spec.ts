import { formatBytes, matchesSearchTerm } from './media-grid';

describe('media-grid shared helpers', () => {
  describe('formatBytes', () => {
    /**
     * The reason this function exists in one place now. `media-library` divided
     * by 1024 and `audiobooks` by 1,000,000,000, so the same file read as a
     * different size depending on which page you were looking at. These pin the
     * surviving behaviour.
     */
    it('uses 1024-based units, matching what the NAS and OS report', () => {
      expect(formatBytes(1024)).toBe('1.0 KB');
      expect(formatBytes(1024 * 1024)).toBe('1.0 MB');
      expect(formatBytes(1024 * 1024 * 1024)).toBe('1.0 GB');
    });

    it('reports a 1.5 GB file the way the media library always did', () => {
      // 1_500_000_000 / 1024^3 = 1.397…  The audiobooks copy said "1.5 GB".
      expect(formatBytes(1_500_000_000)).toBe('1.4 GB');
    });

    /**
     * The audiobooks version only ever produced GB or MB, so anything under a
     * megabyte collapsed to "0 MB". Small files are real — a cover image, a
     * stray text file — and "0 MB" reads as broken.
     */
    it('degrades to smaller units instead of rounding to zero', () => {
      expect(formatBytes(5_000)).toBe('4.9 KB');
      expect(formatBytes(500)).toBe('500.0 B');
    });

    /**
     * The 1000–1023 band, where a decimal threshold and a binary divisor
     * disagree. A unit that advances at 1000 but divides by 1024 reports 1010
     * bytes as "1.0 KB" — wrong, and invisible everywhere else, since every other
     * value rounds to the same string either way.
     */
    it('does not advance a unit until the divisor is actually reached', () => {
      expect(formatBytes(1_000)).toBe('1000.0 B');
      expect(formatBytes(1_023)).toBe('1023.0 B');
      expect(formatBytes(1_024)).toBe('1.0 KB');
    });

    it('climbs all the way to terabytes and stops there', () => {
      expect(formatBytes(1024 ** 4)).toBe('1.0 TB');
      expect(formatBytes(1024 ** 5)).toBe('1024.0 TB');
    });

    /**
     * Absent is not zero. A missing size must produce nothing at all, so the
     * caller can omit the label rather than print "0 B" next to every item whose
     * size the API did not return.
     */
    it('returns undefined for anything that is not a positive size', () => {
      expect(formatBytes(0)).toBeUndefined();
      expect(formatBytes(-1)).toBeUndefined();
      expect(formatBytes(null)).toBeUndefined();
      expect(formatBytes(undefined)).toBeUndefined();
      expect(formatBytes(NaN)).toBeUndefined();
    });
  });

  describe('matchesSearchTerm', () => {
    it('matches a term appearing in any of the given fields', () => {
      expect(matchesSearchTerm('dune', 'Dune', 'Frank Herbert')).toBe(true);
      expect(matchesSearchTerm('herbert', 'Dune', 'Frank Herbert')).toBe(true);
    });

    /**
     * The fields are one haystack, not several. Typing title-then-author into a
     * single search box is the obvious thing to do, and it is what the audiobooks
     * and video grids already supported — testing each field separately would
     * have silently removed it.
     */
    it('matches a term that spans two fields', () => {
      expect(matchesSearchTerm('dune frank', 'Dune', 'Frank Herbert')).toBe(true);
    });

    it('joins fields with a space, so adjacent words do not run together', () => {
      // Without the separator the haystack would read "DuneFrank" and match this.
      expect(matchesSearchTerm('dunefrank', 'Dune', 'Frank Herbert')).toBe(false);
    });

    it('is case-insensitive in both directions', () => {
      expect(matchesSearchTerm('DUNE', 'dune')).toBe(true);
      expect(matchesSearchTerm('dune', 'DUNE')).toBe(true);
    });

    it('matches on a substring, not just a whole word', () => {
      expect(matchesSearchTerm('erbe', 'Frank Herbert')).toBe(true);
    });

    /**
     * A blank search box is not a filter. Returning false here would empty every
     * grid the moment someone cleared the box.
     */
    it('matches everything when the term is blank', () => {
      expect(matchesSearchTerm('', 'anything')).toBe(true);
      expect(matchesSearchTerm('   ', 'anything')).toBe(true);
      expect(matchesSearchTerm(null, 'anything')).toBe(true);
      expect(matchesSearchTerm(undefined, 'anything')).toBe(true);
    });

    it('trims the term, so a trailing space still matches', () => {
      expect(matchesSearchTerm('  dune  ', 'Dune')).toBe(true);
    });

    it('does not match when no field contains the term', () => {
      expect(matchesSearchTerm('asimov', 'Dune', 'Frank Herbert')).toBe(false);
    });

    /**
     * The grids pass optional fields straight through — a narrator, a series, a
     * channel — and most items are missing at least one. A null must be skipped,
     * not stringified into "null" where it could match a search for "null".
     */
    it('skips absent fields rather than stringifying them', () => {
      expect(matchesSearchTerm('null', 'Dune', null, undefined)).toBe(false);
      expect(matchesSearchTerm('dune', null, 'Dune', undefined)).toBe(true);
    });

    it('does not match when there are no fields at all', () => {
      expect(matchesSearchTerm('dune')).toBe(false);
    });
  });
});

import {
  MIN_PAGE_WORDS, pageIndexOf, pageStarts, sectionAt, wordsPerPage
} from './pagination';

/**
 * The paging arithmetic. The interesting failures are all off-by-one: a page
 * that skips a word, a "next" that stalls, a resize that loses somebody's
 * place. These are cheap to catch precisely because none of this touches
 * layout — the fit is a lambda here and a measurement in production.
 */
describe('pagination', () => {
  describe('walking page boundaries', () => {
    /**
     * The fit is asked at each real boundary and may answer differently every
     * time — a page of long words holds fewer of them. Keyed by boundary, so a
     * walk that asked at a guessed offset would fall to the floor and fail.
     */
    it('covers every word exactly once, with variable page sizes', () => {
      const fit: Record<number, number> = { 0: 100, 100: 250, 350: 80, 430: 300 };

      expect(pageStarts(730, at => fit[at])).toEqual([0, 100, 350, 430]);
    });

    it('makes one page of an empty chapter, and one of a short one', () => {
      expect(pageStarts(0, () => 300)).toEqual([0]);
      expect(pageStarts(50, () => 300)).toEqual([0]);
    });

    it('never opens a page past the last word', () => {
      // 600 words at 300 a page: the boundary lands exactly on the end,
      // which must not create an empty page 3.
      expect(pageStarts(600, () => 300)).toEqual([0, 300]);
    });

    /** A collapsed container must not produce a page of zero words. */
    it('holds the floor when the fit reports nonsense', () => {
      expect(pageStarts(100, () => 0)).toEqual([0, 20, 40, 60, 80]);
      expect(pageStarts(60, () => -5).length).toBe(3);
    });
  });

  describe('finding the page an offset is on', () => {
    const STARTS = [0, 100, 350, 430];

    it('owns its first word and everything up to the next start', () => {
      expect(pageIndexOf(STARTS, 0)).toBe(0);
      expect(pageIndexOf(STARTS, 99)).toBe(0);
      expect(pageIndexOf(STARTS, 100)).toBe(1);
      expect(pageIndexOf(STARTS, 349)).toBe(1);
      expect(pageIndexOf(STARTS, 430)).toBe(3);
    });

    it('clamps an offset past the end onto the last page', () => {
      expect(pageIndexOf(STARTS, 99999)).toBe(3);
    });

    it('clamps a negative offset onto the first page', () => {
      expect(pageIndexOf(STARTS, -5)).toBe(0);
    });
  });

  describe('measuring', () => {
    it('gives a bigger container more words', () => {
      expect(wordsPerPage(1200, 800, 18)).toBeGreaterThan(wordsPerPage(400, 800, 18));
    });

    it('gives bigger type fewer words', () => {
      expect(wordsPerPage(800, 800, 28)).toBeLessThan(wordsPerPage(800, 800, 14));
    });

    /** A collapsed container must not produce a page of zero words. */
    it('never measures below the floor, however small the container', () => {
      expect(wordsPerPage(0, 0, 18)).toBe(MIN_PAGE_WORDS);
    });
  });
  /**
   * Shared by the panel that marks where the reader is and the container that
   * decides which section to summarise. If those two disagreed by one word, the
   * reader would see a mark on one section and be charged for its neighbour.
   */
  describe('locating a section', () => {
    const SECTIONS = [
      { startWord: 0, wordCount: 400 },
      { startWord: 400, wordCount: 400 },
      { startWord: 800, wordCount: 300 }
    ];

    it('finds the section a word offset falls in', () => {
      expect(sectionAt(SECTIONS, 0)).toBe(0);
      expect(sectionAt(SECTIONS, 500)).toBe(1);
      expect(sectionAt(SECTIONS, 1099)).toBe(2);
    });

    /** Half-open: a section owns its first word and not the word after its last. */
    it('puts a boundary word in the section that starts there', () => {
      expect(sectionAt(SECTIONS, 399)).toBe(0);
      expect(sectionAt(SECTIONS, 400)).toBe(1);
      expect(sectionAt(SECTIONS, 799)).toBe(1);
      expect(sectionAt(SECTIONS, 800)).toBe(2);
    });

    it('reports no section rather than the last one when the offset runs past the end', () => {
      expect(sectionAt(SECTIONS, 1100)).toBe(-1);
      expect(sectionAt(SECTIONS, 99999)).toBe(-1);
    });

    it('reports no section for a chapter that has none', () => {
      expect(sectionAt([], 0)).toBe(-1);
    });
  });
});

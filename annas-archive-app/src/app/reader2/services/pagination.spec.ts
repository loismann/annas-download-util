import {
  MIN_PAGE_WORDS, pageAt, pageCount, pageOf, repaginate, sectionAt, wordsPerPage
} from './pagination';

/**
 * The interesting failures in paging are all off-by-one: a page that skips a
 * word, a "next" that stalls, a resize that loses somebody's place. These are
 * cheap to catch precisely because none of this touches the DOM.
 */
describe('pagination', () => {
  describe('page arithmetic', () => {
    it('covers every word with no gaps and no overlaps', () => {
      const total = 1000;
      const size = 300;
      let expectedStart = 0;

      for (let i = 0; i < pageCount(total, size); i++) {
        const page = pageAt(i, total, size);
        expect(page.startWord).toBe(expectedStart);
        expectedStart += page.wordCount;
      }

      expect(expectedStart).toBe(total);
    });

    it('gives a short chapter exactly one page', () => {
      expect(pageCount(50, 300)).toBe(1);
      expect(pageAt(0, 50, 300)).toEqual({ index: 0, startWord: 0, wordCount: 50 });
    });

    it('gives an empty chapter one empty page rather than none', () => {
      expect(pageCount(0, 300)).toBe(1);
      expect(pageAt(0, 0, 300).wordCount).toBe(0);
    });

    it('clamps a page index past the end onto the last page', () => {
      expect(pageAt(99, 1000, 300).index).toBe(3);
    });

    it('never returns a page running past the end of the chapter', () => {
      const last = pageAt(3, 1000, 300);
      expect(last.startWord + last.wordCount).toBe(1000);
    });
  });

  describe('word offset to page number', () => {
    it('round-trips', () => {
      for (const offset of [0, 1, 299, 300, 301, 999]) {
        const page = pageOf(offset, 1000, 300);
        const bounds = pageAt(page, 1000, 300);

        expect(offset).toBeGreaterThanOrEqual(bounds.startWord);
        expect(offset).toBeLessThan(bounds.startWord + bounds.wordCount);
      }
    });

    it('clamps at both ends rather than going out of range', () => {
      expect(pageOf(-50, 1000, 300)).toBe(0);
      expect(pageOf(99999, 1000, 300)).toBe(3);
    });
  });

  describe('resizing', () => {
    /** Resizing a window must not move somebody in a book. */
    it('keeps the reader on the same words when the page size changes', () => {
      const total = 1000;
      const before = pageAt(2, total, 300).startWord;   // word 600

      const after = repaginate(2, total, 300, 150);

      expect(pageAt(after, total, 150).startWord).toBe(before);
    });

    it('holds when the page grows as well as when it shrinks', () => {
      const total = 1000;
      const anchor = pageAt(6, total, 100).startWord;

      const after = repaginate(6, total, 100, 400);

      const bounds = pageAt(after, total, 400);
      expect(anchor).toBeGreaterThanOrEqual(bounds.startWord);
      expect(anchor).toBeLessThan(bounds.startWord + bounds.wordCount);
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
      expect(pageCount(1000, 0)).toBe(pageCount(1000, MIN_PAGE_WORDS));
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

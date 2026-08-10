/**
 * Paging arithmetic, as pure functions of (word count, page size).
 *
 * <p>No DOM, no component, no service — measuring the container is the caller's
 * job and everything after it is arithmetic. That split is deliberate: the
 * interesting failures here are all off-by-one (a page that skips a word, a
 * "next" that stalls on the last page, a resize that loses the reader's place),
 * and those are only cheap to test when there is no layout involved.</p>
 */

/** Where one page starts and ends, in words. */
export interface Page {
  index: number;
  startWord: number;
  wordCount: number;
}

/** Never fewer than this, however small the container measures. */
export const MIN_PAGE_WORDS = 20;

/** How many pages a chapter of this length makes. Always at least one. */
export function pageCount(totalWords: number, pageWords: number): number {
  const size = usable(pageWords);
  return totalWords <= 0 ? 1 : Math.ceil(totalWords / size);
}

/** The page containing a word offset, clamped into the chapter. */
export function pageOf(wordOffset: number, totalWords: number, pageWords: number): number {
  const size = usable(pageWords);
  const clamped = clamp(wordOffset, 0, Math.max(0, totalWords - 1));

  return Math.min(Math.floor(clamped / size), pageCount(totalWords, pageWords) - 1);
}

/** The bounds of one page, clamped so the last page never runs past the end. */
export function pageAt(index: number, totalWords: number, pageWords: number): Page {
  const size = usable(pageWords);
  const clampedIndex = clamp(index, 0, pageCount(totalWords, pageWords) - 1);
  const startWord = clampedIndex * size;

  return {
    index: clampedIndex,
    startWord,
    wordCount: Math.max(0, Math.min(size, totalWords - startWord))
  };
}

/**
 * The word offset to keep the reader on when the page size changes.
 *
 * <p>Resizing a window must not move somebody in a book. Page *numbers* are not
 * stable across a resize — page 4 of 12 becomes page 7 of 20 — so the word
 * offset is what carries over, and the page number is recomputed from it. This
 * function exists to make that the obvious thing to call rather than something
 * each caller reinvents.</p>
 */
export function repaginate(
  currentPage: number, totalWords: number, oldPageWords: number, newPageWords: number
): number {
  const anchor = pageAt(currentPage, totalWords, oldPageWords).startWord;
  return pageOf(anchor, totalWords, newPageWords);
}

/**
 * Words that fit in a container, from its measured size and the type in it.
 *
 * <p>An estimate, and honestly so: exact reflow measurement means laying the
 * text out twice on every resize. The character-per-word figure is English
 * prose's long-run average, and being a page out at the end of a long chapter
 * costs a reader one keypress.</p>
 */
export function wordsPerPage(
  heightPx: number, widthPx: number, fontSizePx: number, lineHeight = 1.6
): number {
  const charsPerLine = Math.max(1, Math.floor(widthPx / (fontSizePx * 0.5)));
  const lines = Math.max(1, Math.floor(heightPx / (fontSizePx * lineHeight)));

  return Math.max(MIN_PAGE_WORDS, Math.floor((charsPerLine * lines) / AVERAGE_WORD_CHARS));
}

/**
 * The section a word offset falls in, or -1 when it falls in none.
 *
 * <p>Here rather than in either caller, because both the panel that marks where
 * the reader is and the container that decides which section to summarise have
 * to agree — if one used a closed interval and the other a half-open one, the
 * reader would be shown a mark on one section and charged for its neighbour.
 * Half-open: a section owns its first word and not the word after its last.</p>
 */
export function sectionAt(
  sections: readonly { startWord: number; wordCount: number }[], wordOffset: number
): number {
  return sections.findIndex(
    s => wordOffset >= s.startWord && wordOffset < s.startWord + s.wordCount);
}

/** Including the space after it. */
const AVERAGE_WORD_CHARS = 6;

function usable(pageWords: number): number {
  return Math.max(MIN_PAGE_WORDS, Math.floor(pageWords) || MIN_PAGE_WORDS);
}

function clamp(value: number, low: number, high: number): number {
  return Math.min(Math.max(value, low), Math.max(low, high));
}

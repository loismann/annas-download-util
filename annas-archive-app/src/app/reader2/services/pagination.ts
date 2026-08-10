/**
 * Paging arithmetic, as pure functions.
 *
 * <p>No DOM here — measuring how many words fit is {@link FitAt}'s job, supplied
 * by the caller ({@code page-fit.ts} measures real layout; tests pass a lambda).
 * Everything after that measurement is arithmetic, and the interesting failures
 * are all off-by-one (a page that skips a word, a "next" that stalls on the last
 * page, a resize that loses the reader's place) — only cheap to test when there
 * is no layout involved.</p>
 */

/**
 * How many words fit on the page that starts at a word offset.
 *
 * <p>A function rather than a number because pages are not all the same size:
 * a page of long words, or one carrying the chapter title, holds fewer than a
 * page of short dialogue. One number per chapter is exactly the estimate that
 * either ran text past the bottom edge or left a gap above it.</p>
 */
export type FitAt = (startWord: number) => number;

/** Never fewer than this, however small the container measures. */
export const MIN_PAGE_WORDS = 20;

/**
 * Where every page starts, walked from the front with the real fit at each
 * boundary. Always at least one page, so an empty chapter still renders.
 */
export function pageStarts(totalWords: number, fitAt: FitAt): number[] {
  const starts = [0];

  for (let at = 0; at < totalWords;) {
    at += Math.max(MIN_PAGE_WORDS, Math.floor(fitAt(at)) || MIN_PAGE_WORDS);
    if (at < totalWords) starts.push(at);
  }

  return starts;
}

/** The page containing a word offset. Half-open: a page owns its first word. */
export function pageIndexOf(starts: readonly number[], wordOffset: number): number {
  let low = 0;
  let high = starts.length - 1;

  while (low < high) {
    const mid = Math.ceil((low + high) / 2);

    if (starts[mid] <= wordOffset) low = mid;
    else high = mid - 1;
  }

  return low;
}

/**
 * Words that fit in a container, from its measured size and the type in it.
 *
 * <p>The estimate, kept as the <i>fallback</i> fit for when there is nothing to
 * measure yet — the first paint, a test, a hidden surface. Real pages come from
 * measuring real layout in {@code page-fit.ts}.</p>
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

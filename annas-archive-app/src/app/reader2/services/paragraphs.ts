/**
 * Where the paragraphs are, without disturbing where the words are.
 *
 * <p>The extracted text has had its paragraph breaks since the beginning —
 * `EpubTextExtractor` goes out of its way to keep them, because `SectionChunker`
 * splits on blank lines and would otherwise cut a section mid-sentence. The
 * reader threw them away at the last step: it split the chapter on `/\s+/` and
 * joined a page back with a single space, which is a shape that cannot tell a
 * space from the end of a scene.</p>
 *
 * <p><b>The words must not move.</b> A word offset is the unit of position for
 * the reading position, every bookmark, every search hit and every section
 * boundary, and the server counts words by splitting on whitespace. So this adds
 * a second, parallel answer — <i>which</i> word starts a paragraph — and changes
 * neither the list of words nor their numbering.</p>
 *
 * <p>Pure, and the interesting cases are all shape: a chapter that opens with a
 * blank line, a run of them, a line break inside a sentence (which is not a
 * paragraph), and a page boundary that lands mid-paragraph.</p>
 */

/**
 * A blank line: one newline, then nothing but horizontal space, then another.
 *
 * <p>This, and not a single newline, is what a paragraph break is — the same
 * rule the server's chunker uses, so the two agree on where a paragraph ends.
 * Single newlines are everywhere in extracted text: an EPUB's source is usually
 * hard-wrapped, and the extractor keeps those line ends as it found them.
 * Treating one as a break would put a paragraph mark inside most sentences.</p>
 */
const BLANK_LINE = /\n[^\S\n]*\n/;

/**
 * The index of each word that begins a paragraph, ascending.
 *
 * <p>The first word of a non-empty chapter always begins one, so the result is
 * empty only when there are no words at all.</p>
 */
export function paragraphStarts(text: string): number[] {
  const starts: number[] = [];

  let word = 0;
  let inWord = false;

  // Whitespace seen since the last word ended. Held as text rather than as a
  // newline count because the rule is about blank lines, and "\n \n" is one
  // while "\n" twice over with a word between them is not.
  let gap = '';

  for (let at = 0; at <= text.length; at++) {
    const isWordChar = at < text.length && !isSpace(text[at]);

    if (isWordChar && !inWord) {
      if (word === 0 || BLANK_LINE.test(gap)) starts.push(word);

      word++;
      gap = '';
    } else if (!isWordChar && at < text.length) {
      gap += text[at];
    }

    inWord = isWordChar;
  }

  return starts;
}

/**
 * A range of words as the paragraphs it falls in, `[from, to)`.
 *
 * <p>A page rarely begins or ends on a paragraph boundary, and it is not made to
 * — the first and last paragraphs of a page are simply the parts of them that
 * fit. Paging is a measurement of the container, and moving a boundary to tidy
 * the prose would mean a page that no longer matches what was measured.</p>
 */
export function paragraphsOf(
  words: readonly string[], starts: readonly number[], from: number, to: number
): string[] {
  if (to <= from) return [];

  const cuts = [from, ...starts.filter(start => start > from && start < to), to];

  return cuts
    .slice(0, -1)
    .map((start, at) => words.slice(start, cuts[at + 1]).join(' '));
}

/** Includes the newlines, unlike `char.IsWhiteSpace`'s awkward cousins. */
function isSpace(character: string): boolean {
  return /\s/.test(character);
}

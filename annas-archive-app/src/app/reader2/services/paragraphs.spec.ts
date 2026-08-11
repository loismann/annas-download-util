import { paragraphStarts, paragraphsOf } from './paragraphs';

/**
 * The whole risk in rendering paragraphs is that the words move.
 *
 * <p>A word offset is what the reading position, every bookmark, every search
 * hit and every section boundary are counted in, and the server counts them by
 * splitting on whitespace. So most of what is below is one assertion said
 * several ways: whatever these functions decide about paragraphs, the words are
 * still numbered exactly as <c>text.trim().split(/\s+/)</c> numbers them.</p>
 */
describe('paragraphStarts', () => {
  /** The numbering everything else in the reader agrees on. */
  function words(text: string): string[] {
    return text.trim().length === 0 ? [] : text.trim().split(/\s+/);
  }

  it('starts a paragraph at the first word', () => {
    expect(paragraphStarts('one two three')).toEqual([0]);
  });

  it('finds nothing in an empty chapter', () => {
    expect(paragraphStarts('')).toEqual([]);
    expect(paragraphStarts('   \n\n  ')).toEqual([]);
  });

  it('breaks on a blank line', () => {
    expect(paragraphStarts('one two\n\nthree four\n\nfive')).toEqual([0, 2, 4]);
  });

  /**
   * The one that matters. EPUB source is hard-wrapped, and the extractor keeps
   * those line ends as it found them — so a single newline is a line break
   * inside a sentence far more often than it is the end of a paragraph.
   */
  it('does not break on a line end inside a sentence', () => {
    expect(paragraphStarts('one two\nthree four')).toEqual([0]);
  });

  it('treats a line holding only spaces as blank', () => {
    expect(paragraphStarts('one\n   \ntwo')).toEqual([0, 1]);
  });

  it('collapses a run of blank lines into one break', () => {
    expect(paragraphStarts('one\n\n\n\ntwo')).toEqual([0, 1]);
  });

  it('ignores whitespace before the first word', () => {
    expect(paragraphStarts('\n\n  one two')).toEqual([0]);
  });

  it('numbers words exactly as the rest of the reader does', () => {
    const text = '\n\nAlpha beta\n\n gamma\ndelta \n\n\n epsilon\n';

    expect(words(text)).toEqual(['Alpha', 'beta', 'gamma', 'delta', 'epsilon']);
    expect(paragraphStarts(text))
      .withContext('gamma is word 2 and epsilon is word 4, blank lines or not')
      .toEqual([0, 2, 4]);
  });
});

describe('paragraphsOf', () => {
  const text = 'a b c\n\nd e\n\nf g h i';
  const words = text.trim().split(/\s+/);
  const starts = paragraphStarts(text);

  it('returns the whole chapter as its paragraphs', () => {
    expect(paragraphsOf(words, starts, 0, words.length)).toEqual(['a b c', 'd e', 'f g h i']);
  });

  it('returns nothing for an empty range', () => {
    expect(paragraphsOf(words, starts, 3, 3)).toEqual([]);
    expect(paragraphsOf([], [], 0, 0)).toEqual([]);
  });

  /**
   * A page begins and ends where the measurement said it does, not where the
   * prose would prefer. Its first and last paragraphs are routinely the parts of
   * one that fit — moving a boundary to tidy that up would give a page that no
   * longer matches what was measured for it.
   */
  it('cuts a paragraph where the page does', () => {
    expect(paragraphsOf(words, starts, 1, 6)).toEqual(['b c', 'd e', 'f']);
  });

  it('gives one paragraph back when the range sits inside one', () => {
    expect(paragraphsOf(words, starts, 6, 8)).toEqual(['g h']);
  });

  it('loses no word and adds none, whatever the range', () => {
    for (let from = 0; from < words.length; from++) {
      for (let to = from; to <= words.length; to++) {
        expect(paragraphsOf(words, starts, from, to).join(' ').split(' ').filter(Boolean))
          .withContext(`[${from}, ${to})`)
          .toEqual(words.slice(from, to));
      }
    }
  });
});

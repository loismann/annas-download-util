import { ChapterNamePipe, chapterName } from './chapter-name.pipe';
import { contents } from './testing/cast';

/**
 * One chapter, one name.
 *
 * <p>Chapter indices in the story model are positions in the spine — cover,
 * copyright page, dramatis personae and all — so counting them produced "Ch 14"
 * beside a contents list reading "Chapter Three". The book's own title is the
 * only name a reader can line the two up by.</p>
 */
describe('chapterName', () => {
  const CONTENTS = contents('Cover', 'Copyright', 'Chapter One', 'Chapter Two');

  it('uses the book’s own title rather than counting the index', () => {
    expect(chapterName(CONTENTS, 3)).toBe('Chapter Two');
  });

  it('counts only when the contents list has not arrived', () => {
    expect(chapterName([], 3)).toBe('Chapter 4');
  });

  it('counts rather than blanking when the index is off the end', () => {
    expect(chapterName(CONTENTS, 40)).toBe('Chapter 41');
  });

  /** Edges carry a null `endedChapter`, and "Ended " on its own is not a sentence. */
  it('says nothing at all for a chapter that is not set', () => {
    expect(chapterName(CONTENTS, null)).toBe('');
  });

  it('is the same rule through the pipe', () => {
    expect(new ChapterNamePipe().transform(3, CONTENTS)).toBe('Chapter Two');
  });
});

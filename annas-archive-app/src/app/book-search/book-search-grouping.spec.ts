import { BookSearchGrouping } from './book-search-grouping';
import { BookDto } from '../models/book-dto.model';

/**
 * Grouping decides what a person actually sees on the results grid: which
 * duplicates collapse into one card, and which copy that card shows. None of
 * it was reachable from a test while it sat behind an AI call and three
 * component getters.
 */
describe('BookSearchGrouping', () => {
  const book = (over: Partial<BookDto> = {}): BookDto => ({
    title: 'Dune',
    md5: 'md5-1',
    authors: ['Frank Herbert'],
    language: 'English',
    format: 'EPUB',
    source: 'annas-archive',
    fileSize: '1.5 MB',
    bookType: 'book',
    publisher: 'Ace',
    year: 1965,
    isbn: null,
    coverCandidates: [],
    sendState: 'idle',
    libraryState: 'idle',
    dadsKindleState: 'idle',
    momsKindleState: 'idle',
    ...over
  });

  /** Substring match, standing in for the component's real name matcher. */
  const authorMatches = (author: string, selected: string) =>
    author.toLowerCase().includes(selected.toLowerCase());

  describe('fromMd5Groups', () => {
    it('collapses the md5s the model clustered together', () => {
      const epub = book({ md5: 'a', format: 'EPUB' });
      const pdf = book({ md5: 'b', format: 'PDF' });
      const other = book({ md5: 'c', title: 'Neuromancer' });

      const groups = BookSearchGrouping.fromMd5Groups([epub, pdf, other], [['a', 'b'], ['c']]);

      expect(groups.length).toBe(2);
      expect(groups[0].books.map(b => b.md5)).toEqual(['a', 'b']);
      expect(groups[0].key).toBe('a');
      expect(groups[1].books.map(b => b.md5)).toEqual(['c']);
    });

    it('drops an md5 the current results no longer hold', () => {
      // The model echoes back what it was sent, but page 2 can land between
      // the request and the response.
      const groups = BookSearchGrouping.fromMd5Groups([book({ md5: 'a' })], [['a', 'gone']]);

      expect(groups.length).toBe(1);
      expect(groups[0].books.map(b => b.md5)).toEqual(['a']);
    });

    it('drops a group left with nothing rather than rendering an empty card', () => {
      const groups = BookSearchGrouping.fromMd5Groups([book({ md5: 'a' })], [['a'], ['ghost']]);

      expect(groups.length).toBe(1);
    });

    it('returns nothing for an empty response', () => {
      expect(BookSearchGrouping.fromMd5Groups([book()], [])).toEqual([]);
    });
  });

  describe('ungrouped', () => {
    it('gives every book its own group', () => {
      // The failure path: duplicates stay uncollapsed, but nothing vanishes.
      const groups = BookSearchGrouping.ungrouped([book({ md5: 'a' }), book({ md5: 'b' })]);

      expect(groups.map(g => g.key)).toEqual(['a', 'b']);
      expect(groups.every(g => g.books.length === 1)).toBeTrue();
    });
  });

  describe('filter', () => {
    const groups = [
      { key: 'a', books: [book({ md5: 'a', format: 'EPUB' }), book({ md5: 'b', format: 'PDF' })] },
      { key: 'c', books: [book({ md5: 'c', format: 'PDF', authors: ['William Gibson'] })] }
    ];

    it('keeps a group when only some of its books match the format', () => {
      // The point of filtering inside groups: the card stays and shows the
      // copy that matched, instead of the whole book disappearing.
      const filtered = BookSearchGrouping.filter(groups, { format: 'EPUB' }, authorMatches);

      expect(filtered.length).toBe(1);
      expect(filtered[0].key).toBe('a');
      expect(filtered[0].books.map(b => b.md5)).toEqual(['a']);
    });

    it('drops a group where nothing matches', () => {
      expect(BookSearchGrouping.filter(groups, { format: 'MOBI' }, authorMatches)).toEqual([]);
    });

    it('filters by author', () => {
      const filtered = BookSearchGrouping.filter(groups, { author: 'Gibson' }, authorMatches);

      expect(filtered.map(g => g.key)).toEqual(['c']);
    });

    it('applies author and format together', () => {
      const filtered = BookSearchGrouping.filter(
        groups, { author: 'Herbert', format: 'PDF' }, authorMatches);

      expect(filtered.length).toBe(1);
      expect(filtered[0].books.map(b => b.md5)).toEqual(['b']);
    });

    it('treats an empty string as no filter', () => {
      // Which is what the selects report for "All".
      const filtered = BookSearchGrouping.filter(groups, { author: '', format: '' }, authorMatches);

      expect(filtered.length).toBe(2);
    });

    it('keeps the group key stable so per-group state survives filtering', () => {
      // groupSelection is keyed by this; a new key would silently forget which
      // variant the user picked every time they touched a filter.
      const filtered = BookSearchGrouping.filter(groups, { format: 'PDF' }, authorMatches);

      expect(filtered.map(g => g.key)).toEqual(['a', 'c']);
    });

    it('does not mutate the groups it was given', () => {
      BookSearchGrouping.filter(groups, { format: 'EPUB' }, authorMatches);

      expect(groups[0].books.length).toBe(2);
    });
  });

  describe('activeBookIn', () => {
    it('honours the variant the user picked', () => {
      const group = { key: 'a', books: [book({ md5: 'a', format: 'EPUB' }), book({ md5: 'b', format: 'PDF' })] };

      expect(BookSearchGrouping.activeBookIn(group, 'b').md5).toBe('b');
    });

    it('ignores a pick that filtering has removed', () => {
      // Otherwise the card would show nothing after a format change.
      const group = { key: 'a', books: [book({ md5: 'a', format: 'EPUB' })] };

      expect(BookSearchGrouping.activeBookIn(group, 'gone').md5).toBe('a');
    });

    it('prefers a standard format over whatever happens to be first', () => {
      // A card must not default to AZW3 while an EPUB sits in the same group.
      const group = {
        key: 'z',
        books: [book({ md5: 'z', format: 'AZW3' }), book({ md5: 'e', format: 'EPUB' })]
      };

      expect(BookSearchGrouping.activeBookIn(group).format).toBe('EPUB');
    });

    it('prefers EPUB over PDF', () => {
      const group = {
        key: 'p',
        books: [book({ md5: 'p', format: 'PDF' }), book({ md5: 'e', format: 'EPUB' })]
      };

      expect(BookSearchGrouping.activeBookIn(group).format).toBe('EPUB');
    });

    it('falls back to the first book when no format is a standard one', () => {
      // A card showing an unusual format beats a card showing nothing.
      const group = {
        key: 'x',
        books: [book({ md5: 'x', format: 'DJVU' }), book({ md5: 'y', format: 'CBZ' })]
      };

      expect(BookSearchGrouping.activeBookIn(group).md5).toBe('x');
    });
  });

  describe('toDisplayGroups', () => {
    it('pairs each group with its active book', () => {
      const groups = [
        { key: 'a', books: [book({ md5: 'a', format: 'PDF' }), book({ md5: 'b', format: 'EPUB' })] }
      ];

      const display = BookSearchGrouping.toDisplayGroups(groups, new Map());

      expect(display.length).toBe(1);
      expect(display[0].group.key).toBe('a');
      expect(display[0].active.format).toBe('EPUB');
    });

    it('reads each group\'s own selection', () => {
      const groups = [
        { key: 'a', books: [book({ md5: 'a' }), book({ md5: 'a2', format: 'PDF' })] },
        { key: 'b', books: [book({ md5: 'b' }), book({ md5: 'b2', format: 'PDF' })] }
      ];

      const display = BookSearchGrouping.toDisplayGroups(groups, new Map([['b', 'b2']]));

      expect(display[0].active.md5).toBe('a');
      expect(display[1].active.md5).toBe('b2');
    });
  });
});

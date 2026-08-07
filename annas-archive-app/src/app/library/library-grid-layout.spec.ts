import { GridBook, LibraryGridLayout } from './library-grid-layout';

/**
 * The A–Z rail and the virtual scroller count different things: the scroller
 * counts rows, everything else counts books. Getting the conversion wrong does
 * not throw — it scrolls to the wrong place, or lights up the wrong letter —
 * which is why it is worth pinning both directions rather than trusting that
 * they still agree.
 */
describe('LibraryGridLayout', () => {
  const book = (title: string, extra: Partial<GridBook> = {}): GridBook => ({ title, ...extra });
  const books = (...titles: string[]) => titles.map(t => book(t));

  describe('tile geometry', () => {
    it('gives smaller tiles more books per row and less height', () => {
      expect(LibraryGridLayout.itemsPerRow('small')).toBe(8);
      expect(LibraryGridLayout.itemsPerRow('medium')).toBe(6);
      expect(LibraryGridLayout.itemsPerRow('large')).toBe(4);

      expect(LibraryGridLayout.rowHeight('small')).toBe(320);
      expect(LibraryGridLayout.rowHeight('medium')).toBe(400);
      expect(LibraryGridLayout.rowHeight('large')).toBe(480);
    });

    it('keeps height and density moving in opposite directions', () => {
      // A row of small tiles holds more and is shorter. If these ever agreed in
      // direction the grid would have gaps or overlap.
      const sizes = ['small', 'medium', 'large'] as const;
      const perRow = sizes.map(s => LibraryGridLayout.itemsPerRow(s));
      const heights = sizes.map(s => LibraryGridLayout.rowHeight(s));

      expect(perRow).toEqual([...perRow].sort((a, b) => b - a));
      expect(heights).toEqual([...heights].sort((a, b) => a - b));
    });
  });

  describe('toRows', () => {
    it('fills rows in order and leaves the last one short', () => {
      const rows = LibraryGridLayout.toRows(books('a', 'b', 'c', 'd', 'e'), 2);

      expect(rows.map(r => r.map(b => b.title))).toEqual([['a', 'b'], ['c', 'd'], ['e']]);
    });

    it('returns nothing for an empty library', () => {
      expect(LibraryGridLayout.toRows([], 6)).toEqual([]);
    });

    it('survives a nonsensical row width instead of hanging', () => {
      // A zero here would loop forever, which as a failure mode beats a wrong
      // layout only in that it is easier to notice.
      expect(LibraryGridLayout.toRows(books('a', 'b'), 0).length).toBe(2);
    });
  });

  describe('row and book indexes are inverses', () => {
    it('maps a book to its row', () => {
      expect(LibraryGridLayout.rowIndexOf(0, 6)).toBe(0);
      expect(LibraryGridLayout.rowIndexOf(5, 6)).toBe(0);
      expect(LibraryGridLayout.rowIndexOf(6, 6)).toBe(1);
      expect(LibraryGridLayout.rowIndexOf(13, 6)).toBe(2);
    });

    it('maps a row back to its first book', () => {
      expect(LibraryGridLayout.firstBookIndexOf(0, 6)).toBe(0);
      expect(LibraryGridLayout.firstBookIndexOf(2, 6)).toBe(12);
    });

    it('round-trips every book in a plausible library', () => {
      for (const perRow of [4, 6, 8]) {
        for (let bookIndex = 0; bookIndex < 200; bookIndex++) {
          const row = LibraryGridLayout.rowIndexOf(bookIndex, perRow);
          const firstOfRow = LibraryGridLayout.firstBookIndexOf(row, perRow);

          expect(firstOfRow).toBeLessThanOrEqual(bookIndex);
          expect(bookIndex - firstOfRow).toBeLessThan(perRow);
        }
      }
    });
  });

  describe('letterOf', () => {
    it('follows the title by default', () => {
      expect(LibraryGridLayout.letterOf(book('Dune'), 'title')).toBe('D');
      expect(LibraryGridLayout.letterOf(book('Dune'), 'recent')).toBe('D');
    });

    it('follows the first author when sorted by author', () => {
      const b = book('Dune', { authors: ['Frank Herbert', 'Brian Herbert'] });

      expect(LibraryGridLayout.letterOf(b, 'author')).toBe('F');
      expect(LibraryGridLayout.letterOf(b, 'title')).toBe('D');
    });

    it('follows the series when sorted by series', () => {
      const b = book('Judas Unchained', { series: 'Commonwealth Saga' });

      expect(LibraryGridLayout.letterOf(b, 'series')).toBe('C');
    });

    it('falls back to the title for a standalone book in a series sort', () => {
      expect(LibraryGridLayout.letterOf(book('Dune', { series: '   ' }), 'series')).toBe('D');
      expect(LibraryGridLayout.letterOf(book('Dune', { series: null }), 'series')).toBe('D');
    });

    it('files anything that is not A-Z under #', () => {
      // The rail has 27 slots. A book with nowhere to go would be unreachable
      // from it, so everything unsortable lands in the same place.
      expect(LibraryGridLayout.letterOf(book('1984'), 'title')).toBe('#');
      expect(LibraryGridLayout.letterOf(book('"Salem\'s Lot'), 'title')).toBe('#');
      expect(LibraryGridLayout.letterOf(book('日本語'), 'title')).toBe('#');
      expect(LibraryGridLayout.letterOf(book(''), 'title')).toBe('#');
      expect(LibraryGridLayout.letterOf({}, 'title')).toBe('#');
    });

    it('ignores leading whitespace and case', () => {
      expect(LibraryGridLayout.letterOf(book('  dune'), 'title')).toBe('D');
    });

    it('files a book with no author under # when sorted by author', () => {
      expect(LibraryGridLayout.letterOf(book('Beowulf', { authors: [] }), 'author')).toBe('#');
      expect(LibraryGridLayout.letterOf(book('Beowulf'), 'author')).toBe('#');
    });
  });

  describe('the rail itself', () => {
    it('offers # first, then A-Z', () => {
      const alphabet = LibraryGridLayout.alphabet();

      expect(alphabet.length).toBe(27);
      expect(alphabet[0]).toBe('#');
      expect(alphabet[26]).toBe('Z');
    });

    it('only appears for orders that are actually alphabetical', () => {
      // Under 'recent' the letters would be in no order, so jumping to one
      // would move the view somewhere arbitrary.
      expect(LibraryGridLayout.showsAlphabetIndex('title')).toBeTrue();
      expect(LibraryGridLayout.showsAlphabetIndex('author')).toBeTrue();
      expect(LibraryGridLayout.showsAlphabetIndex('series')).toBeTrue();

      expect(LibraryGridLayout.showsAlphabetIndex('recent')).toBeFalse();
      expect(LibraryGridLayout.showsAlphabetIndex('stars')).toBeFalse();
      expect(LibraryGridLayout.showsAlphabetIndex('goodreads')).toBeFalse();
    });

    it('lists only letters that have books behind them, sorted', () => {
      const library = books('Dune', 'Anathem', '1984', 'Dracula');

      expect(LibraryGridLayout.availableLetters(library, 'title')).toEqual(['#', 'A', 'D']);
    });

    it('re-derives the letters when the sort order changes', () => {
      const library = [book('Dune', { authors: ['Frank Herbert'] })];

      expect(LibraryGridLayout.availableLetters(library, 'title')).toEqual(['D']);
      expect(LibraryGridLayout.availableLetters(library, 'author')).toEqual(['F']);
    });
  });

  describe('jumping to a letter', () => {
    const library = books('Anathem', 'Blindsight', 'Cryptonomicon', 'Dune', 'Excession');

    it('finds the row holding the first book under that letter', () => {
      expect(LibraryGridLayout.rowIndexOfLetter(library, 'A', 'title', 2)).toBe(0);
      expect(LibraryGridLayout.rowIndexOfLetter(library, 'C', 'title', 2)).toBe(1);
      expect(LibraryGridLayout.rowIndexOfLetter(library, 'E', 'title', 2)).toBe(2);
    });

    it('reports -1 for a letter nothing files under', () => {
      expect(LibraryGridLayout.rowIndexOfLetter(library, 'Z', 'title', 2)).toBe(-1);
      expect(LibraryGridLayout.rowIndexOfLetter([], 'A', 'title', 6)).toBe(-1);
    });

    it('lands on the first of several books sharing a letter', () => {
      const duplicates = books('Dune', 'Dune Messiah', 'Children of Dune');

      expect(LibraryGridLayout.rowIndexOfLetter(duplicates, 'D', 'title', 1)).toBe(0);
    });
  });

  describe('lighting up the letter for a scrolled row', () => {
    const library = books('Anathem', 'Blindsight', 'Cryptonomicon', 'Dune', 'Excession');

    it('reads the first book of the row', () => {
      expect(LibraryGridLayout.letterAtRow(library, 0, 'title', 2)).toBe('A');
      expect(LibraryGridLayout.letterAtRow(library, 1, 'title', 2)).toBe('C');
      expect(LibraryGridLayout.letterAtRow(library, 2, 'title', 2)).toBe('E');
    });

    it('returns null past the end rather than a wrong letter', () => {
      // The viewport can report a range beyond the list mid-update; a stale
      // letter would be worse than none.
      expect(LibraryGridLayout.letterAtRow(library, 99, 'title', 2)).toBeNull();
      expect(LibraryGridLayout.letterAtRow([], 0, 'title', 6)).toBeNull();
    });

    it('always lands on a row that actually contains the book asked for', () => {
      // The guarantee that holds for every letter. The stronger one — that the
      // rail then highlights the same letter — does not, see below.
      for (const letter of LibraryGridLayout.availableLetters(library, 'title')) {
        const row = LibraryGridLayout.rowIndexOfLetter(library, letter, 'title', 2);
        const rowBooks = LibraryGridLayout.toRows(library, 2)[row];

        expect(rowBooks.some(b => LibraryGridLayout.letterOf(b, 'title') === letter))
          .withContext(`row ${row} should contain a "${letter}" book`).toBeTrue();
      }
    });

    it('highlights the row\'s first letter, which may not be the one tapped', () => {
      // Real, pre-existing behaviour, not a rounding slip: a row holds several
      // books with different letters and the rail can only show one, so it
      // shows the row's first. Tapping 'B' when Blindsight sits second in a row
      // beside Anathem scrolls correctly and lights up 'A'.
      //
      // Pinned rather than fixed because the alternative — highlighting the
      // tapped letter — would then disagree with the rail as soon as the user
      // scrolls one pixel, which is worse.
      const row = LibraryGridLayout.rowIndexOfLetter(library, 'B', 'title', 2);

      expect(row).toBe(0);
      expect(LibraryGridLayout.letterAtRow(library, row, 'title', 2)).toBe('A');
    });

    it('agrees in both directions whenever a letter starts a row', () => {
      const oneBookPerRow = 1;

      for (const letter of LibraryGridLayout.availableLetters(library, 'title')) {
        const row = LibraryGridLayout.rowIndexOfLetter(library, letter, 'title', oneBookPerRow);

        expect(LibraryGridLayout.letterAtRow(library, row, 'title', oneBookPerRow)).toBe(letter);
      }
    });
  });
});

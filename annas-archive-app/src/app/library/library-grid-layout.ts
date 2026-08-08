/**
 * Only the fields the grid actually files books by. Declared structurally
 * rather than imported because two `LibraryBook` types are in play — the API's
 * and the richer one `BookCardComponent` extends it into — and this module has
 * no reason to care which it is given.
 */
export interface GridBook {
  title?: string;
  authors?: string[];
  series?: string | null;
}

export type TileSize = 'small' | 'medium' | 'large';
export type SortOrder = 'title' | 'author' | 'recent' | 'series' | 'stars' | 'goodreads';

/**
 * The geometry and alphabet logic behind the library grid.
 *
 * All of this used to live inside `LibraryComponent`, tangled up with the
 * virtual-scroll viewport, the change detector and the zone — which meant the
 * one genuinely error-prone part could only be exercised by standing up a
 * component. That part is the conversion between a **row** index (what
 * `CdkVirtualScrollViewport` counts) and a **book** index (what everything else
 * counts). It was written out by hand at three call sites, in both directions,
 * and getting it wrong does not throw: it scrolls to the wrong place, or lights
 * up the wrong letter in the A–Z rail.
 *
 * Everything here is a pure function of its arguments.
 */
export const LibraryGridLayout = {
  /**
   * Row height in pixels. Virtual scrolling needs a fixed number up front —
   * it cannot measure rows it has not rendered — so these are constants that
   * must stay in step with the tile sizes in `library.component.scss`.
   */
  rowHeight(tileSize: TileSize): number {
    switch (tileSize) {
      case 'small': return 320;
      case 'large': return 480;
      default: return 400;
    }
  },

  /**
   * How many books share a row. CSS does the real layout; this only has to
   * agree with it well enough for the row maths to land on the right book.
   */
  itemsPerRow(tileSize: TileSize): number {
    switch (tileSize) {
      case 'small': return 8;
      case 'large': return 4;
      default: return 6;
    }
  },

  /** Groups a flat book list into the rows the viewport renders. */
  toRows<T extends GridBook>(books: T[], itemsPerRow: number): T[][] {
    const perRow = Math.max(1, itemsPerRow);
    const rows: T[][] = [];
    for (let i = 0; i < books.length; i += perRow) {
      rows.push(books.slice(i, i + perRow));
    }
    return rows;
  },

  /** The row a book sits in. */
  rowIndexOf(bookIndex: number, itemsPerRow: number): number {
    return Math.floor(bookIndex / Math.max(1, itemsPerRow));
  },

  /** The first book of a row — the inverse of {@link rowIndexOf}. */
  firstBookIndexOf(rowIndex: number, itemsPerRow: number): number {
    return rowIndex * Math.max(1, itemsPerRow);
  },

  /**
   * The letter a book files under, which depends on what the list is sorted by:
   * sorted by author the rail must follow authors, by series it must follow
   * series. Anything that does not start with A–Z — a number, a quote mark, a
   * non-Latin script — files under '#', because the rail has exactly 27 slots
   * and a book with nowhere to go would be unreachable from it.
   */
  letterOf(book: GridBook, sortOrder: SortOrder): string {
    let value: string;
    switch (sortOrder) {
      case 'author':
        value = book.authors?.[0] || '';
        break;
      case 'series':
        // Falls back to the title so a standalone book still files somewhere
        // sensible when the list is grouped by series.
        value = book.series?.trim() || book.title || '';
        break;
      default:
        value = book.title || '';
        break;
    }

    const letter = value.trim().charAt(0).toUpperCase();
    return letter >= 'A' && letter <= 'Z' ? letter : '#';
  },

  /** The letters that actually have books behind them, sorted. */
  availableLetters(books: GridBook[], sortOrder: SortOrder): string[] {
    const letters = new Set(books.map(book => LibraryGridLayout.letterOf(book, sortOrder)));
    return Array.from(letters).sort();
  },

  /** The full rail. '#' leads because it is where everything unsortable lands. */
  alphabet(): string[] {
    return ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('')];
  },

  /**
   * The rail is only meaningful for orders that are actually alphabetical.
   * Under 'recent' or a rating sort the letters would be in no order at all,
   * so jumping to one would move the view somewhere arbitrary.
   */
  showsAlphabetIndex(sortOrder: SortOrder): boolean {
    return sortOrder === 'title' || sortOrder === 'author' || sortOrder === 'series';
  },

  /** The row to scroll to for a letter, or -1 when no book files under it. */
  rowIndexOfLetter(
    books: GridBook[],
    letter: string,
    sortOrder: SortOrder,
    itemsPerRow: number
  ): number {
    const bookIndex = books.findIndex(book => LibraryGridLayout.letterOf(book, sortOrder) === letter);
    return bookIndex === -1 ? -1 : LibraryGridLayout.rowIndexOf(bookIndex, itemsPerRow);
  },

  /** The letter to light up when the viewport has scrolled to a given row. */
  letterAtRow(
    books: GridBook[],
    rowIndex: number,
    sortOrder: SortOrder,
    itemsPerRow: number
  ): string | null {
    const book = books[LibraryGridLayout.firstBookIndexOf(rowIndex, itemsPerRow)];
    return book ? LibraryGridLayout.letterOf(book, sortOrder) : null;
  }
};

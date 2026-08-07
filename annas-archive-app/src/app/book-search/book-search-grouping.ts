import { BookDto } from '../models/book-dto.model';
import { BookGroup } from '../models/book-group.model';
import { DisplayGroup } from '../components/search-results/search-results.component';
import { DISPLAYABLE_BOOK_FORMATS } from '../constants/book-formats';

/** Which books a group is narrowed to. Both are optional; an empty string
 *  means "no filter", matching how the selects report "All". */
export interface GroupFilters {
  author?: string;
  format?: string;
}

/**
 * Turning a flat result list into the collapsed cards the grid renders.
 *
 * Pulled out of `BookSearchComponent` because every rule here decides what a
 * person sees and none of them were reachable from a test: the component held
 * them behind an AI call, a MatDialog and three getters.
 */
export const BookSearchGrouping = {
  /**
   * Rebuilds groups from the AI's md5 clusters. Any md5 the response invents
   * or the current page no longer holds is dropped, and a group left with
   * nothing disappears — a card cannot render a book that is not there.
   */
  fromMd5Groups(books: BookDto[], md5Groups: string[][]): BookGroup[] {
    const byMd5 = new Map(books.map(b => [b.md5, b]));

    return md5Groups
      .map(md5s => {
        const groupBooks = md5s
          .map(md5 => byMd5.get(md5))
          .filter((b): b is BookDto => !!b);
        return groupBooks.length > 0 ? { key: groupBooks[0].md5, books: groupBooks } : null;
      })
      .filter((g): g is BookGroup => g !== null);
  },

  /**
   * One book per group. Used when grouping fails: duplicates stay uncollapsed,
   * but nothing disappears, which is the right way round.
   */
  ungrouped(books: BookDto[]): BookGroup[] {
    return books.map(b => ({ key: b.md5, books: [b] }));
  },

  /**
   * Applies the author/format filters *inside* each group rather than across
   * the flat list. A format filter therefore narrows which books in a group
   * are eligible without dropping the group — the card stays, showing the
   * copy that matched.
   */
  filter(
    groups: BookGroup[],
    filters: GroupFilters,
    authorMatches: (author: string, selected: string) => boolean
  ): BookGroup[] {
    return groups
      .map(group => {
        let books = group.books;

        if (filters.author) {
          const selected = filters.author;
          books = books.filter(b => b.authors.some(author => authorMatches(author, selected)));
        }

        if (filters.format) {
          books = books.filter(b => b.format === filters.format);
        }

        return books.length > 0 ? { key: group.key, books } : null;
      })
      .filter((g): g is BookGroup => g !== null);
  },

  /**
   * The book a group's card shows and its send buttons act on:
   *
   * 1. the variant the user explicitly picked, if it survived filtering;
   * 2. otherwise the first match in `DISPLAYABLE_BOOK_FORMATS` order, so a
   *    card never defaults to an AZW3 while an EPUB sits in the same group;
   * 3. otherwise whatever is first, because a card with nothing on it is
   *    worse than a card showing an unusual format.
   */
  activeBookIn(group: BookGroup, selectedMd5?: string): BookDto {
    const selected = selectedMd5 ? group.books.find(b => b.md5 === selectedMd5) : undefined;
    if (selected) return selected;

    for (const format of DISPLAYABLE_BOOK_FORMATS) {
      const preferred = group.books.find(b => b.format === format);
      if (preferred) return preferred;
    }

    return group.books[0];
  },

  /** What the grid renders: each group paired with its active book. */
  toDisplayGroups(groups: BookGroup[], selection: ReadonlyMap<string, string>): DisplayGroup[] {
    return groups.map(group => ({
      group,
      active: BookSearchGrouping.activeBookIn(group, selection.get(group.key))
    }));
  }
};

/** The fields a bulk edit can change on a book. */
export interface BulkEditableBook {
  title?: string;
  authors?: string[];
  primaryGenre?: string | null;
  tags?: string[] | null;
  series?: string | null;
}

/** What the bulk-edit dialog came back with. Every field is optional: the
 * dialog only reports what the user actually touched, and `undefined` means
 * "leave this alone" — which is not the same as `null`, meaning "clear it". */
export interface BulkEditChanges {
  authors?: string[];
  primaryGenre?: string;
  tags?: string[];
  tagsMode?: 'append' | 'replace';
  series?: string | null;
  owners?: string[];
}

/** The new field values for one book. */
export interface BulkEditResult {
  authors?: string[];
  primaryGenre?: string | null;
  tags: string[];
  series?: string | null;
}

/** The shape `updateLibraryBookMetadata` expects. */
export interface BulkEditMetadataPayload {
  primaryGenre: string;
  tags: string[];
  series: string | null;
  title?: string;
  authors?: string[];
}

/**
 * The rules for applying one dialog result across a batch of books.
 *
 * These lived inside the `afterClosed()` subscription, which is why the
 * owner rule below — the one with a comment saying it had already been got
 * wrong once — had no test. Applying a change to fifty books at once is
 * exactly where a merge-versus-replace mistake does the most damage and is
 * hardest to notice.
 */
export const LibraryBulkEdit = {
  /**
   * Genre tags offer append or replace because both are reasonable: "add
   * 'space opera' to these forty books" and "these are the only tags now" are
   * both things people mean.
   */
  mergeTags(existing: string[] | null | undefined, incoming: string[], mode?: 'append' | 'replace'): string[] {
    if (mode !== 'append') return [...incoming];

    // Set, not concat: appending a tag a book already carries must not
    // duplicate it.
    return [...new Set([...(existing ?? []), ...incoming])];
  },

  /**
   * Owners are **always** a replace, even though they live in the same `tags`
   * array as genres and genres have an append/replace toggle.
   *
   * "Set owner to Mom" across a batch means these books are Mom's now — not
   * "add Mom alongside whoever is already on each one". Appending here is the
   * same mistake that was once fixed in the Kindle-send path: it silently
   * accumulates owners until a book belongs to everybody, and nothing in the
   * UI shows that it happened.
   */
  replaceOwners(tags: string[] | null | undefined, owners: string[], ownerTags: readonly string[]): string[] {
    const nonOwnerTags = (tags ?? []).filter(tag => !ownerTags.includes(tag));
    return [...nonOwnerTags, ...owners];
  },

  /**
   * The new values for one book. Owners are applied after tags because they
   * share the array — a genre replace followed by an owner change has to keep
   * the owner, not the pre-replace state.
   */
  applyTo(book: BulkEditableBook, changes: BulkEditChanges, ownerTags: readonly string[]): BulkEditResult {
    const result: BulkEditResult = {
      authors: changes.authors ? changes.authors : book.authors,
      primaryGenre: changes.primaryGenre !== undefined ? changes.primaryGenre : book.primaryGenre,
      tags: changes.tags
        ? LibraryBulkEdit.mergeTags(book.tags, changes.tags, changes.tagsMode)
        : [...(book.tags ?? [])],
      series: changes.series !== undefined ? changes.series : book.series
    };

    if (changes.owners && changes.owners.length > 0) {
      result.tags = LibraryBulkEdit.replaceOwners(result.tags, changes.owners, ownerTags);
    }

    return result;
  },

  /**
   * What gets sent to the server, derived from the values already applied
   * rather than from the original book — otherwise clearing a field would
   * read back the value it just cleared.
   */
  metadataPayload(applied: BulkEditResult, title?: string): BulkEditMetadataPayload {
    return {
      primaryGenre: applied.primaryGenre ?? 'Uncategorized',
      tags: applied.tags,
      series: applied.series ?? null,
      title,
      authors: applied.authors
    };
  }
};

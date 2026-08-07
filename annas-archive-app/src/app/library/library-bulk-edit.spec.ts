import { BulkEditableBook, LibraryBulkEdit } from './library-bulk-edit';

/**
 * Bulk edit applies one dialog result to every selected book at once, which is
 * where a merge-versus-replace mistake does the most damage and is hardest to
 * spot: nothing errors, the books just quietly end up wrong, fifty at a time.
 *
 * These rules lived in an `afterClosed()` subscription and had no test —
 * including the owner rule, which carries a comment saying it had already been
 * got wrong once elsewhere.
 */
describe('LibraryBulkEdit', () => {
  const OWNER_TAGS = ["Paul's Books", "Mom's Books", "Dad's Books"] as const;

  const book = (over: Partial<BulkEditableBook> = {}): BulkEditableBook => ({
    title: 'Dune',
    authors: ['Frank Herbert'],
    primaryGenre: 'Science Fiction',
    tags: ['space opera'],
    series: 'Dune Chronicles',
    ...over
  });

  describe('mergeTags', () => {
    it('replaces by default', () => {
      expect(LibraryBulkEdit.mergeTags(['old'], ['new'])).toEqual(['new']);
      expect(LibraryBulkEdit.mergeTags(['old'], ['new'], 'replace')).toEqual(['new']);
    });

    it('appends without duplicating what is already there', () => {
      expect(LibraryBulkEdit.mergeTags(['a', 'b'], ['b', 'c'], 'append')).toEqual(['a', 'b', 'c']);
    });

    it('appends onto a book with no tags', () => {
      expect(LibraryBulkEdit.mergeTags(null, ['a'], 'append')).toEqual(['a']);
      expect(LibraryBulkEdit.mergeTags(undefined, ['a'], 'append')).toEqual(['a']);
    });

    it('does not hand back the caller\'s array', () => {
      // Shared across every book in the batch — one book mutating it would
      // change the rest.
      const incoming = ['a'];
      const merged = LibraryBulkEdit.mergeTags(['x'], incoming, 'replace');
      merged.push('b');

      expect(incoming).toEqual(['a']);
    });

    it('can clear every tag', () => {
      expect(LibraryBulkEdit.mergeTags(['a', 'b'], [], 'replace')).toEqual([]);
    });
  });

  describe('replaceOwners', () => {
    it('removes the previous owner rather than stacking a new one', () => {
      // The rule this exists for. "Set owner to Mom" across a batch means these
      // are Mom's books now — appending would accumulate owners until every
      // book belonged to everybody, with nothing in the UI showing it.
      const tags = ["Paul's Books", 'space opera'];

      expect(LibraryBulkEdit.replaceOwners(tags, ["Mom's Books"], OWNER_TAGS))
        .toEqual(['space opera', "Mom's Books"]);
    });

    it('keeps every tag that is not an owner', () => {
      const tags = ['space opera', "Dad's Books", 'hard sf'];

      expect(LibraryBulkEdit.replaceOwners(tags, ["Paul's Books"], OWNER_TAGS))
        .toEqual(['space opera', 'hard sf', "Paul's Books"]);
    });

    it('can assign a book to several people at once', () => {
      expect(LibraryBulkEdit.replaceOwners(["Paul's Books"], ["Mom's Books", "Dad's Books"], OWNER_TAGS))
        .toEqual(["Mom's Books", "Dad's Books"]);
    });

    it('handles a book that had no owner', () => {
      expect(LibraryBulkEdit.replaceOwners(['space opera'], ["Mom's Books"], OWNER_TAGS))
        .toEqual(['space opera', "Mom's Books"]);
      expect(LibraryBulkEdit.replaceOwners(null, ["Mom's Books"], OWNER_TAGS))
        .toEqual(["Mom's Books"]);
    });
  });

  describe('applyTo', () => {
    it('leaves alone every field the dialog did not touch', () => {
      // undefined means "not touched" — distinct from null, which means clear.
      const applied = LibraryBulkEdit.applyTo(book(), {}, OWNER_TAGS);

      expect(applied.authors).toEqual(['Frank Herbert']);
      expect(applied.primaryGenre).toBe('Science Fiction');
      expect(applied.tags).toEqual(['space opera']);
      expect(applied.series).toBe('Dune Chronicles');
    });

    it('clears a series set explicitly to null', () => {
      const applied = LibraryBulkEdit.applyTo(book(), { series: null }, OWNER_TAGS);

      expect(applied.series).toBeNull();
    });

    it('applies owners on top of a tag replace, not underneath it', () => {
      // Both land in the same array, so order decides the outcome. Applying
      // owners first would let the genre replace wipe them out again.
      const applied = LibraryBulkEdit.applyTo(
        book({ tags: ["Paul's Books", 'old genre'] }),
        { tags: ['new genre'], tagsMode: 'replace', owners: ["Mom's Books"] },
        OWNER_TAGS
      );

      expect(applied.tags).toEqual(['new genre', "Mom's Books"]);
    });

    it('applies owners on top of a tag append', () => {
      const applied = LibraryBulkEdit.applyTo(
        book({ tags: ["Paul's Books", 'space opera'] }),
        { tags: ['hard sf'], tagsMode: 'append', owners: ["Mom's Books"] },
        OWNER_TAGS
      );

      expect(applied.tags).toEqual(['space opera', 'hard sf', "Mom's Books"]);
      expect(applied.tags).not.toContain("Paul's Books");
    });

    it('ignores an empty owner list rather than unowning the book', () => {
      // "I did not change the owner" must not read as "remove the owner".
      const applied = LibraryBulkEdit.applyTo(
        book({ tags: ["Paul's Books"] }), { owners: [] }, OWNER_TAGS);

      expect(applied.tags).toEqual(["Paul's Books"]);
    });

    it('does not mutate the book it was given', () => {
      const original = book({ tags: ['space opera'] });

      LibraryBulkEdit.applyTo(original, { tags: ['x'], tagsMode: 'replace' }, OWNER_TAGS);

      expect(original.tags).toEqual(['space opera']);
    });

    it('copies the tags even when nothing changed them', () => {
      const original = book({ tags: ['space opera'] });
      const applied = LibraryBulkEdit.applyTo(original, {}, OWNER_TAGS);
      applied.tags.push('leaked');

      expect(original.tags).toEqual(['space opera']);
    });
  });

  describe('metadataPayload', () => {
    it('is built from the applied values, not the original book', () => {
      // Otherwise clearing a field would read back the value it just cleared.
      const applied = LibraryBulkEdit.applyTo(book(), { series: null }, OWNER_TAGS);

      expect(LibraryBulkEdit.metadataPayload(applied, 'Dune').series).toBeNull();
    });

    it('falls back to Uncategorized rather than sending nothing', () => {
      const applied = LibraryBulkEdit.applyTo(
        book({ primaryGenre: null }), {}, OWNER_TAGS);

      expect(LibraryBulkEdit.metadataPayload(applied).primaryGenre).toBe('Uncategorized');
    });

    it('keeps an explicitly emptied genre rather than defaulting it', () => {
      // '' is a value the user chose; only null/undefined mean "unset".
      const applied = LibraryBulkEdit.applyTo(book(), { primaryGenre: '' }, OWNER_TAGS);

      expect(LibraryBulkEdit.metadataPayload(applied).primaryGenre).toBe('');
    });

    it('carries the tags and authors that were applied', () => {
      const applied = LibraryBulkEdit.applyTo(
        book(),
        { tags: ['new'], tagsMode: 'replace', authors: ['Brian Herbert'], owners: ["Dad's Books"] },
        OWNER_TAGS
      );

      const payload = LibraryBulkEdit.metadataPayload(applied, 'Dune');

      expect(payload.tags).toEqual(['new', "Dad's Books"]);
      expect(payload.authors).toEqual(['Brian Herbert']);
      expect(payload.title).toBe('Dune');
    });
  });
});

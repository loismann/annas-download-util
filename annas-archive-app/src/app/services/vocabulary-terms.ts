/**
 * Deciding when two words the user tapped are the same word.
 *
 * Everything the vocabulary list does — "have I already learned this?", "is
 * this in my study list?", "don't ask the model to define it again" — is a
 * lookup against a normalised key. Normalise too little and *Rhizomes*,
 * *rhizome's* and *rhizome* become three separate entries in the study list.
 * Normalise too much and distinct words collide, and marking one known
 * silently hides the other.
 *
 * Pure functions, extracted from `VocabularyService` where they were private
 * and reachable only through a passthrough.
 */
export const VocabularyTerms = {
  /**
   * The canonical key for a term. Lower-cased, curly apostrophes straightened,
   * hyphens treated as spaces (so "root-book" and "root book" agree),
   * punctuation dropped, then a light singularisation.
   */
  normalize(term: string | null | undefined): string {
    if (!term) return '';

    let normalized = term
      .toLowerCase()
      .replace(/[‘’]/g, "'")
      .replace(/-/g, ' ')
      .replace(/[^a-z0-9'\s]/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();

    // Possessive first, then a trailing plural. The length guard keeps short
    // words whose final 's' is part of the word — "gas", "bus" — intact;
    // it is a heuristic, and words like "analysis" are why it stays cautious.
    if (normalized.endsWith("'s")) {
      normalized = normalized.slice(0, -2);
    } else if (normalized.endsWith('s') && normalized.length > 3) {
      normalized = normalized.slice(0, -1);
    }

    return normalized;
  },

  /**
   * A wider set of spellings for the "already known, do not define these"
   * list sent to the model.
   *
   * This is deliberately looser than {@link normalize}: over-listing costs a
   * few prompt tokens, while under-listing means the model burns a card
   * re-explaining a word the reader told it they knew.
   */
  promptVariants(terms: Iterable<string>): string[] {
    const variants = new Set<string>();

    for (const term of terms) {
      if (!term) continue;

      variants.add(term);
      variants.add(term.replace(/-/g, ' '));
      variants.add(term.replace(/-/g, ''));

      if (!term.endsWith('s') && term.length > 2) variants.add(`${term}s`);
      if (!term.endsWith('es') && term.length > 2) variants.add(`${term}es`);
    }

    return Array.from(variants).filter(Boolean);
  },

  /**
   * The book to filter by, or null for "everything". Returning null rather
   * than a boolean lets the caller narrow the id to a string, and keeps the
   * two spellings of "no filter" — absent, and the literal 'all' — in one
   * place instead of at each call site.
   */
  bookFilter(filterBookId?: string): string | null {
    return !filterBookId || filterBookId === 'all' ? null : filterBookId;
  },

  /**
   * Adds a book to a term's association list, without duplicating it.
   * Returns the new list; the caller decides where to store it.
   */
  withBook(existing: string[] | undefined, bookId: string): string[] {
    const books = existing ?? [];
    return books.includes(bookId) ? books : [...books, bookId];
  },

  /**
   * Removes a book from a term's association list. Returns null when the term
   * is left associated with nothing, which is the caller's signal to drop the
   * entry rather than keep an empty one.
   */
  withoutBook(existing: string[] | undefined, bookId: string): string[] | null {
    const remaining = (existing ?? []).filter(id => id !== bookId);
    return remaining.length > 0 ? remaining : null;
  }
};

import { VocabularyTerms } from './vocabulary-terms';

/**
 * Every vocabulary lookup — "do I already know this?", "is it in my study
 * list?", "don't re-define it" — is a comparison of normalised keys. Too
 * little normalisation and one word appears three times in the study list;
 * too much and two words collide, so marking one known silently hides the
 * other.
 */
describe('VocabularyTerms', () => {
  describe('normalize', () => {
    it('treats case and surrounding space as noise', () => {
      expect(VocabularyTerms.normalize('  Rhizome ')).toBe('rhizome');
      expect(VocabularyTerms.normalize('RHIZOME')).toBe('rhizome');
    });

    it('collapses the spellings of one word to one key', () => {
      // The whole point: these are one entry in the study list, not four.
      const forms = ['rhizome', 'Rhizomes', "rhizome's", 'Rhizome.'];

      expect(new Set(forms.map(f => VocabularyTerms.normalize(f))).size).toBe(1);
    });

    it('straightens curly apostrophes', () => {
      // Copied out of an EPUB, quotes are curly; typed by hand they are not.
      expect(VocabularyTerms.normalize('rhizome’s')).toBe(VocabularyTerms.normalize("rhizome's"));
    });

    it('treats a hyphen as a space', () => {
      expect(VocabularyTerms.normalize('root-book')).toBe('root book');
      expect(VocabularyTerms.normalize('root-book')).toBe(VocabularyTerms.normalize('root book'));
    });

    it('drops punctuation without gluing words together', () => {
      expect(VocabularyTerms.normalize('deus,ex')).toBe('deus ex');
      expect(VocabularyTerms.normalize('"Salem"')).toBe('salem');
    });

    it('keeps a short word whose last letter is s', () => {
      // Singularising "gas" to "ga" would make it unfindable.
      expect(VocabularyTerms.normalize('gas')).toBe('gas');
      expect(VocabularyTerms.normalize('bus')).toBe('bus');
    });

    it('strips a plural from a longer word', () => {
      expect(VocabularyTerms.normalize('rhizomes')).toBe('rhizome');
    });

    it('is idempotent', () => {
      // It is applied on write and again on lookup; a second pass must not
      // move the key, or a stored word stops matching itself.
      for (const term of ['Rhizomes', "rhizome's", 'root-book', 'gas', 'deus,ex', '']) {
        const once = VocabularyTerms.normalize(term);

        expect(VocabularyTerms.normalize(once)).withContext(term).toBe(once);
      }
    });

    it('returns empty for nothing usable', () => {
      expect(VocabularyTerms.normalize('')).toBe('');
      expect(VocabularyTerms.normalize('   ')).toBe('');
      expect(VocabularyTerms.normalize('!!!')).toBe('');
      expect(VocabularyTerms.normalize(null)).toBe('');
      expect(VocabularyTerms.normalize(undefined)).toBe('');
    });
  });

  describe('promptVariants', () => {
    it('offers the spellings the model might otherwise re-define', () => {
      const variants = VocabularyTerms.promptVariants(['rhizome']);

      expect(variants).toContain('rhizome');
      expect(variants).toContain('rhizomes');
      expect(variants).toContain('rhizomees');
    });

    it('expands a hyphenated term both ways', () => {
      const variants = VocabularyTerms.promptVariants(['root-book']);

      expect(variants).toContain('root book');
      expect(variants).toContain('rootbook');
    });

    it('does not add a plural to a word that already has one', () => {
      expect(VocabularyTerms.promptVariants(['rhizomes'])).not.toContain('rhizomess');
    });

    it('leaves very short words alone', () => {
      // "ax" -> "axs" is noise, and the list costs prompt tokens.
      expect(VocabularyTerms.promptVariants(['ax'])).toEqual(['ax']);
    });

    it('drops empties and duplicates', () => {
      const variants = VocabularyTerms.promptVariants(['rhizome', 'rhizome', '']);

      expect(variants.filter(v => v === 'rhizome').length).toBe(1);
      expect(variants).not.toContain('');
    });

    it('handles an empty vocabulary', () => {
      expect(VocabularyTerms.promptVariants([])).toEqual([]);
    });
  });

  describe('bookFilter', () => {
    it('treats both spellings of "no filter" the same', () => {
      expect(VocabularyTerms.bookFilter(undefined)).toBeNull();
      expect(VocabularyTerms.bookFilter('')).toBeNull();
      expect(VocabularyTerms.bookFilter('all')).toBeNull();
    });

    it('passes a real book id through', () => {
      expect(VocabularyTerms.bookFilter('dune.epub')).toBe('dune.epub');
    });
  });

  describe('book associations', () => {
    it('adds a book once', () => {
      expect(VocabularyTerms.withBook(['a'], 'b')).toEqual(['a', 'b']);
      expect(VocabularyTerms.withBook(['a', 'b'], 'b')).toEqual(['a', 'b']);
      expect(VocabularyTerms.withBook(undefined, 'a')).toEqual(['a']);
    });

    it('removes a book', () => {
      expect(VocabularyTerms.withoutBook(['a', 'b'], 'a')).toEqual(['b']);
    });

    it('reports null when nothing is left, so the entry can be dropped', () => {
      // Keeping a term mapped to an empty list would leave it in every
      // per-book filter's blind spot: present, but belonging to no book.
      expect(VocabularyTerms.withoutBook(['a'], 'a')).toBeNull();
      expect(VocabularyTerms.withoutBook([], 'a')).toBeNull();
      expect(VocabularyTerms.withoutBook(undefined, 'a')).toBeNull();
    });

    it('does not mutate the list it was given', () => {
      const books = ['a', 'b'];

      VocabularyTerms.withBook(books, 'c');
      VocabularyTerms.withoutBook(books, 'a');

      expect(books).toEqual(['a', 'b']);
    });
  });
});

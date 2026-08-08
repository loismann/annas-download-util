import {
  KnownWordLimit, MaxSectionWords, chapterPrompt, knownWordsFor, mergeVocabulary,
  parseVocabulary, sectionText, selectionPrompt, splitDefinitions, summaryWithoutDefinitions,
  wordCount
} from './vocabulary-extraction';

/**
 * All of this used to sit in a 2400-line component, so none of it had ever been run
 * without a TestBed. The parser in particular makes several judgement calls about
 * text a language model produced, which is the least predictable input in the app.
 */
describe('vocabulary-extraction', () => {
  /** Nothing is known, and terms compare case-insensitively — the common case. */
  const normalize = (t: string) => t.trim().toLowerCase();
  const nothingKnown: (normalized: string) => boolean = () => false;

  const parse = (text: string, isKnown: (normalized: string) => boolean = nothingKnown) =>
    parseVocabulary(text, normalize, isKnown);

  describe('splitDefinitions', () => {
    it('splits prose from the definitions that follow it', () => {
      const { analysis, definitions } = splitDefinitions(
        'The chapter argues X.\n\nDefinitions:\n- **Ontology**: the study of being');

      expect(analysis).toBe('The chapter argues X.');
      expect(definitions).toBe('- **Ontology**: the study of being');
    });

    /**
     * The prompt asks for "Definitions:" but the model writes the singular often
     * enough that matching only the plural would silently drop the entire list.
     */
    it('accepts the singular heading the model actually writes', () => {
      expect(splitDefinitions('Prose.\n\nDefinition: a thing').definitions).toBe('a thing');
    });

    it('is case-insensitive about the heading', () => {
      expect(splitDefinitions('Prose.\n\nDEFINITIONS:\nx: y').definitions).toBe('x: y');
    });

    it('treats a summary with no heading as all prose', () => {
      const { analysis, definitions } = splitDefinitions('  Just a summary.  ');

      expect(analysis).toBe('Just a summary.');
      expect(definitions).toBe('');
    });

    it('exposes the prose half on its own for rendering', () => {
      expect(summaryWithoutDefinitions('Prose.\n\nDefinitions:\nx: y')).toBe('Prose.');
    });
  });

  describe('parseVocabulary', () => {
    it('reads the bold form the model usually produces', () => {
      expect(parse('- **Ontology**: the study of being'))
        .toEqual([{ term: 'Ontology', definition: 'the study of being' }]);
    });

    it('reads a plain term and definition', () => {
      expect(parse('Ontology: the study of being'))
        .toEqual([{ term: 'Ontology', definition: 'the study of being' }]);
    });

    it('strips numbered and bulleted list markers', () => {
      const words = parse('1. Alpha: first\n2) Beta: second\n• Gamma: third\n- Delta: fourth');

      expect(words.map(w => w.term)).toEqual(['Alpha', 'Beta', 'Gamma', 'Delta']);
    });

    /**
     * The reason the bullet regex has a negative lookahead. A lone `*` is a bullet,
     * but `**` opens a bold term — stripping one of the pair leaves `*term**`, which
     * no longer matches the bold pattern and keeps a stray asterisk in the term.
     */
    it('does not mistake the opening of a bold term for a bullet', () => {
      expect(parse('**Ontology**: the study of being'))
        .toEqual([{ term: 'Ontology', definition: 'the study of being' }]);
    });

    it('strips a bullet that really is a bullet before a bold term', () => {
      expect(parse('* **Ontology**: the study of being'))
        .toEqual([{ term: 'Ontology', definition: 'the study of being' }]);
    });

    /**
     * Definitions frequently contain colons. The term stops at the first one and the
     * rest stays in the definition.
     */
    it('splits on the first colon, so a definition may contain more', () => {
      expect(parse('Ontology: the study of being: what exists'))
        .toEqual([{ term: 'Ontology', definition: 'the study of being: what exists' }]);
    });

    /**
     * The bold pattern's lazy quantifier earns its keep here and nowhere else. When the
     * model puts two definitions on one line, a greedy `.+` runs to the *last* `**:`
     * and produces the term `Alpha**: first **Beta` — a card for a string that is not a
     * word. Lazy stops at the first closing pair.
     */
    it('stops at the first bold term when the model crams two onto one line', () => {
      expect(parse('**Alpha**: first **Beta**: second'))
        .toEqual([{ term: 'Alpha', definition: 'first **Beta**: second' }]);
    });

    it('skips blank lines and lines with no definition', () => {
      expect(parse('\n   \nOntology\n\nEpistemology: knowledge\n')).toEqual([
        { term: 'Epistemology', definition: 'knowledge' }
      ]);
    });

    it('drops a term whose definition is empty', () => {
      expect(parse('Ontology:   ')).toEqual([]);
    });

    it('omits words the reader already knows', () => {
      const known = (n: string) => n === 'ontology';

      expect(parse('Ontology: being\nEpistemology: knowledge', known))
        .toEqual([{ term: 'Epistemology', definition: 'knowledge' }]);
    });

    /**
     * The model repeats itself across a long definitions block, and the same term
     * twice would render as two identical cards.
     */
    it('keeps only the first of a repeated term, ignoring case', () => {
      const words = parse('Ontology: being\nontology: existence\nONTOLOGY: what is');

      expect(words).toEqual([{ term: 'Ontology', definition: 'being' }]);
    });

    it('returns nothing for an empty block', () => {
      expect(parse('')).toEqual([]);
    });
  });

  describe('mergeVocabulary', () => {
    const existing = [{ term: 'Ontology', definition: 'being' }];

    it('appends terms that are not already shown', () => {
      expect(mergeVocabulary(existing, [{ term: 'Epistemology', definition: 'knowledge' }]))
        .toEqual([
          { term: 'Ontology', definition: 'being' },
          { term: 'Epistemology', definition: 'knowledge' }
        ]);
    });

    /** The model does not capitalise consistently between requests. */
    it('treats a differently-cased duplicate as already present', () => {
      expect(mergeVocabulary(existing, [{ term: 'ONTOLOGY', definition: 'existence' }]))
        .toEqual(existing);
    });

    it('keeps the definition already on screen rather than overwriting it', () => {
      const merged = mergeVocabulary(existing, [{ term: 'Ontology', definition: 'something else' }]);

      expect(merged[0].definition).toBe('being');
    });

    it('de-duplicates within the incoming batch as well', () => {
      const merged = mergeVocabulary([], [
        { term: 'Alpha', definition: 'first' },
        { term: 'alpha', definition: 'again' }
      ]);

      expect(merged).toEqual([{ term: 'Alpha', definition: 'first' }]);
    });

    /**
     * Returning the same array reference when nothing was added is what lets the
     * caller report "all of these already exist" by comparing lengths.
     */
    it('returns the original list untouched when everything is a duplicate', () => {
      expect(mergeVocabulary(existing, [{ term: 'Ontology', definition: 'x' }])).toBe(existing);
    });

    it('handles an empty batch', () => {
      expect(mergeVocabulary(existing, [])).toBe(existing);
    });
  });

  describe('sectionText', () => {
    const chapter = Array.from({ length: 50 }, (_, i) => `w${i}`).join(' ');

    it('takes exactly the words the chunk covers', () => {
      expect(sectionText(chapter, { start: 10, wordCount: 3 })).toBe('w10 w11 w12');
    });

    it('collapses the whitespace the chapter happens to use', () => {
      expect(sectionText('a\n\nb   c', { start: 0, wordCount: 3 })).toBe('a b c');
    });

    /**
     * The cap is the reason this is worth testing: past it a section is silently sent
     * as a prefix of itself, and a truncation nobody can see is one nobody debugs.
     */
    it('caps the slice, so a long section is truncated rather than sent whole', () => {
      expect(wordCount(sectionText(chapter, { start: 0, wordCount: 50 }, 10))).toBe(10);
    });

    it('caps at a thousand words by default', () => {
      const huge = Array.from({ length: 1500 }, (_, i) => `w${i}`).join(' ');

      expect(wordCount(sectionText(huge, { start: 0, wordCount: 1500 }))).toBe(MaxSectionWords);
    });

    it('returns nothing for a chunk past the end of the chapter', () => {
      expect(sectionText(chapter, { start: 99, wordCount: 5 })).toBe('');
    });
  });

  describe('knownWordsFor', () => {
    const words = Array.from({ length: 250 }, (_, i) => `w${i}`);

    /** The tail, not the head — recently learned words are the ones worth excluding. */
    it('takes the most recent words, not the oldest', () => {
      expect(knownWordsFor(words, 3)).toEqual(['w247', 'w248', 'w249']);
    });

    it('returns everything when there are fewer than the limit', () => {
      expect(knownWordsFor(['a', 'b'], 100)).toEqual(['a', 'b']);
    });

    /** A chapter prompt covers more ground, so it can afford a longer exclusion list. */
    it('gives a chapter a longer tail than a selection', () => {
      expect(knownWordsFor(words, KnownWordLimit.chapter).length)
        .toBeGreaterThan(knownWordsFor(words, KnownWordLimit.selection).length);
    });
  });

  describe('selectionPrompt', () => {
    it('asks for exactly one card for a single word', () => {
      const prompt = selectionPrompt('ontology');

      expect(prompt).toContain('SINGLE WORD MODE');
      expect(prompt).toContain('"ontology"');
    });

    /**
     * Without the passage instruction the model returns a card per word, which buries
     * the few terms actually worth learning.
     */
    it('asks for selected key concepts for a passage', () => {
      const prompt = selectionPrompt('the study of being and what exists');

      expect(prompt).toContain('PHRASE/PASSAGE MODE');
      expect(prompt).toContain('DO NOT create a card for every word');
    });

    it('ignores surrounding whitespace when deciding which mode applies', () => {
      expect(selectionPrompt('   ontology   ')).toContain('SINGLE WORD MODE');
    });

    it('bounds the chapter-level ask so it cannot return the whole glossary', () => {
      expect(chapterPrompt()).toContain('10-20');
    });
  });

  describe('wordCount', () => {
    it('counts words separated by any whitespace', () => {
      expect(wordCount('a b\tc\nd')).toBe(4);
    });

    it('counts nothing for a blank string', () => {
      expect(wordCount('   ')).toBe(0);
      expect(wordCount('')).toBe(0);
    });
  });
});

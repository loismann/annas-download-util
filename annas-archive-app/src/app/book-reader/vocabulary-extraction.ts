import { VocabularyWord } from '../services/vocabulary.service';

/**
 * The decisions behind the reader's vocabulary features, as pure functions.
 *
 * These lived inside `book-reader.component.ts` — 2400 lines with a sanitizer, a
 * router, six services and a `destroy$` — so the parser that decides whether
 * `**term**: definition` is a bullet or a bold run could only be exercised by
 * standing up the component. None of it needs any of that.
 *
 * What stays in the component is the part that genuinely belongs to it: calling
 * `aiApi.createFlashcard(...)`, subscribing, and assigning the result. What moves here
 * is everything that decides *what* to ask for and *what to do with the answer*.
 */

/** How many previously-known words to send so the model does not re-teach them. */
export const KnownWordLimit = {
  /** A selection or one section — a short prompt, so a shorter tail. */
  selection: 100,
  /** A whole chapter earns a longer tail; there is more ground to avoid repeating. */
  chapter: 200
} as const;

/** Beyond this the section prompt is more text than the model needs. */
export const MaxSectionWords = 1000;

/**
 * Splits an AI summary into prose and its trailing definitions list.
 *
 * The model is asked for "Definitions:" but writes "Definition:" often enough that
 * matching only the plural silently drops the whole list.
 */
export function splitDefinitions(summary: string): { analysis: string; definitions: string } {
  const match = /definitions?\s*:/i.exec(summary);
  if (!match) return { analysis: summary.trim(), definitions: '' };

  return {
    analysis: summary.substring(0, match.index).trim(),
    definitions: summary.substring(match.index + match[0].length).trim()
  };
}

/** The prose half alone — what gets rendered as the section summary. */
export function summaryWithoutDefinitions(summary: string): string {
  return splitDefinitions(summary).analysis;
}

/**
 * Parses a definitions block into terms.
 *
 * The model's formatting is not stable, so this accepts the shapes it actually
 * produces: `- **Term**: Definition`, `1. Term: Definition`, `• Term: Definition`,
 * and the bare `Term: Definition`.
 *
 * `isKnown` and `normalize` are passed in rather than reached for, so the parser can
 * be tested without a `VocabularyService` and its `localStorage`.
 */
export function parseVocabulary(
  definitionsText: string,
  normalize: (term: string) => string,
  isKnown: (normalized: string) => boolean
): VocabularyWord[] {
  const words: VocabularyWord[] = [];
  const added = new Set<string>();

  for (const line of definitionsText.split('\n')) {
    const trimmed = line.trim();
    if (!trimmed) continue;

    const cleaned = trimmed
      // A lone '*' is a bullet, but '**' opens a bold term. Stripping one of the
      // pair leaves '*term**', which no longer matches the bold pattern below and
      // ends up keeping the stray asterisk.
      .replace(/^(?:[-•]|\*(?!\*))\s*/, '')
      .replace(/^\d+[.)]\s*/, '')
      .trim();

    // Bold form first: a bare-colon match would otherwise capture '**term**' whole.
    const match = cleaned.match(/^\*\*(.+?)\*\*:\s*(.+)$/) ||
                  cleaned.match(/^([^:]+?):\s*(.+)$/);
    if (!match) continue;

    const term = match[1].trim().replace(/\*\*/g, '');
    const definition = match[2].trim();
    const normalized = normalize(term);

    if (!normalized || !definition) continue;
    if (isKnown(normalized) || added.has(normalized)) continue;

    words.push({ term, definition });
    added.add(normalized);
  }

  return words;
}

/**
 * Adds newly generated cards to what is already on screen, ignoring any term already
 * shown. Case-insensitive, because the model does not capitalise consistently and the
 * same word arriving as "Ontology" and "ontology" would otherwise appear twice.
 */
export function mergeVocabulary(
  existing: VocabularyWord[],
  incoming: VocabularyWord[]
): VocabularyWord[] {
  const seen = new Set(existing.map(w => w.term.toLowerCase()));
  const fresh: VocabularyWord[] = [];

  for (const card of incoming) {
    const key = card.term.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    fresh.push({ term: card.term, definition: card.definition });
  }

  return fresh.length === 0 ? existing : [...existing, ...fresh];
}

/**
 * The slice of chapter text a section covers, capped.
 *
 * The cap is why this is worth having separately: a long section silently becomes a
 * prefix of itself, and that truncation should be visible and testable rather than
 * buried in a subscribe callback.
 */
export function sectionText(
  chapterContent: string,
  chunk: { start: number; wordCount: number },
  maxWords: number = MaxSectionWords
): string {
  const words = chapterContent.split(/\s+/);
  return words
    .slice(chunk.start, chunk.start + chunk.wordCount)
    .slice(0, maxWords)
    .join(' ');
}

/** The tail of the known-word list to send with a prompt. */
export function knownWordsFor(allKnownWords: string[], limit: number): string[] {
  return allKnownWords.slice(-limit);
}

/**
 * What to tell the model about a selection. One word is a straightforward lookup;
 * a passage is a judgement call, and without this instruction the model returns a card
 * per word, which buries the terms actually worth learning.
 */
export function selectionPrompt(selection: string): string {
  const isSingleWord = wordCount(selection) === 1;

  return isSingleWord
    ? `SINGLE WORD MODE: Create exactly ONE flashcard for the word "${selection}". Include etymology, definition, usage examples, and any specialized meanings in this context.`
    : `PHRASE/PASSAGE MODE: Analyze this selection and create flashcards for the KEY CONCEPTS or DIFFICULT TERMS (not every word). Create 1-5 cards depending on complexity. Focus on:
- Main philosophical/technical concepts being discussed
- Specialized terminology that needs explanation
- Foreign phrases or archaic language
- Historical/cultural references

DO NOT create a card for every word. Only create cards for terms that add educational value.`;
}

/** Chapter-level ask: a bounded set of the terms that matter most. */
export function chapterPrompt(): string {
  return `CHAPTER-LEVEL VOCABULARY: Analyze this entire chapter and identify 10-20 of the most important, challenging, or specialized terms that a reader should understand. Focus on:
- Key philosophical, technical, or domain-specific concepts
- Specialized terminology crucial to understanding the chapter
- Difficult or archaic language
- Important historical or cultural references

DO NOT include common words. Only create flashcards for terms that significantly enhance comprehension of the chapter's main ideas.`;
}

export function wordCount(text: string): number {
  const trimmed = text.trim();
  return trimmed === '' ? 0 : trimmed.split(/\s+/).length;
}

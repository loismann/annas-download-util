import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

export interface VocabularyWord {
  term: string;
  definition: string;
}

interface StoredVocabEntry {
  term: string;
  books: string[];
}

interface LearnMoreEntry {
  detail: string;
  images: string[];
}

@Injectable({
  providedIn: 'root'
})
export class VocabularyService {
  private readonly KNOWN_WORDS_KEY = 'vocabulary_known_words_map';
  private readonly UNKNOWN_WORDS_KEY = 'vocabulary_unknown_words_map';
  private readonly BOOK_NAMES_KEY = 'vocabulary_book_names';
  private readonly LEARN_MORE_CACHE_KEY = 'vocabulary_learn_more_cache';

  private knownWordsSubject = new BehaviorSubject<Map<string, Set<string>>>(new Map());
  private unknownWordsSubject = new BehaviorSubject<Map<string, { books: Set<string>; definition?: string }>>(new Map());
  private bookNames = new Map<string, string>();
  private learnMoreCache = new Map<string, LearnMoreEntry>();

  public knownWords$: Observable<Map<string, Set<string>>> = this.knownWordsSubject.asObservable();
  public unknownWords$: Observable<Map<string, { books: Set<string>; definition?: string }>> = this.unknownWordsSubject.asObservable();

  constructor() {
    this.loadFromStorage();
  }

  /**
   * Normalize a term for matching/prompting: lower-case, trim, collapse punctuation,
   * strip simple plurals/possessives, and normalize curly quotes.
   */
  private normalizeTerm(term: string): string {
    if (!term) return '';
    let normalized = term
      .toLowerCase()
      .replace(/[\u2018\u2019]/g, "'") // curly to straight apostrophe
      .replace(/[^a-z0-9'\\-\\s]/g, ' ') // remove other punctuation
      .replace(/\s+/g, ' ')
      .trim();

    if (normalized.endsWith("'s")) {
      normalized = normalized.slice(0, -2);
    } else if (normalized.endsWith('s') && normalized.length > 3) {
      normalized = normalized.slice(0, -1);
    }

    return normalized;
  }

  normalizeForMatch(term: string): string {
    return this.normalizeTerm(term);
  }

  private loadFromStorage(): void {
    try {
      const knownJson = localStorage.getItem(this.KNOWN_WORDS_KEY);
      if (knownJson) {
        const knownArray = JSON.parse(knownJson) as StoredVocabEntry[];
        const map = new Map<string, Set<string>>();
        knownArray.forEach(item => {
          const term = this.normalizeTerm(item.term);
          if (!term) return;
          const books = new Set(item.books || []);
          map.set(term, books.size ? books : new Set(['global']));
        });
        this.knownWordsSubject.next(map);
      }

      const unknownJson = localStorage.getItem(this.UNKNOWN_WORDS_KEY);
      if (unknownJson) {
        const unknownArray = JSON.parse(unknownJson) as Array<[string, { definition?: string; books?: string[] }]>;
        const map = new Map<string, { books: Set<string>; definition?: string }>();
        unknownArray.forEach(([term, info]) => {
          const normalized = this.normalizeTerm(term);
          if (!normalized) return;
          const books = new Set(info.books || []);
          map.set(normalized, { definition: info.definition, books: books.size ? books : new Set(['global']) });
        });
        this.unknownWordsSubject.next(map);
      }

      const namesJson = localStorage.getItem(this.BOOK_NAMES_KEY);
      if (namesJson) {
        const parsed = JSON.parse(namesJson) as Record<string, string>;
        Object.entries(parsed).forEach(([id, name]) => this.bookNames.set(id, name));
      }

      const learnMoreJson = localStorage.getItem(this.LEARN_MORE_CACHE_KEY);
      if (learnMoreJson) {
        const parsed = JSON.parse(learnMoreJson) as Record<string, LearnMoreEntry>;
        Object.entries(parsed).forEach(([term, entry]) => {
          const normalized = this.normalizeTerm(term);
          if (normalized && entry?.detail) {
            this.learnMoreCache.set(normalized, {
              detail: entry.detail,
              images: entry.images || []
            });
          }
        });
      }
    } catch (error) {
      console.error('Failed to load vocabulary from storage', error);
    }
  }

  private saveToStorage(): void {
    try {
      const knownArray: StoredVocabEntry[] = Array.from(this.knownWordsSubject.value.entries()).map(([term, books]) => ({
        term,
        books: Array.from(books)
      }));
      localStorage.setItem(this.KNOWN_WORDS_KEY, JSON.stringify(knownArray));

      const unknownArray = Array.from(this.unknownWordsSubject.value.entries()).map(([term, info]) => [
        term,
        { definition: info.definition, books: Array.from(info.books) }
      ]);
      localStorage.setItem(this.UNKNOWN_WORDS_KEY, JSON.stringify(unknownArray));

      const names: Record<string, string> = {};
      this.bookNames.forEach((name, id) => (names[id] = name));
      localStorage.setItem(this.BOOK_NAMES_KEY, JSON.stringify(names));

      const learnMore: Record<string, LearnMoreEntry> = {};
      this.learnMoreCache.forEach((entry, term) => (learnMore[term] = entry));
      localStorage.setItem(this.LEARN_MORE_CACHE_KEY, JSON.stringify(learnMore));
    } catch (error) {
      console.error('Failed to save vocabulary to storage', error);
    }
  }

  markAsKnown(term: string, bookId?: string): void {
    const id = bookId || 'global';
    const known = this.knownWordsSubject.value;
    const unknown = this.unknownWordsSubject.value;

    const normalizedTerm = this.normalizeTerm(term);
    if (!normalizedTerm) return;

    const knownBooks = known.get(normalizedTerm) ?? new Set<string>();
    knownBooks.add(id);
    known.set(normalizedTerm, knownBooks);
    this.knownWordsSubject.next(known);

    const unknownInfo = unknown.get(normalizedTerm);
    if (unknownInfo) {
      unknownInfo.books.delete(id);
      if (unknownInfo.books.size === 0) {
        unknown.delete(normalizedTerm);
      } else {
        unknown.set(normalizedTerm, unknownInfo);
      }
      this.unknownWordsSubject.next(unknown);
    }

    this.saveToStorage();
  }

  markAsUnknown(term: string, definition: string, bookId?: string): void {
    const id = bookId || 'global';
    const known = this.knownWordsSubject.value;
    const unknown = this.unknownWordsSubject.value;

    const normalizedTerm = this.normalizeTerm(term);
    if (!normalizedTerm) return;

    const unknownInfo = unknown.get(normalizedTerm) ?? { books: new Set<string>(), definition };
    unknownInfo.books.add(id);
    if (!unknownInfo.definition) unknownInfo.definition = definition;
    unknown.set(normalizedTerm, unknownInfo);
    this.unknownWordsSubject.next(unknown);

    if (known.has(normalizedTerm)) {
      const books = known.get(normalizedTerm)!;
      books.delete(id);
      if (books.size === 0) {
        known.delete(normalizedTerm);
      } else {
        known.set(normalizedTerm, books);
      }
      this.knownWordsSubject.next(known);
    }

    this.saveToStorage();
  }

  isKnown(term: string): boolean {
    const normalizedTerm = this.normalizeTerm(term);
    return !!normalizedTerm && this.knownWordsSubject.value.has(normalizedTerm);
  }

  /**
   * Return a richer set of tokens for the prompt so the model is less likely
   * to re-define already-known terms (includes simple variants).
   */
  getKnownWordsForPrompt(): string[] {
    const variants = new Set<string>();
    this.knownWordsSubject.value.forEach((_, term) => {
      if (!term) return;
      variants.add(term);
      // common simple variants
      variants.add(term.replace(/-/g, ' '));
      variants.add(term.replace(/-/g, ''));
      if (!term.endsWith('s') && term.length > 2) {
        variants.add(`${term}s`);
      }
      if (!term.endsWith('es') && term.length > 2) {
        variants.add(`${term}es`);
      }
    });
    return Array.from(variants).filter(Boolean);
  }

  getKnownWords(filterBookId?: string): string[] {
    const terms: string[] = [];
    this.knownWordsSubject.value.forEach((books, term) => {
      if (!filterBookId || books.has(filterBookId) || books.has('global')) {
        terms.push(term);
      }
    });
    return terms;
  }

  getUnknownWords(filterBookId?: string): Map<string, string> {
    const result = new Map<string, string>();
    this.unknownWordsSubject.value.forEach((info, term) => {
      if (!filterBookId || info.books.has(filterBookId) || info.books.has('global')) {
        result.set(term, info.definition ?? '');
      }
    });
    return result;
  }

  clearAll(): void {
    this.knownWordsSubject.next(new Map());
    this.unknownWordsSubject.next(new Map());
    localStorage.removeItem(this.KNOWN_WORDS_KEY);
    localStorage.removeItem(this.UNKNOWN_WORDS_KEY);
    localStorage.removeItem(this.BOOK_NAMES_KEY);
  }

  clearKnown(): void {
    this.knownWordsSubject.next(new Map());
    localStorage.removeItem(this.KNOWN_WORDS_KEY);
  }

  clearUnknown(): void {
    this.unknownWordsSubject.next(new Map());
    localStorage.removeItem(this.UNKNOWN_WORDS_KEY);
  }

  registerBook(bookId: string, name: string): void {
    if (!bookId) return;
    this.bookNames.set(bookId, name || bookId);
    this.saveToStorage();
  }

  getBookFilters(): { id: string; name: string }[] {
    const list: { id: string; name: string }[] = [{ id: 'all', name: 'All books' }];
    this.bookNames.forEach((name, id) => list.push({ id, name }));
    return list;
  }

  cacheLearnMore(term: string, detail: string, images: string[]): void {
    const normalized = this.normalizeTerm(term);
    if (!normalized) return;
    this.learnMoreCache.set(normalized, { detail, images });
    this.saveToStorage();
  }

  getCachedLearnMore(term: string): LearnMoreEntry | undefined {
    const normalized = this.normalizeTerm(term);
    if (!normalized) return undefined;
    return this.learnMoreCache.get(normalized);
  }
}

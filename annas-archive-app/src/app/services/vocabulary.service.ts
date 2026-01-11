import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';
import { AnnaArchiveApiService } from './anna-archive-api.service';

export interface VocabularyWord {
  term: string;
  definition: string;
}

interface LearnMoreEntry {
  detail: string;
  images: string[];
}

@Injectable({
  providedIn: 'root'
})
export class VocabularyService {
  private readonly BOOK_NAMES_KEY = 'vocabulary_book_names';
  private readonly LEARN_MORE_CACHE_KEY = 'vocabulary_learn_more_cache';
  private readonly DEFINITIONS_CACHE_KEY = 'vocabulary_definitions_cache';

  private knownWordsSubject = new BehaviorSubject<Set<string>>(new Set());
  private studyWordsSubject = new BehaviorSubject<Map<string, string>>(new Map());
  private knownWordsWithBooks = new Map<string, string[]>(); // term -> bookIds
  private studyWordsWithBooks = new Map<string, { definition: string; books: string[] }>(); // term -> { definition, bookIds }
  private definitionsCache = new Map<string, string>(); // term -> definition (persistent cache, never deleted)
  private bookNames = new Map<string, string>();
  private learnMoreCache = new Map<string, LearnMoreEntry>();

  public knownWords$: Observable<Set<string>> = this.knownWordsSubject.asObservable();
  public studyWords$: Observable<Map<string, string>> = this.studyWordsSubject.asObservable();

  constructor(private apiService: AnnaArchiveApiService) {
    this.loadFromServer();
    this.loadClientOnlyStorage();
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
      .replace(/-/g, ' ') // replace hyphens with spaces (so "root-book" becomes "root book")
      .replace(/[^a-z0-9'\s]/g, ' ') // remove other punctuation
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

  private loadFromServer(): void {
    console.log('🔄 [loadFromServer] Initializing vocabulary service, fetching from server...');

    this.apiService.getKnownWords().subscribe({
      next: (wordsWithBooks) => {
        console.log(`📥 [loadFromServer] Received known words from API:`, wordsWithBooks);

        // Store book associations
        this.knownWordsWithBooks.clear();
        const normalized = new Set<string>();

        Object.entries(wordsWithBooks).forEach(([term, bookIds]) => {
          const normalizedTerm = this.normalizeTerm(term);
          if (normalizedTerm) {
            normalized.add(normalizedTerm);
            this.knownWordsWithBooks.set(normalizedTerm, bookIds);
          }
        });

        this.knownWordsSubject.next(normalized);
        console.log(`✅ [loadFromServer] Loaded ${normalized.size} known words with book associations`, Array.from(normalized));
      },
      error: (err) => {
        console.error('❌ [loadFromServer] Failed to load known words from server:', err);
      }
    });

    this.apiService.getStudyWords().subscribe({
      next: (wordsWithBooks) => {
        console.log(`📥 [loadFromServer] Received study words from API:`, wordsWithBooks);

        // Store book associations
        this.studyWordsWithBooks.clear();
        const map = new Map<string, string>();

        Object.entries(wordsWithBooks).forEach(([term, data]) => {
          const normalized = this.normalizeTerm(term);
          if (normalized) {
            map.set(normalized, data.definition);
            this.studyWordsWithBooks.set(normalized, {
              definition: data.definition,
              books: data.books
            });
            // Cache definition for future use
            if (data.definition && data.definition.trim()) {
              this.definitionsCache.set(normalized, data.definition);
            }
          }
        });

        this.studyWordsSubject.next(map);
        this.saveClientOnlyStorage(); // Save cached definitions to localStorage
        console.log(`✅ [loadFromServer] Loaded ${map.size} study words with book associations`, Array.from(map.entries()));
      },
      error: (err) => {
        console.error('❌ [loadFromServer] Failed to load study words from server:', err);
      }
    });
  }

  private loadClientOnlyStorage(): void {
    try {
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

      const definitionsJson = localStorage.getItem(this.DEFINITIONS_CACHE_KEY);
      if (definitionsJson) {
        const parsed = JSON.parse(definitionsJson) as Record<string, string>;
        Object.entries(parsed).forEach(([term, definition]) => {
          const normalized = this.normalizeTerm(term);
          if (normalized && definition) {
            this.definitionsCache.set(normalized, definition);
          }
        });
        console.log(`💿 [loadClientOnlyStorage] Loaded ${this.definitionsCache.size} cached definitions`);
      }
    } catch (error) {
      console.error('Failed to load client-only vocabulary data', error);
    }
  }

  private saveClientOnlyStorage(): void {
    try {
      const names: Record<string, string> = {};
      this.bookNames.forEach((name, id) => (names[id] = name));
      localStorage.setItem(this.BOOK_NAMES_KEY, JSON.stringify(names));

      const learnMore: Record<string, LearnMoreEntry> = {};
      this.learnMoreCache.forEach((entry, term) => (learnMore[term] = entry));
      localStorage.setItem(this.LEARN_MORE_CACHE_KEY, JSON.stringify(learnMore));

      const definitions: Record<string, string> = {};
      this.definitionsCache.forEach((definition, term) => (definitions[term] = definition));
      localStorage.setItem(this.DEFINITIONS_CACHE_KEY, JSON.stringify(definitions));
    } catch (error) {
      console.error('Failed to save client-only vocabulary data', error);
    }
  }

  markAsKnown(term: string, bookId?: string): void {
    console.log(`📗 [markAsKnown] Called with term='${term}', bookId='${bookId}'`);
    const normalizedTerm = this.normalizeTerm(term);
    if (!normalizedTerm) {
      console.warn(`⚠️ [markAsKnown] Normalized term is empty, skipping`);
      return;
    }
    console.log(`🔤 [markAsKnown] Normalized term: '${normalizedTerm}'`);

    // Update local state immediately for UI responsiveness
    // Create new Set instance to trigger BehaviorSubject emission
    const known = new Set(this.knownWordsSubject.value);
    const wasAlreadyKnown = known.has(normalizedTerm);
    known.add(normalizedTerm);
    this.knownWordsSubject.next(known);
    console.log(`💾 [markAsKnown] Updated local state (wasAlreadyKnown=${wasAlreadyKnown}, totalKnown=${known.size})`);

    // Update book association maps immediately for filtering
    if (bookId) {
      const existingBooks = this.knownWordsWithBooks.get(normalizedTerm) || [];
      if (!existingBooks.includes(bookId)) {
        this.knownWordsWithBooks.set(normalizedTerm, [...existingBooks, bookId]);
        console.log(`📚 [markAsKnown] Added book association: '${normalizedTerm}' -> book '${bookId}'`);
      }
    }

    // Create new Map instance to trigger BehaviorSubject emission
    const study = new Map(this.studyWordsSubject.value);
    if (study.has(normalizedTerm)) {
      study.delete(normalizedTerm);
      this.studyWordsSubject.next(study);
      console.log(`🔄 [markAsKnown] Removed from study list`);

      // Remove from study books association
      this.studyWordsWithBooks.delete(normalizedTerm);
      console.log(`📚 [markAsKnown] Removed from study book associations`);
    }

    // Persist to server with bookId
    console.log(`🌐 [markAsKnown] Calling API to persist to server with bookId='${bookId}'...`);
    this.apiService.addKnownWord(normalizedTerm, bookId).subscribe({
      next: (response) => {
        console.log(`✅ [markAsKnown] Server confirmed: '${normalizedTerm}' marked as known for book '${bookId}'`, response);
      },
      error: (err) => {
        console.error(`❌ [markAsKnown] Failed to mark "${normalizedTerm}" as known:`, err);
        // Rollback on error
        const rolledBack = new Set(this.knownWordsSubject.value);
        rolledBack.delete(normalizedTerm);
        this.knownWordsSubject.next(rolledBack);

        // Rollback book association
        if (bookId) {
          const books = this.knownWordsWithBooks.get(normalizedTerm) || [];
          const filtered = books.filter(id => id !== bookId);
          if (filtered.length > 0) {
            this.knownWordsWithBooks.set(normalizedTerm, filtered);
          } else {
            this.knownWordsWithBooks.delete(normalizedTerm);
          }
        }
        console.log(`↩️ [markAsKnown] Rolled back local state and book associations`);
      }
    });
  }

  markAsUnknown(term: string, definition: string, bookId?: string): void {
    console.log(`📕 [markAsUnknown] Called with term='${term}', definition='${definition}', bookId='${bookId}'`);
    const normalizedTerm = this.normalizeTerm(term);
    if (!normalizedTerm) {
      console.warn(`⚠️ [markAsUnknown] Normalized term is empty, skipping`);
      return;
    }
    console.log(`🔤 [markAsUnknown] Normalized term: '${normalizedTerm}'`);

    // Cache the definition permanently (even if empty, we'll try to retrieve it later if needed)
    if (definition && definition.trim()) {
      this.definitionsCache.set(normalizedTerm, definition);
      this.saveClientOnlyStorage();
      console.log(`💿 [markAsUnknown] Cached definition for '${normalizedTerm}'`);
    }

    // Update local state immediately for UI responsiveness
    // Create new Map instance to trigger BehaviorSubject emission
    const study = new Map(this.studyWordsSubject.value);
    const wasAlreadyStudy = study.has(normalizedTerm);
    study.set(normalizedTerm, definition);
    this.studyWordsSubject.next(study);
    console.log(`💾 [markAsUnknown] Updated local state (wasAlreadyStudy=${wasAlreadyStudy}, totalStudy=${study.size})`);

    // Update book association maps immediately for filtering
    if (bookId) {
      const existing = this.studyWordsWithBooks.get(normalizedTerm);
      if (existing) {
        // Add to existing books if not already present
        if (!existing.books.includes(bookId)) {
          existing.books.push(bookId);
        }
        // Update definition (may be different from different books)
        existing.definition = definition;
      } else {
        // Create new entry
        this.studyWordsWithBooks.set(normalizedTerm, {
          definition,
          books: [bookId]
        });
      }
      console.log(`📚 [markAsUnknown] Added study book association: '${normalizedTerm}' -> book '${bookId}'`);
    }

    // Create new Set instance to trigger BehaviorSubject emission
    const known = new Set(this.knownWordsSubject.value);
    if (known.has(normalizedTerm)) {
      known.delete(normalizedTerm);
      this.knownWordsSubject.next(known);
      console.log(`🔄 [markAsUnknown] Removed from known list`);

      // Remove from known books association
      this.knownWordsWithBooks.delete(normalizedTerm);
      console.log(`📚 [markAsUnknown] Removed from known book associations`);
    }

    // Persist to server with bookId
    console.log(`🌐 [markAsUnknown] Calling API to persist to server with bookId='${bookId}'...`);
    this.apiService.addStudyWord(normalizedTerm, definition, bookId).subscribe({
      next: (response) => {
        console.log(`✅ [markAsUnknown] Server confirmed: '${normalizedTerm}' marked as study word for book '${bookId}'`, response);
      },
      error: (err) => {
        console.error(`❌ [markAsUnknown] Failed to mark "${normalizedTerm}" as study word:`, err);
        // Rollback on error
        const rolledBack = new Map(this.studyWordsSubject.value);
        rolledBack.delete(normalizedTerm);
        this.studyWordsSubject.next(rolledBack);

        // Rollback book association
        if (bookId) {
          const existing = this.studyWordsWithBooks.get(normalizedTerm);
          if (existing) {
            const filtered = existing.books.filter(id => id !== bookId);
            if (filtered.length > 0) {
              existing.books = filtered;
            } else {
              this.studyWordsWithBooks.delete(normalizedTerm);
            }
          }
        }
        console.log(`↩️ [markAsUnknown] Rolled back local state and book associations`);
      }
    });
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
    this.knownWordsSubject.value.forEach(term => {
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
    // If no filter or filter is 'all', return all known words
    if (!filterBookId || filterBookId === 'all') {
      const allWords = Array.from(this.knownWordsSubject.value);
      console.log(`📚 [getKnownWords] No filter - returning all ${allWords.length} known words`);
      return allWords;
    }

    // Filter by book ID
    const filtered: string[] = [];
    this.knownWordsWithBooks.forEach((bookIds, term) => {
      if (bookIds.includes(filterBookId)) {
        filtered.push(term);
      }
    });

    console.log(`📚 [getKnownWords] Filter='${filterBookId}': found ${filtered.length} words out of ${this.knownWordsSubject.value.size} total known`, filtered.slice(0, 5));
    return filtered;
  }

  getUnknownWords(filterBookId?: string): Map<string, string> {
    // If no filter or filter is 'all', return all study words
    if (!filterBookId || filterBookId === 'all') {
      const allWords = new Map(this.studyWordsSubject.value);
      console.log(`📚 [getUnknownWords] No filter - returning all ${allWords.size} study words`);
      return allWords;
    }

    // Filter by book ID
    const filtered = new Map<string, string>();
    this.studyWordsWithBooks.forEach((data, term) => {
      if (data.books.includes(filterBookId)) {
        filtered.set(term, data.definition);
      }
    });

    const sampleTerms = Array.from(filtered.keys()).slice(0, 5);
    console.log(`📚 [getUnknownWords] Filter='${filterBookId}': found ${filtered.size} words out of ${this.studyWordsSubject.value.size} total study`, sampleTerms);
    return filtered;
  }

  clearAll(): void {
    // Capture current state before clearing
    const knownWords = Array.from(this.knownWordsSubject.value);
    const studyWords = Array.from(this.studyWordsSubject.value.keys());

    // Clear local state
    this.knownWordsSubject.next(new Set());
    this.studyWordsSubject.next(new Map());

    // Clear known words on server
    knownWords.forEach(term => {
      this.apiService.removeKnownWord(term).subscribe({
        error: (err) => console.error(`Failed to remove known word "${term}"`, err)
      });
    });

    // Clear study words on server
    studyWords.forEach(term => {
      this.apiService.removeStudyWord(term).subscribe({
        error: (err) => console.error(`Failed to remove study word "${term}"`, err)
      });
    });

    localStorage.removeItem(this.BOOK_NAMES_KEY);
  }

  clearKnown(): void {
    const knownWords = Array.from(this.knownWordsSubject.value);
    this.knownWordsSubject.next(new Set());

    knownWords.forEach(term => {
      this.apiService.removeKnownWord(term).subscribe({
        error: (err) => console.error(`Failed to remove known word "${term}"`, err)
      });
    });
  }

  clearUnknown(): void {
    const studyWords = Array.from(this.studyWordsSubject.value.keys());
    this.studyWordsSubject.next(new Map());

    studyWords.forEach(term => {
      this.apiService.removeStudyWord(term).subscribe({
        error: (err) => console.error(`Failed to remove study word "${term}"`, err)
      });
    });
  }

  deleteBook(bookId: string, callback?: (success: boolean, message: string) => void): void {
    console.log(`🗑️ [deleteBook] Deleting all vocabulary for book '${bookId}'`);

    // Get book name before deleting
    const bookName = this.bookNames.get(bookId) || bookId;

    // Call API to delete all vocabulary for this book
    this.apiService.deleteBookVocab(bookId).subscribe({
      next: (response) => {
        console.log(`✅ [deleteBook] Server confirmed deletion:`, response);

        // Remove book from bookNames map
        this.bookNames.delete(bookId);
        this.saveClientOnlyStorage();

        // Reload vocabulary from server to refresh state
        this.loadFromServer();

        const message = `Deleted ${response.totalRemoved} vocabulary words from "${bookName}"`;
        if (callback) callback(true, message);
      },
      error: (err) => {
        console.error(`❌ [deleteBook] Failed to delete book vocabulary:`, err);
        if (callback) callback(false, 'Failed to delete book vocabulary');
      }
    });
  }

  registerBook(bookId: string, name: string): void {
    if (!bookId) return;
    this.bookNames.set(bookId, name || bookId);
    this.saveClientOnlyStorage();
  }

  getBookFilters(): { id: string; name: string }[] {
    const list: { id: string; name: string }[] = [{ id: 'all', name: 'All books' }];
    this.bookNames.forEach((name, id) => list.push({ id, name }));
    return list;
  }

  getCachedDefinition(term: string): string | undefined {
    const normalized = this.normalizeTerm(term);
    if (!normalized) return undefined;
    const cached = this.definitionsCache.get(normalized);
    console.log(`💿 [getCachedDefinition] Looking up '${term}' (normalized: '${normalized}'): ${cached ? 'FOUND' : 'NOT FOUND'}`);
    return cached;
  }

  cacheLearnMore(term: string, detail: string, images: string[]): void {
    const normalized = this.normalizeTerm(term);
    if (!normalized) return;
    this.learnMoreCache.set(normalized, { detail, images });
    this.saveClientOnlyStorage();
  }

  getCachedLearnMore(term: string): LearnMoreEntry | undefined {
    const normalized = this.normalizeTerm(term);
    if (!normalized) return undefined;
    return this.learnMoreCache.get(normalized);
  }
}

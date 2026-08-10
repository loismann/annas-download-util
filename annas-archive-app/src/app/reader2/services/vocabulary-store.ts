import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks } from './reader-tasks';
import { Definition, SectionVocabulary, TermState, VocabularyTerm } from '../reader2.models';

/**
 * The reader's own vocabulary, and the hard words in what they are reading.
 *
 * <p>Two different lifetimes in one store, deliberately: {@link terms} is the
 * reader's, survives every book, and is free to change; {@link sectionTerms} is
 * this passage's and costs a model call to produce. They belong together because
 * the first is what filters the second — a word you have marked known must stop
 * appearing, and keeping that relationship in one place is what makes it
 * impossible to update one without the other.</p>
 *
 * <p><b>Three methods here spend money</b> and all three say so in their names.
 * Everything else is a database row or a cache read.</p>
 */
@Injectable()
export class VocabularyStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);

  /** Everything this reader has filed, both states. */
  readonly terms = signal<VocabularyTerm[]>([]);

  readonly known = computed(() => this.terms().filter(t => t.state === 'Known'));
  readonly studying = computed(() => this.terms().filter(t => t.state === 'Studying'));

  /** The hard words in the section on screen, already filtered by the server. */
  readonly sectionTerms = signal<Definition[]>([]);

  /** The open deep dive, and which term it is about. */
  readonly deepDive = signal<{ term: string; html: string } | null>(null);

  // ─── the reader's own list, all free ────────────────────────────────

  async loadAsync(): Promise<void> {
    const loaded = await this.tasks.run(
      'Loading your vocabulary', () => firstValueFrom(this.api.vocabulary()));

    if (loaded) this.terms.set(loaded);
  }

  /**
   * Files a term, or moves one between known and studying.
   *
   * <p>Re-reads the list rather than patching it locally: the server normalises
   * the term to decide which row this is, and guessing at that here is how the
   * panel would come to show *naïveté* and *naivete* as two entries.</p>
   */
  async saveTermAsync(
    term: string, state: TermState, definition?: string, bookId?: string
  ): Promise<void> {
    const saved = await this.tasks.run(
      'Saving the term',
      async () => { await firstValueFrom(this.api.saveTerm(term, state, definition, bookId)); return true; });

    if (saved) await this.loadAsync();
  }

  async removeTermAsync(term: string): Promise<void> {
    const removed = await this.tasks.run(
      'Removing the term',
      async () => { await firstValueFrom(this.api.removeTerm(term)); return true; });

    if (removed) await this.loadAsync();
  }

  /**
   * Drops this book's vocabulary provenance.
   *
   * <p>The reader's terms survive — they were learnt, and forgetting a book does
   * not unlearn them. What goes is the record of which book each was first met
   * in, which is why this is a separate control from clearing a list.</p>
   */
  async forgetBookAsync(bookId: string): Promise<void> {
    const forgotten = await this.tasks.run(
      'Forgetting this book’s vocabulary',
      async () => { await firstValueFrom(this.api.forgetBookVocabulary(bookId)); return true; });

    if (forgotten) await this.loadAsync();
  }

  /** Clears one state, or everything when no state is given. */
  async clearAsync(state?: TermState): Promise<void> {
    const cleared = await this.tasks.run(
      'Clearing your vocabulary',
      async () => { await firstValueFrom(this.api.clearVocabulary(state)); return true; });

    if (cleared) await this.loadAsync();
  }

  // ─── the passage in front of the reader ─────────────────────────────

  /**
   * Whatever has already been generated for this section, or nothing.
   *
   * <p>Free, and safe to call on a page turn: a `GET` reads the cache and never
   * reaches a model. The panel shows an empty list and a button rather than
   * quietly generating, which is the rule the whole reader is built on.</p>
   */
  async loadSectionAsync(bookId: string, chapter: number, section: number): Promise<void> {
    this.sectionTerms.set([]);

    const loaded = await this.tasks.run(
      'Loading vocabulary',
      () => firstValueFrom(this.api.sectionVocabulary(bookId, chapter, section)));

    if (loaded) this.sectionTerms.set(loaded.terms);
  }

  async generateSectionAsync(
    bookId: string, chapter: number, section: number, force = false
  ): Promise<void> {
    const generated = await this.tasks.run(
      'Finding the hard words',
      () => firstValueFrom(this.api.generateSectionVocabulary(bookId, chapter, section, force)));

    if (generated) this.sectionTerms.set(generated.terms);
  }

  /** Every section of the chapter, streamed because it is one call per section. */
  async generateChapterAsync(bookId: string, chapter: number, force = false): Promise<void> {
    await this.tasks.stream<SectionVocabulary>(
      'Finding the hard words',
      this.api.chapterVocabulary(bookId, chapter, force),
      vocabulary => this.sectionTerms.set(vocabulary.terms));
  }

  async learnMoreAsync(
    bookId: string, term: string, context?: string, force = false
  ): Promise<void> {
    this.deepDive.set(null);

    const dive = await this.tasks.run(
      `Looking up “${term}”`,
      () => firstValueFrom(this.api.learnMore(bookId, term, context, force)));

    if (dive) this.deepDive.set({ term, html: dive.html });
  }

  closeDeepDive(): void {
    this.deepDive.set(null);
  }
}

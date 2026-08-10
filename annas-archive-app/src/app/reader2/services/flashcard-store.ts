import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks } from './reader-tasks';
import { Flashcard } from '../reader2.models';

/**
 * One book's deck of cards.
 *
 * <p>Separate from {@link VocabularyStore} because the two have different
 * owners: a filed term belongs to the reader and outlives every book, while a
 * deck belongs to a book and goes when the book is un-enrolled. Putting them in
 * one store would make that difference invisible at the call site.</p>
 *
 * <p>Nothing here spends money — a card is a term and a definition the reader
 * already has. Every route returns the whole deck, so there is no local
 * reconciliation to get wrong.</p>
 */
@Injectable()
export class FlashcardStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);

  readonly cards = signal<Flashcard[]>([]);

  private readonly bookId = signal<string | null>(null);

  async loadAsync(bookId: string): Promise<void> {
    this.bookId.set(bookId);
    this.cards.set([]);

    await this.applyAsync('Loading your cards', id => firstValueFrom(this.api.flashcards(id)));
  }

  async addAsync(term: string, definition: string): Promise<void> {
    await this.applyAsync(
      'Saving the card', id => firstValueFrom(this.api.addFlashcard(id, term, definition)));
  }

  async removeAsync(term: string): Promise<void> {
    await this.applyAsync(
      'Removing the card', id => firstValueFrom(this.api.removeFlashcard(id, term)));
  }

  async clearAsync(): Promise<void> {
    await this.applyAsync('Clearing the deck', id => firstValueFrom(this.api.clearFlashcards(id)));
  }

  /**
   * Every card route answers with the deck as it now stands, so one helper
   * covers all four — the alternative is the same six lines written four times,
   * differing only in the verb.
   */
  private async applyAsync(
    what: string, call: (bookId: string) => Promise<{ cards: Flashcard[] }>
  ): Promise<void> {
    const bookId = this.bookId();
    if (!bookId) return;

    const deck = await this.tasks.run(what, () => call(bookId));
    if (deck) this.cards.set(deck.cards);
  }
}

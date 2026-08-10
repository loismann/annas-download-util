import { ChangeDetectionStrategy, Component, Input, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { VocabularyPanelComponent, TermChange } from './components/vocabulary-panel.component';
import { FlashcardPanelComponent } from './components/flashcard-panel.component';
import { ReaderStore } from './services/reader-store';
import { VocabularyStore } from './services/vocabulary-store';
import { FlashcardStore } from './services/flashcard-store';
import { ReaderConfirm } from './services/reader-confirm';
import { sectionAt } from './services/pagination';

/**
 * The two panels about words: the reader's vocabulary, and this book's cards.
 *
 * <p>Its own container because the two share both stores and every action moves
 * between them — a word is found, defined, filed, and then made into a card.
 * Splitting them across containers would mean passing one store's state through
 * the other, and merging them into the general panel container would put two
 * unrelated topics in one file.</p>
 *
 * <p>Nothing here except finding hard words reaches a model. A card is made from
 * a definition that was already paid for.</p>
 */
@Component({
  selector: 'app-reader2-words',
  standalone: true,
  imports: [CommonModule, VocabularyPanelComponent, FlashcardPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-reader2-vocabulary-panel
      *ngIf="show === 'vocabulary'"
      [terms]="vocabulary.sectionTerms()"
      [known]="vocabulary.known()"
      [studying]="vocabulary.studying()"
      [dive]="vocabulary.deepDive()"
      [busy]="!!store.busy()"
      (generate)="findHardWords($event)"
      (generateChapter)="findHardWordsInChapter($event)"
      (makeCard)="flashcards.addAsync($event.term, $event.definition)"
      (forgetBook)="forgetBookVocabulary()"
      (file)="fileTerm($event)"
      (forget)="vocabulary.removeTermAsync($event)"
      (clear)="vocabulary.clearAsync($event)"
      (learnMore)="learnMore($event)"
      (closeDive)="vocabulary.closeDeepDive()" />

    <app-reader2-flashcard-panel
      *ngIf="show === 'flashcards'"
      [cards]="flashcards.cards()"
      (remove)="flashcards.removeAsync($event)"
      (clear)="flashcards.clearAsync()" />
  `,
  styleUrl: './reader-panels.component.scss'
})
export class WordsPanelComponent {
  protected readonly store = inject(ReaderStore);
  protected readonly vocabulary = inject(VocabularyStore);
  protected readonly flashcards = inject(FlashcardStore);
  private readonly confirm = inject(ReaderConfirm);

  @Input() show: 'vocabulary' | 'flashcards' | null = null;

  private get bookId(): string | null {
    return this.store.book()?.bookId ?? null;
  }

  /**
   * The hard words in the section the reader is actually in, rather than an
   * arbitrary one. Does nothing when the offset falls in no section.
   */
  protected async findHardWords(force: boolean): Promise<void> {
    const bookId = this.bookId;
    const section = sectionAt(this.store.sections(), this.store.wordOffset());
    if (!bookId || section < 0) return;

    await this.confirm.spendAsync(force, 'this vocabulary', () =>
      this.vocabulary.generateSectionAsync(bookId, this.store.chapterIndex(), section, force));
  }

  /** Every section of the chapter, streamed — one call per section. */
  protected async findHardWordsInChapter(force: boolean): Promise<void> {
    const bookId = this.bookId;
    if (!bookId) return;

    await this.confirm.spendAsync(force, 'this vocabulary', () =>
      this.vocabulary.generateChapterAsync(bookId, this.store.chapterIndex(), force));
  }

  protected async learnMore(term: string): Promise<void> {
    if (this.bookId) await this.vocabulary.learnMoreAsync(this.bookId, term);
  }

  protected async fileTerm(change: TermChange): Promise<void> {
    await this.vocabulary.saveTermAsync(
      change.term, change.state, change.definition, this.bookId ?? undefined);
  }

  protected async forgetBookVocabulary(): Promise<void> {
    if (this.bookId) await this.vocabulary.forgetBookAsync(this.bookId);
  }
}

import { Injectable, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks } from './reader-tasks';
import { ReaderStore } from './reader-store';
import { Prose } from '../reader2.models';

/** What the analysis pane is showing. */
export type AnalysisKind = 'summary' | 'explain-simply' | 'passage';

/**
 * Everything the model has written about what the reader is looking at.
 *
 * <p><b>Every method that reaches a model spends money</b>, which is why they
 * are named for it. {@link refreshAsync} is the one exception: it reads
 * {@link ReaderStore} for where the reader now is and shows whatever chapter
 * summary is already stored there, and every navigation path — a chapter click,
 * a bookmark jump, a search-hit jump, a lens switch — calls it after moving, so
 * the chapter list's "already summarised" tick is never a promise the panel
 * cannot keep.</p>
 */
@Injectable()
export class AnalysisStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);
  private readonly reader = inject(ReaderStore);

  readonly kind = signal<AnalysisKind>('summary');
  readonly markdown = signal<string | null>(null);

  /** The section summary, and which section it is for. */
  readonly sectionMarkdown = signal<string | null>(null);
  readonly openSection = signal(-1);

  /** Called whenever the reader moves to another chapter. */
  clear(): void {
    this.markdown.set(null);
    this.sectionMarkdown.set(null);
    this.openSection.set(-1);
  }

  /**
   * Shows whatever chapter summary is already stored for wherever the reader
   * now is. Free, and silent — a `GET` of something already paid for is not a
   * purchase, so this is not named alongside the methods above it.
   */
  async refreshAsync(): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (!bookId) return;

    const prose = await firstValueFrom(
      this.api.peekChapterSummary(bookId, this.reader.chapterIndex())).catch(() => null);

    // Only on a hit: this peeks a summary and nothing else, so it must not
    // relabel the pane as showing one while leaving it empty.
    if (prose) {
      this.kind.set('summary');
      this.markdown.set(prose.markdown);
    }
  }

  /** The three-tier ladder, streamed because it can take a minute. */
  async summariseChapterAsync(bookId: string, chapter: number, force: boolean): Promise<void> {
    this.kind.set('summary');
    this.markdown.set(null);

    await this.tasks.stream<Prose>(
      'Summarising the chapter',
      this.api.chapterSummary(bookId, chapter, force),
      prose => this.markdown.set(prose.markdown));
  }

  async explainSimplyAsync(bookId: string, chapter: number, force: boolean): Promise<void> {
    this.kind.set('explain-simply');
    await this.setAsync('Putting it plainly', this.api.explainSimply(bookId, chapter, force));
  }

  async analysePassageAsync(
    bookId: string, chapter: number, wordOffset: number, text: string
  ): Promise<void> {
    this.kind.set('passage');
    await this.setAsync(
      'Reading the passage', this.api.analysePassage(bookId, chapter, wordOffset, text));
  }

  /**
   * A section summary, kept apart from the chapter one so opening a section does
   * not wipe a chapter summary the reader has already paid for.
   */
  async summariseSectionAsync(
    bookId: string, chapter: number, section: number, force: boolean
  ): Promise<void> {
    this.openSection.set(section);
    this.sectionMarkdown.set(null);

    const prose = await this.tasks.run(
      `Summarising section ${section + 1}`,
      () => firstValueFrom(this.api.sectionSummary(bookId, chapter, section, force)));

    if (prose) this.sectionMarkdown.set(prose.markdown);
  }

  /**
   * The two single-call analyses differ only in which route they hit and what
   * the banner says, so they share one body.
   */
  private async setAsync(what: string, call: Observable<Prose>): Promise<void> {
    this.markdown.set(null);

    const prose = await this.tasks.run(what, () => firstValueFrom(call));
    if (prose) this.markdown.set(prose.markdown);
  }
}

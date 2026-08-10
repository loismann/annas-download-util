import { Injectable, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks } from './reader-tasks';
import { Prose } from '../reader2.models';

/** What the analysis pane is showing. */
export type AnalysisKind = 'summary' | 'explain-simply' | 'passage';

/**
 * Everything the model has written about what the reader is looking at.
 *
 * <p><b>Every method here spends money</b>, which is why they are all in one
 * place with names that say so. Nothing on this store is called on open, on a
 * page turn, or on a chapter change — the shell clears it instead, so a stale
 * summary is never shown against the wrong chapter.</p>
 */
@Injectable()
export class AnalysisStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);

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

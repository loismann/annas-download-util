import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderConfirm } from './reader-confirm';
import { ReaderStore } from './reader-store';
import { ReaderTasks } from './reader-tasks';
import { ActorCorrection, StoryModel } from '../reader2.models';

/**
 * The book's cast, as far as the reader has read.
 *
 * <p>The server does the filtering, not this store. A client that held the whole
 * model and hid part of it would be one bug away from showing a reader who dies
 * in chapter forty, and the filter would be opt-in rather than structural.</p>
 *
 * <p><b>Nothing here builds the model on its own.</b> Chapters arrive one at a
 * time on the back of summaries the reader asked for; the only bulk action is
 * {@link buildFromSummariesAsync}, and something has to press it.</p>
 */
@Injectable()
export class StoryStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);
  private readonly reader = inject(ReaderStore);
  private readonly confirm = inject(ReaderConfirm);

  readonly model = signal<StoryModel | null>(null);

  /** What this book type calls the three parts. Null until a model is loaded. */
  readonly vocabulary = computed(() => this.model()?.vocabulary ?? null);

  /** Ambiguities waiting for an answer. */
  readonly openQuestions = computed(() => this.model()?.openQuestions ?? []);

  clear(): void {
    this.model.set(null);
  }

  /** Free — reading what is already stored never reaches a model. */
  async loadAsync(bookId: string, throughChapter: number): Promise<void> {
    const model = await this.tasks.run(
      'Loading the cast', () => firstValueFrom(this.api.storyModel(bookId, throughChapter)));

    if (model) this.model.set(model);
  }

  /**
   * The reader's correction to one entry.
   *
   * <p>Free, and stored apart from the record it corrects — so a rebuild, which
   * discards everything the extraction found, leaves it standing.</p>
   */
  async correctAsync(bookId: string, actorId: string, correction: ActorCorrection): Promise<void> {
    const model = await this.tasks.run(
      'Saving your correction',
      () => firstValueFrom(this.api.correctActor(bookId, actorId, correction)));

    if (model) this.model.set(model);
  }

  /** Free, and stored beside the correction it belongs to. */
  async hideAsync(bookId: string, actorId: string, hidden: boolean): Promise<void> {
    const model = await this.tasks.run(
      hidden ? 'Hiding them from the map' : 'Putting them back on the map',
      () => firstValueFrom(this.api.hideActor(bookId, actorId, hidden)));

    if (model) this.model.set(model);
  }

  /**
   * Answers one open question, and holds what the server sends back.
   *
   * <p>Free, and the server returns the whole filtered model rather than the one
   * row that changed — accepting fuses two entries, which repoints edges, group
   * memberships, and thread participants. Patching that here would be a second
   * implementation of the merge, in the language with no tests for it.</p>
   */
  async resolveAsync(bookId: string, mergeId: string, accept: boolean): Promise<void> {
    const model = await this.tasks.run(
      accept ? 'Merging them' : 'Keeping them apart',
      () => firstValueFrom(this.api.resolveMerge(bookId, mergeId, accept)));

    if (model) this.model.set(model);
  }

  /**
   * Builds the model from the chapters already summarised.
   *
   * <p>Offered after switching a book to a type that keeps a cast, because that
   * switch leaves the model empty — the earlier chapters were never ingested
   * under it. One extraction per summarised chapter and no re-summarising, and
   * it is resumable, so pressing it twice costs only what was missing.</p>
   *
   * @param rebuild Discards what is recorded and reads every summarised chapter
   *   again, for a record gathered under extraction rules that have since
   *   changed. Chapters already folded in are walked past for free otherwise, so
   *   without this a record cannot be corrected at all.
   */
  async buildFromSummariesAsync(bookId: string, rebuild = false): Promise<void> {
    await this.tasks.stream<StoryModel>(
      rebuild ? 'Building the story model again' : 'Building the story model',
      this.api.backFillStoryModel(bookId, rebuild),
      (model: StoryModel) => this.model.set(model));
  }

  /**
   * Asks whether to build the cast, after a switch to a type that keeps one.
   *
   * <p><b>Offered, never run.</b> Switching a book's type is free; turning it
   * into an action that quietly starts one request per summarised chapter would
   * be exactly the behaviour this reader was rebuilt to remove. A book with
   * nothing summarised is not asked at all — there would be nothing to build
   * from, and a dialog offering to do nothing teaches the reader to dismiss
   * dialogs.</p>
   *
   * <p>Lives here rather than in the shell because "should this book have a cast
   * built" is a question about the story model, and the shell already decides
   * enough.</p>
   */
  async offerBuildAsync(lensKey: string): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    const lens = this.reader.lenses().find(l => l.key === lensKey);
    const summarised = this.reader.chapters().filter(c => c.hasSummary).length;

    if (!bookId || !lens?.buildsStoryModel || summarised === 0) return;

    const chapters = summarised === 1 ? '1 chapter' : `${summarised} chapters`;
    if (await this.confirm.confirmBackFillAsync(chapters)) await this.buildFromSummariesAsync(bookId);
  }
}

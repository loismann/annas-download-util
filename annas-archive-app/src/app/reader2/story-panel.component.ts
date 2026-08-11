import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CharacterTableComponent } from './components/character-table.component';
import { CastFilterComponent } from './components/cast-filter.component';
import { ThreadPanelComponent } from './components/thread-panel.component';
import { StoryMapComponent } from './components/story-map.component';
import { PlaceListComponent } from './components/place-list.component';
import { MergeAnswer, MergeResolverComponent } from './components/merge-resolver.component';
import { ActorCorrection } from './reader2.models';
import { ReaderStore } from './services/reader-store';
import { StoryStore } from './services/story-store';
import { ReaderConfirm } from './services/reader-confirm';
import { ReaderTasks } from './services/reader-tasks';
import { CastFilter, DEFAULT_FILTER, filterCast, onTheMap } from './services/cast-filter';

/** The three ways of looking at one model. */
type StoryView = 'cast' | 'threads' | 'places' | 'map';

/**
 * The story panel: the cast, the threads, and the map, over one loaded model.
 *
 * <p>The fourth sub-container, by the same rule that produced the words panel —
 * a topic with enough of its own state to deserve one. It loads when opened and
 * never before: the model is served already filtered to the reader's position,
 * and reading it is free, but a panel that loaded on book-open would be one more
 * thing paid for before anybody asked.</p>
 *
 * <p>Open questions render above everything. They are the one part of the model
 * waiting on the reader, and a question buried under a tab is a question that
 * gets answered by nobody.</p>
 */
@Component({
  selector: 'app-reader2-story-panel',
  standalone: true,
  imports: [
    CommonModule, CharacterTableComponent, CastFilterComponent, ThreadPanelComponent,
    StoryMapComponent, PlaceListComponent, MergeResolverComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './story-panel.component.html',
  styleUrl: './story-panel.component.scss'
})
export class StoryPanelComponent implements OnInit {
  protected readonly story = inject(StoryStore);
  protected readonly reader = inject(ReaderStore);
  private readonly confirm = inject(ReaderConfirm);
  protected readonly tasks = inject(ReaderTasks);

  protected readonly view = signal<StoryView>('cast');

  /** Shared by the cast list and the map, so the two never disagree on who is shown. */
  protected readonly filter = signal<CastFilter>(DEFAULT_FILTER);

  protected readonly shown = computed(() => {
    const model = this.story.model();

    return model
      ? filterCast(model.actors, model.threads, this.filter(), model.throughChapter)
      : [];
  });

  /**
   * Who the map draws: the same filter as the list, less whoever the reader has
   * hidden. The list is the record and keeps everybody; the map is a picture, and
   * a picture with forty walk-ons on it shows nothing.
   */
  protected readonly onMap = computed(() => onTheMap(this.shown()));

  /** How many the reader has hidden — what the review control counts. */
  protected readonly hiddenCount = computed(() =>
    this.story.model()?.actors.filter(a => a.hidden).length ?? 0);

  protected readonly views: { key: StoryView; label: string }[] = [
    { key: 'cast', label: 'Cast' },
    { key: 'threads', label: 'Threads' },
    { key: 'places', label: 'Places' },
    { key: 'map', label: 'Map' }
  ];

  /** Nothing folded in yet — the state that offers the build. */
  protected readonly empty = computed(() =>
    (this.story.model()?.chaptersIngested.length ?? 0) === 0);

  /** How many chapters a build would walk. Zero means there is nothing to offer. */
  protected readonly summarised = computed(() =>
    this.reader.chapters().filter(c => c.hasSummary).length);

  async ngOnInit(): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (!bookId) return;

    await this.story.loadAsync(bookId, this.reader.chapterIndex());
  }

  /**
   * Straight to the build, no confirm dialog: the button names the count and the
   * work, so it is its own consent. The dialog belongs to the lens-switch flow,
   * where nobody has pressed anything about building yet.
   */
  protected async build(): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (bookId) await this.story.buildFromSummariesAsync(bookId);
  }

  /**
   * Throws the record away and reads every summarised chapter again.
   *
   * <p>Confirmed, unlike {@link build}: that button offers work not yet done and
   * names its own cost, this one discards work already paid for. A record is
   * otherwise uncorrectable — chapters already folded in are walked past for
   * free, so a plain rebuild would do nothing at all.</p>
   */
  protected async rebuild(): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    const count = this.summarised();
    if (!bookId || count === 0) return;

    const chapters = count === 1 ? '1 chapter' : `${count} chapters`;
    if (await this.confirm.confirmRebuildAsync(chapters)) {
      await this.story.buildFromSummariesAsync(bookId, true);
    }
  }

  /**
   * The reader overruling the record. Free, and kept apart from it — see
   * `CastOverrides` on the server: a correction outlives a rebuild because it was
   * never stored inside the thing a rebuild discards.
   */
  protected async correct(edit: { actorId: string; correction: ActorCorrection }): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (bookId) await this.story.correctAsync(bookId, edit.actorId, edit.correction);
  }

  protected async hide(request: { actorId: string; hidden: boolean }): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (bookId) await this.story.hideAsync(bookId, request.actorId, request.hidden);
  }

  protected async answer(answer: MergeAnswer): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (!bookId) return;

    await this.story.resolveAsync(bookId, answer.mergeId, answer.accept);
  }
}

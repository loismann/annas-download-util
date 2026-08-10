import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CharacterTableComponent } from './components/character-table.component';
import { ThreadPanelComponent } from './components/thread-panel.component';
import { StoryMapComponent } from './components/story-map.component';
import { MergeAnswer, MergeResolverComponent } from './components/merge-resolver.component';
import { ReaderStore } from './services/reader-store';
import { StoryStore } from './services/story-store';

/** The three ways of looking at one model. */
type StoryView = 'cast' | 'threads' | 'map';

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
    CommonModule, CharacterTableComponent, ThreadPanelComponent, StoryMapComponent,
    MergeResolverComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './story-panel.component.html',
  styleUrl: './story-panel.component.scss'
})
export class StoryPanelComponent implements OnInit {
  protected readonly story = inject(StoryStore);
  protected readonly reader = inject(ReaderStore);

  protected readonly view = signal<StoryView>('cast');

  protected readonly views: { key: StoryView; label: string }[] = [
    { key: 'cast', label: 'Cast' },
    { key: 'threads', label: 'Threads' },
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

  protected async answer(answer: MergeAnswer): Promise<void> {
    const bookId = this.reader.book()?.bookId;
    if (!bookId) return;

    await this.story.resolveAsync(bookId, answer.mergeId, answer.accept);
  }
}

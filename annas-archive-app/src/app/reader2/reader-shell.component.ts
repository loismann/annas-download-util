import { ChangeDetectionStrategy, Component, HostListener, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { ChapterListComponent } from './components/chapter-list.component';
import { ChapterViewComponent } from './components/chapter-view.component';
import { LensPickerComponent } from './components/lens-picker.component';
import { BookShelfComponent } from './components/book-shelf.component';
import { BookmarkBarComponent } from './components/bookmark-bar.component';
import { ReaderToolsComponent, ToolPanel } from './components/reader-tools.component';
import { SplitHandleComponent } from './components/split-handle.component';
import { ReaderPanelsComponent } from './reader-panels.component';
import { ReaderStore } from './services/reader-store';
import { ReaderConfirm } from './services/reader-confirm';
import { ReaderTasks } from './services/reader-tasks';
import { AnalysisStore } from './services/analysis-store';
import { VocabularyStore } from './services/vocabulary-store';
import { FlashcardStore } from './services/flashcard-store';
import { BookmarkStore } from './services/bookmark-store';
import { StoryStore } from './services/story-store';
import { ReaderMeasure } from './services/reader-measure';
import { Bookmark, PassageSelection } from './reader2.models';

/**
 * The container: which book, which chapter, where on the page.
 *
 * <p>Everything visible is a presenter with inputs and outputs, and everything
 * that talks to the server is a store. Reader I put all three in one 2,283-line
 * component, and the result was that no rendering change could be made without
 * reasoning about fetching.</p>
 *
 * <p>Provides the stores, so a second reader on the page would get its own —
 * and so a reader's error never outlives the reader it belongs to.</p>
 */
@Component({
  selector: 'app-reader2-shell',
  standalone: true,
  imports: [
    CommonModule, ChapterListComponent, ChapterViewComponent, LensPickerComponent,
    BookShelfComponent, BookmarkBarComponent, ReaderToolsComponent, SplitHandleComponent,
    ReaderPanelsComponent
  ],
  providers: [
    ReaderTasks, ReaderStore, AnalysisStore, VocabularyStore, FlashcardStore, BookmarkStore,
    StoryStore, ReaderMeasure
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-shell.component.html',
  styleUrl: './reader-shell.component.scss'
})
export class ReaderShellComponent implements OnInit {
  protected readonly store = inject(ReaderStore);
  protected readonly analysis = inject(AnalysisStore);
  protected readonly bookmarks = inject(BookmarkStore);
  private readonly vocabulary = inject(VocabularyStore);
  private readonly flashcards = inject(FlashcardStore);
  private readonly story = inject(StoryStore);
  private readonly confirm = inject(ReaderConfirm);
  private readonly route = inject(ActivatedRoute);
  private readonly measurer = inject(ReaderMeasure);

  protected readonly panel = signal<ToolPanel | null>(null);
  protected readonly sidebarOpen = signal(true);
  protected readonly bookmarksOpen = signal(false);
  protected readonly splitRatio = signal(0.6);

  /**
   * What the reader has highlighted. Held rather than acted on: selecting text
   * is easy to do by accident while reading, and the panel offers two named
   * choices instead of buying an explanation the moment the mouse comes up.
   */
  protected readonly selection = signal<PassageSelection | null>(null);

  async ngOnInit(): Promise<void> {
    await this.store.loadShelfAsync();
    this.splitRatio.set(this.store.preferences().splitRatio);
    await this.vocabulary.loadAsync();

    const bookId = this.route.snapshot.queryParamMap.get('book');
    if (bookId) await this.openBook(bookId);

    this.measure();
  }

  /** Re-measure on resize, keeping the reader on the same words. */
  @HostListener('window:resize')
  measure(): void {
    this.measurer.remeasure();
  }

  protected async openBook(bookId: string): Promise<void> {
    this.selection.set(null);
    this.analysis.clear();
    this.story.clear();

    await this.store.openAsync(bookId);
    await Promise.all([
      this.bookmarks.loadAsync(bookId), this.flashcards.loadAsync(bookId), this.analysis.refreshAsync()
    ]);

    this.markPlace();
    this.measure();
  }

  protected closeBook(): void {
    this.store.book.set(null);
    this.analysis.clear();
    this.story.clear();
    this.panel.set(null);
  }

  protected async openChapter(index: number): Promise<void> {
    await this.moveTo(index, 0);
    await this.store.rememberPositionAsync();
  }

  protected async turnPage(forward: boolean): Promise<void> {
    await (forward ? this.store.pageForwardAsync() : this.store.pageBackAsync());
    this.markPlace();
  }

  /** Costs and destroys nothing: artifacts are keyed by lens, so switching back finds it all. */
  protected async changeLens(lensKey: string): Promise<void> {
    await this.store.setLensAsync(lensKey);
    this.analysis.clear();
    this.story.clear();

    // The story panel button goes with the type that had one; the open panel
    // must go with it, or the reader is left looking at a pane with no button.
    if (this.panel() === 'story' && !this.store.lens()?.buildsStoryModel) this.panel.set(null);

    // A type that keeps a cast starts with an empty one. The store asks; nothing
    // is built without an answer.
    await this.story.offerBuildAsync(lensKey);
    await this.analysis.refreshAsync();
  }

  /** Un-enrols a book: its artifacts and extracted text go with it. */
  protected async removeBook(bookId: string): Promise<void> {
    if (!await this.confirm.confirmRemovalAsync()) return;

    await this.store.unenrolAsync(bookId);
    if (this.store.book()?.bookId === bookId) this.closeBook();
  }

  /**
   * Drops the extracted text and re-extracts. Keeps every artifact — this is for
   * a bad extraction, not for throwing away work that was paid for.
   */
  protected async reIndex(): Promise<void> {
    const book = this.store.book();
    if (!book || !await this.confirm.confirmReIndexAsync()) return;

    await this.store.reIndexAsync(book.bookId);
  }

  // ─── bookmarks ──────────────────────────────────────────────────────

  protected async jumpTo(mark: Bookmark): Promise<void> {
    this.bookmarksOpen.set(false);
    await this.moveTo(mark.chapter, mark.wordOffset);
  }

  /** Every navigation drops the selection, re-marks the place, and reloads the analysis. */
  private async moveTo(chapter: number, wordOffset: number): Promise<void> {
    this.selection.set(null);
    this.analysis.clear();
    await this.store.goToAsync(chapter, wordOffset);
    this.markPlace();
    await this.analysis.refreshAsync();
  }

  /**
   * Tells the bookmark store where the reader is; the bookmark toggle derives
   * from this, so it cannot claim a page the reader has turned past.
   */
  private markPlace(): void {
    this.bookmarks.setPlace(this.store.chapterIndex(), this.store.wordOffset());
  }

  // ─── appearance ─────────────────────────────────────────────────────

  /** While dragging: applied, not saved. */
  protected dragSplit(ratio: number): void {
    this.splitRatio.set(ratio);
  }

  /** On release: saved once, and the text re-measured at its new width. */
  protected async commitSplit(ratio: number): Promise<void> {
    this.splitRatio.set(ratio);
    await this.store.savePreferencesAsync({ ...this.store.preferences(), splitRatio: ratio });
    this.measure();
  }

  protected toggleFullscreen(): void {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen().catch(() => undefined);
  }
}

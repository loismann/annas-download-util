import {
  ChangeDetectionStrategy, Component, EventEmitter, Input, Output, computed, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { AnalysisPanelComponent } from './components/analysis-panel.component';
import { SectionSummaryComponent, SectionRequest } from './components/section-summary.component';
import { SearchPanelComponent, HitTarget } from './components/search-panel.component';
import { AppearanceControlsComponent } from './components/appearance-controls.component';
import { ToolPanel } from './components/reader-tools.component';
import { WordsPanelComponent } from './words-panel.component';
import { StoryPanelComponent } from './story-panel.component';
import { AnalysisKind, AnalysisStore } from './services/analysis-store';
import { ReaderStore } from './services/reader-store';
import { VocabularyStore } from './services/vocabulary-store';
import { ReaderConfirm } from './services/reader-confirm';
import { PassageSelection } from './reader2.models';

/**
 * The right-hand pane: the analysis, or whichever tool the reader opened.
 *
 * <p>A second container rather than more of the shell, because the shell had
 * outgrown what one file should decide. The split is by <i>question</i>: the
 * shell answers "which book, which chapter, where on the page", this answers
 * "what is the reader asking about it", and {@link WordsPanelComponent} answers
 * the one topic with enough of its own state to deserve a third — words.</p>
 *
 * <p>Everything that bills goes through {@link ReaderConfirm.spendAsync}.</p>
 */
@Component({
  selector: 'app-reader2-panels',
  standalone: true,
  imports: [
    CommonModule, AnalysisPanelComponent, SectionSummaryComponent,
    SearchPanelComponent, AppearanceControlsComponent, WordsPanelComponent,
    StoryPanelComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-reader2-analysis-panel
      *ngIf="open === null"
      [kind]="analysis.kind()"
      [markdown]="analysis.markdown()"
      [stale]="summaryIsStale()"
      [busy]="store.busy()"
      [error]="store.error()"
      [selection]="selection"
      (generate)="generate($event, false)"
      (regenerate)="generate($event, true)"
      (analyseSelection)="analyseSelection($event)"
      (fileSelection)="fileSelection($event)"
      (dismissSelection)="dismiss.emit()" />

    <app-reader2-section-summary
      *ngIf="open === 'sections'"
      [sections]="store.sections()"
      [openIndex]="analysis.openSection()"
      [markdown]="analysis.sectionMarkdown()"
      [busy]="!!store.busy()"
      [wordOffset]="store.wordOffset()"
      (open)="summariseSection($event)" />

    <app-reader2-words
      *ngIf="open === 'vocabulary' || open === 'flashcards'"
      [show]="open" />

    <app-reader2-story-panel *ngIf="open === 'story'" />

    <app-reader2-search-panel
      *ngIf="open === 'search'"
      [hits]="store.searchHits()"
      [busy]="!!store.busy()"
      [searched]="searched"
      (search)="search($event)"
      (jump)="jump($event)" />

    <app-reader2-appearance-controls
      *ngIf="open === 'appearance'"
      [preferences]="store.preferences()"
      (change)="store.savePreferencesAsync($event)" />
  `,
  styleUrl: './reader-panels.component.scss'
})
export class ReaderPanelsComponent {
  protected readonly store = inject(ReaderStore);
  protected readonly analysis = inject(AnalysisStore);
  private readonly vocabulary = inject(VocabularyStore);
  private readonly confirm = inject(ReaderConfirm);

  @Input() open: ToolPanel | null = null;

  /** What the reader has highlighted and not yet decided about. */
  @Input() selection: PassageSelection | null = null;

  @Output() dismiss = new EventEmitter<void>();

  /** The last query actually run, so "nothing found" names the right thing. */
  protected searched: string | null = null;

  /**
   * Whether what the summary panel is showing predates the current prompt.
   *
   * <p>Only for the summary itself — "I'm a Dummy" is written from the summary
   * rather than stored against a chapter, so the chapter list has nothing to say
   * about it and claiming otherwise would be a marker that is sometimes a
   * guess.</p>
   */
  protected readonly summaryIsStale = computed(() =>
    this.analysis.kind() === 'summary'
    && (this.store.currentChapter()?.summaryIsStale ?? false));

  private get bookId(): string | null {
    return this.store.book()?.bookId ?? null;
  }

  // ─── what the reader highlighted ────────────────────────────────────

  /** Explaining a passage is a purchase, so it happens on a named button. */
  protected async analyseSelection(selection: PassageSelection): Promise<void> {
    const bookId = this.bookId;
    if (!bookId) return;

    this.dismiss.emit();
    await this.analysis.analysePassageAsync(
      bookId, this.store.chapterIndex(), selection.wordOffset, selection.text);
  }

  /** Filing what was highlighted costs nothing and reaches no model. */
  protected async fileSelection(selection: PassageSelection): Promise<void> {
    this.dismiss.emit();
    await this.vocabulary.saveTermAsync(
      selection.text, 'Studying', undefined, this.bookId ?? undefined);
  }

  // ─── the two that bill ──────────────────────────────────────────────

  protected async generate(kind: AnalysisKind, force: boolean): Promise<void> {
    const bookId = this.bookId;
    if (!bookId || kind === 'passage') return;

    const chapter = this.store.chapterIndex();

    await this.confirm.spendAsync(force, 'this analysis', () => kind === 'summary'
      ? this.analysis.summariseChapterAsync(bookId, chapter, force)
      : this.analysis.explainSimplyAsync(bookId, chapter, force));
  }

  protected async summariseSection(request: SectionRequest): Promise<void> {
    const bookId = this.bookId;
    if (!bookId) return;

    await this.confirm.spendAsync(request.force, 'this section summary', () =>
      this.analysis.summariseSectionAsync(
        bookId, this.store.chapterIndex(), request.index, request.force));
  }

  // ─── free ───────────────────────────────────────────────────────────

  protected async search(query: string): Promise<void> {
    this.searched = query;
    await this.store.searchAsync(query);
  }

  protected async jump(target: HitTarget): Promise<void> {
    this.analysis.clear();
    await this.store.goToAsync(target.chapter, target.wordOffset);
    await this.store.rememberPositionAsync();
    await this.analysis.refreshAsync();
  }
}

import {
  Component,
  ElementRef,
  HostListener,
  NgZone,
  OnDestroy,
  OnInit,
  ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { CharacterGraphModalComponent } from '../character-graph-modal/character-graph-modal.component';
import { ConfirmDialogComponent } from '../components/confirm-dialog/confirm-dialog.component';

import {
  DropboxBookSearchResult,
  DropboxChapterContent,
  DropboxEpubChapter,
  DropboxEpubStatus,
  FlashcardItem,
  FullChapterSummaryResponse,
  ChunkBoundariesResponse,
  SectionSummaryResponse,
  LibraryReaderBook
} from '../models/dropbox-epub.model';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { VocabularyService, VocabularyWord } from '../services/vocabulary.service';
import { AuthService } from '../services/auth.service';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked } from 'marked';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

interface ViewedBook {
  fileName: string;
  readerKey: string;
  title: string;
  updatedAt?: string;
}

interface BookmarkEntry {
  id: string;
  readerKey: string;
  chapterId: number;
  wordOffset: number;
  createdAt: string;
}

interface ReadingPosition {
  chapterId: number;
  wordOffset: number;
  updatedAt?: string;
}

@Component({
  selector: 'app-dropbox-reader',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatSelectModule,
    MatInputModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressBarModule,
    MatSliderModule,
    MatTooltipModule
  ],
  templateUrl: './dropbox-reader.component.html',
  styleUrls: ['./dropbox-reader.component.css']
})
export class DropboxReaderComponent implements OnInit, OnDestroy {
  private destroy$ = new Subject<void>();

  readerBooks: LibraryReaderBook[] = [];
  chapters: DropboxEpubChapter[] = [];

  selectedBookPath: string | null = null;
  selectedBookFileName: string | null = null;
  selectedChapterId: number | null = null;
  chapterContent: DropboxChapterContent | null = null;

  loadingBooks = false;
  loadingChapters = false;
  loadingContent = false;
  error: string | null = null;

  searchTerm = '';
  wordOffset = 0;
  pageSizeWords = 200;

  previouslyViewed: ViewedBook[] = [];
  status: DropboxEpubStatus | null = null;
  private statusPoll: any;
  private statusPollAttempts = 0;
  private lastStatusPercent: number | null = null;
  private timeoutIds: any[] = [];
  private pendingRestorePosition: { readerKey: string; chapterId: number; wordOffset: number } | null = null;

  bookSearchTerm = '';
  bookSearchResults: DropboxBookSearchResult[] = [];
  bookSearchError: string | null = null;
  private pendingCharOffset: number | null = null;
  private pendingWordOffset: number | null = null;

  topHeightPercent = 50;
  isResizing = false;
  isHorizontalResizing = false;

  @ViewChild('contentStack') contentStackRef!: ElementRef<HTMLDivElement>;
  @ViewChild('textWindow') textWindowRef!: ElementRef<HTMLDivElement>;

  summary: string | null = null;
  loadingSummary = false;
  loadingFullChapterSummary = false;
  loadingUltraChapterSummary = false;
  fullChapterSummary: string | null = null;
  fullSummaryTokens: { total: number; prompt: number; completion: number; allowancePercent?: number | null; remaining?: number | null } | null = null;
  ultraChapterSummary: string | null = null;
  ultraSummaryTokens: { total: number; prompt: number; completion: number; allowancePercent?: number | null; remaining?: number | null } | null = null;
  chapterSummaryProgress: {
    stage: 'chunks' | 'sections' | 'final' | 'complete' | 'error';
    currentStep: number;
    totalSteps: number;
    message: string;
    error?: string;
  } | null = null;
  formattedAnalysis: string | null = null;
  vocabularyWords: VocabularyWord[] = [];
  analysisText: string | null = null;
  showVocabModal = false;
  vocabKnownList: string[] = [];
  vocabUnknownList: { term: string; definition: string }[] = [];
  flashcards: FlashcardItem[] = [];
  learnMoreContent: string | null = null;
  learnMoreImages: string[] = [];
  learnMoreSafeContent: SafeHtml | null = null;
  learnMoreTerm: string | null = null;
  loadingLearnMore = false;
  loadingFlashcard = false;
  loadingSelectionVocab = false;
  vocabFilter: string = 'all';
  vocabFilters: { id: string; name: string }[] = [{ id: 'all', name: 'All books' }];
  leftFlex = '1 1 0';
  rightFlex = '1 1 0';
  showSidebar = true;
  showCacheSection = false;
  showAiUsageSection = false;
  showReaderControlsSection = false;
  showChapterRegenerateConfirm = false;
  fontFamily: 'serif' | 'sans' | 'mono' = 'serif';
  fontSize: number = 14;
  theme: 'light' | 'sepia' | 'dark' = 'sepia';
  analysisMode: 'section' | 'page' | 'chapter' = 'section';
  selectedText: string | null = null;
  tokenUsage: { promptTokens: number; completionTokens: number; totalTokens: number; allowance?: number | null; allowanceUsedPercent?: number | null; tokensRemaining?: number | null; resetsAtUtc?: string | null; totalCostUsd?: number | null } | null = null;
  fullChapterSummaryCache = new Map<number, FullChapterSummaryResponse>();
  bookmarks: BookmarkEntry[] = [];
  bookmarkSelectValue: string | null = null;
  showBookmarksDropdown = false;
  lastPositions = new Map<string, ReadingPosition>();

  // Section/chunk boundary state
  chunkBoundaries: ChunkBoundariesResponse | null = null;
  loadingChunkBoundaries = false;
  chunkBoundariesProgress: {
    stage: 'indexing' | 'detecting' | 'complete' | 'error';
    currentStep: number;
    totalSteps: number;
    message: string;
    error?: string;
  } | null = null;
  sectionSummaries = new Map<number, SectionSummaryResponse>();
  loadingSectionSummary = false;
  currentSectionIndex: number | null = null;
  private chapterContentCache = new Map<string, DropboxChapterContent>();
  private pendingFileName: string | null = null;
  private pendingChapterId: number | null = null;
  private autoSelectFirstChapter = false;
  private autoSelectChapterId: number | null = null;

  get readerTextStyles(): any {
    return {
      'font-family': this.fontFamily === 'serif'
        ? '"Georgia", "Times New Roman", serif'
        : this.fontFamily === 'mono'
          ? '"SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace'
          : '"Inter", "Segoe UI", system-ui, -apple-system, sans-serif',
      'font-size.px': this.fontSize
    };
  }

  get fullChapterSummaryHtml(): SafeHtml | null {
    if (!this.fullChapterSummary) return null;
    const html = marked(this.fullChapterSummary);
    return this.sanitizer.sanitize(1, html) ? this.sanitizer.bypassSecurityTrustHtml(html as string) : null;
  }

  get ultraChapterSummaryHtml(): SafeHtml | null {
    if (!this.ultraChapterSummary) return null;
    const html = marked(this.ultraChapterSummary);
    return this.sanitizer.sanitize(1, html) ? this.sanitizer.bypassSecurityTrustHtml(html as string) : null;
  }

  constructor(
    private api: AnnaArchiveApiService,
    private vocabularyService: VocabularyService,
    private sanitizer: DomSanitizer,
    private authService: AuthService,
    private ngZone: NgZone,
    private dialog: MatDialog,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    this.loadPreviouslyViewed();
    this.loadLastPositions();
    this.loadBookmarks();
    this.subscribeToReaderParams();
    this.loadBooks();
    this.vocabFilters = this.vocabularyService.getBookFilters();
    this.timeoutIds.push(setTimeout(() => this.recalcPageSize(), 0));
    this.refreshTokenUsage();

    // Subscribe to vocabulary changes for real-time updates
    // Always keep lists in sync so they're ready when modal opens
    this.vocabularyService.knownWords$
      .pipe(takeUntil(this.destroy$))
      .subscribe(() => {
        this.refreshVocabLists();
        this.vocabFilters = this.vocabularyService.getBookFilters();
      });

    this.vocabularyService.studyWords$
      .pipe(takeUntil(this.destroy$))
      .subscribe(() => {
        this.refreshVocabLists();
        this.vocabFilters = this.vocabularyService.getBookFilters();
      });
  }

  ngOnDestroy(): void {
    this.stopStatusPolling();

    // Clear all pending timeouts
    this.timeoutIds.forEach(id => clearTimeout(id));
    this.timeoutIds = [];

    // Unsubscribe from all observables
    this.destroy$.next();
    this.destroy$.complete();
  }

  get selectedBook(): LibraryReaderBook | null {
    return this.readerBooks.find(b => b.readerKey === this.selectedBookPath) ?? null;
  }

  get totalBookWords(): number {
    return this.chapters.reduce((sum, ch) => sum + (ch.wordCount || 0), 0);
  }

  get totalBookPages(): number {
    if (!this.pageSizeWords || this.pageSizeWords <= 0) return 1;
    const totalWords = this.totalBookWords;
    return Math.max(1, Math.ceil(totalWords / this.pageSizeWords));
  }

  get currentBookWordOffset(): number {
    if (!this.selectedChapterId) return this.wordOffset;
    const chapterIndex = this.chapters.findIndex(ch => ch.id === this.selectedChapterId);
    if (chapterIndex < 0) return this.wordOffset;
    const before = this.chapters
      .slice(0, chapterIndex)
      .reduce((sum, ch) => sum + (ch.wordCount || 0), 0);
    return before + this.wordOffset;
  }

  get currentBookPage(): number {
    if (!this.pageSizeWords || this.pageSizeWords <= 0) return 1;
    return Math.floor(this.currentBookWordOffset / this.pageSizeWords) + 1;
  }

  get bookProgressPercent(): number {
    const total = this.totalBookWords;
    if (!total) return 0;
    const progress = (this.currentBookWordOffset + 1) / total;
    return Math.min(100, Math.max(0, Math.round(progress * 100)));
  }

  get visibleBookmarks(): BookmarkEntry[] {
    if (!this.selectedBookPath) return [];
    return this.bookmarks
      .filter(b => b.readerKey === this.selectedBookPath)
      .sort((a, b) => a.chapterId - b.chapterId || a.wordOffset - b.wordOffset);
  }

  get isCurrentPageBookmarked(): boolean {
    if (!this.selectedBookPath || this.selectedChapterId === null) return false;
    const page = Math.floor(this.wordOffset / Math.max(1, this.pageSizeWords));
    return this.visibleBookmarks.some(b => b.chapterId === this.selectedChapterId &&
      Math.floor(b.wordOffset / Math.max(1, this.pageSizeWords)) === page);
  }

  get visibleText(): string {
    if (!this.chapterContent) return '';
    return this.sliceByWords(
      this.chapterContent.content,
      this.wordOffset,
      this.pageSizeWords
    );
  }

  get highlightedVisibleText(): string {
    const text = this.visibleText;
    if (!text) return '';

    let processedText = this.escapeHtml(text);

    if (this.chunkBoundaries) {
      processedText = this.applySectionAnnotations(processedText);
    }

    // Apply search highlighting
    if (this.searchTerm.trim()) {
      const safeTerm = this.escapeRegExp(this.searchTerm.trim());
      processedText = processedText.replace(new RegExp(`(${safeTerm})`, 'gi'), '<mark>$1</mark>');
    }

    return processedText.replace(/\n/g, '<br/>');
  }

  private applySectionAnnotations(escapedText: string): string {
    if (!this.chunkBoundaries || !this.chapterContent) {
      return escapedText;
    }

    const visibleStart = this.wordOffset;
    const visibleEnd = this.wordOffset + this.pageSizeWords;
    const boundaryMarkers = new Map<number, string>();

    if (this.chunkBoundaries.chunks.length > 1) {
      this.chunkBoundaries.chunks.slice(0, -1).forEach((chunk, index) => {
        const boundary = chunk.end;
        if (boundary < visibleStart || boundary > visibleEnd) return;
        const boundaryInVisible = boundary - visibleStart;
        boundaryMarkers.set(
          boundaryInVisible,
          `${index + 1} <span class="section-marker-icon">&#9660;</span> ${index + 2}`
        );
      });
    }

    let sectionStartInVisible: number | null = null;
    let sectionEndInVisible: number | null = null;
    if (this.analysisMode === 'section' && this.currentSectionIndex !== null) {
      const chunk = this.chunkBoundaries.chunks[this.currentSectionIndex];
      if (chunk && !(chunk.end <= visibleStart || chunk.start >= visibleEnd)) {
        sectionStartInVisible = Math.max(0, chunk.start - visibleStart);
        sectionEndInVisible = Math.min(this.pageSizeWords, chunk.end - visibleStart);
      }
    }

    const words = escapedText.split(/(\s+)/);
    let wordCount = 0;
    let result = '';

    for (let i = 0; i < words.length; i++) {
      const word = words[i];
      const isWhitespace = /^\s+$/.test(word);

      if (!isWhitespace) {
        if (boundaryMarkers.has(wordCount)) {
          const marker = boundaryMarkers.get(wordCount);
          if (marker) {
            result += ` <span class="section-marker">${marker}</span> `;
            boundaryMarkers.delete(wordCount);
          }
        }

        if (sectionStartInVisible !== null && sectionEndInVisible !== null &&
            wordCount >= sectionStartInVisible && wordCount < sectionEndInVisible) {
          result += `<span class="section-highlight">${word}</span>`;
        } else {
          result += word;
        }
        wordCount++;
      } else {
        result += word;
      }
    }

    if (boundaryMarkers.has(wordCount)) {
      const marker = boundaryMarkers.get(wordCount);
      if (marker) {
        result += ` <span class="section-marker">${marker}</span> `;
      }
    }

    return result;
  }

  get formattedSummary(): string | null {
    return this.formattedAnalysis;
  }

  private parseSummaryOnce(summary: string): void {
    console.log('parseSummaryOnce called with:', summary.substring(0, 200));

    // Find "Definitions:" in a flexible way (no dependency on preceding newline)
    const defRegex = /definitions?\s*:/i;
    const match = defRegex.exec(summary);

    if (match) {
      console.log('Found definitions section at index:', match.index);
      const defIndex = match.index;
      this.analysisText = summary.substring(0, defIndex).trim();
      const definitionsSection = summary.substring(defIndex + match[0].length).trim();
      console.log('Definitions section:', definitionsSection.substring(0, 200));
      this.vocabularyWords = this.parseVocabulary(definitionsSection);
    } else {
      console.log('No definitions section found');
      this.analysisText = summary.trim();
      this.vocabularyWords = [];
    }

    this.formattedAnalysis = this.formatAnalysis(this.analysisText ?? '');
  }

  private extractDefinitionsFromSummary(summary: string): VocabularyWord[] {
    const defRegex = /definitions?\s*:/i;
    const match = defRegex.exec(summary);
    if (match) {
      const definitionsSection = summary.substring(match.index + match[0].length).trim();
      return this.parseVocabulary(definitionsSection);
    }
    return [];
  }

  private parseVocabulary(definitionsText: string): VocabularyWord[] {
    const words: VocabularyWord[] = [];
    const added = new Set<string>();

    // Match patterns like:
    // - **Term**: Definition
    // - Term: Definition
    // **Term**: Definition
    // 1. Term: Definition (numbered)
    const lines = definitionsText.split('\n');
    console.log(`Parsing ${lines.length} lines for vocabulary`);

    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) continue;

      // Remove leading dash, bullet points, asterisks, numbers with dots/parens, and whitespace
      let cleaned = trimmed
        .replace(/^[-•*]\s*/, '')  // Remove dash/bullet/asterisk
        .replace(/^\d+[\.)]\s*/, '') // Remove "1." or "1)"
        .trim();

      // Match patterns:
      // 1. **term**: definition
      // 2. term: definition
      const match = cleaned.match(/^\*\*(.+?)\*\*:\s*(.+)$/) ||
                    cleaned.match(/^([^:]+?):\s*(.+)$/);

      if (match) {
        // Remove any remaining asterisks from term
        const term = match[1].trim().replace(/\*\*/g, '');
        const definition = match[2].trim();
        const normalized = this.vocabularyService.normalizeForMatch(term);

        console.log(`Found vocab: "${term}" -> "${definition.substring(0, 50)}..."`);

        // Only add if not already known and has valid content
        if (normalized && definition && !this.vocabularyService.isKnown(normalized) && !added.has(normalized)) {
          words.push({ term, definition });
          added.add(normalized);
        } else if (this.vocabularyService.isKnown(normalized)) {
          console.log(`Skipping known word: "${term}"`);
        }
      }
    }

    console.log(`Total vocabulary words parsed: ${words.length}`);
    return words;
  }

  getSummaryWithoutDefinitions(summary: string): string {
    const defRegex = /definitions?\s*:/i;
    const match = defRegex.exec(summary);

    if (match) {
      return summary.substring(0, match.index).trim();
    }
    return summary.trim();
  }

  formatSectionSummaryAsHtml(summary: string): SafeHtml {
    const summaryText = this.getSummaryWithoutDefinitions(summary);
    const html = marked.parse(summaryText, { async: false }) as string;
    return this.sanitizer.sanitize(1 /* HTML */, html) || '';
  }

  removeVocabularyWord(term: string): void {
    this.vocabularyWords = this.vocabularyWords.filter(w => w.term !== term);
  }

  markWordAsKnown(word: VocabularyWord): void {
    this.vocabularyService.markAsKnown(word.term, this.selectedBookPath ?? undefined);
    this.removeVocabularyWord(word.term);
  }

  markWordAsUnknown(word: VocabularyWord): void {
    this.vocabularyService.markAsUnknown(word.term, word.definition, this.selectedBookPath ?? undefined);
    this.removeVocabularyWord(word.term);
    // Deep dive analysis will only be fetched when user explicitly views the word from study list
  }

  private formatAnalysis(text: string): string {
    const lines = text.split(/\n+/);
    const parts: string[] = [];
    let inList = false;

    for (const raw of lines) {
      const line = raw.trim();
      if (!line) continue;

      if (line.startsWith('- ')) {
        if (!inList) {
          parts.push('<ul>');
          inList = true;
        }
        const item = this.escapeHtml(line.substring(2))
          .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
          .replace(/\*(.+?)\*/g, '<em>$1</em>');
        parts.push(`<li>${item}</li>`);
      } else {
        if (inList) {
          parts.push('</ul>');
          inList = false;
        }
        const paragraph = this.escapeHtml(line)
          .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
          .replace(/\*(.+?)\*/g, '<em>$1</em>');
        parts.push(`<p>${paragraph}</p>`);
      }
    }

    if (inList) parts.push('</ul>');
    return parts.join('');
  }

  get searchMatchCount(): number {
    if (!this.chapterContent || !this.searchTerm.trim()) return 0;
    const safeTerm = this.escapeRegExp(this.searchTerm.trim());
    const matches = this.chapterContent.content.match(new RegExp(safeTerm, 'gi'));
    return matches ? matches.length : 0;
  }

  get currentPage(): number {
    if (!this.chapterContent || !this.pageSizeWords || this.pageSizeWords <= 0) return 1;
    return Math.floor(this.wordOffset / this.pageSizeWords) + 1;
  }

  get totalPages(): number {
    if (!this.chapterContent || !this.pageSizeWords || this.pageSizeWords <= 0) return 1;
    return Math.max(
      1,
      Math.ceil((this.chapterContent.wordCount || 0) / this.pageSizeWords)
    );
  }

  reloadBooks(): void {
    this.loadBooks();
  }

  goToLibrary(): void {
    this.router.navigate(['/library']);
  }

  onBookSelected(fileName: string): void {
    if (!fileName) return;
    const selected = this.readerBooks.find(book => book.fileName === fileName);
    if (!selected) return;

    this.selectedBookFileName = selected.fileName;
    this.selectedBookPath = selected.readerKey;
    this.selectedChapterId = null;
    this.chapterContent = null;
    this.searchTerm = '';
    this.wordOffset = 0;
    this.pendingCharOffset = null;
    this.error = null;
    this.summary = null;
    this.formattedAnalysis = null;
    this.vocabularyWords = [];
    this.analysisText = null;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;
    this.loadingUltraChapterSummary = false;
    this.status = null;
    this.bookmarkSelectValue = null;
    this.pendingWordOffset = null;
    this.chunkBoundariesProgress = null;
    this.loadingChunkBoundaries = false;
    this.stopStatusPolling();
    this.pendingRestorePosition = null;

    // Note: Preloading disabled for performance - cached summaries are loaded on-demand instead
    // Previously supported offline preload; intentionally no-op for lean runtime.

    this.recordViewed(selected);
    this.queueRestorePosition(selected.readerKey);
    if (this.selectedBook) {
      this.vocabularyService.registerBook(this.selectedBookPath ?? '', this.selectedBook.title);
      this.vocabFilters = this.vocabularyService.getBookFilters();
    }
    this.fetchStatus(selected.fileName, true);
    this.loadChapters(selected.fileName);
  }

  onChapterSelected(chapterId: number, preservePendingOffset: boolean = false): void {
    if (!this.selectedBookFileName || !this.selectedBookPath) return;
    this.selectedBookPath = this.selectedBookPath; // keep for clarity
    this.selectedChapterId = chapterId;
    this.chapterContent = null;
    this.searchTerm = '';
    this.wordOffset = 0;
    if (!preservePendingOffset) {
      this.pendingCharOffset = null;
      this.pendingWordOffset = null;
    } else if (this.pendingCharOffset != null) {
      this.pendingWordOffset = null;
    }
    this.error = null;
    this.summary = null;
    this.formattedAnalysis = null;
    this.vocabularyWords = [];
    this.analysisText = null;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;
    this.loadingUltraChapterSummary = false;
    this.chunkBoundaries = null;
    this.sectionSummaries.clear();
    this.currentSectionIndex = null;

    this.loadChapterContent(this.selectedBookFileName, chapterId);
    if (this.analysisMode === 'section') {
      this.loadChunkBoundaries(this.selectedBookPath, chapterId);
    }
  }

  onSearchTermChange(): void {
    // trigger template update
  }

  onAnalysisModeChange(mode: 'section' | 'page' | 'chapter'): void {
    this.analysisMode = mode;
    if (mode === 'section' && this.selectedBookPath && this.selectedChapterId !== null && !this.chunkBoundaries) {
      this.loadChunkBoundaries(this.selectedBookPath, this.selectedChapterId);
    }
  }

  clearSearch(): void {
    this.searchTerm = '';
  }

  handleSummaryAction(): void {
    if (this.analysisMode === 'section') {
      if (this.currentSectionIndex !== null) {
        this.generateSectionSummary(this.currentSectionIndex);
      }
      return;
    }

    if (this.hasFullChapterSummary()) {
      this.showChapterRegenerateConfirm = true;
      return;
    }

    this.summarizeFullChapter();
  }

  confirmRegenerateChapterSummary(): void {
    this.showChapterRegenerateConfirm = false;
    this.summarizeFullChapter(true);
  }

  cancelRegenerateChapterSummary(): void {
    this.showChapterRegenerateConfirm = false;
  }

  hasFullChapterSummary(): boolean {
    if (this.selectedChapterId === null) return false;
    if (this.fullChapterSummary) return true;
    return this.fullChapterSummaryCache.has(this.selectedChapterId);
  }

  canPageBack(): boolean {
    if (!this.chapterContent) return false;
    if (this.wordOffset > 0) return true;
    if (this.selectedChapterId === null) return false;
    const currentChapterIndex = this.chapters.findIndex(ch => ch.id === this.selectedChapterId);
    return currentChapterIndex > 0;
  }

  pageForward(): void {
    if (!this.chapterContent) return;
    const totalPages = this.totalPages;
    const maxStart = Math.max(0, (totalPages - 1) * this.pageSizeWords);
    const newOffset = this.wordOffset + this.pageSizeWords;

    // Check if we're moving past the last page
    if (newOffset >= maxStart && this.selectedChapterId !== null) {
      // Try to advance to next chapter
      const currentChapterIndex = this.chapters.findIndex(ch => ch.id === this.selectedChapterId);
      if (currentChapterIndex !== -1 && currentChapterIndex < this.chapters.length - 1) {
        const nextChapter = this.chapters[currentChapterIndex + 1];
        console.log(`📖 End of chapter ${this.selectedChapterId} reached, advancing to chapter ${nextChapter.id}`);
        this.onChapterSelected(nextChapter.id);
        return;
      }
    }

    // Normal page forward within same chapter
    this.wordOffset = Math.min(newOffset, maxStart);
    this.updateReadingPosition();
  }

  pageBack(): void {
    if (!this.chapterContent) return;
    const newOffset = this.wordOffset - this.pageSizeWords;

    // Check if we're moving before the first page
    if (newOffset < 0 && this.selectedChapterId !== null) {
      // Try to go to previous chapter
      const currentChapterIndex = this.chapters.findIndex(ch => ch.id === this.selectedChapterId);
      if (currentChapterIndex > 0) {
        const prevChapter = this.chapters[currentChapterIndex - 1];
        console.log(`📖 Start of chapter ${this.selectedChapterId} reached, going back to chapter ${prevChapter.id}`);
        // Store that we want to go to the last page of the previous chapter
        this.pendingCharOffset = -1; // Special marker for "last page"
    this.onChapterSelected(prevChapter.id, true);
        return;
      }
    }

    // Normal page back within same chapter
    this.wordOffset = Math.max(0, newOffset);
    this.updateReadingPosition();
  }

  startIndexing(): void {
    if (!this.selectedBookFileName) return;
    this.api.startLibraryReaderIndex(this.selectedBookFileName)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: () => {
        this.stopStatusPolling();
        this.fetchStatus(this.selectedBookFileName!, true);
      },
      error: err => {
        console.error('Failed to start indexing', err);
        this.error = 'Unable to start indexing.';
      }
    });
  }

  deleteIndex(): void {
    if (!this.selectedBookFileName) return;
    this.api.deleteLibraryReaderIndex(this.selectedBookFileName)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: () => {
        this.status = null;
        this.bookSearchResults = [];
        this.summary = null;
        this.stopStatusPolling();
      },
      error: err => {
        console.error('Failed to delete cache', err);
        this.error = 'Unable to delete cache.';
      }
    });
  }

  searchWholeBook(): void {
    this.bookSearchError = null;
    this.bookSearchResults = [];
    if (!this.selectedBookFileName) {
      this.bookSearchError = 'Select a book first.';
      return;
    }

    const term = this.bookSearchTerm.trim();
    if (term.length < 10) {
      this.bookSearchError = 'Enter at least 10 characters.';
      return;
    }

    this.api.searchLibraryReaderBook(this.selectedBookFileName, term)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: results => {
        this.bookSearchResults = results;
        if (!results.length) {
          this.bookSearchError = 'No matches found.';
        }
      },
      error: err => {
        console.error('Failed to search book', err);
        this.bookSearchError = 'Search failed.';
      }
    });
  }

  goToBookMatch(match: DropboxBookSearchResult): void {
    if (!this.selectedBookFileName) return;
    this.pendingCharOffset = match.position;
    this.onChapterSelected(match.chapterId, true);
  }

  toggleBookmark(): void {
    if (!this.selectedBookPath || this.selectedChapterId === null || !this.chapterContent) return;
    const page = Math.floor(this.wordOffset / Math.max(1, this.pageSizeWords));
    const existingIndex = this.bookmarks.findIndex(b =>
      b.readerKey === this.selectedBookPath &&
      b.chapterId === this.selectedChapterId &&
      Math.floor(b.wordOffset / Math.max(1, this.pageSizeWords)) === page
    );

    if (existingIndex >= 0) {
      this.bookmarks.splice(existingIndex, 1);
    } else {
      const entry: BookmarkEntry = {
        id: `${this.selectedBookPath}|${this.selectedChapterId}|${this.wordOffset}`,
        readerKey: this.selectedBookPath,
        chapterId: this.selectedChapterId,
        wordOffset: this.wordOffset,
        createdAt: new Date().toISOString()
      };
      this.bookmarks.push(entry);
    }

    this.saveBookmarks();
  }

  toggleBookmarksDropdown(): void {
    if (!this.visibleBookmarks.length) return;
    this.showBookmarksDropdown = !this.showBookmarksDropdown;
  }

  selectBookmarkFromDropdown(bookmarkId: string): void {
    if (!bookmarkId) return;
    this.onBookmarkSelected(bookmarkId);
    this.showBookmarksDropdown = false;
  }

  get selectedBookmarkLabel(): string {
    if (!this.bookmarkSelectValue) return 'Bookmarks';
    const entry = this.bookmarks.find(b => b.id === this.bookmarkSelectValue);
    return entry ? this.formatBookmarkLabel(entry) : 'Bookmarks';
  }

  onBookmarkSelected(bookmarkId: string): void {
    if (!bookmarkId) return;
    const entry = this.bookmarks.find(b => b.id === bookmarkId);
    if (!entry) return;

    this.bookmarkSelectValue = bookmarkId;
    if (this.selectedChapterId === entry.chapterId && this.chapterContent) {
      const totalWords = this.chapterContent.wordCount ?? this.countWords(this.chapterContent.content);
      this.wordOffset = Math.max(0, Math.min(entry.wordOffset, totalWords));
      this.pendingWordOffset = null;
      this.updateReadingPosition();
      this.updateCurrentSection();
      return;
    }

    this.pendingWordOffset = entry.wordOffset;
    this.onChapterSelected(entry.chapterId, true);
  }

  formatBookmarkLabel(bookmark: BookmarkEntry): string {
    const chapter = this.chapters.find(ch => ch.id === bookmark.chapterId);
    const chapterIndex = this.chapters.findIndex(ch => ch.id === bookmark.chapterId);
    const chapterNumber = chapterIndex >= 0 ? chapterIndex + 1 : bookmark.chapterId + 1;
    const page = Math.floor(bookmark.wordOffset / Math.max(1, this.pageSizeWords)) + 1;
    const chapterLabel = chapter?.displayLabel || chapter?.title || `Chapter ${chapterNumber}`;
    const normalized = chapterLabel.trim();
    const chapterMatch = normalized.match(/^chapter\s+(\d+)/i);
    if (chapterMatch) {
      return `Ch. ${chapterMatch[1]} p. ${page}`;
    }

    const romanMatch = normalized.match(/^([ivxlcdm]+)\b/i);
    if (romanMatch && !/^chapter\b/i.test(normalized)) {
      return `${romanMatch[1].toLowerCase()} p. ${page}`;
    }

    return `${chapterLabel} • p. ${page}`;
  }

  summarize(): void {
    if (!this.chapterContent) return;
    this.loadingSummary = true;
    this.selectedText = null;

    // Use fixed 1000-word chunks based on current word offset
    const chunkSize = 1000;
    const chunkStartWord = Math.floor(this.wordOffset / chunkSize) * chunkSize;
    const text = this.sliceByWords(this.chapterContent.content, chunkStartWord, chunkSize);

    const allKnownWords = this.vocabularyService.getKnownWordsForPrompt();
    // Limit to 100 most recent/varied known words to avoid overwhelming the AI
    const knownWords = allKnownWords.slice(-100);

    console.log(`Summarizing chunk starting at word ${chunkStartWord} (${text.split(' ').length} words) with ${knownWords.length} known words excluded`);

    const payload = {
      text,
      bookTitle: this.selectedBook?.title ?? '',
      dropboxPath: this.selectedBookPath ?? '',
      chapterId: this.selectedChapterId ?? undefined,
      wordOffset: this.wordOffset,
      knownWords
    };

    this.api.summarizeText(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        this.summary = resp.summary;
        this.parseSummaryOnce(resp.summary);
        this.loadingSummary = false;
        this.refreshTokenUsage();
      },
      error: err => {
        console.error('Failed to summarize', err);
        this.summary = 'Summarization failed.';
        this.loadingSummary = false;
        this.refreshTokenUsage();
      }
    });
  }

  summarizeFullChapter(force: boolean = false): void {
    if (!this.chapterContent || !this.selectedBookPath || this.selectedChapterId === null) return;

    // Check if summary already exists in cache
    const cached = this.fullChapterSummaryCache.get(this.selectedChapterId);
    if (cached && !force) {
      console.log('Chapter summary already exists. Use the existing summary.');
      return;
    }
    if (force && cached) {
      this.fullChapterSummaryCache.delete(this.selectedChapterId);
    }

    this.loadingFullChapterSummary = true;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;
    this.selectedText = null;
    this.chapterSummaryProgress = {
      stage: 'chunks',
      currentStep: 0,
      totalSteps: 1,
      message: 'Starting chapter analysis...',
      error: undefined
    };

    const payload = {
      dropboxPath: this.selectedBookPath,
      chapterId: this.selectedChapterId,
      bookTitle: this.selectedBook?.title ?? '',
      forceRegenerate: force
    };

    // Use fetch to POST the request and get streaming response
    const apiBase = window.location.protocol + '//' + window.location.hostname + ':5051';
    const token = this.authService.getToken();
    const headers: Record<string, string> = {
      'Content-Type': 'application/json'
    };
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    fetch(`${apiBase}/api/ai/summarize/chapter/stream`, {
      method: 'POST',
      headers: headers,
      body: JSON.stringify(payload)
    }).then(response => {
      if (response.status === 409) {
        this.chapterSummaryProgress = {
          stage: 'error',
          currentStep: 0,
          totalSteps: 1,
          message: 'Summary already in progress',
          error: 'Please wait for the current summary to finish.'
        };
        this.loadingFullChapterSummary = false;
        return;
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const reader = response.body?.getReader();
      const decoder = new TextDecoder();
      let buffer = ''; // Buffer for incomplete SSE messages

      const readStream = async (): Promise<void> => {
        const { done, value } = await reader!.read();
        if (done) {
          console.log('SSE stream complete');
          return;
        }

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');

        // Keep the last incomplete line in the buffer
        buffer = lines.pop() || '';

        let currentEvent = '';
        for (const line of lines) {
          if (line.startsWith('event:')) {
            currentEvent = line.substring(6).trim();
            continue;
          }

          if (line.startsWith('data:')) {
            const data = line.substring(5).trim();
            if (!data) continue;

            try {
              const parsed = JSON.parse(data);
              console.log(`SSE ${currentEvent}:`, parsed);
              this.handleSSEEvent(parsed);
            } catch (e) {
              console.error('Failed to parse SSE data:', data, e);
            }
          }
        }

        return readStream();
      };

      return readStream();
    }).catch(err => {
      console.error('Failed to summarize full chapter', err);
      this.fullChapterSummary = 'Full chapter summary failed.';
      this.chapterSummaryProgress = {
        stage: 'error',
        currentStep: 0,
        totalSteps: 1,
        message: 'Summary failed',
        error: err.message || 'Unknown error'
      };
      this.loadingFullChapterSummary = false;
      this.refreshTokenUsage();
    });
  }

  handleUltraSummaryAction(): void {
    if (!this.selectedBookPath || this.selectedChapterId === null || !this.fullChapterSummary) return;
    if (this.loadingUltraChapterSummary) return;

    this.loadingUltraChapterSummary = true;
    const payload = {
      dropboxPath: this.selectedBookPath,
      chapterId: this.selectedChapterId,
      bookTitle: this.selectedBook?.title,
      forceRegenerate: !!this.ultraChapterSummary
    };

    this.api.generateUltraChapterSummary(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        this.ultraChapterSummary = resp.summary;
        this.ultraSummaryTokens = {
          total: resp.totalTokens,
          prompt: resp.promptTokens,
          completion: resp.completionTokens,
          allowancePercent: resp.allowanceUsedPercent ?? undefined,
          remaining: resp.tokensRemaining ?? undefined
        };
        this.loadingUltraChapterSummary = false;
      },
      error: err => {
        console.error('Ultra summary failed', err);
        this.loadingUltraChapterSummary = false;
      }
    });
  }

  private handleSSEEvent(event: any): void {
    console.log('Handling SSE event:', event);

    // Run inside Angular zone to trigger change detection
    this.ngZone.run(() => {
      if (event.stage && event.stepNumber !== undefined) {
        // Progress event
        console.log(`Progress: ${event.stage} ${event.stepNumber}/${event.totalSteps}`);
        this.chapterSummaryProgress = {
          stage: event.stage as any,
          currentStep: event.stepNumber,
          totalSteps: event.totalSteps,
          message: event.message,
          error: event.error
        };
      } else if (event.summary) {
        // Complete event
        console.log('Summary complete! Length:', event.summary.length);
        this.fullChapterSummary = event.summary;
        this.fullSummaryTokens = {
          total: event.totalTokens,
          prompt: event.promptTokens,
          completion: event.completionTokens,
          allowancePercent: event.allowanceUsedPercent ?? undefined,
          remaining: event.tokensRemaining ?? undefined
        };

        this.chapterSummaryProgress = {
          stage: 'complete',
          currentStep: 1,
          totalSteps: 1,
          message: 'Chapter summary complete!',
          error: undefined
        };

        this.loadingFullChapterSummary = false;

        const resp = {
          summary: event.summary,
          promptTokens: event.promptTokens,
          completionTokens: event.completionTokens,
          totalTokens: event.totalTokens,
          allowanceUsedPercent: event.allowanceUsedPercent,
          tokensRemaining: event.tokensRemaining,
          cachedAt: event.cachedAt,
          steps: []
        };
        this.fullChapterSummaryCache.set(this.selectedChapterId!, resp);
        this.refreshTokenUsage();

        this.timeoutIds.push(setTimeout(() => this.chapterSummaryProgress = null, 5000));
      } else if (event.message && event.error) {
        // Error event
        console.error('Error event:', event);
        this.chapterSummaryProgress = {
          stage: 'error',
          currentStep: event.stepNumber || 0,
          totalSteps: event.totalSteps || 1,
          message: event.message,
          error: event.error
        };
        this.loadingFullChapterSummary = false;
        this.refreshTokenUsage();
      } else {
        console.warn('Unknown SSE event format:', event);
      }
    });
  }

  getStageLabel(stage: string): string {
    switch (stage) {
      case 'chunks': return 'Analyzing Chunks';
      case 'sections': return 'Synthesizing Sections';
      case 'final': return 'Final Summary';
      case 'complete': return 'Complete';
      case 'error': return 'Error';
      default: return 'Processing';
    }
  }

  private loadBooks(): void {
    this.loadingBooks = true;
    this.error = null;

    this.api.getLibraryReaderBooks()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: books => {
        this.readerBooks = books;
        this.reconcilePreviouslyViewed();
        this.loadingBooks = false;
        this.applyPendingSelection();
      },
      error: err => {
        console.error('Failed to load reader library books', err);
        this.error = 'Unable to load reader books.';
        this.loadingBooks = false;
      }
    });
  }

  private loadChapters(fileName: string): void {
    this.loadingChapters = true;
    this.chapters = [];

    this.api.getLibraryReaderChapters(fileName)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        this.chapters = resp.chapters
          .filter(ch => ch.wordCount >= 50)
          .map(ch => ({
            ...ch,
            displayLabel: ch.displayLabel ?? ch.title
          }));
        this.loadingChapters = false;
        if (!this.chapters.length) {
          this.error = 'No chapters found in this EPUB.';
          this.pendingRestorePosition = null;
          return;
        }

        if (this.pendingRestorePosition && this.selectedBookPath === this.pendingRestorePosition.readerKey) {
          const targetChapter = this.chapters.find(ch => ch.id === this.pendingRestorePosition!.chapterId);
          if (targetChapter) {
            this.pendingWordOffset = this.pendingRestorePosition.wordOffset;
            this.onChapterSelected(targetChapter.id, true);
          }
          this.pendingRestorePosition = null;
        }
        this.applyPendingChapterSelection();
      },
      error: err => {
        console.error('Failed to load EPUB chapters', err);
        this.error = 'Unable to load chapters for this book.';
        this.loadingChapters = false;
      }
    });
  }

  private stopStatusPolling(): void {
    if (this.statusPoll) {
      clearInterval(this.statusPoll);
      this.statusPoll = null;
    }
    this.statusPollAttempts = 0;
    this.lastStatusPercent = null;
  }

  private loadChapterContent(fileName: string, chapterId: number): void {
    this.loadingContent = true;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;

    const cacheKey = this.getChapterCacheKey(fileName, chapterId);
    const cached = this.chapterContentCache.get(cacheKey);
    if (cached) {
      this.applyChapterContent(cached);
      this.loadingContent = false;
    }

    this.api.getLibraryReaderChapterContent(fileName, chapterId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: content => {
        const normalized = this.normalizeChapterContent(content);
        this.chapterContentCache.set(cacheKey, normalized);
        if (this.selectedBookFileName === fileName && this.selectedChapterId === chapterId) {
          this.applyChapterContent(normalized);
          this.loadingContent = false;
        }
      },
      error: err => {
        console.error('Failed to load chapter content', err);
        this.error = 'Unable to load chapter content.';
        this.loadingContent = false;
      }
    });
  }

  private subscribeToReaderParams(): void {
    this.route.queryParamMap
      .pipe(takeUntil(this.destroy$))
      .subscribe(params => {
        const fileName = params.get('fileName');
        if (!fileName) {
          this.pendingFileName = null;
          this.pendingChapterId = null;
          this.autoSelectFirstChapter = false;
          this.autoSelectChapterId = null;
          return;
        }

        const chapterParam = params.get('chapterId');
        const chapterId = chapterParam != null ? Number(chapterParam) : null;
        this.pendingFileName = fileName;
        this.pendingChapterId = Number.isFinite(chapterId) ? chapterId : null;
        this.applyPendingSelection();
      });
  }

  private applyPendingSelection(): void {
    if (!this.pendingFileName || !this.readerBooks.length) return;

    const match = this.readerBooks.find(book => book.fileName === this.pendingFileName);
    if (!match) return;

    this.autoSelectFirstChapter = this.pendingChapterId == null;
    this.autoSelectChapterId = this.pendingChapterId;
    this.pendingFileName = null;
    this.pendingChapterId = null;

    this.onBookSelected(match.fileName);
  }

  private applyPendingChapterSelection(): void {
    if (!this.chapters.length) return;

    if (this.autoSelectChapterId != null) {
      const match = this.chapters.find(ch => ch.id === this.autoSelectChapterId);
      if (match) {
        this.onChapterSelected(match.id);
      }
      this.autoSelectChapterId = null;
      this.autoSelectFirstChapter = false;
      return;
    }

    if (this.autoSelectFirstChapter) {
      this.onChapterSelected(this.chapters[0].id);
      this.autoSelectFirstChapter = false;
    }
  }

  private getChapterCacheKey(fileName: string, chapterId: number): string {
    return `${fileName}::${chapterId}`;
  }

  private normalizeChapterContent(content: DropboxChapterContent): DropboxChapterContent {
    const collapsed = this.collapseBlankLines(content.content);
    const computedWordCount = this.countWords(collapsed);
    return {
      ...content,
      content: collapsed,
      wordCount: computedWordCount
    };
  }

  private applyChapterContent(content: DropboxChapterContent): void {
    const computedWordCount = content.wordCount ?? this.countWords(content.content);
    this.chapterContent = {
      ...content,
      wordCount: computedWordCount
    };
    if (this.pendingWordOffset != null) {
      const totalWords = computedWordCount;
      this.wordOffset = Math.max(0, Math.min(this.pendingWordOffset, totalWords));
      this.pendingWordOffset = null;
    } else if (this.pendingCharOffset != null) {
      if (this.pendingCharOffset === -1) {
        // Special marker: go to last page
        const totalPages = Math.max(1, Math.ceil(computedWordCount / this.pageSizeWords));
        this.wordOffset = Math.max(0, (totalPages - 1) * this.pageSizeWords);
        console.log(`📖 Jumped to last page (offset ${this.wordOffset}) of chapter with ${computedWordCount} words`);
      } else {
        const slice = content.content.slice(0, Math.min(this.pendingCharOffset, content.content.length));
        this.wordOffset = this.countWords(slice);
      }
    } else {
      this.wordOffset = 0;
    }
    this.pendingCharOffset = null;
    this.loadCachedFullChapterSummary();
    this.updateReadingPosition();
    if (this.selectedBookPath && this.selectedChapterId !== null && !this.chunkBoundaries && !this.loadingChunkBoundaries) {
      this.loadChunkBoundaries(this.selectedBookPath, this.selectedChapterId);
    }
    this.timeoutIds.push(setTimeout(() => this.recalcPageSize(), 0));
  }

  private fetchStatus(fileName: string, keepPolling: boolean = false): void {
    this.api.getLibraryReaderStatus(fileName)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: status => {
        const percent = status?.percent ?? null;
        const progressed = percent !== null && percent !== this.lastStatusPercent;
        if (progressed) {
          this.lastStatusPercent = percent;
          this.statusPollAttempts = 0;
        } else if (keepPolling) {
          this.statusPollAttempts += 1;
        }

        this.status = status;
        if (keepPolling && status.inProgress) {
          if (this.statusPoll) {
            clearInterval(this.statusPoll);
            this.statusPoll = null;
          }
          if (this.statusPollAttempts >= 30) {
            this.stopStatusPolling();
            return;
          }
          this.statusPoll = setInterval(() => this.fetchStatus(fileName, false), 3000);
        } else if (this.statusPoll && !status.inProgress) {
          this.stopStatusPolling();
        }
      },
      error: err => {
        console.error('Failed to fetch status', err);
        this.stopStatusPolling();
      }
    });
  }

  private loadPreviouslyViewed(): void {
    if (typeof localStorage === 'undefined') return;

    try {
      const raw = localStorage.getItem('epub_recent');
      if (!raw) return;
      const parsed = JSON.parse(raw) as any[];
      if (!Array.isArray(parsed)) {
        this.previouslyViewed = [];
        return;
      }

      this.previouslyViewed = parsed.map(item => {
        if ('fileName' in item && 'readerKey' in item) {
          return item as ViewedBook;
        }
        return {
          fileName: item.path ?? '',
          readerKey: item.path ?? '',
          title: item.name ?? item.path ?? 'Unknown',
          updatedAt: item.serverModified ?? undefined
        } as ViewedBook;
      }).filter(entry => entry.fileName && entry.readerKey);
      this.reconcilePreviouslyViewed();
    } catch {
      this.previouslyViewed = [];
    }
  }

  private loadBookmarks(): void {
    if (typeof localStorage === 'undefined') return;
    try {
      const raw = localStorage.getItem('epub_bookmarks');
      if (!raw) {
        this.bookmarks = [];
        return;
      }
      const parsed = JSON.parse(raw) as BookmarkEntry[];
      this.bookmarks = Array.isArray(parsed) ? parsed : [];
    } catch {
      this.bookmarks = [];
    }
  }

  private saveBookmarks(): void {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem('epub_bookmarks', JSON.stringify(this.bookmarks));
  }

  private loadLastPositions(): void {
    if (typeof localStorage === 'undefined') return;
    try {
      const raw = localStorage.getItem('epub_last_positions');
      if (!raw) return;
      const parsed = JSON.parse(raw) as Record<string, ReadingPosition>;
      if (!parsed || typeof parsed !== 'object') return;
      Object.entries(parsed).forEach(([key, value]) => {
        if (value && typeof value.chapterId === 'number' && typeof value.wordOffset === 'number') {
          this.lastPositions.set(key, value);
        }
      });
    } catch {
      this.lastPositions.clear();
    }
  }

  private persistLastPositions(): void {
    if (typeof localStorage === 'undefined') return;
    const payload: Record<string, ReadingPosition> = {};
    this.lastPositions.forEach((value, key) => {
      payload[key] = value;
    });
    localStorage.setItem('epub_last_positions', JSON.stringify(payload));
  }

  private updateReadingPosition(): void {
    if (!this.selectedBookPath || this.selectedChapterId === null) return;
    const entry: ReadingPosition = {
      chapterId: this.selectedChapterId,
      wordOffset: this.wordOffset,
      updatedAt: new Date().toISOString()
    };
    this.lastPositions.set(this.selectedBookPath, entry);
    this.persistLastPositions();
  }

  private queueRestorePosition(readerKey: string): void {
    if (!readerKey) return;
    const last = this.lastPositions.get(readerKey);
    if (!last) return;
    this.pendingRestorePosition = {
      readerKey,
      chapterId: last.chapterId,
      wordOffset: last.wordOffset
    };
  }

  private recordViewed(book: LibraryReaderBook): void {
    if (typeof localStorage === 'undefined') return;

    const entry: ViewedBook = {
      fileName: book.fileName,
      readerKey: book.readerKey,
      title: book.title,
      updatedAt: new Date().toISOString()
    };

    this.previouslyViewed = [
      entry,
      ...this.previouslyViewed.filter(b => b.readerKey !== book.readerKey)
    ].slice(0, 8);

    this.persistPreviouslyViewed();
  }

  clearPreviouslyViewed(): void {
    if (typeof localStorage === 'undefined') return;
    this.previouslyViewed = [];
    localStorage.removeItem('epub_recent');
  }

  removeFromReader(): void {
    if (!this.selectedBookFileName) return;
    const confirmRemove = typeof window !== 'undefined'
      ? window.confirm('Remove this book from the reader list?')
      : true;
    if (!confirmRemove) return;

    const fileName = this.selectedBookFileName;
    this.api.updateLibraryBookReaderEnabled(fileName, false)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.readerBooks = this.readerBooks.filter(book => book.fileName !== fileName);
          this.removePreviouslyViewedEntry(fileName);
          if (this.selectedBookPath) {
            this.lastPositions.delete(this.selectedBookPath);
            this.persistLastPositions();
          }
          this.resetSelectedBookState();
        },
        error: err => {
          console.error('Failed to remove book from reader', err);
          this.error = 'Unable to remove book from reader.';
        }
      });
  }

  private reconcilePreviouslyViewed(): void {
    if (!this.readerBooks.length) return;
    const allowed = new Set(this.readerBooks.map(book => book.fileName));
    const filtered = this.previouslyViewed.filter(entry => allowed.has(entry.fileName));
    if (filtered.length === this.previouslyViewed.length) return;
    this.previouslyViewed = filtered;
    this.persistPreviouslyViewed();
  }

  private persistPreviouslyViewed(): void {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem('epub_recent', JSON.stringify(this.previouslyViewed));
  }

  private removePreviouslyViewedEntry(fileName: string): void {
    const next = this.previouslyViewed.filter(entry => entry.fileName !== fileName);
    if (next.length === this.previouslyViewed.length) return;
    this.previouslyViewed = next;
    this.persistPreviouslyViewed();
  }

  private resetSelectedBookState(): void {
    this.selectedBookFileName = null;
    this.selectedBookPath = null;
    this.selectedChapterId = null;
    this.chapterContent = null;
    this.chapters = [];
    this.status = null;
    this.searchTerm = '';
    this.wordOffset = 0;
    this.error = null;
    this.summary = null;
    this.formattedAnalysis = null;
    this.vocabularyWords = [];
    this.analysisText = null;
    this.bookmarkSelectValue = null;
    this.pendingCharOffset = null;
    this.pendingWordOffset = null;
    this.chunkBoundaries = null;
    this.sectionSummaries.clear();
    this.currentSectionIndex = null;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;
    this.loadingUltraChapterSummary = false;
    this.stopStatusPolling();
  }

  // preloadCachedSummaries removed (offline preload deprecated for lean runtime).

  private loadCachedFullChapterSummary(): void {
    if (!this.selectedBookPath || this.selectedChapterId == null) return;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;
    this.selectedText = null;

    const cached = this.fullChapterSummaryCache.get(this.selectedChapterId);
    if (cached) {
      this.fullChapterSummary = cached.summary;
      this.fullSummaryTokens = {
        total: cached.totalTokens,
        prompt: cached.promptTokens,
        completion: cached.completionTokens,
        allowancePercent: cached.allowanceUsedPercent ?? undefined,
        remaining: cached.tokensRemaining ?? undefined
      };
      this.loadCachedUltraChapterSummary();
      return;
    }

    this.api.getFullChapterSummary(this.selectedBookPath, this.selectedChapterId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        this.fullChapterSummaryCache.set(this.selectedChapterId!, resp);
        this.fullChapterSummary = resp.summary;
        this.fullSummaryTokens = {
          total: resp.totalTokens,
          prompt: resp.promptTokens,
          completion: resp.completionTokens,
          allowancePercent: resp.allowanceUsedPercent ?? undefined,
          remaining: resp.tokensRemaining ?? undefined
        };
        this.loadCachedUltraChapterSummary();
      },
      error: () => {
        // No cached summary; ignore
      }
    });
  }

  private loadCachedUltraChapterSummary(): void {
    if (!this.selectedBookPath || this.selectedChapterId == null || !this.fullChapterSummary) return;
    this.ultraChapterSummary = null;
    this.ultraSummaryTokens = null;

    this.api.getUltraChapterSummary(this.selectedBookPath, this.selectedChapterId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        this.ultraChapterSummary = resp.summary;
        this.ultraSummaryTokens = {
          total: resp.totalTokens,
          prompt: resp.promptTokens,
          completion: resp.completionTokens,
          allowancePercent: resp.allowanceUsedPercent ?? undefined,
          remaining: resp.tokensRemaining ?? undefined
        };
      },
      error: () => {
        // No cached ultra summary; ignore
      }
    });
  }

  private refreshTokenUsage(): void {
    this.api.getTokenUsage()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: usage => {
        this.tokenUsage = usage;
      },
      error: err => {
        console.error('Failed to fetch token usage', err);
      }
    });
  }

  openVocabModal(): void {
    if (this.selectedBookPath && this.selectedBook) {
      this.vocabularyService.registerBook(this.selectedBookPath, this.selectedBook.title);
    }
    this.vocabFilters = this.vocabularyService.getBookFilters();

    // Auto-select the currently loaded book in the filter if available
    if (this.selectedBookPath && this.vocabFilters.some(f => f.id === this.selectedBookPath)) {
      this.vocabFilter = this.selectedBookPath;
    }

    this.refreshVocabLists();
    this.loadFlashcards();
    this.showVocabModal = true;
  }

  closeVocabModal(): void {
    this.showVocabModal = false;
  }

  onModalOverlayClick(event: MouseEvent): void {
    // Close modal when clicking the overlay (but not the modal itself)
    this.closeVocabModal();
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    if (this.showVocabModal) {
      event.preventDefault();
      this.closeVocabModal();
    }
  }

  openCharacterGraphModal(): void {
    if (!this.selectedBookPath || !this.selectedBook) return;

    this.dialog.open(CharacterGraphModalComponent, {
      width: '90vw',
      maxWidth: '1200px',
      height: '80vh',
      data: {
        dropboxPath: this.selectedBookPath,
        bookTitle: this.selectedBook.title
      }
    });
  }

  clearKnownWords(): void {
    this.vocabularyService.clearKnown();
    this.refreshVocabLists();
  }

  clearUnknownWords(): void {
    this.vocabularyService.clearUnknown();
    this.refreshVocabLists();
  }

  clearAllVocab(): void {
    this.vocabularyService.clearAll();
    this.refreshVocabLists();
  }

  moveKnownToStudy(term: string): void {
    // Use the vocab filter (selected book in modal) if available, otherwise use currently loaded book
    const bookId = this.vocabFilter !== 'all' ? this.vocabFilter : this.selectedBookPath ?? undefined;

    // Retrieve cached definition if available
    const cachedDefinition = this.vocabularyService.getCachedDefinition(term) || '';
    console.log(`🔄 [moveKnownToStudy] Moving '${term}' to study with cached definition: '${cachedDefinition}'`);

    this.vocabularyService.markAsUnknown(term, cachedDefinition, bookId);
    this.refreshVocabLists();
  }

  moveStudyToKnown(term: string): void {
    // Use the vocab filter (selected book in modal) if available, otherwise use currently loaded book
    const bookId = this.vocabFilter !== 'all' ? this.vocabFilter : this.selectedBookPath ?? undefined;
    this.vocabularyService.markAsKnown(term, bookId);
    this.refreshVocabLists();
  }

  private fetchLearnMoreAndImages(payload: any, cacheResult: boolean): void {
    this.loadingLearnMore = true;
    console.log(`Fetching learn more for "${payload.term}"`);

    this.api.learnMore(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: resp => {
        console.log(`Learn more response received for "${payload.term}"`);
        const cleaned = this.cleanModelHtml(resp.detail);

        // Extract Wikipedia URLs from the content
        const wikiUrls = this.extractWikipediaUrls(cleaned);
        console.log(`Found ${wikiUrls.length} Wikipedia URLs:`, wikiUrls);

        if (wikiUrls.length > 0) {
          // Fetch images from the first Wikipedia URL
          const firstWikiUrl = wikiUrls[0];
          const articleTitle = this.getWikipediaTitleFromUrl(firstWikiUrl);
          console.log(`Fetching images for Wikipedia article: "${articleTitle}"`);

          this.api.getWikiImages(articleTitle)
            .pipe(takeUntil(this.destroy$))
            .subscribe({
            next: wiki => {
              const images = wiki?.images || [];
              console.log(`Wiki images received:`, images.length, 'images', images);
              this.learnMoreImages = images;
              this.learnMoreContent = cleaned;
              this.learnMoreSafeContent = this.sanitizer.bypassSecurityTrustHtml(cleaned);
              if (cacheResult) {
                this.vocabularyService.cacheLearnMore(payload.term, cleaned, images);
              }
              this.loadingLearnMore = false;
            },
            error: err => {
              console.error(`Wiki images lookup failed:`, err);
              this.learnMoreImages = [];
              this.learnMoreContent = cleaned;
              this.learnMoreSafeContent = this.sanitizer.bypassSecurityTrustHtml(cleaned);
              if (cacheResult) {
                this.vocabularyService.cacheLearnMore(payload.term, cleaned, []);
              }
              this.loadingLearnMore = false;
            }
          });
        } else {
          console.log('No Wikipedia URLs found in content');
          this.learnMoreImages = [];
          this.learnMoreContent = cleaned;
          this.learnMoreSafeContent = this.sanitizer.bypassSecurityTrustHtml(cleaned);
          if (cacheResult) {
            this.vocabularyService.cacheLearnMore(payload.term, cleaned, []);
          }
          this.loadingLearnMore = false;
        }
      },
      error: (err) => {
        console.error(`Learn more failed for "${payload.term}":`, err);
        this.learnMoreContent = 'Failed to load details.';
        this.learnMoreSafeContent = this.learnMoreContent;
        this.learnMoreImages = [];
        this.loadingLearnMore = false;
      }
    });
  }

  private extractWikipediaUrls(html: string): string[] {
    const urls: string[] = [];
    // Match Wikipedia URLs in href attributes
    const regex = /href="(https?:\/\/en\.wikipedia\.org\/wiki\/[^"]+)"/gi;
    let match;
    while ((match = regex.exec(html)) !== null) {
      if (!urls.includes(match[1])) {
        urls.push(match[1]);
      }
    }
    return urls;
  }

  private getWikipediaTitleFromUrl(url: string): string {
    // Extract title from URL like https://en.wikipedia.org/wiki/Article_Title
    const match = url.match(/\/wiki\/(.+)$/);
    if (match) {
      return decodeURIComponent(match[1]);
    }
    return '';
  }

  loadFlashcards(): void {
    if (!this.selectedBookPath) {
      this.flashcards = [];
      return;
    }
    this.api.getFlashcards(this.selectedBookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: cards => (this.flashcards = cards || []),
      error: () => (this.flashcards = [])
    });
  }

  clearFlashcards(): void {
    if (!this.selectedBookPath) return;
    this.api.clearFlashcards(this.selectedBookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: () => (this.flashcards = []),
      error: () => {}
    });
  }

  deleteFlashcard(card: FlashcardItem): void {
    // Use the vocab filter (selected book in modal) if available, otherwise use currently loaded book
    const bookPath = this.vocabFilter !== 'all' ? this.vocabFilter : this.selectedBookPath;
    if (!bookPath) return;

    this.api.deleteFlashcard(bookPath, card.term)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.flashcards = this.flashcards.filter(c => c.term !== card.term);
        },
        error: () => {}
      });
  }

  learnMore(item: { term: string; definition: string }): void {
    if (this.loadingLearnMore) return;
    this.loadingLearnMore = true;
    this.learnMoreTerm = item.term;
    this.learnMoreContent = 'Loading…';
    this.learnMoreSafeContent = this.learnMoreContent;
    this.learnMoreImages = [];

    const cached = this.vocabularyService.getCachedLearnMore(item.term);
    if (cached) {
      this.learnMoreContent = cached.detail;
      this.learnMoreSafeContent = this.sanitizer.bypassSecurityTrustHtml(cached.detail);
      this.learnMoreImages = cached.images || [];
      this.loadingLearnMore = false;
      return;
    }

    const payload = {
      term: item.term,
      definition: item.definition,
      dropboxPath: this.selectedBookPath ?? undefined,
      bookTitle: this.selectedBook?.title ?? undefined,
      context: this.analysisText ?? undefined
    };
    this.fetchLearnMoreAndImages(payload, true);
  }

  closeLearnMore(): void {
    this.learnMoreContent = null;
    this.learnMoreTerm = null;
  }

  makeFlashcard(item: { term: string; definition: string }): void {
    if (this.loadingFlashcard) return;

    // Use the current book from reader, or fall back to vocab filter if no book is selected
    const bookPath = this.selectedBookPath || (this.vocabFilter !== 'all' ? this.vocabFilter : null);
    if (!bookPath) {
      console.warn('No book selected for flashcard creation');
      return;
    }

    this.loadingFlashcard = true;
    const payload = {
      term: item.term,
      definition: item.definition,
      dropboxPath: bookPath,
      bookTitle: this.selectedBook?.title ?? undefined,
      context: this.analysisText ?? undefined,
      saveToLibrary: true
    };
    this.api.createFlashcard(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: cards => {
        const normalized = Array.isArray(cards) ? cards : [cards as any];
        const updated = [...this.flashcards];
        normalized.forEach(card => {
          const idx = updated.findIndex(fc => fc.term.toLowerCase() === card.term.toLowerCase());
          if (idx >= 0) {
            updated[idx] = card;
          } else {
            updated.push(card);
          }
        });
        this.flashcards = updated;
        this.loadingFlashcard = false;
      },
      error: () => {
        this.loadingFlashcard = false;
      }
    });
  }

  private refreshVocabLists(): void {
    const filter = this.vocabFilter === 'all' ? undefined : this.vocabFilter;
    this.vocabKnownList = this.vocabularyService.getKnownWords(filter)
      .map(term => this.capitalizeWords(term))
      .sort((a, b) => a.localeCompare(b));
    const unknownMap = this.vocabularyService.getUnknownWords(filter);
    this.vocabUnknownList = Array.from(unknownMap.entries())
      .map(([term, definition]) => ({
        term: this.capitalizeWords(term),
        definition
      }))
      .sort((a, b) => a.term.localeCompare(b.term));
  }

  private capitalizeWords(text: string): string {
    return text.split(' ').map(word =>
      word.charAt(0).toUpperCase() + word.slice(1)
    ).join(' ');
  }

  onVocabFilterChange(id: string): void {
    this.vocabFilter = id;
    this.refreshVocabLists();

    // Also reload flashcards based on the filter
    if (id === 'all') {
      // Show flashcards for the currently open book
      this.loadFlashcards();
    } else {
      // Show flashcards for the filtered book
      this.loadFlashcardsForBook(id);
    }
  }

  private loadFlashcardsForBook(bookPath: string): void {
    if (!bookPath) {
      this.flashcards = [];
      return;
    }
    this.api.getFlashcards(bookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: cards => (this.flashcards = cards || []),
        error: () => (this.flashcards = [])
      });
  }

  deleteSelectedBook(): void {
    if (!this.vocabFilter || this.vocabFilter === 'all') {
      return;
    }

    const bookName = this.vocabFilters.find(f => f.id === this.vocabFilter)?.name || this.vocabFilter;

    const dialogRef = this.dialog.open(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Book Vocabulary',
        message: `Delete all vocabulary words from "${bookName}"?\n\nThis will remove all known words, study words, and flashcards associated with this book. This action cannot be undone.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        isDanger: true
      }
    });

    dialogRef.afterClosed().subscribe(confirmed => {
      if (!confirmed) {
        return;
      }

      // Delete vocabulary for the book
      this.vocabularyService.deleteBook(this.vocabFilter, (success, message) => {
        if (success) {
          console.log(`✅ ${message}`);

          // Delete flashcards for the book
          this.api.clearFlashcards(this.vocabFilter)
            .pipe(takeUntil(this.destroy$))
            .subscribe({
              next: () => {
                console.log(`✅ Deleted flashcards for "${bookName}"`);
              },
              error: (err) => {
                console.error(`❌ Failed to delete flashcards:`, err);
              }
            });

          // Switch back to "all" filter
          this.vocabFilter = 'all';
          this.vocabFilters = this.vocabularyService.getBookFilters();
          this.refreshVocabLists();
          this.loadFlashcards();

          // Show success message with another dialog
          this.dialog.open(ConfirmDialogComponent, {
            width: '400px',
            data: {
              title: 'Success',
              message: `Successfully deleted all vocabulary from "${bookName}"`,
              confirmText: 'OK',
              cancelText: null,
              isDanger: false
            }
          });
        } else {
          // Show error message with another dialog
          this.dialog.open(ConfirmDialogComponent, {
            width: '400px',
            data: {
              title: 'Error',
              message: `Failed to delete vocabulary: ${message}`,
              confirmText: 'OK',
              cancelText: null,
              isDanger: true
            }
          });
        }
      });
    });
  }

  private cleanModelHtml(text: string): string {
    if (!text) return '';
    // Strip common code fences (```html ... ```)
    let cleaned = text.replace(/```[\s]*html?/gi, '').replace(/```/g, '').trim();
    // Normalize double-slash image URLs to https and encode spaces/whitespace
    cleaned = cleaned.replace(/<img([^>]+)src="(\/\/|https?:\/\/)([^"]+)"/gi, (_m, pre, _proto, rest) => {
      const encoded = encodeURI(rest.trim().replace(/\s+/g, '_'));
      return `<img${pre}src="https://${encoded}"`;
    });
    // Ensure images have lazy loading, referrer policy, error hide, and basic styling
    cleaned = cleaned.replace(/<img([^>]*?)>/gi, (_match, attrs) => {
      const hasLoading = /loading\s*=/.test(attrs);
      const hasReferrer = /referrerpolicy\s*=/.test(attrs);
      const hasStyle = /style\s*=/.test(attrs);
      const hasOnError = /onerror\s*=/.test(attrs);
      const styleAppend = 'display:block;margin:6px 0;max-width:100%;border-radius:8px;';

      let finalAttrs = `${attrs}`;
      if (!hasLoading) finalAttrs += ' loading="lazy"';
      if (!hasReferrer) finalAttrs += ' referrerpolicy="no-referrer"';
      if (!hasOnError) finalAttrs += ' onerror="this.style.display=\'none\'"';
      if (!hasStyle) finalAttrs += ` style="${styleAppend}"`;

      return `<img${finalAttrs}>`;
    });
    return cleaned;
  }

  private sliceByWords(text: string, startWord: number, count: number): string {
    const regex = /\S+/g;
    let match: RegExpExecArray | null;
    let wordIndex = 0;
    let startIdx: number | null = null;
    let endIdx: number | null = null;

    while ((match = regex.exec(text)) !== null) {
      if (wordIndex === startWord) startIdx = match.index;
      if (wordIndex === startWord + count) {
        endIdx = match.index;
        break;
      }
      wordIndex++;
    }

    if (startIdx === null) return '';
    if (endIdx === null) endIdx = text.length;
    return text.slice(startIdx, endIdx);
  }

  @HostListener('mouseup')
  @HostListener('touchend')
  captureSelection(): void {
    this.updateSelection();
  }

  @HostListener('document:selectionchange')
  onSelectionChange(): void {
    this.updateSelection();
  }

  private updateSelection(): void {
    const selection = window.getSelection();
    const text = selection ? selection.toString().trim() : '';
    this.selectedText = text || null;
  }

  private countWords(text: string): number {
    // Count words the same way sliceByWords iterates: any non-space token
    const regex = /\S+/g;
    const matches = text.match(regex);
    return matches ? matches.length : 0;
  }

  private collapseBlankLines(text: string): string {
    return text
      .replace(/\n[ \t]*\n[ \t]*\n+/g, '\n\n')
      .replace(/\n{3,}/g, '\n\n')
      .replace(/^\s*\n+/, '')
      .replace(/\n+\s*$/, '');
  }

  startResize(): void {
    this.isResizing = true;
  }

  startHorizontalResize(event: MouseEvent): void {
    this.isHorizontalResizing = true;
    event.preventDefault();
  }

  startHorizontalResizeTouch(event: TouchEvent): void {
    this.isHorizontalResizing = true;
    event.preventDefault();
  }

  toggleSidebar(show: boolean): void {
    this.showSidebar = show;
    this.timeoutIds.push(setTimeout(() => this.recalcPageSize(), 0));
  }

  changeFontSize(delta: number): void {
    const next = Math.min(28, Math.max(12, this.fontSize + delta));
    if (next !== this.fontSize) {
      this.fontSize = next;
      // Wait for DOM to update with new font size before recalculating
      this.timeoutIds.push(setTimeout(() => this.recalcPageSize(), 0));
    }
  }

  createVocabFromSelection(): void {
    const text = (this.selectedText || '').trim();
    if (!text || this.loadingSelectionVocab || !this.selectedBookPath) return;

    // Check if we're in section mode and have a valid section
    if (this.currentSectionIndex === null) {
      console.warn('⚠️ Cannot create vocab - no section selected');
      return;
    }

    // Capture the section index for use in callbacks (TypeScript null safety)
    const sectionIndex = this.currentSectionIndex;

    this.loadingSelectionVocab = true;

    // Determine selection type
    const wordCount = text.split(/\s+/).length;
    const isSingleWord = wordCount === 1;

    console.log(`🔤 Creating vocab from selection: ${isSingleWord ? 'single word' : `${wordCount} words`}`);

    const allKnownWords = this.vocabularyService.getKnownWordsForPrompt();
    const knownWords = allKnownWords.slice(-100);

    // Build context-aware instruction
    let contextInstruction = '';
    if (isSingleWord) {
      contextInstruction = `SINGLE WORD MODE: Create exactly ONE flashcard for the word "${text}". Include etymology, definition, usage examples, and any specialized meanings in this context.`;
    } else {
      contextInstruction = `PHRASE/PASSAGE MODE: Analyze this selection and create flashcards for the KEY CONCEPTS or DIFFICULT TERMS (not every word). Create 1-5 cards depending on complexity. Focus on:
- Main philosophical/technical concepts being discussed
- Specialized terminology that needs explanation
- Foreign phrases or archaic language
- Historical/cultural references

DO NOT create a card for every word. Only create cards for terms that add educational value.`;
    }

    const flashcardPayload = {
      term: text,
      definition: undefined,
      dropboxPath: this.selectedBookPath,
      bookTitle: this.selectedBook?.title ?? undefined,
      context: contextInstruction,
      knownWords: knownWords
    };

    this.api.createFlashcard(flashcardPayload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: cards => {
        console.log(`✅ Generated ${cards.length} vocabulary card(s) from selection`);

        // Merge new cards with existing section vocab (avoid duplicates)
        const existingTerms = new Set(this.vocabularyWords.map(v => v.term.toLowerCase()));
        const newCards = cards.filter(card => !existingTerms.has(card.term.toLowerCase()));

        if (newCards.length === 0) {
          console.log('ℹ️ All generated cards already exist in current section');
        } else {
          // Add new cards to vocabulary display
          const newVocabWords = newCards.map(card => ({
            term: card.term,
            definition: card.definition
          }));
          this.vocabularyWords = [...this.vocabularyWords, ...newVocabWords];

          // Get all current section cards (existing + new)
          const allSectionCards = [
            ...(this.sectionSummaries.get(sectionIndex)?.vocab || []),
            ...newCards
          ];

          // Save merged vocab to section cache
          if (this.selectedBookPath && this.selectedChapterId !== null) {
            this.api.saveSectionVocab(
              this.selectedBookPath,
              this.selectedChapterId,
              sectionIndex,
              allSectionCards
            )
              .pipe(takeUntil(this.destroy$))
              .subscribe({
              next: () => {
                console.log(`💾 Saved ${newCards.length} new vocab card(s) to section ${sectionIndex} cache`);

                // Update in-memory section summary with new vocab
                const summary = this.sectionSummaries.get(sectionIndex);
                if (summary) {
                  this.sectionSummaries.set(sectionIndex, {
                    ...summary,
                    vocab: allSectionCards
                  });
                }
              },
              error: err => console.error('Failed to save vocab to cache', err)
            });
          }
        }

        this.loadingSelectionVocab = false;
        this.refreshTokenUsage();
        this.selectedText = null;
      },
      error: err => {
        console.error('Failed to generate vocabulary cards from selection', err);
        this.loadingSelectionVocab = false;
        this.refreshTokenUsage();
      }
    });
  }

  @HostListener('window:mousemove', ['$event'])
  onMouseMove(event: MouseEvent): void {
    if (this.isHorizontalResizing && this.contentStackRef) {
      const rect = this.contentStackRef.nativeElement.getBoundingClientRect();
      const offsetX = event.clientX - rect.left;
      const percentX = Math.min(80, Math.max(20, (offsetX / rect.width) * 100));
      const left = percentX / 100;
      const right = 1 - left;
      this.leftFlex = `${left} 1 0`;
      this.rightFlex = `${right} 1 0`;
      this.recalcPageSize();
    }
  }

  @HostListener('window:touchmove', ['$event'])
  onTouchMove(event: TouchEvent): void {
    if (this.isHorizontalResizing && this.contentStackRef && event.touches.length > 0) {
      const rect = this.contentStackRef.nativeElement.getBoundingClientRect();
      const offsetX = event.touches[0].clientX - rect.left;
      const percentX = Math.min(80, Math.max(20, (offsetX / rect.width) * 100));
      const left = percentX / 100;
      const right = 1 - left;
      this.leftFlex = `${left} 1 0`;
      this.rightFlex = `${right} 1 0`;
      this.recalcPageSize();
    }
  }

  @HostListener('window:mouseup')
  onMouseUp(): void {
    this.isResizing = false;
    this.isHorizontalResizing = false;
    this.recalcPageSize();
  }

  @HostListener('window:touchend')
  onTouchEnd(): void {
    this.isResizing = false;
    this.isHorizontalResizing = false;
    this.recalcPageSize();
  }

  private escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  private escapeHtml(value: string): string {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.recalcPageSize();
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.showBookmarksDropdown) {
      this.showBookmarksDropdown = false;
    }
  }

  private recalcPageSize(adjustOffset: boolean = true): void {
    if (!this.textWindowRef) return;
    const el = this.textWindowRef.nativeElement;
    const height = el.clientHeight;
    const width = el.clientWidth;
    if (!height || !width) return;

    const style = getComputedStyle(el);
    const fontSize = parseFloat(style.fontSize) || this.fontSize;
    const lineHeight = parseFloat(style.lineHeight) || (fontSize * 1.7);
    const paddingY = parseFloat(style.paddingTop || '0') + parseFloat(style.paddingBottom || '0');
    const availableHeight = Math.max(0, height - paddingY);
    const lines = Math.max(3, Math.floor(availableHeight / lineHeight));

    // Estimate words per line based on available width and font size
    // Average character width is roughly 0.6 * fontSize for most fonts
    const avgCharWidth = fontSize * 0.6;
    const approxCharsPerLine = Math.max(16, Math.floor(width / avgCharWidth));
    const approxWordsPerLine = Math.max(4, Math.floor(approxCharsPerLine / 6));

    // Apply a safety factor to avoid overfilling a page (increased to fill more space)
    const safetyFactor = 0.75;
    const newSize = Math.max(20, Math.floor(lines * approxWordsPerLine * safetyFactor));

    if (newSize !== this.pageSizeWords) {
      const pageIndex = adjustOffset ? Math.floor(this.wordOffset / this.pageSizeWords) : 0;
      this.pageSizeWords = newSize;
      if (adjustOffset) {
        this.wordOffset = pageIndex * this.pageSizeWords;
      }
    }

    // Clamp offset to last page start
    const totalPages = this.totalPages;
    const maxStart = Math.max(0, (totalPages - 1) * this.pageSizeWords);
    if (this.wordOffset > maxStart) {
      this.wordOffset = maxStart;
    }

    // Update current section index based on word offset
    this.updateCurrentSection();
    this.updateReadingPosition();
  }

  // ─── Section/Chunk boundary methods ──────────────────────────────────────

  private loadChunkBoundaries(path: string, chapterId: number): void {
    if (!path || chapterId < 0) return;

    this.loadingChunkBoundaries = true;
    this.chunkBoundariesProgress = {
      stage: 'detecting',
      currentStep: 0,
      totalSteps: 1,
      message: 'Starting section detection...',
      error: undefined
    };

    // Use fetch to GET with SSE support
    const apiBase = window.location.protocol + '//' + window.location.hostname + ':5051';
    const token = this.authService.getToken();
    const headers: Record<string, string> = {};
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const url = `${apiBase}/api/ai/chunk-boundaries?dropboxPath=${encodeURIComponent(path)}&chapterId=${chapterId}`;

    fetch(url, {
      method: 'GET',
      headers: headers
    }).then(response => {
      if (response.status === 409) {
        this.ngZone.run(() => {
          this.chunkBoundariesProgress = {
            stage: 'error',
            currentStep: 0,
            totalSteps: 1,
            message: 'Section detection already in progress',
            error: 'Please wait for the current detection to finish.'
          };
          this.loadingChunkBoundaries = false;
        });
        return;
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const contentType = response.headers.get('content-type');
      if (contentType && contentType.includes('application/json')) {
        // Cached result - return as JSON
        return response.json().then(data => {
          this.ngZone.run(() => {
            this.chunkBoundaries = data as ChunkBoundariesResponse;
            this.loadingChunkBoundaries = false;
            this.chunkBoundariesProgress = null;
            this.updateCurrentSection();
            console.log(`✅ Loaded ${data.chunks.length} cached chunk boundaries for chapter ${chapterId}`);

            // Auto-load cached summary for current section (if it exists)
            if (this.currentSectionIndex !== null) {
              this.loadCachedSectionSummary(this.currentSectionIndex);
            }
          });
        });
      }

      // SSE stream - parse events
      const reader = response.body?.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      const readStream = async (): Promise<void> => {
        const { done, value } = await reader!.read();
        if (done) {
          console.log('SSE stream complete');
          return;
        }

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data:')) {
            const data = line.substring(5).trim();
            if (!data) continue;

            try {
              const parsed = JSON.parse(data);
              console.log('SSE event:', parsed);
              this.handleChunkBoundarySSEEvent(parsed);
            } catch (e) {
              console.error('Failed to parse SSE data:', data, e);
            }
          }
        }

        return readStream();
      };

      return readStream();
    }).catch(err => {
      console.error('Failed to load chunk boundaries', err);
      this.ngZone.run(() => {
        this.chunkBoundariesProgress = {
          stage: 'error',
          currentStep: 0,
          totalSteps: 1,
          message: 'Failed to detect section boundaries',
          error: err.message || 'Unknown error'
        };
        this.loadingChunkBoundaries = false;
      });
    });
  }

  private handleChunkBoundarySSEEvent(event: any): void {
    this.ngZone.run(() => {
      if (event.stage && event.stepNumber !== undefined) {
        // Progress event
        console.log(`Boundary detection: ${event.stage} ${event.stepNumber}/${event.totalSteps}`);
        this.chunkBoundariesProgress = {
          stage: event.stage as any,
          currentStep: event.stepNumber,
          totalSteps: event.totalSteps,
          message: event.message,
          error: event.error
        };
      } else if (event.chapterId !== undefined && event.chunks) {
        // Complete event
        console.log('Boundary detection complete! Sections:', event.chunks.length);
        this.chunkBoundaries = event as ChunkBoundariesResponse;
        this.chunkBoundariesProgress = {
          stage: 'complete',
          currentStep: 1,
          totalSteps: 1,
          message: 'Section boundaries detected!',
          error: undefined
        };
        this.loadingChunkBoundaries = false;
        this.updateCurrentSection();

        // Auto-load cached summary for current section
        if (this.currentSectionIndex !== null) {
          this.loadCachedSectionSummary(this.currentSectionIndex);
        }

        this.timeoutIds.push(setTimeout(() => this.chunkBoundariesProgress = null, 3000));
      } else if (event.error) {
        // Error event
        console.error('Boundary detection error:', event);
        this.chunkBoundariesProgress = {
          stage: 'error',
          currentStep: event.stepNumber || 0,
          totalSteps: event.totalSteps || 1,
          message: event.message || 'Detection failed',
          error: event.error
        };
        this.loadingChunkBoundaries = false;
      }
    });
  }

  private updateCurrentSection(): void {
    if (!this.chunkBoundaries || !this.chunkBoundaries.chunks.length) {
      this.currentSectionIndex = null;
      return;
    }

    // Find which section contains the current word offset
    for (let i = 0; i < this.chunkBoundaries.chunks.length; i++) {
      const chunk = this.chunkBoundaries.chunks[i];
      if (this.wordOffset >= chunk.start && this.wordOffset < chunk.end) {
        this.currentSectionIndex = i;
        return;
      }
    }

    // If not found, set to last section if we're beyond all chunks
    if (this.wordOffset >= this.chunkBoundaries.chunks[this.chunkBoundaries.chunks.length - 1].end) {
      this.currentSectionIndex = this.chunkBoundaries.chunks.length - 1;
    }
  }

  generateSectionSummary(sectionIndex: number): void {
    if (!this.selectedBookPath || this.selectedChapterId === null || !this.chunkBoundaries || !this.chapterContent) return;
    if (sectionIndex < 0 || sectionIndex >= this.chunkBoundaries.chunks.length) return;

    // Check if already loading
    if (this.loadingSectionSummary) return;

    // If regenerating (summary already exists), remove old summary and clear vocab
    const isRegenerating = this.sectionSummaries.has(sectionIndex);
    if (isRegenerating) {
      console.log(`🔄 Regenerating summary for section ${sectionIndex}`);
      this.sectionSummaries.delete(sectionIndex);
      this.vocabularyWords = [];
    }

    this.loadingSectionSummary = true;

    const summaryPayload = {
      dropboxPath: this.selectedBookPath,
      chapterId: this.selectedChapterId,
      sectionIndex: sectionIndex,
      bookTitle: this.selectedBook?.title ?? undefined,
      author: undefined
    };

    // Step 1: Generate summary (fast)
    this.api.generateSectionSummary(summaryPayload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: summary => {
        this.sectionSummaries.set(sectionIndex, summary);
        this.loadingSectionSummary = false;
        this.refreshTokenUsage();
        console.log(`✅ Generated summary for section ${sectionIndex}`);

        // Clear vocabulary while we generate it
        this.vocabularyWords = [];

        // Step 2: Generate vocabulary cards from section text (separate call)
        this.generateSectionVocabulary(sectionIndex);
      },
      error: err => {
        console.error('Failed to generate section summary', err);
        console.error('Error details:', {
          status: err.status,
          statusText: err.statusText,
          message: err.error?.error || err.message,
          url: err.url
        });
        this.loadingSectionSummary = false;
        this.refreshTokenUsage();
      }
    });
  }

  private generateSectionVocabulary(sectionIndex: number): void {
    if (!this.selectedBookPath || !this.chapterContent || !this.chunkBoundaries) return;
    if (sectionIndex < 0 || sectionIndex >= this.chunkBoundaries.chunks.length) return;

    // Extract section text from chapter content
    const chunk = this.chunkBoundaries.chunks[sectionIndex];
    const words = this.chapterContent.content.split(/\s+/);
    const sectionWords = words.slice(chunk.start, chunk.start + chunk.wordCount);

    // Limit to max 1000 words to avoid overwhelming the API
    const limitedWords = sectionWords.slice(0, 1000);
    const sectionText = limitedWords.join(' ');

    console.log(`🔤 Generating vocabulary cards for section ${sectionIndex} (${limitedWords.length} words)...`);

    // Get known words to exclude from vocabulary
    const allKnownWords = this.vocabularyService.getKnownWordsForPrompt();
    const knownWords = allKnownWords.slice(-100); // Limit to 100 most recent

    const flashcardPayload = {
      term: sectionText,
      dropboxPath: this.selectedBookPath,
      bookTitle: this.selectedBook?.title ?? undefined,
      knownWords: knownWords,
      saveToLibrary: false
    };

    this.api.createFlashcard(flashcardPayload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: cards => {
        // Convert flashcard items to vocabulary words
        this.vocabularyWords = cards.map(card => ({
          term: card.term,
          definition: card.definition
        }));
        console.log(`✅ Generated ${cards.length} vocabulary cards for section ${sectionIndex}`);

        // Save vocab to cache
        if (this.selectedBookPath && this.selectedChapterId !== null && cards.length > 0) {
          this.api.saveSectionVocab(this.selectedBookPath, this.selectedChapterId, sectionIndex, cards)
            .pipe(takeUntil(this.destroy$))
            .subscribe({
            next: () => console.log(`💾 Saved ${cards.length} vocab cards to cache`),
            error: err => console.error('Failed to save vocab to cache', err)
          });
        }

        this.refreshTokenUsage();
      },
      error: err => {
        console.error('Failed to generate vocabulary cards', err);
        this.vocabularyWords = [];
        this.refreshTokenUsage();
      }
    });
  }

  navigateToSection(sectionIndex: number): void {
    if (!this.chunkBoundaries || sectionIndex < 0 || sectionIndex >= this.chunkBoundaries.chunks.length) return;

    const chunk = this.chunkBoundaries.chunks[sectionIndex];
    this.wordOffset = chunk.start;
    this.currentSectionIndex = sectionIndex;
    this.updateReadingPosition();

    // Clear vocabulary immediately when switching sections
    // It will be repopulated from cache if the new section has vocab
    this.vocabularyWords = [];

    // Auto-load cached summary and vocab if they exist
    this.loadCachedSectionSummary(sectionIndex);
  }

  private loadCachedSectionSummary(sectionIndex: number): void {
    if (!this.selectedBookPath || this.selectedChapterId === null) return;

    // Check if summary is already loaded in memory
    if (this.sectionSummaries.has(sectionIndex)) {
      console.log(`✅ Summary for section ${sectionIndex} already in memory`);
      // Still need to load vocab if it exists in the cached summary
      const summary = this.sectionSummaries.get(sectionIndex);
      if (summary?.vocab && summary.vocab.length > 0) {
        this.vocabularyWords = summary.vocab.map(card => ({
          term: card.term,
          definition: card.definition
        }));
        console.log(`✅ Loaded ${summary.vocab.length} vocab cards from memory`);
      } else {
        this.vocabularyWords = [];
      }
      return;
    }

    // Try to load from server cache (GET endpoint - no generation)
    this.api.getCachedSectionSummary(this.selectedBookPath, this.selectedChapterId, sectionIndex)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
      next: summary => {
        this.sectionSummaries.set(sectionIndex, summary);
        console.log(`✅ Auto-loaded cached summary for section ${sectionIndex}`);

        // Load vocab if available in cached summary
        if (summary.vocab && summary.vocab.length > 0) {
          this.vocabularyWords = summary.vocab.map(card => ({
            term: card.term,
            definition: card.definition
          }));
          console.log(`✅ Auto-loaded ${summary.vocab.length} vocab cards for section ${sectionIndex}`);
        } else {
          // Clear vocab if this section doesn't have any cached
          this.vocabularyWords = [];
        }

        // NOTE: Do NOT auto-generate vocab here
        // Vocab should only be generated when user explicitly clicks "Generate" or "Regenerate" button
      },
      error: () => {
        // No cached summary exists, that's fine - user will need to click "Generate"
        console.log(`ℹ️ No cached summary for section ${sectionIndex}`);
        // Clear vocab since there's no cached summary
        this.vocabularyWords = [];
      }
    });
  }

  hasSectionSummary(sectionIndex: number): boolean {
    return this.sectionSummaries.has(sectionIndex);
  }
}

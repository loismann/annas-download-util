import {
  Component,
  ElementRef,
  HostListener,
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

import {
  DropboxBookSearchResult,
  DropboxChapterContent,
  DropboxEpubChapter,
  DropboxEpubFile,
  DropboxEpubStatus,
  FlashcardItem,
  FullChapterSummaryResponse
} from '../models/dropbox-epub.model';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { VocabularyService, VocabularyWord } from '../services/vocabulary.service';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

interface ViewedBook {
  path: string;
  name: string;
  serverModified?: string;
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
    MatSliderModule
  ],
  templateUrl: './dropbox-reader.component.html',
  styleUrls: ['./dropbox-reader.component.css']
})
export class DropboxReaderComponent implements OnInit, OnDestroy {
  dropboxBooks: DropboxEpubFile[] = [];
  chapters: DropboxEpubChapter[] = [];

  selectedBookPath: string | null = null;
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

  bookSearchTerm = '';
  bookSearchResults: DropboxBookSearchResult[] = [];
  bookSearchError: string | null = null;
  private pendingCharOffset: number | null = null;

  topHeightPercent = 50;
  isResizing = false;
  isHorizontalResizing = false;

  @ViewChild('contentStack') contentStackRef!: ElementRef<HTMLDivElement>;
  @ViewChild('textWindow') textWindowRef!: ElementRef<HTMLDivElement>;

  summary: string | null = null;
  loadingSummary = false;
  loadingFullChapterSummary = false;
  fullChapterSummary: string | null = null;
  fullSummaryTokens: { total: number; prompt: number; completion: number; allowancePercent?: number | null; remaining?: number | null } | null = null;
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
  vocabFilter: string = 'all';
  vocabFilters: { id: string; name: string }[] = [{ id: 'all', name: 'All books' }];
  leftFlex = '1 1 0';
  rightFlex = '1 1 0';
  showSidebar = true;
  fontFamily: 'serif' | 'sans' | 'mono' = 'serif';
  fontSize: number = 16;
  theme: 'light' | 'sepia' | 'dark' = 'dark';
  analysisMode: 'page' | 'chapter' = 'page';
  tokenUsage: { promptTokens: number; completionTokens: number; totalTokens: number; allowance?: number | null; allowanceUsedPercent?: number | null; tokensRemaining?: number | null; resetsAtUtc?: string | null } | null = null;
  fullChapterSummaryCache = new Map<number, FullChapterSummaryResponse>();

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

  constructor(
    private api: AnnaArchiveApiService,
    private vocabularyService: VocabularyService,
    private sanitizer: DomSanitizer
  ) {}

  ngOnInit(): void {
    this.loadPreviouslyViewed();
    this.loadBooks();
    this.vocabFilters = this.vocabularyService.getBookFilters();
    setTimeout(() => this.recalcPageSize(), 0);
    this.refreshTokenUsage();
  }

  ngOnDestroy(): void {
    if (this.statusPoll) clearInterval(this.statusPoll);
  }

  get selectedBook(): DropboxEpubFile | null {
    return this.dropboxBooks.find(b => b.path === this.selectedBookPath) ?? null;
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

    const escaped = this.escapeHtml(text);
    if (!this.searchTerm.trim()) {
      return escaped.replace(/\n/g, '<br/>');
    }

    const safeTerm = this.escapeRegExp(this.searchTerm.trim());
    const highlighted = escaped.replace(new RegExp(`(${safeTerm})`, 'gi'), '<mark>$1</mark>');
    return highlighted.replace(/\n/g, '<br/>');
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
    this.prefetchLearnMore(word.term, word.definition);
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
    if (!this.chapterContent) return 1;
    return Math.floor(this.wordOffset / this.pageSizeWords) + 1;
  }

  get totalPages(): number {
    if (!this.chapterContent) return 1;
    return Math.max(
      1,
      Math.ceil((this.chapterContent.wordCount || 0) / this.pageSizeWords)
    );
  }

  reloadBooks(): void {
    this.loadBooks();
  }

  onBookSelected(path: string): void {
    if (!path) return;
    this.selectedBookPath = path;
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

    this.recordViewed(path);
    if (this.selectedBook) {
      this.vocabularyService.registerBook(path, this.selectedBook.name);
      this.vocabFilters = this.vocabularyService.getBookFilters();
    }
    this.fetchStatus(path, true);
    this.loadChapters(path);
  }

  onChapterSelected(chapterId: number): void {
    if (!this.selectedBookPath) return;
    this.selectedBookPath = this.selectedBookPath; // keep for clarity
    this.selectedChapterId = chapterId;
    this.chapterContent = null;
    this.searchTerm = '';
    this.wordOffset = 0;
    this.pendingCharOffset = null;
    this.error = null;
    this.summary = null;
    this.formattedAnalysis = null;
    this.vocabularyWords = [];
    this.analysisText = null;
    this.analysisMode = 'page';

    this.loadChapterContent(this.selectedBookPath, chapterId);
  }

  onSearchTermChange(): void {
    // trigger template update
  }

  clearSearch(): void {
    this.searchTerm = '';
  }

  pageForward(): void {
    if (!this.chapterContent) return;
    const totalPages = this.totalPages;
    const maxStart = Math.max(0, (totalPages - 1) * this.pageSizeWords);
    this.wordOffset = Math.min(this.wordOffset + this.pageSizeWords, maxStart);
  }

  pageBack(): void {
    if (!this.chapterContent) return;
    this.wordOffset = Math.max(0, this.wordOffset - this.pageSizeWords);
  }

  startIndexing(): void {
    if (!this.selectedBookPath) return;
    this.api.startDropboxIndex(this.selectedBookPath).subscribe({
      next: () => this.fetchStatus(this.selectedBookPath!, true),
      error: err => {
        console.error('Failed to start indexing', err);
        this.error = 'Unable to start indexing.';
      }
    });
  }

  deleteIndex(): void {
    if (!this.selectedBookPath) return;
    this.api.deleteDropboxIndex(this.selectedBookPath).subscribe({
      next: () => {
        this.status = null;
        this.bookSearchResults = [];
        this.summary = null;
        if (this.statusPoll) clearInterval(this.statusPoll);
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
    if (!this.selectedBookPath) {
      this.bookSearchError = 'Select a book first.';
      return;
    }

    const term = this.bookSearchTerm.trim();
    if (term.length < 10) {
      this.bookSearchError = 'Enter at least 10 characters.';
      return;
    }

    this.api.searchDropboxBook(this.selectedBookPath, term).subscribe({
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
    if (!this.selectedBookPath) return;
    this.pendingCharOffset = match.position;
    this.onChapterSelected(match.chapterId);
  }

  summarize(): void {
    if (!this.chapterContent) return;
    this.loadingSummary = true;
    this.fullChapterSummary = null;
    const text = this.visibleText || this.chapterContent.content;

    const allKnownWords = this.vocabularyService.getKnownWordsForPrompt();
    // Limit to 100 most recent/varied known words to avoid overwhelming the AI
    const knownWords = allKnownWords.slice(-100);

    console.log(`Summarizing ${text.split(' ').length} words with ${knownWords.length} known words excluded`);

    const payload = {
      text,
      bookTitle: this.selectedBook?.name ?? '',
      dropboxPath: this.selectedBookPath ?? '',
      chapterId: this.selectedChapterId ?? undefined,
      wordOffset: this.wordOffset,
      knownWords
    };

    this.api.summarizeText(payload).subscribe({
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

  summarizeFullChapter(): void {
    if (!this.chapterContent || !this.selectedBookPath || this.selectedChapterId === null) return;
    this.loadingFullChapterSummary = true;
    this.fullChapterSummary = null;
    this.fullSummaryTokens = null;

    const payload = {
      dropboxPath: this.selectedBookPath,
      chapterId: this.selectedChapterId,
      bookTitle: this.selectedBook?.name ?? ''
    };

    this.api.summarizeFullChapter(payload).subscribe({
      next: resp => {
        this.fullChapterSummary = resp.summary;
        this.fullSummaryTokens = {
          total: resp.totalTokens,
          prompt: resp.promptTokens,
          completion: resp.completionTokens,
          allowancePercent: resp.allowanceUsedPercent ?? undefined,
          remaining: resp.tokensRemaining ?? undefined
        };
        this.loadingFullChapterSummary = false;
        this.fullChapterSummaryCache.set(this.selectedChapterId!, resp);
        this.refreshTokenUsage();
      },
      error: err => {
        console.error('Failed to summarize full chapter', err);
        this.fullChapterSummary = 'Full chapter summary failed.';
        this.loadingFullChapterSummary = false;
        this.refreshTokenUsage();
      }
    });
  }

  private loadBooks(): void {
    this.loadingBooks = true;
    this.error = null;

    this.api.getDropboxEpubs().subscribe({
      next: books => {
        this.dropboxBooks = books;
        this.loadingBooks = false;
      },
      error: err => {
        console.error('Failed to load Dropbox files', err);
        this.error = 'Unable to load Dropbox books.';
        this.loadingBooks = false;
      }
    });
  }

  private loadChapters(path: string): void {
    this.loadingChapters = true;
    this.chapters = [];

    this.api.getDropboxEpubChapters(path).subscribe({
      next: resp => {
        this.chapters = resp.chapters.filter(ch => ch.wordCount >= 50);
        this.loadingChapters = false;
        if (!this.chapters.length) {
          this.error = 'No chapters found in this EPUB.';
        }
      },
      error: err => {
        console.error('Failed to load EPUB chapters', err);
        this.error = 'Unable to load chapters for this book.';
        this.loadingChapters = false;
      }
    });
  }

  private loadChapterContent(path: string, chapterId: number): void {
    this.loadingContent = true;

    this.api.getDropboxChapterContent(path, chapterId).subscribe({
      next: content => {
        const collapsed = this.collapseBlankLines(content.content);
        const computedWordCount = this.countWords(collapsed);
        this.chapterContent = {
          ...content,
          content: collapsed,
          wordCount: computedWordCount
        };
        if (this.pendingCharOffset != null) {
          const slice = collapsed.slice(0, Math.min(this.pendingCharOffset, collapsed.length));
          this.wordOffset = this.countWords(slice);
        } else {
          this.wordOffset = 0;
        }
        this.pendingCharOffset = null;
        this.loadingContent = false;
        this.loadCachedFullChapterSummary();
        setTimeout(() => this.recalcPageSize(), 0);
      },
      error: err => {
        console.error('Failed to load chapter content', err);
        this.error = 'Unable to load chapter content.';
        this.loadingContent = false;
      }
    });
  }

  private fetchStatus(path: string, keepPolling: boolean = false): void {
    this.api.getDropboxEpubStatus(path).subscribe({
      next: status => {
        this.status = status;
        if (keepPolling && status.inProgress) {
          if (this.statusPoll) clearInterval(this.statusPoll);
          this.statusPoll = setInterval(() => this.fetchStatus(path, false), 2000);
        } else if (this.statusPoll && !status.inProgress) {
          clearInterval(this.statusPoll);
        }
      },
      error: err => {
        console.error('Failed to fetch status', err);
      }
    });
  }

  private loadPreviouslyViewed(): void {
    if (typeof localStorage === 'undefined') return;

    try {
      const raw = localStorage.getItem('epub_recent');
      if (!raw) return;
      const parsed = JSON.parse(raw) as ViewedBook[];
      this.previouslyViewed = Array.isArray(parsed) ? parsed : [];
    } catch {
      this.previouslyViewed = [];
    }
  }

  private recordViewed(path: string): void {
    if (typeof localStorage === 'undefined') return;

    const found = this.dropboxBooks.find(b => b.path === path);
    const entry: ViewedBook = {
      path,
      name: found?.name ?? path.split('/').pop() ?? path,
      serverModified: found?.serverModified
    };

    this.previouslyViewed = [
      entry,
      ...this.previouslyViewed.filter(b => b.path !== path)
    ].slice(0, 8);

    localStorage.setItem('epub_recent', JSON.stringify(this.previouslyViewed));
  }

  private loadCachedFullChapterSummary(): void {
    if (!this.selectedBookPath || this.selectedChapterId == null) return;

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
      return;
    }

    this.api.getFullChapterSummary(this.selectedBookPath, this.selectedChapterId).subscribe({
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
      },
      error: () => {
        // No cached summary; ignore
      }
    });
  }

  private refreshTokenUsage(): void {
    this.api.getTokenUsage().subscribe({
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
      this.vocabularyService.registerBook(this.selectedBookPath, this.selectedBook.name);
    }
    this.vocabFilters = this.vocabularyService.getBookFilters();
    this.refreshVocabLists();
    this.loadFlashcards();
    this.showVocabModal = true;
  }

  closeVocabModal(): void {
    this.showVocabModal = false;
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
    this.vocabularyService.markAsUnknown(term, '', this.selectedBookPath ?? undefined);
    this.refreshVocabLists();
  }

  moveStudyToKnown(term: string): void {
    this.vocabularyService.markAsKnown(term, this.selectedBookPath ?? undefined);
    this.refreshVocabLists();
  }

  private fetchLearnMoreAndImages(payload: any, cacheResult: boolean): void {
    this.loadingLearnMore = true;
    console.log(`Fetching learn more for "${payload.term}"`);

    this.api.learnMore(payload).subscribe({
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

          this.api.getWikiImages(articleTitle).subscribe({
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

  private prefetchLearnMore(term: string, definition: string): void {
    if (this.vocabularyService.getCachedLearnMore(term)) return;
    const payload = {
      term,
      definition,
      dropboxPath: this.selectedBookPath ?? undefined,
      bookTitle: this.selectedBook?.name ?? undefined,
      context: this.analysisText ?? undefined
    };
    this.fetchLearnMoreAndImages(payload, true);
  }

  loadFlashcards(): void {
    if (!this.selectedBookPath) {
      this.flashcards = [];
      return;
    }
    this.api.getFlashcards(this.selectedBookPath).subscribe({
      next: cards => (this.flashcards = cards || []),
      error: () => (this.flashcards = [])
    });
  }

  clearFlashcards(): void {
    if (!this.selectedBookPath) return;
    this.api.clearFlashcards(this.selectedBookPath).subscribe({
      next: () => (this.flashcards = []),
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
      bookTitle: this.selectedBook?.name ?? undefined,
      context: this.analysisText ?? undefined
    };
    this.fetchLearnMoreAndImages(payload, true);
  }

  closeLearnMore(): void {
    this.learnMoreContent = null;
    this.learnMoreTerm = null;
  }

  makeFlashcard(item: { term: string; definition: string }): void {
    if (!this.selectedBookPath || this.loadingFlashcard) return;
    this.loadingFlashcard = true;
    const payload = {
      term: item.term,
      definition: item.definition,
      dropboxPath: this.selectedBookPath,
      bookTitle: this.selectedBook?.name ?? undefined,
      context: this.analysisText ?? undefined
    };
    this.api.createFlashcard(payload).subscribe({
      next: card => {
        // replace or add
        const existingIndex = this.flashcards.findIndex(fc => fc.term.toLowerCase() === card.term.toLowerCase());
        if (existingIndex >= 0) {
          this.flashcards[existingIndex] = card;
        } else {
          this.flashcards = [...this.flashcards, card];
        }
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
    setTimeout(() => this.recalcPageSize(), 0);
  }

  changeFontSize(delta: number): void {
    const next = Math.min(28, Math.max(12, this.fontSize + delta));
    if (next !== this.fontSize) {
      this.fontSize = next;
      // Wait for DOM to update with new font size before recalculating
      setTimeout(() => this.recalcPageSize(), 0);
    }
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
  }
}

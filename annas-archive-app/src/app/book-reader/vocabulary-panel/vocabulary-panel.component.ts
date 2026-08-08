import {
  Component,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  OnDestroy,
  OnInit,
  Output,
  SimpleChanges, SecurityContext } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

import { ConfirmDialogComponent } from '../../components/confirm-dialog/confirm-dialog.component';
import { FlashcardItem, LearnMoreRequestPayload } from '../../models/dropbox-epub.model';
import { AiApiService } from '../../services/ai-api.service';
import { LoggerService } from '../../services/logger.service';
import { VocabularyService } from '../../services/vocabulary.service';
import { ReaderTextUtilsService } from '../services';
import { HideBrokenImagesDirective } from '../../shared/hide-broken-images.directive';

/**
 * The "Vocabulary lists" modal and its Learn More companion.
 *
 * Split out of BookReaderComponent: this is a self-contained modal surface whose
 * state (known/study lists, flashcards, the book filter, the Learn More payload)
 * is never read by the reader itself. The reader keeps the *inline* vocabulary
 * cards shown beneath a summary — those are a different feature that shares only
 * the VocabularyService.
 *
 * The two stay in sync without the reader mediating, because both subscribe to
 * `VocabularyService.knownWords$` / `studyWords$`. Marking a word known from the
 * inline list refreshes these lists on the next emission.
 */
@Component({
  selector: 'app-vocabulary-panel',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
    MatTooltipModule
  ,
    HideBrokenImagesDirective
  ],
  templateUrl: './vocabulary-panel.component.html',
  styleUrl: './vocabulary-panel.component.scss'
})
export class VocabularyPanelComponent implements OnInit, OnChanges, OnDestroy {
  /** Whether the vocabulary modal is showing. The reader owns the button that opens it. */
  @Input() open = false;
  /** Reader key of the book currently open, or null when none is. */
  @Input() bookPath: string | null = null;
  /** Title of the book currently open — used to label it in the filter list. */
  @Input() bookTitle: string | null = null;
  /** The current chapter analysis, passed to the AI as context for Learn More and flashcards. */
  @Input() context: string | null = null;

  @Output() closed = new EventEmitter<void>();

  private destroy$ = new Subject<void>();

  vocabFilter = 'all';
  vocabFilters: { id: string; name: string }[] = [{ id: 'all', name: 'All books' }];
  vocabKnownList: string[] = [];
  vocabUnknownList: { term: string; definition: string }[] = [];
  flashcards: FlashcardItem[] = [];

  learnMoreTerm: string | null = null;
  learnMoreContent: string | null = null;
  learnMoreSafeContent: SafeHtml | null = null;
  learnMoreImages: string[] = [];
  loadingLearnMore = false;
  loadingFlashcard = false;

  constructor(
    private aiApi: AiApiService,
    private vocabularyService: VocabularyService,
    private textUtils: ReaderTextUtilsService,
    private sanitizer: DomSanitizer,
    private dialog: MatDialog,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.vocabFilters = this.vocabularyService.getBookFilters();

    // Keep the lists current whether the change came from this modal or from the
    // reader's inline vocabulary cards.
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

  ngOnChanges(changes: SimpleChanges): void {
    // Opening is the trigger that used to be openVocabModal().
    if (changes['open'] && this.open && !changes['open'].previousValue) {
      this.onOpened();
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private onOpened(): void {
    // `bookTitle` is null exactly when no book is open — the reader passes
    // `selectedBook?.title ?? null`. Testing for null rather than truthiness
    // matters: a book whose title is an empty string is still an open book, and
    // the original guarded on the book object, not on the title's contents.
    if (this.bookPath && this.bookTitle !== null) {
      this.vocabularyService.registerBook(this.bookPath, this.bookTitle);
    }
    this.vocabFilters = this.vocabularyService.getBookFilters();

    // Auto-select the currently loaded book in the filter if available
    if (this.bookPath && this.vocabFilters.some(f => f.id === this.bookPath)) {
      this.vocabFilter = this.bookPath;
    }

    this.refreshVocabLists();
    this.loadFlashcards();
  }

  close(): void {
    this.closed.emit();
  }

  onModalOverlayClick(_event: MouseEvent): void {
    // Close modal when clicking the overlay (but not the modal itself)
    this.close();
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    if (this.open) {
      event.preventDefault();
      this.close();
    }
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
    const bookId = this.vocabFilter !== 'all' ? this.vocabFilter : this.bookPath ?? undefined;

    // Retrieve cached definition if available
    const cachedDefinition = this.vocabularyService.getCachedDefinition(term) || '';
    this.logger.log(`🔄 [moveKnownToStudy] Moving '${term}' to study with cached definition: '${cachedDefinition}'`);

    this.vocabularyService.markAsUnknown(term, cachedDefinition, bookId);
    this.refreshVocabLists();
  }

  moveStudyToKnown(term: string): void {
    // Use the vocab filter (selected book in modal) if available, otherwise use currently loaded book
    const bookId = this.vocabFilter !== 'all' ? this.vocabFilter : this.bookPath ?? undefined;
    // Get the definition from study words to preserve it when marking as known
    const definition = this.vocabularyService.getStudyWordDefinition(term);
    this.vocabularyService.markAsKnown(term, bookId, definition);
    this.refreshVocabLists();
  }

  loadFlashcards(): void {
    if (!this.bookPath) {
      this.flashcards = [];
      return;
    }
    this.aiApi.getFlashcards(this.bookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: cards => (this.flashcards = cards || []),
        error: () => (this.flashcards = [])
      });
  }

  private loadFlashcardsForBook(bookPath: string): void {
    this.aiApi.getFlashcards(bookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: cards => (this.flashcards = cards || []),
        error: () => (this.flashcards = [])
      });
  }

  clearFlashcards(): void {
    if (!this.bookPath) return;
    this.aiApi.clearFlashcards(this.bookPath)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => (this.flashcards = []),
        error: () => {}
      });
  }

  deleteFlashcard(card: FlashcardItem): void {
    // Use the vocab filter (selected book in modal) if available, otherwise use currently loaded book
    const bookPath = this.vocabFilter !== 'all' ? this.vocabFilter : this.bookPath;
    if (!bookPath) return;

    this.aiApi.deleteFlashcard(bookPath, card.term)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.flashcards = this.flashcards.filter(c => c.term !== card.term);
        },
        error: () => {}
      });
  }

  makeFlashcard(item: { term: string; definition: string }): void {
    if (this.loadingFlashcard) return;

    // Use the current book from reader, or fall back to vocab filter if no book is selected
    const bookPath = this.bookPath || (this.vocabFilter !== 'all' ? this.vocabFilter : null);
    if (!bookPath) {
      this.logger.warn('No book selected for flashcard creation');
      return;
    }

    this.loadingFlashcard = true;
    const payload = {
      term: item.term,
      definition: item.definition,
      dropboxPath: bookPath,
      bookTitle: this.bookTitle ?? undefined,
      context: this.context ?? undefined,
      saveToLibrary: true
    };
    this.aiApi.createFlashcard(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: cards => {
          const normalized: FlashcardItem[] = Array.isArray(cards) ? cards : [cards];
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
      this.learnMoreSafeContent = this.renderModelHtml(cached.detail);
      this.learnMoreImages = cached.images || [];
      this.loadingLearnMore = false;
      return;
    }

    this.fetchLearnMoreAndImages(
      {
        term: item.term,
        definition: item.definition,
        dropboxPath: this.bookPath ?? undefined,
        bookTitle: this.bookTitle ?? undefined,
        context: this.context ?? undefined
      },
      true
    );
  }

  closeLearnMore(): void {
    this.learnMoreContent = null;
    this.learnMoreTerm = null;
  }

  private fetchLearnMoreAndImages(payload: LearnMoreRequestPayload, cacheResult: boolean): void {
    this.loadingLearnMore = true;
    this.logger.log(`Fetching learn more for "${payload.term}"`);

    this.aiApi.learnMore(payload)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: resp => {
          this.logger.log(`Learn more response received for "${payload.term}"`);
          const cleaned = this.textUtils.cleanModelHtml(resp.detail);

          // Extract Wikipedia URLs from the content
          const wikiUrls = this.textUtils.extractWikipediaUrls(cleaned);
          this.logger.log(`Found ${wikiUrls.length} Wikipedia URLs:`, wikiUrls);

          if (wikiUrls.length === 0) {
            this.logger.log('No Wikipedia URLs found in content');
            this.applyLearnMoreResult(cleaned, [], payload.term, cacheResult);
            return;
          }

          // Fetch images from the first Wikipedia URL
          const articleTitle = this.textUtils.getWikipediaTitleFromUrl(wikiUrls[0]);
          this.logger.log(`Fetching images for Wikipedia article: "${articleTitle}"`);

          this.aiApi.getWikiImages(articleTitle)
            .pipe(takeUntil(this.destroy$))
            .subscribe({
              next: wiki => {
                const images = wiki?.images || [];
                this.logger.log(`Wiki images received:`, images.length, 'images', images);
                this.applyLearnMoreResult(cleaned, images, payload.term, cacheResult);
              },
              error: err => {
                // The article text is still worth showing without its pictures.
                this.logger.error(`Wiki images lookup failed:`, err);
                this.applyLearnMoreResult(cleaned, [], payload.term, cacheResult);
              }
            });
        },
        error: err => {
          this.logger.error(`Learn more failed for "${payload.term}":`, err);
          this.learnMoreContent = 'Failed to load details.';
          this.learnMoreSafeContent = this.learnMoreContent;
          this.learnMoreImages = [];
          this.loadingLearnMore = false;
        }
      });
  }

  /**
   * Model-written HTML is rendered, never trusted.
   *
   * The learn-more prompt asks for HTML on purpose — paragraphs, lists, links
   * and images are the feature — so the answer cannot simply be escaped. It is
   * sanitised instead: Angular keeps that structure and drops scripts, event
   * handlers and live `javascript:` URLs. Both the fresh and the cached path go
   * through here, because a poisoned cache entry outlives the response that
   * produced it.
   */
  private renderModelHtml(html: string): SafeHtml {
    return this.sanitizer.sanitize(SecurityContext.HTML, html) ?? '';
  }

  /** Shared tail of the three success paths above, which differ only in the image list. */
  private applyLearnMoreResult(
    cleaned: string,
    images: string[],
    term: string,
    cacheResult: boolean
  ): void {
    this.learnMoreImages = images;
    this.learnMoreContent = cleaned;
    this.learnMoreSafeContent = this.renderModelHtml(cleaned);
    if (cacheResult) {
      this.vocabularyService.cacheLearnMore(term, cleaned, images);
    }
    this.loadingLearnMore = false;
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

  private refreshVocabLists(): void {
    const filter = this.vocabFilter === 'all' ? undefined : this.vocabFilter;
    this.vocabKnownList = this.vocabularyService.getKnownWords(filter)
      .map(term => this.textUtils.capitalizeWords(term))
      .sort((a, b) => a.localeCompare(b));
    const unknownMap = this.vocabularyService.getUnknownWords(filter);
    this.vocabUnknownList = Array.from(unknownMap.entries())
      .map(([term, definition]) => ({
        term: this.textUtils.capitalizeWords(term),
        definition
      }))
      .sort((a, b) => a.term.localeCompare(b.term));
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
          this.logger.log(`✅ ${message}`);

          // Delete flashcards for the book
          this.aiApi.clearFlashcards(this.vocabFilter)
            .pipe(takeUntil(this.destroy$))
            .subscribe({
              next: () => {
                this.logger.log(`✅ Deleted flashcards for "${bookName}"`);
              },
              error: (err) => {
                this.logger.error(`❌ Failed to delete flashcards:`, err);
              }
            });

          // Switch back to "all" filter
          this.vocabFilter = 'all';
          this.vocabFilters = this.vocabularyService.getBookFilters();
          this.refreshVocabLists();
          this.loadFlashcards();

          this.showNotice('Success', `Successfully deleted all vocabulary from "${bookName}"`, false);
        } else {
          this.showNotice('Error', `Failed to delete vocabulary: ${message}`, true);
        }
      });
    });
  }

  /** Single-button ConfirmDialog used as an alert. */
  private showNotice(title: string, message: string, isDanger: boolean): void {
    this.dialog.open(ConfirmDialogComponent, {
      width: '400px',
      data: { title, message, confirmText: 'OK', cancelText: null, isDanger }
    });
  }
}

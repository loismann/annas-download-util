/**
 * Characterization tests for BookReaderComponent.
 *
 * These pin what the reader does *today*, against a real component fixture.
 * They are a safety net for the ongoing split (see DOCS/REFACTORING_TODO.md), not a
 * statement of what the behaviour ought to be — where current behaviour looks
 * questionable it is pinned anyway and called out in a comment.
 *
 * If one of these fails after a refactor, the refactor changed behaviour. The
 * default response is to fix the refactor, not the test.
 *
 * This file replaced `book-reader.component.spec.ts`, which never instantiated the
 * component: every block there tested a local re-implementation of component logic
 * and would have passed if the real version were deleted. Add new coverage here,
 * against the fixture — never as a local helper.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { BookReaderComponent } from './book-reader.component';
import { AiApiService } from '../services/ai-api.service';
import { LibraryApiService } from '../services/library-api.service';
import { ReaderPaginationService } from './services';
import { VocabularyService } from '../services/vocabulary.service';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';
import { DropboxChapterContent, DropboxEpubChapter } from '../models/dropbox-epub.model';

describe('BookReaderComponent (characterization)', () => {
  let component: BookReaderComponent;
  let fixture: ComponentFixture<BookReaderComponent>;
  let vocabulary: jasmine.SpyObj<VocabularyService>;

  /** Chapter word counts are deliberately uneven — real EPUBs never divide cleanly. */
  const chapters: DropboxEpubChapter[] = [
    { id: 10, title: 'Chapter 1: Opening', level: 1, wordCount: 100 },
    { id: 20, title: 'Chapter 2: Middle', level: 1, wordCount: 250 },
    { id: 30, title: 'Epilogue', level: 1, wordCount: 60, displayLabel: 'The End' }
  ];

  /** 100 words, "w1".."w100", so an offset maps to a predictable slice. */
  const words = Array.from({ length: 100 }, (_, i) => `w${i + 1}`);
  const chapterContent: DropboxChapterContent = {
    id: 20,
    title: 'Chapter 2: Middle',
    content: words.join(' '),
    characterCount: words.join(' ').length,
    wordCount: 100
  };

  beforeEach(async () => {
    localStorage.clear();

    const aiApi = jasmine.createSpyObj<AiApiService>('AiApiService', [
      'getTokenUsage', 'getAllUsersTokenUsage', 'saveSectionVocab', 'getCachedSectionSummary'
    ]);
    // The real endpoint 404s when nothing is cached, which is the default state
    // for a chapter nobody has summarised — so that is what the stub models.
    aiApi.getCachedSectionSummary.and.returnValue(throwError(() => ({ status: 404 })));
    aiApi.getTokenUsage.and.returnValue(of(null as any));
    aiApi.getAllUsersTokenUsage.and.returnValue(of([]));
    aiApi.saveSectionVocab.and.returnValue(of({} as any));

    const libraryApi = jasmine.createSpyObj<LibraryApiService>('LibraryApiService', [
      'getLibraryReaderBooks', 'getLibraryReaderChapters'
    ]);
    libraryApi.getLibraryReaderBooks.and.returnValue(of([]));
    libraryApi.getLibraryReaderChapters.and.returnValue(of({ title: '', chapters: [] } as any));

    vocabulary = jasmine.createSpyObj<VocabularyService>(
      'VocabularyService',
      ['getBookFilters', 'normalizeForMatch', 'isKnown', 'markAsKnown', 'markAsUnknown'],
      { knownWords$: of(new Set<string>()), studyWords$: of(new Map<string, string>()) }
    );
    vocabulary.getBookFilters.and.returnValue([{ id: 'all', name: 'All books' }]);
    // Match the real service closely enough for the parser: casefold + trim.
    vocabulary.normalizeForMatch.and.callFake((t: string) => (t ?? '').trim().toLowerCase());
    vocabulary.isKnown.and.returnValue(false);

    await TestBed.configureTestingModule({
      imports: [BookReaderComponent, NoopAnimationsModule],
      providers: [
        { provide: AiApiService, useValue: aiApi },
        { provide: LibraryApiService, useValue: libraryApi },
        { provide: VocabularyService, useValue: vocabulary },
        { provide: AuthService, useValue: jasmine.createSpyObj('AuthService', ['getToken']) },
        {
          provide: LoggerService,
          useValue: jasmine.createSpyObj('LoggerService', ['log', 'error', 'warn'])
        },
        { provide: MatDialog, useValue: jasmine.createSpyObj('MatDialog', ['open']) },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        { provide: ActivatedRoute, useValue: { queryParamMap: of(new Map() as any) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(BookReaderComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    fixture.destroy();
    localStorage.clear();
  });

  /** Puts the reader mid-book without going through the loading pipeline. */
  function seatInChapter(offset = 0, pageSize = 25): void {
    component.chapters = chapters;
    component.selectedBookPath = 'book-key';
    component.selectedChapterId = 20;
    component.chapterContent = chapterContent;
    component.pageSizeWords = pageSize;
    component.wordOffset = offset;
  }

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  // The three reading themes live in a second component stylesheet
  // (book-reader-themes.component.scss) so that neither file exceeds the 32 kB
  // per-component-style budget. Nothing in the build enforces that it is still
  // listed in `styleUrls`: drop it, or revert `styleUrls` to `styleUrl`, and the
  // reader simply renders unthemed — no error, no warning. This is the guard.
  describe('the reading themes stylesheet', () => {
    /** The reading pane only exists once a chapter is on screen. */
    function paneUnderTheme(theme: 'light' | 'sepia' | 'dark'): HTMLElement {
      seatInChapter();
      component.appearance.setTheme(theme);
      fixture.detectChanges();

      return fixture.nativeElement.querySelector('.text-window') as HTMLElement;
    }

    it('should style the reading panes for the current theme', () => {
      // --slate-900, which the dark theme paints the reading surface with.
      expect(getComputedStyle(paneUnderTheme('dark')).backgroundColor).toBe('rgb(15, 23, 42)');
    });

    it('should repaint when the theme changes', () => {
      paneUnderTheme('dark');

      component.appearance.setTheme('sepia');
      fixture.detectChanges();

      const pane = fixture.nativeElement.querySelector('.text-window') as HTMLElement;

      expect(getComputedStyle(pane).backgroundColor).toBe('rgb(244, 233, 215)');
    });
  });

  // Everything rendered in the analysis pane goes through [innerHTML], and all
  // of it is model output derived from an EPUB — a file downloaded from Anna's
  // Archive or LibGen, i.e. arbitrary third-party content. The page holds the
  // signed-in user's token, and the CSP is `script-src 'self' 'unsafe-inline'`
  // with `img-src https:`, so an injected handler both runs and can beacon out.
  describe('untrusted HTML from the model', () => {
    const attacks = [
      '<img src="x" onerror="alert(1)">',
      '<script>alert(1)</script>',
      '<a href="javascript:alert(1)">click</a>',
      '<iframe src="https://evil.example"></iframe>',
      '<svg onload="alert(1)"></svg>',
      '<div onmouseover="alert(1)">hover</div>'
    ];

    function rendered(value: unknown): string {
      return String(value ?? '');
    }

    attacks.forEach(attack => {
      it(`should strip the handler from a chapter summary containing ${attack.slice(0, 24)}…`, () => {
        component.fullChapterSummary = `Real summary text.\n\n${attack}`;

        const html = rendered(component.fullChapterSummaryHtml);

        expect(html).not.toContain('onerror');
        expect(html).not.toContain('onload');
        expect(html).not.toContain('onmouseover');
        expect(html).not.toContain('<script');
        expect(html).not.toContain('<iframe');
        // Angular neutralises a javascript: URL by rewriting the scheme to
        // `unsafe:`, which no browser will navigate to. So the property to
        // assert is that no *live* one survives, not that the substring is
        // absent — the inert form still contains it.
        expect(html).not.toMatch(/(href|src)\s*=\s*["']javascript:/i);
      });

      it(`should strip the handler from a dummy summary containing ${attack.slice(0, 24)}…`, () => {
        component.ultraChapterSummary = `Real summary text.\n\n${attack}`;

        const html = rendered(component.ultraChapterSummaryHtml);

        expect(html).not.toContain('onerror');
        expect(html).not.toContain('onload');
        expect(html).not.toContain('onmouseover');
        expect(html).not.toContain('<script');
        expect(html).not.toContain('<iframe');
        // Angular neutralises a javascript: URL by rewriting the scheme to
        // `unsafe:`, which no browser will navigate to. So the property to
        // assert is that no *live* one survives, not that the substring is
        // absent — the inert form still contains it.
        expect(html).not.toMatch(/(href|src)\s*=\s*["']javascript:/i);
      });
    });

    it('should still render the markdown the summary is actually made of', () => {
      component.fullChapterSummary = '# Heading\n\nSome **bold** text and a [link](https://example.com).';

      const html = rendered(component.fullChapterSummaryHtml);

      expect(html).toContain('<h1');
      expect(html).toContain('<strong>bold</strong>');
      expect(html).toContain('href="https://example.com"');
    });

    it('should render nothing when there is no summary', () => {
      component.fullChapterSummary = null;
      component.ultraChapterSummary = null;

      expect(component.fullChapterSummaryHtml).toBeNull();
      expect(component.ultraChapterSummaryHtml).toBeNull();
    });

    it('should strip a handler from a section summary too', () => {
      const html = rendered(
        component.formatSectionSummaryAsHtml('Text.\n\n<img src="x" onerror="alert(1)">')
      );

      expect(html).not.toContain('onerror');
    });
  });

  describe('visible text windowing', () => {
    it('should return nothing before a chapter is loaded', () => {
      expect(component.visibleText).toBe('');
    });

    // The slice runs from the first word's index to the *start* index of the
    // word after the window, so it carries the separating whitespace with it.
    // Harmless in the reader (the text is rendered in a pre-wrap block), but it
    // means a page is not simply `words.join(' ')` — pinned so a refactor that
    // "tidies" it has to be a deliberate choice.
    it('should slice a page of words at the current offset', () => {
      seatInChapter(0, 10);
      expect(component.visibleText).toBe('w1 w2 w3 w4 w5 w6 w7 w8 w9 w10 ');
    });

    it('should slice from the middle of the chapter', () => {
      seatInChapter(20, 5);
      expect(component.visibleText).toBe('w21 w22 w23 w24 w25 ');
    });

    it('should return a short final page rather than padding it', () => {
      // No trailing space here: the final page runs to end-of-text, so there is
      // no following word to slice up to. The asymmetry is intentional to pin.
      seatInChapter(95, 10);
      expect(component.visibleText).toBe('w96 w97 w98 w99 w100');
    });
  });

  describe('page counting', () => {
    it('should report a single page before a chapter is loaded', () => {
      expect(component.currentPage).toBe(1);
      expect(component.totalPages).toBe(1);
    });

    it('should round the last partial page up', () => {
      seatInChapter(0, 30);
      expect(component.totalPages).toBe(4); // 100 words / 30
    });

    it('should be 1-indexed', () => {
      seatInChapter(0, 25);
      expect(component.currentPage).toBe(1);
      component.wordOffset = 25;
      expect(component.currentPage).toBe(2);
    });

    it('should guard against a zero page size', () => {
      seatInChapter(0, 0);
      expect(component.totalPages).toBe(1);
      expect(component.currentPage).toBe(1);
    });
  });

  describe('paging within a chapter', () => {
    it('should advance by one page', () => {
      seatInChapter(0, 25);
      component.pageForward();
      expect(component.wordOffset).toBe(25);
    });

    it('should go back by one page', () => {
      seatInChapter(50, 25);
      component.pageBack();
      expect(component.wordOffset).toBe(25);
    });

    it('should stop at the start of the first chapter', () => {
      component.chapters = chapters;
      component.selectedChapterId = 10; // first chapter, nothing before it
      component.chapterContent = chapterContent;
      component.pageSizeWords = 25;
      component.wordOffset = 0;

      component.pageBack();

      expect(component.wordOffset).toBe(0);
      expect(component.canPageBack()).toBe(false);
    });

    it('should clamp forward paging to the last page start', () => {
      seatInChapter(75, 25); // already the last page of 4
      component.selectedChapterId = 30; // last chapter, nothing after it
      component.pageForward();
      expect(component.wordOffset).toBe(75);
    });

    it('should not page at all with no chapter loaded', () => {
      component.pageForward();
      expect(component.wordOffset).toBe(0);
      expect(component.canPageForward()).toBe(false);
      expect(component.canPageBack()).toBe(false);
    });
  });

  describe('paging across chapter boundaries', () => {
    it('should allow paging back from the first page of a middle chapter', () => {
      seatInChapter(0, 25); // chapter 20, which has chapter 10 before it
      expect(component.canPageBack()).toBe(true);
    });

    it('should allow paging forward from the last page of a middle chapter', () => {
      seatInChapter(75, 25);
      expect(component.canPageForward()).toBe(true);
    });

    it('should not offer forward paging past the final chapter', () => {
      seatInChapter(75, 25);
      component.selectedChapterId = 30;
      expect(component.canPageForward()).toBe(false);
    });
  });

  describe('whole-book progress', () => {
    it('should sum word counts across all chapters', () => {
      seatInChapter(0, 25);
      expect(component.totalBookWords).toBe(410); // 100 + 250 + 60
    });

    it('should offset by the words in preceding chapters', () => {
      seatInChapter(30, 25); // 100 words of chapter 1 come first
      expect(component.currentBookWordOffset).toBe(130);
    });

    it('should report 0% progress with no chapters', () => {
      expect(component.bookProgressPercent).toBe(0);
    });

    it('should clamp progress to 100%', () => {
      seatInChapter(0, 25);
      component.selectedChapterId = null;
      component.wordOffset = 99999;
      expect(component.bookProgressPercent).toBe(100);
    });
  });

  describe('chapter labels', () => {
    it('should return null when no chapter is selected', () => {
      component.chapters = chapters;
      component.selectedChapterId = null;
      expect(component.currentChapterLabel).toBeNull();
    });

    it('should prefer displayLabel over title', () => {
      component.chapters = chapters;
      component.selectedChapterId = 30;
      expect(component.currentChapterLabel).toBe('The End');
    });

    it('should fall back to the title', () => {
      component.chapters = chapters;
      component.selectedChapterId = 10;
      expect(component.currentChapterLabel).toBe('Chapter 1: Opening');
    });

    it('should truncate a long label for the header', () => {
      component.chapters = [
        { id: 1, title: 'A Very Long Chapter Title Indeed', level: 1, wordCount: 1 }
      ];
      component.selectedChapterId = 1;
      expect(component.truncatedChapterLabel).toBe('A Very Long Chapter ...');
    });
  });

  describe('bookmarks', () => {
    const bookmark = (chapterId: number, wordOffset: number, readerKey = 'book-key') => ({
      id: `${chapterId}-${wordOffset}`,
      readerKey,
      chapterId,
      wordOffset,
      createdAt: new Date().toISOString()
    });

    it('should only show bookmarks for the open book', () => {
      seatInChapter(0, 25);
      component.bookmarks = [bookmark(20, 0), bookmark(20, 50, 'another-book')];
      expect(component.visibleBookmarks.length).toBe(1);
    });

    it('should sort by chapter then offset', () => {
      seatInChapter(0, 25);
      component.bookmarks = [bookmark(30, 10), bookmark(20, 50), bookmark(20, 0)];
      expect(component.visibleBookmarks.map(b => b.id)).toEqual(['20-0', '20-50', '30-10']);
    });

    it('should treat a bookmark anywhere on the current page as the current page', () => {
      seatInChapter(25, 25);
      component.bookmarks = [bookmark(20, 30)]; // same page: offsets 25-49
      expect(component.isCurrentPageBookmarked).toBe(true);
    });

    it('should not match a bookmark on an adjacent page', () => {
      seatInChapter(25, 25);
      component.bookmarks = [bookmark(20, 50)];
      expect(component.isCurrentPageBookmarked).toBe(false);
    });
  });

  describe('search match counting', () => {
    it('should count case-insensitively', () => {
      seatInChapter(0, 25);
      component.chapterContent = { ...chapterContent, content: 'Cat cat CAT dog' };
      component.searchTerm = 'cat';
      expect(component.searchMatchCount).toBe(3);
    });

    it('should treat regex metacharacters literally', () => {
      seatInChapter(0, 25);
      component.chapterContent = { ...chapterContent, content: 'a.b axb a.b' };
      component.searchTerm = 'a.b';
      expect(component.searchMatchCount).toBe(2); // not 3 — the '.' is escaped
    });

    it('should count nothing for a blank term', () => {
      seatInChapter(0, 25);
      component.searchTerm = '   ';
      expect(component.searchMatchCount).toBe(0);
    });
  });

  // The vocabulary/flashcard/Learn More block is what Phase 2 extracts into a
  // child component, so this is the part that most needs pinning first.
  describe('summary and vocabulary parsing', () => {
    it('should split the prose from the definitions list', () => {
      const summary = 'The hero departs.\n\nDefinitions:\n- **liminal**: on a threshold';

      component['parseSummaryOnce'](summary);

      expect(component.analysisText).toBe('The hero departs.');
      expect(component.vocabularyWords).toEqual([
        { term: 'liminal', definition: 'on a threshold' }
      ]);
    });

    it('should treat the whole summary as prose when there is no definitions section', () => {
      component['parseSummaryOnce']('Just prose, no list.');

      expect(component.analysisText).toBe('Just prose, no list.');
      expect(component.vocabularyWords).toEqual([]);
    });

    it('should accept singular "Definition:" and any casing', () => {
      component['parseSummaryOnce']('Prose.\nDEFINITION: \n- term: meaning');
      expect(component.vocabularyWords.length).toBe(1);
    });

    it('should parse bullets, numbers and bold markers alike', () => {
      const parsed = component['parseVocabulary'](
        ['- **alpha**: first', '2) beta: second', '* gamma: third', '**delta**: fourth'].join('\n')
      );

      expect(parsed.map(w => w.term)).toEqual(['alpha', 'beta', 'gamma', 'delta']);
      expect(parsed[1].definition).toBe('second');
    });

    // Regression: the bullet stripper used to eat one asterisk of the '**' pair
    // when the model omitted the bullet, yielding the term '*delta'.
    it('should not mistake a bold marker for a bullet', () => {
      const parsed = component['parseVocabulary']('**delta**: fourth');
      expect(parsed.map(w => w.term)).toEqual(['delta']);
    });

    it('should still strip a lone asterisk used as a bullet', () => {
      const parsed = component['parseVocabulary']('* gamma: third');
      expect(parsed.map(w => w.term)).toEqual(['gamma']);
    });

    it('should handle a bullet followed by a bold term', () => {
      const parsed = component['parseVocabulary']('* **epsilon**: fifth');
      expect(parsed.map(w => w.term)).toEqual(['epsilon']);
    });

    it('should skip blank lines and lines with no definition', () => {
      const parsed = component['parseVocabulary']('\n- justaterm\n\n- real: meaning\n');
      expect(parsed.map(w => w.term)).toEqual(['real']);
    });

    it('should drop duplicates that normalize to the same term', () => {
      const parsed = component['parseVocabulary']('- Alpha: first\n- alpha: again');
      expect(parsed.length).toBe(1);
    });

    it('should drop terms the reader already knows', () => {
      vocabulary.isKnown.and.callFake((t: string) => t === 'known');
      const parsed = component['parseVocabulary']('- known: skip me\n- fresh: keep me');
      expect(parsed.map(w => w.term)).toEqual(['fresh']);
    });

    it('should keep only the text before the definitions marker', () => {
      expect(component.getSummaryWithoutDefinitions('Prose here.\nDefinitions:\n- a: b'))
        .toBe('Prose here.');
    });

    it('should return the whole summary when there is no marker', () => {
      expect(component.getSummaryWithoutDefinitions('  Prose only.  ')).toBe('Prose only.');
    });

    it('should split on the first colon, so a definition may contain colons', () => {
      const parsed = component['parseVocabulary']('- ratio: a:b comparison');
      expect(parsed[0].term).toBe('ratio');
      expect(parsed[0].definition).toBe('a:b comparison');
    });
  });

  describe('removing a vocabulary word', () => {
    it('should drop it from the in-memory list', () => {
      component.vocabularyWords = [
        { term: 'alpha', definition: 'first' },
        { term: 'beta', definition: 'second' }
      ];

      component.removeVocabularyWord('alpha');

      expect(component.vocabularyWords.map(w => w.term)).toEqual(['beta']);
    });

    it('should not persist anything when no section is open', () => {
      const aiApi = TestBed.inject(AiApiService) as jasmine.SpyObj<AiApiService>;
      component.vocabularyWords = [{ term: 'alpha', definition: 'first' }];
      component.currentSectionIndex = null;

      component.removeVocabularyWord('alpha');

      expect(aiApi.saveSectionVocab).not.toHaveBeenCalled();
    });

    it('should persist the remaining words for the open section', () => {
      const aiApi = TestBed.inject(AiApiService) as jasmine.SpyObj<AiApiService>;
      seatInChapter(0, 25);
      component.currentSectionIndex = 2;
      component.vocabularyWords = [
        { term: 'alpha', definition: 'first' },
        { term: 'beta', definition: 'second' }
      ];

      component.removeVocabularyWord('alpha');

      expect(aiApi.saveSectionVocab).toHaveBeenCalledWith(
        'book-key',
        20,
        2,
        [{ term: 'beta', definition: 'second', etymology: '', usageExamples: [], notes: '' }]
      );
    });

    it('should mark a word known and remove it in one step', () => {
      component.vocabularyWords = [{ term: 'alpha', definition: 'first' }];
      component.selectedBookPath = 'book-key';

      component.markWordAsKnown({ term: 'alpha', definition: 'first' });

      expect(vocabulary.markAsKnown).toHaveBeenCalledWith('alpha', 'book-key', 'first');
      expect(component.vocabularyWords).toEqual([]);
    });
  });

  // Was simulated in book-reader.component.spec.ts as
  // "Chapter Summary Number Calculation" — a local helper that re-implemented the
  // index maths. The behaviour that actually matters is upstream of it: short
  // front matter is dropped on load, which is what makes chapter *positions*
  // differ from EPUB ids, and what the summary's display number counts.
  // The server sends failures as a fully-formed progress event — stage 'error',
  // stepNumber 0 — not as a separate error shape. Dispatching on "has a stage
  // and a stepNumber" therefore matched failures too, and the spinner never
  // stopped.
  describe('section detection failing', () => {
    function receive(event: any): void {
      component['handleChunkBoundarySSEEvent'](event, 20);
    }

    beforeEach(() => {
      component.loadingChunkBoundaries = true;
      // Boundaries are only applied to the chapter they were requested for.
      component.selectedChapterId = 20;
    });

    it('should stop loading when the server reports an error', () => {
      receive({ stage: 'error', stepNumber: 0, totalSteps: 1, message: 'Failed to index book: disk full' });

      expect(component.loadingChunkBoundaries).toBe(false);
    });

    it('should show the reason the server gave', () => {
      receive({ stage: 'error', stepNumber: 0, totalSteps: 1, message: 'Chapter file not found after indexing' });

      expect(component.chunkBoundariesProgress?.stage).toBe('error');
      expect(component.chunkBoundariesProgress?.message).toBe('Chapter file not found after indexing');
    });

    it('should keep loading while real progress arrives', () => {
      receive({ stage: 'detecting', stepNumber: 0, totalSteps: 1, message: 'Finding section breaks...' });

      expect(component.loadingChunkBoundaries).toBe(true);
      expect(component.chunkBoundariesProgress?.stage).toBe('detecting');
    });

    it('should stop loading when the boundaries arrive', () => {
      receive({ chapterId: 1, chunks: [{ start: 0, end: 100, wordCount: 100 }], cachedAt: '2026-08-08T00:00:00Z' });

      expect(component.loadingChunkBoundaries).toBe(false);
    });
  });

  describe('chapter loading', () => {
    function loadChaptersWith(chapters: any[]): void {
      const libraryApi = TestBed.inject(LibraryApiService) as jasmine.SpyObj<LibraryApiService>;
      libraryApi.getLibraryReaderChapters.and.returnValue(
        of({ title: 'Book', chapters } as any)
      );
      component['loadChapters']('book.epub');
    }

    it('should drop front matter shorter than 50 words', () => {
      loadChaptersWith([
        { id: 0, title: 'Preface', level: 0, wordCount: 30 },
        { id: 1, title: 'Introduction', level: 0, wordCount: 45 },
        { id: 2, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 3, title: 'Chapter 2', level: 0, wordCount: 3000 }
      ]);

      expect(component.chapters.map(c => c.id)).toEqual([2, 3]);
    });

    it('should keep a chapter of exactly 50 words', () => {
      loadChaptersWith([{ id: 1, title: 'Short', level: 0, wordCount: 50 }]);
      expect(component.chapters.length).toBe(1);
    });

    it('should number chapters by position, not by EPUB id', () => {
      // This is what the summary's displayChapterNumber counts: the first real
      // chapter is "1" even though its EPUB id is 2.
      loadChaptersWith([
        { id: 0, title: 'Preface', level: 0, wordCount: 30 },
        { id: 2, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 3, title: 'Chapter 2', level: 0, wordCount: 3000 }
      ]);

      expect(component.chapters.findIndex(c => c.id === 2) + 1).toBe(1);
      expect(component.chapters.findIndex(c => c.id === 3) + 1).toBe(2);
    });

    it('should default displayLabel to the title', () => {
      loadChaptersWith([{ id: 1, title: 'Chapter 1', level: 0, wordCount: 100 }]);
      expect(component.chapters[0].displayLabel).toBe('Chapter 1');
    });

    it('should keep an explicit displayLabel', () => {
      loadChaptersWith([
        { id: 1, title: 'ch01.xhtml', level: 0, wordCount: 100, displayLabel: 'Chapter One' }
      ]);
      expect(component.chapters[0].displayLabel).toBe('Chapter One');
    });

    it('should report an error when every chapter is filtered out', () => {
      loadChaptersWith([{ id: 0, title: 'Preface', level: 0, wordCount: 10 }]);
      expect(component.chapters).toEqual([]);
      expect(component.error).toBe('No chapters found in this EPUB.');
    });
  });

  // Page size changes underneath the reader on rotate/resize/font change. Keeping
  // the reader's place across that is the behaviour most likely to be noticed if
  // it breaks, and it cannot be checked in a headless browser through real layout
  // — so the measurement is stubbed and only the offset maths is pinned.
  describe('preserving position when the page size changes', () => {
    let pagination: jasmine.SpyObj<ReaderPaginationService>;

    beforeEach(() => {
      pagination = TestBed.inject(ReaderPaginationService) as any;
      spyOn(pagination, 'calculatePageSize');
      // recalcPageSize reads the live element for its dimension guard.
      component.textWindowRef = {
        nativeElement: { clientHeight: 400, clientWidth: 600 }
      } as any;
    });

    function recalc(newSize: number, cached = false): void {
      (pagination.calculatePageSize as jasmine.Spy).and.returnValue({
        pageSize: newSize,
        cacheKey: 'k',
        cached
      });
      component['recalcPageSize']();
    }

    it('should keep the reader on the same page when the page shrinks', () => {
      seatInChapter(50, 25); // page 3 of 4
      recalc(10);
      // Page index 2 is preserved, re-expressed in the new page size.
      expect(component.pageSizeWords).toBe(10);
      expect(component.wordOffset).toBe(20);
    });

    it('should keep the reader on the same page when the page grows', () => {
      seatInChapter(20, 10); // page 3
      recalc(25);
      expect(component.wordOffset).toBe(50);
    });

    it('should not move the offset when the size is unchanged', () => {
      seatInChapter(50, 25);
      recalc(25);
      expect(component.wordOffset).toBe(50);
    });

    it('should pull the offset back when it lands past the end', () => {
      seatInChapter(90, 10); // page 10 of 10
      recalc(50); // now only 2 pages
      expect(component.wordOffset).toBe(50);
    });

    it('should do nothing but clamp on a cache hit', () => {
      seatInChapter(50, 25);
      recalc(10, true);
      // Page size is left alone, so the offset is still valid and untouched.
      expect(component.pageSizeWords).toBe(25);
      expect(component.wordOffset).toBe(50);
    });

    it('should not run before a chapter is loaded', () => {
      component.chapterContent = null;
      component['recalcPageSize']();
      expect(pagination.calculatePageSize).not.toHaveBeenCalled();
    });

    it('should not run while the pane has no size', () => {
      seatInChapter(0, 25);
      component.textWindowRef = { nativeElement: { clientHeight: 0, clientWidth: 0 } } as any;
      component['recalcPageSize']();
      expect(pagination.calculatePageSize).not.toHaveBeenCalled();
    });
  });

  // Every one of these fetches names the chapter it was started for, and the
  // reader lets you switch chapters while one is in flight. A response that
  // lands after the switch describes a chapter that is no longer open.
  describe('responses that arrive after the reader has moved on', () => {
    it('should ignore section boundaries for a chapter that is no longer open', () => {
      component.selectedBookPath = 'book-key';
      component.selectedChapterId = 20;

      // Boundaries for chapter 10 arrive after the reader moved to 20.
      component['applyChunkBoundaries'](
        { chapterId: 10, chunks: [{ start: 0, end: 50, wordCount: 50 }], cachedAt: '' } as any,
        10
      );

      expect(component.chunkBoundaries).toBeNull();
    });

    it('should apply section boundaries for the chapter still open', () => {
      component.selectedBookPath = 'book-key';
      component.selectedChapterId = 20;

      component['applyChunkBoundaries'](
        { chapterId: 20, chunks: [{ start: 0, end: 50, wordCount: 50 }], cachedAt: '' } as any,
        20
      );

      expect(component.chunkBoundaries?.chunks.length).toBe(1);
    });

    it('should not clear the spinner for a chapter that is no longer loading', () => {
      // The failure path had no staleness guard where the success path did, so
      // a slow failure for the previous chapter reported itself against the one
      // now on screen.
      component.selectedBookFileName = 'book.epub';
      component.selectedChapterId = 20;
      component.loadingContent = true;

      component['reportChapterContentFailure']('book.epub', 10, new Error('boom'));

      expect(component.error).toBeNull();
      expect(component.loadingContent).toBe(true);
    });

    it('should report a failure for the chapter that is actually open', () => {
      component.selectedBookFileName = 'book.epub';
      component.selectedChapterId = 20;
      component.loadingContent = true;

      component['reportChapterContentFailure']('book.epub', 20, new Error('boom'));

      expect(component.error).toBe('Unable to load chapter content.');
      expect(component.loadingContent).toBe(false);
    });
  });

});

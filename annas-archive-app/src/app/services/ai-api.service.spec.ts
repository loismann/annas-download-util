import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { AiApiService, AiBookSearchResult, SuggestAuthorsResponse, RelatedBooksResponse } from './ai-api.service';
import { LoggerService } from './logger.service';
import {
  SummarizeResponse,
  FullChapterSummaryResponse,
  TokenUsageResponse,
  FlashcardItem,
  CharacterGraphResponse,
  ChunkBoundariesResponse,
  SectionSummaryResponse,
  UserTokenUsage,
  LearnMoreResponse,
  WikiImagesResponse
} from '../models/dropbox-epub.model';

describe('AiApiService', () => {
  let service: AiApiService;
  let httpMock: HttpTestingController;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'debug', 'error', 'warn']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        AiApiService,
        { provide: LoggerService, useValue: mockLogger }
      ]
    });

    service = TestBed.inject(AiApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  // ─── Summarization Tests ────────────────────────────────────────────────────

  describe('summarizeText', () => {
    it('should summarize text', () => {
      const mockResponse: SummarizeResponse = {
        summary: 'This is a summary of the text.'
      };

      service.summarizeText({ text: 'Long text to summarize...' }).subscribe(response => {
        expect(response.summary).toBe('This is a summary of the text.');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/summarize'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  describe('getFullChapterSummary', () => {
    it('should fetch cached chapter summary', () => {
      const mockResponse: FullChapterSummaryResponse = {
        summary: 'Chapter summary here',
        promptTokens: 100,
        completionTokens: 50,
        totalTokens: 150,
        cachedAt: '2026-01-14T00:00:00Z',
        steps: []
      };

      service.getFullChapterSummary('/Books/test.epub', 1).subscribe(response => {
        expect(response.summary).toBe('Chapter summary here');
        expect(response.cachedAt).toBeTruthy();
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/summarize/chapter'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('dropboxPath')).toBe('/Books/test.epub');
      expect(req.request.params.get('chapterId')).toBe('1');
      req.flush(mockResponse);
    });
  });

  // ─── Token Usage Tests ──────────────────────────────────────────────────────

  describe('getTokenUsage', () => {
    it('should fetch token usage', () => {
      const mockResponse: TokenUsageResponse = {
        promptTokens: 5000,
        completionTokens: 2000,
        totalTokens: 7000,
        totalCostUsd: 0.15
      };

      service.getTokenUsage().subscribe(response => {
        expect(response.totalTokens).toBe(7000);
        expect(response.totalCostUsd).toBe(0.15);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/usage'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  // ─── Flashcard Tests ────────────────────────────────────────────────────────

  describe('createFlashcard', () => {
    it('should create flashcards from text', () => {
      const mockResponse = {
        cards: [
          {
            term: 'photosynthesis',
            definition: 'Process by which plants convert sunlight to energy',
            etymology: 'Greek: photo (light) + synthesis (putting together)',
            usageExamples: ['Plants use photosynthesis to create food.']
          }
        ]
      };

      service.createFlashcard({ term: 'photosynthesis' }).subscribe(cards => {
        expect(cards.length).toBe(1);
        expect(cards[0].term).toBe('photosynthesis');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/flashcards'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  describe('getFlashcards', () => {
    it('should fetch flashcards for a book', () => {
      const mockCards: FlashcardItem[] = [
        { term: 'protagonist', definition: 'Main character', etymology: 'Greek', usageExamples: [] },
        { term: 'antagonist', definition: 'Character opposing the protagonist', etymology: 'Greek', usageExamples: [] }
      ];

      service.getFlashcards('/Books/test.epub').subscribe(cards => {
        expect(cards.length).toBe(2);
        expect(cards[0].term).toBe('protagonist');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/flashcards'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('path')).toBe('/Books/test.epub');
      req.flush(mockCards);
    });
  });

  describe('clearFlashcards', () => {
    it('should clear all flashcards for a book', () => {
      service.clearFlashcards('/Books/test.epub').subscribe(response => {
        expect(response.cleared).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/flashcards'));
      expect(req.request.method).toBe('DELETE');
      expect(req.request.params.get('path')).toBe('/Books/test.epub');
      req.flush({ cleared: true });
    });
  });

  // ─── Vocabulary Tests ───────────────────────────────────────────────────────

  describe('getKnownWords', () => {
    it('should fetch known words', () => {
      const mockResponse = {
        'word1': ['book1', 'book2'],
        'word2': ['book1']
      };

      service.getKnownWords().subscribe(response => {
        expect(response['word1'].length).toBe(2);
        expect(response['word2'].length).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/known'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  describe('addKnownWord', () => {
    it('should add a known word', () => {
      service.addKnownWord('vocabulary', 'book123').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/known'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ term: 'vocabulary', bookId: 'book123' });
      req.flush(null);
    });
  });

  describe('removeKnownWord', () => {
    it('should remove a known word', () => {
      service.removeKnownWord('vocabulary').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/known/vocabulary'));
      expect(req.request.method).toBe('DELETE');
      req.flush(null);
    });
  });

  // ─── Character Graph Tests ──────────────────────────────────────────────────

  describe('getCharacterGraph', () => {
    it('should fetch character graph', () => {
      const mockResponse: CharacterGraphResponse = {
        nodes: [
          { id: 'john', label: 'John', description: 'Main character' }
        ],
        edges: [],
        summaryCount: 5,
        cachedAt: '2026-01-14T00:00:00Z'
      };

      service.getCharacterGraph('/Books/test.epub').subscribe(response => {
        expect(response.nodes.length).toBe(1);
        expect(response.nodes[0].label).toBe('John');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/characters/graph'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('dropboxPath')).toBe('/Books/test.epub');
      req.flush(mockResponse);
    });
  });

  // ─── Book Search & Recommendations Tests ────────────────────────────────────

  describe('aiBookSearch', () => {
    it('should perform AI book search', () => {
      const mockResponse: AiBookSearchResult = {
        summary: 'Books about machine learning',
        books: [
          { title: 'Deep Learning', author: 'Ian Goodfellow', summary: 'Comprehensive guide', importance: 'Essential' }
        ]
      };

      service.aiBookSearch('machine learning books').subscribe(response => {
        expect(response.books.length).toBe(1);
        expect(response.books[0].title).toBe('Deep Learning');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/book-search'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ query: 'machine learning books' });
      req.flush(mockResponse);
    });
  });

  describe('suggestAuthors', () => {
    it('should suggest authors for a book title', () => {
      const mockResponse: SuggestAuthorsResponse = {
        authors: [
          { author: 'Brandon Sanderson', confidence: 'high' },
          { author: 'Patrick Rothfuss', confidence: 'medium' }
        ]
      };

      service.suggestAuthors('The Way of Kings').subscribe(response => {
        expect(response.authors.length).toBe(2);
        expect(response.authors[0].author).toBe('Brandon Sanderson');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/suggest-authors'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ bookTitle: 'The Way of Kings' });
      req.flush(mockResponse);
    });
  });

  describe('getRelatedBooks', () => {
    it('should fetch related books', () => {
      const mockResponse: RelatedBooksResponse = {
        sameSeries: [
          { title: 'Words of Radiance', order: 2, description: 'Second book' }
        ],
        otherSeries: [],
        seriesSummary: 'The Stormlight Archive series'
      };

      service.getRelatedBooks('The Way of Kings', 'Brandon Sanderson').subscribe(response => {
        expect(response.sameSeries.length).toBe(1);
        expect(response.seriesSummary).toContain('Stormlight');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/related-books'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ bookTitle: 'The Way of Kings', author: 'Brandon Sanderson' });
      req.flush(mockResponse);
    });
  });

  describe('matchSeriesBooks', () => {
    it('should match series books', () => {
      const mockResponse = {
        matches: [
          { bookTitle: 'Book 1', order: 1, status: 'matched', confidence: 'high', reason: 'Exact match' }
        ]
      };

      service.matchSeriesBooks({
        author: 'Test Author',
        books: []
      }).subscribe(response => {
        expect(response.matches.length).toBe(1);
        expect(response.matches[0].status).toBe('matched');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/match-series-books'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  // ─── Additional Summarization Tests ────────────────────────────────────────

  describe('summarizeFullChapter', () => {
    it('should generate full chapter summary', () => {
      const mockResponse: FullChapterSummaryResponse = {
        summary: 'Generated chapter summary',
        promptTokens: 200,
        completionTokens: 100,
        totalTokens: 300,
        cachedAt: '2026-01-15T00:00:00Z',
        steps: []
      };

      service.summarizeFullChapter({
        dropboxPath: '/Books/test.epub',
        chapterId: 1,
        bookTitle: 'Test Book'
      }).subscribe(response => {
        expect(response.summary).toBe('Generated chapter summary');
        expect(response.totalTokens).toBe(300);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/summarize/chapter'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  describe('getAllCachedSummaries', () => {
    it('should fetch all cached summaries for a book', () => {
      const mockResponse = {
        1: { summary: 'Chapter 1 summary' },
        2: { summary: 'Chapter 2 summary' }
      };

      service.getAllCachedSummaries('/Books/test.epub').subscribe(response => {
        expect(response[1].summary).toBe('Chapter 1 summary');
        expect(response[2].summary).toBe('Chapter 2 summary');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/summarize/book'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('dropboxPath')).toBe('/Books/test.epub');
      req.flush(mockResponse);
    });
  });

  describe('getChunkBoundaries', () => {
    it('should fetch chunk boundaries for a chapter', () => {
      const mockResponse: ChunkBoundariesResponse = {
        chapterId: 1,
        chunks: [
          { start: 0, end: 1000, wordCount: 500 },
          { start: 1000, end: 2000, wordCount: 500 },
          { start: 2000, end: 3000, wordCount: 500 }
        ],
        cachedAt: '2026-01-15T00:00:00Z'
      };

      service.getChunkBoundaries('/Books/test.epub', 1).subscribe(response => {
        expect(response.chunks.length).toBe(3);
        expect(response.chapterId).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/chunk-boundaries'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('chapterId')).toBe('1');
      req.flush(mockResponse);
    });
  });

  describe('getCachedSectionSummary', () => {
    it('should fetch cached section summary', () => {
      const mockResponse: SectionSummaryResponse = {
        summary: 'Section summary here',
        sectionIndex: 0,
        promptTokens: 50,
        completionTokens: 25,
        totalTokens: 75,
        cachedAt: '2026-01-15T00:00:00Z'
      };

      service.getCachedSectionSummary('/Books/test.epub', 1, 0).subscribe(response => {
        expect(response.summary).toBe('Section summary here');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/section-summary'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  describe('generateSectionSummary', () => {
    it('should generate section summary', () => {
      const mockResponse: SectionSummaryResponse = {
        summary: 'Generated section summary',
        sectionIndex: 0,
        promptTokens: 100,
        completionTokens: 50,
        totalTokens: 150,
        cachedAt: '2026-01-15T00:00:00Z'
      };

      service.generateSectionSummary({
        dropboxPath: '/Books/test.epub',
        chapterId: 1,
        sectionIndex: 0,
        bookTitle: 'Test Book'
      }).subscribe(response => {
        expect(response.summary).toBe('Generated section summary');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/section-summary'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  // ─── Additional Token Usage Tests ──────────────────────────────────────────

  describe('getAllUsersTokenUsage', () => {
    it('should fetch all users token usage (admin)', () => {
      const mockResponse: UserTokenUsage[] = [
        {
          userId: 'user1',
          displayName: 'User One',
          promptTokens: 1000,
          completionTokens: 500,
          totalTokens: 1500,
          totalCostUsd: 0.05,
          allowanceUsd: 10.0,
          allowanceUsedPercent: 0.5,
          resetsAtUtc: '2026-02-01T00:00:00Z',
          isOverLimit: false
        },
        {
          userId: 'user2',
          displayName: 'User Two',
          promptTokens: 2000,
          completionTokens: 1000,
          totalTokens: 3000,
          totalCostUsd: 0.10,
          allowanceUsd: 10.0,
          allowanceUsedPercent: 1.0,
          resetsAtUtc: '2026-02-01T00:00:00Z',
          isOverLimit: false
        }
      ];

      service.getAllUsersTokenUsage().subscribe(response => {
        expect(response.length).toBe(2);
        expect(response[0].userId).toBe('user1');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/usage/all-users'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  // ─── Additional Flashcard Tests ────────────────────────────────────────────

  describe('deleteFlashcard', () => {
    it('should delete a single flashcard', () => {
      service.deleteFlashcard('/Books/test.epub', 'protagonist').subscribe(response => {
        expect(response.deleted).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/flashcard'));
      expect(req.request.method).toBe('DELETE');
      expect(req.request.params.get('path')).toBe('/Books/test.epub');
      expect(req.request.params.get('term')).toBe('protagonist');
      req.flush({ deleted: true });
    });
  });

  describe('saveSectionVocab', () => {
    it('should save section vocabulary', () => {
      const vocab: FlashcardItem[] = [
        { term: 'soliloquy', definition: 'A speech in a play', etymology: '', usageExamples: [] }
      ];

      service.saveSectionVocab('/Books/test.epub', 1, 0, vocab).subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.vocabCount).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/section-vocab'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body.dropboxPath).toBe('/Books/test.epub');
      expect(req.request.body.vocab.length).toBe(1);
      req.flush({ success: true, vocabCount: 1 });
    });
  });

  // ─── Additional Vocabulary Tests ───────────────────────────────────────────

  describe('learnMore', () => {
    it('should get detailed information about a term', () => {
      const mockResponse: LearnMoreResponse = {
        detail: 'Detailed explanation of the term'
      };

      service.learnMore({ term: 'photosynthesis' }).subscribe(response => {
        expect(response.detail).toContain('explanation');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/vocab/learn-more'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body.term).toBe('photosynthesis');
      req.flush(mockResponse);
    });
  });

  describe('getWikiImages', () => {
    it('should fetch Wikipedia images for a term', () => {
      const mockResponse: WikiImagesResponse = {
        images: ['https://upload.wikimedia.org/image1.jpg', 'https://upload.wikimedia.org/image2.jpg']
      };

      service.getWikiImages('Mount Everest').subscribe(response => {
        expect(response.images.length).toBe(2);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/media/wiki-images'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('term')).toBe('Mount Everest');
      req.flush(mockResponse);
    });
  });

  describe('getStudyWords', () => {
    it('should fetch study words', () => {
      const mockResponse = {
        'word1': { definition: 'Definition 1', books: ['book1'] },
        'word2': { definition: 'Definition 2', books: ['book1', 'book2'] }
      };

      service.getStudyWords().subscribe(response => {
        expect(response['word1'].definition).toBe('Definition 1');
        expect(response['word2'].books.length).toBe(2);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/study'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  describe('addStudyWord', () => {
    it('should add a study word', () => {
      service.addStudyWord('vocabulary', 'The body of words used in a language', 'book123').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/study'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({
        term: 'vocabulary',
        definition: 'The body of words used in a language',
        bookId: 'book123'
      });
      req.flush(null);
    });

    it('should add a study word without definition', () => {
      service.addStudyWord('unknown', undefined, 'book123').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/study'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body.term).toBe('unknown');
      req.flush(null);
    });
  });

  describe('removeStudyWord', () => {
    it('should remove a study word', () => {
      service.removeStudyWord('vocabulary').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/study/vocabulary'));
      expect(req.request.method).toBe('DELETE');
      req.flush(null);
    });

    it('should encode special characters in term', () => {
      service.removeStudyWord('term with spaces').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/study/term%20with%20spaces'));
      expect(req.request.method).toBe('DELETE');
      req.flush(null);
    });
  });

  describe('deleteBookVocab', () => {
    it('should delete all vocabulary for a book', () => {
      const mockResponse = {
        success: true,
        knownWordsAffected: 10,
        studyWordsAffected: 5,
        totalRemoved: 15
      };

      service.deleteBookVocab('book123').subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.totalRemoved).toBe(15);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/vocab/book/book123'));
      expect(req.request.method).toBe('DELETE');
      req.flush(mockResponse);
    });
  });

  // ─── Additional Character Graph Tests ──────────────────────────────────────

  describe('generateCharacterGraph', () => {
    it('should generate character graph', () => {
      const mockResponse: CharacterGraphResponse = {
        nodes: [
          { id: 'frodo', label: 'Frodo', description: 'The ring bearer' },
          { id: 'gandalf', label: 'Gandalf', description: 'The wizard' }
        ],
        edges: [
          { from: 'gandalf', to: 'frodo', label: 'guides' }
        ],
        summaryCount: 10,
        cachedAt: '2026-01-15T00:00:00Z'
      };

      service.generateCharacterGraph({
        dropboxPath: '/Books/lotr.epub',
        bookTitle: 'The Lord of the Rings'
      }).subscribe(response => {
        expect(response.nodes.length).toBe(2);
        expect(response.edges.length).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/characters/graph'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  describe('updateCharacterGraph', () => {
    it('should update character graph', () => {
      const mockResponse: CharacterGraphResponse = {
        nodes: [
          { id: 'frodo', label: 'Frodo Baggins', description: 'Updated description' }
        ],
        edges: [],
        summaryCount: 15,
        cachedAt: '2026-01-15T00:00:00Z'
      };

      service.updateCharacterGraph({
        dropboxPath: '/Books/lotr.epub',
        newContent: 'New chapter summary content to update the graph with'
      }).subscribe(response => {
        expect(response.nodes[0].label).toBe('Frodo Baggins');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/characters/update'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  // ─── Error Handling Tests ──────────────────────────────────────────────────

  describe('Error handling', () => {
    it('should handle 404 error for summarizeText', () => {
      service.summarizeText({ text: 'test' }).subscribe({
        error: (err) => {
          expect(err.status).toBe(404);
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/summarize'));
      req.flush({ error: 'Not found' }, { status: 404, statusText: 'Not Found' });
    });

    it('should handle 500 error for aiBookSearch', () => {
      service.aiBookSearch('test query').subscribe({
        error: (err) => {
          expect(err.status).toBe(500);
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/book-search'));
      req.flush('Server Error', { status: 500, statusText: 'Internal Server Error' });
    });

    it('should handle network error for getTokenUsage', () => {
      service.getTokenUsage().subscribe({
        error: (err) => {
          expect(err).toBeTruthy();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/usage'));
      req.error(new ProgressEvent('Network error'));
    });
  });
});

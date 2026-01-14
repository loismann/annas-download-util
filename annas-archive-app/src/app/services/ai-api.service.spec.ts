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
  CharacterGraphResponse
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
});

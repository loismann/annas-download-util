import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { AiApiService, AiBookSearchResult, SuggestAuthorsResponse, RelatedBooksResponse } from './ai-api.service';
import { LoggerService } from './logger.service';

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

  describe('Error handling', () => {
    it('should handle 500 error for aiBookSearch', () => {
      service.aiBookSearch('test query').subscribe({
        error: (err) => {
          expect(err.status).toBe(500);
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/ai/book-search'));
      req.flush('Server Error', { status: 500, statusText: 'Internal Server Error' });
    });
  });
});

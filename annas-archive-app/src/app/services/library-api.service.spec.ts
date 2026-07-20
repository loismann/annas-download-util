import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { LibraryApiService, LibraryBook, LibraryBookMetadata, CoverCandidatesResponse } from './library-api.service';
import { LoggerService } from './logger.service';
import { LibraryReaderBook, DropboxEpubChaptersResponse, DropboxChapterContent, DropboxEpubStatus, DropboxBookSearchResult } from '../models/dropbox-epub.model';

describe('LibraryApiService', () => {
  let service: LibraryApiService;
  let httpMock: HttpTestingController;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'error', 'warn', 'debug']);

    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        LibraryApiService,
        { provide: LoggerService, useValue: mockLogger }
      ]
    });

    service = TestBed.inject(LibraryApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ─── Library Book Endpoint Tests ────────────────────────────────────────

  describe('getLibraryBooks', () => {
    it('should fetch all library books', () => {
      const mockBooks: LibraryBook[] = [
        {
          fileName: 'book1.epub',
          title: 'Test Book 1',
          authors: ['Author 1'],
          format: 'EPUB',
          fileSize: '1.2 MB',
          coverUrl: 'http://example.com/cover1.jpg'
        },
        {
          fileName: 'book2.pdf',
          title: 'Test Book 2',
          authors: ['Author 2'],
          format: 'PDF',
          fileSize: '3.4 MB'
        }
      ];

      service.getLibraryBooks().subscribe(books => {
        expect(books.length).toBe(2);
        expect(books[0].title).toBe('Test Book 1');
        expect(books[1].format).toBe('PDF');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/books'));
      expect(req.request.method).toBe('GET');
      req.flush(mockBooks);
    });
  });

  describe('getLibraryReaderBooks', () => {
    it('should fetch reader-enabled books', () => {
      const mockBooks: LibraryReaderBook[] = [
        {
          fileName: 'reader-book.epub',
          title: 'Reader Book',
          authors: ['Author'],
          readerKey: 'reader-key-123',
          format: 'EPUB',
          hasSummaries: true
        }
      ];

      service.getLibraryReaderBooks().subscribe(books => {
        expect(books.length).toBe(1);
        expect(books[0].readerKey).toBe('reader-key-123');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/books'));
      expect(req.request.method).toBe('GET');
      req.flush(mockBooks);
    });
  });

  describe('sendLibraryToKindle', () => {
    it('should send book to Kindle with all parameters', () => {
      const mockResponse = { success: true, message: 'Sent to Kindle' };

      service.sendLibraryToKindle('test-book.epub', 'Test Book', 'dad', false).subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/send-to-kindle'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('fileName')).toBe('test-book.epub');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('target')).toBe('dad');
      expect(req.request.params.get('toDropbox')).toBe('false');
      req.flush(mockResponse);
    });

    it('should send book to Dropbox when toDropbox is true', () => {
      const mockResponse = { success: true };

      service.sendLibraryToKindle('test-book.epub', 'Test Book', 'mom', true).subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/send-to-kindle'));
      expect(req.request.params.get('toDropbox')).toBe('true');
      expect(req.request.params.get('target')).toBe('mom');
      req.flush(mockResponse);
    });

    it('should work without title parameter', () => {
      const mockResponse = { success: true };

      service.sendLibraryToKindle('test-book.epub', undefined, 'dad').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/send-to-kindle'));
      expect(req.request.params.has('title')).toBe(false);
      req.flush(mockResponse);
    });
  });

  describe('updateLibraryBookMetadata', () => {
    it('should update book metadata', () => {
      const metadata: LibraryBookMetadata = {
        primaryGenre: 'Science Fiction',
        tags: ['sci-fi', 'adventure'],
        series: 'Test Series',
        title: 'Updated Title',
        authors: ['New Author']
      };
      const mockResponse: LibraryBook = {
        fileName: 'test.epub',
        title: 'Updated Title',
        authors: ['New Author'],
        format: 'EPUB',
        fileSize: '1 MB',
        primaryGenre: 'Science Fiction',
        tags: ['sci-fi', 'adventure'],
        series: 'Test Series'
      };

      service.updateLibraryBookMetadata('test.epub', metadata).subscribe(response => {
        expect(response.title).toBe('Updated Title');
        expect(response.primaryGenre).toBe('Science Fiction');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/metadata'));
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual(metadata);
      req.flush(mockResponse);
    });

    it('should encode special characters in fileName', () => {
      const metadata: LibraryBookMetadata = {
        primaryGenre: 'Fiction',
        tags: [],
        series: null
      };

      service.updateLibraryBookMetadata('book with spaces.epub', metadata).subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/book%20with%20spaces.epub/metadata'));
      expect(req.request.method).toBe('PATCH');
      req.flush({});
    });
  });

  describe('updateLibraryBookCover', () => {
    it('should update book cover from URL', () => {
      const newCoverUrl = 'http://example.com/new-cover.jpg';
      const mockResponse = { coverUrl: newCoverUrl };

      service.updateLibraryBookCover('test.epub', newCoverUrl).subscribe(response => {
        expect(response.coverUrl).toBe(newCoverUrl);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/cover'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ coverUrl: newCoverUrl });
      req.flush(mockResponse);
    });
  });

  describe('updateLibraryBookRatings', () => {
    it('should update personal rating', () => {
      const mockResponse: LibraryBook = {
        fileName: 'test.epub',
        title: 'Test',
        authors: [],
        format: 'EPUB',
        fileSize: '1 MB',
        personalRating: 5
      };

      service.updateLibraryBookRatings('test.epub', { personalRating: 5 }).subscribe(response => {
        expect(response.personalRating).toBe(5);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/ratings'));
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual({ personalRating: 5 });
      req.flush(mockResponse);
    });

    it('should update Goodreads rating', () => {
      service.updateLibraryBookRatings('test.epub', { goodreadsRating: 4.5 }).subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/ratings'));
      expect(req.request.body).toEqual({ goodreadsRating: 4.5 });
      req.flush({});
    });

    it('should clear rating with null', () => {
      service.updateLibraryBookRatings('test.epub', { personalRating: null }).subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/ratings'));
      expect(req.request.body).toEqual({ personalRating: null });
      req.flush({});
    });
  });

  describe('updateLibraryBookReaderEnabled', () => {
    it('should enable reader for book', () => {
      const mockResponse = { success: true, enabled: true };

      service.updateLibraryBookReaderEnabled('test.epub', true).subscribe(response => {
        expect(response.enabled).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/reader'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('fileName')).toBe('test.epub');
      expect(req.request.body).toEqual({ enabled: true });
      req.flush(mockResponse);
    });

    it('should disable reader for book', () => {
      const mockResponse = { success: true, enabled: false };

      service.updateLibraryBookReaderEnabled('test.epub', false).subscribe(response => {
        expect(response.enabled).toBe(false);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/reader'));
      expect(req.request.body).toEqual({ enabled: false });
      req.flush(mockResponse);
    });
  });

  describe('wipeLibraryGenres', () => {
    it('should wipe all genres', () => {
      const mockResponse = { success: true, updated: 42 };

      service.wipeLibraryGenres().subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.updated).toBe(42);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/books/genres/wipe'));
      expect(req.request.method).toBe('POST');
      req.flush(mockResponse);
    });
  });

  describe('deleteLibraryBook', () => {
    it('should delete a book', () => {
      const mockResponse = { success: true };

      service.deleteLibraryBook('book-to-delete.epub').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/book-to-delete.epub'));
      expect(req.request.method).toBe('DELETE');
      req.flush(mockResponse);
    });
  });

  describe('getLibraryBookSummary', () => {
    it('should fetch book summary', () => {
      const mockResponse = { summary: 'A great book about testing.', source: 'googlebooks' };

      service.getLibraryBookSummary('test.epub').subscribe(response => {
        expect(response.summary).toBe('A great book about testing.');
        expect(response.source).toBe('googlebooks');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/test.epub/summary'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });

    it('should handle null summary', () => {
      const mockResponse = { summary: null, source: null };

      service.getLibraryBookSummary('unknown.epub').subscribe(response => {
        expect(response.summary).toBeNull();
        expect(response.source).toBeNull();
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/unknown.epub/summary'));
      req.flush(mockResponse);
    });
  });

  describe('fetchLibraryCoverCandidates', () => {
    it('should fetch cover candidates with author', () => {
      const mockResponse: CoverCandidatesResponse = {
        covers: [
          'http://example.com/cover1.jpg',
          'http://example.com/cover2.jpg'
        ]
      };

      service.fetchLibraryCoverCandidates('Test Book', 'Test Author').subscribe(response => {
        expect(response.covers.length).toBe(2);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/cover-candidates'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('author')).toBe('Test Author');
      req.flush(mockResponse);
    });

    it('should fetch cover candidates without author', () => {
      const mockResponse: CoverCandidatesResponse = { covers: [] };

      service.fetchLibraryCoverCandidates('Unknown Book').subscribe(response => {
        expect(response.covers.length).toBe(0);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/book/cover-candidates'));
      expect(req.request.params.get('title')).toBe('Unknown Book');
      expect(req.request.params.has('author')).toBe(false);
      req.flush(mockResponse);
    });
  });

  // ─── Library Reader Endpoint Tests ────────────────────────────────────────

  describe('getLibraryReaderChapters', () => {
    it('should fetch chapter list', () => {
      const mockResponse: DropboxEpubChaptersResponse = {
        title: 'Test Book',
        chapters: [
          { id: 0, title: 'Chapter 1', level: 1, wordCount: 500 },
          { id: 1, title: 'Chapter 2', level: 1, wordCount: 750 }
        ]
      };

      service.getLibraryReaderChapters('test.epub').subscribe(response => {
        expect(response.chapters.length).toBe(2);
        expect(response.chapters[0].title).toBe('Chapter 1');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/chapters'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('fileName')).toBe('test.epub');
      req.flush(mockResponse);
    });
  });

  describe('getLibraryReaderChapterContent', () => {
    it('should fetch chapter content', () => {
      const mockResponse: DropboxChapterContent = {
        id: 1,
        title: 'Chapter 1',
        content: '<p>Chapter content here</p>',
        characterCount: 1500,
        wordCount: 300
      };

      service.getLibraryReaderChapterContent('test.epub', 1).subscribe(response => {
        expect(response.id).toBe(1);
        expect(response.content).toContain('Chapter content');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/chapter'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('fileName')).toBe('test.epub');
      expect(req.request.params.get('chapterId')).toBe('1');
      req.flush(mockResponse);
    });
  });

  describe('getLibraryReaderStatus', () => {
    it('should fetch indexing status', () => {
      const mockResponse: DropboxEpubStatus = {
        cached: true,
        inProgress: false,
        chaptersTotal: 25,
        chaptersCached: 25,
        percent: 100
      };

      service.getLibraryReaderStatus('test.epub').subscribe(response => {
        expect(response.cached).toBe(true);
        expect(response.percent).toBe(100);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/status'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('fileName')).toBe('test.epub');
      req.flush(mockResponse);
    });
  });

  describe('startLibraryReaderIndex', () => {
    it('should start indexing', () => {
      const mockResponse = { success: true };

      service.startLibraryReaderIndex('test.epub').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/index'));
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ fileName: 'test.epub' });
      req.flush(mockResponse);
    });
  });

  describe('deleteLibraryReaderIndex', () => {
    it('should delete index cache', () => {
      const mockResponse = { success: true };

      service.deleteLibraryReaderIndex('test.epub').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/index'));
      expect(req.request.method).toBe('DELETE');
      expect(req.request.body).toEqual({ fileName: 'test.epub' });
      req.flush(mockResponse);
    });
  });

  describe('searchLibraryReaderBook', () => {
    it('should search within book', () => {
      const mockResults: DropboxBookSearchResult[] = [
        { chapterId: 2, title: 'Chapter 3', matchCount: 3, position: 150, snippet: '...found the search term here...' },
        { chapterId: 5, title: 'Chapter 6', matchCount: 1, position: 420, snippet: '...another match here...' }
      ];

      service.searchLibraryReaderBook('test.epub', 'search term').subscribe(results => {
        expect(results.length).toBe(2);
        expect(results[0].chapterId).toBe(2);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/search'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('fileName')).toBe('test.epub');
      expect(req.request.params.get('query')).toBe('search term');
      req.flush(mockResults);
    });

    it('should return empty array for no matches', () => {
      service.searchLibraryReaderBook('test.epub', 'nonexistent').subscribe(results => {
        expect(results.length).toBe(0);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/library/reader/epub/search'));
      req.flush([]);
    });
  });
});

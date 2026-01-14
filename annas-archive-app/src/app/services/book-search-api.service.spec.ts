import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { BookSearchApiService } from './book-search-api.service';
import { LoggerService } from './logger.service';

describe('BookSearchApiService', () => {
  let service: BookSearchApiService;
  let httpMock: HttpTestingController;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'debug', 'error', 'warn']);

    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        BookSearchApiService,
        { provide: LoggerService, useValue: mockLogger }
      ]
    });

    service = TestBed.inject(BookSearchApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ─── Anna's Archive Search Tests ────────────────────────────────────────

  describe('searchBooks', () => {
    it('should search books with name and exact parameters', () => {
      const mockBooks = [
        { title: 'Test Book', md5: 'abc123', authors: ['Author'], language: 'English', format: 'EPUB' }
      ];

      service.searchBooks('test query', false).subscribe(books => {
        expect(books.length).toBe(1);
        expect(books[0].title).toBe('Test Book');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/anna/book'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('name')).toBe('test query');
      expect(req.request.params.get('exact')).toBe('false');
      req.flush(mockBooks);
    });

    it('should convert single object response to array', () => {
      const singleBook = { title: 'Single Book', md5: 'single123', authors: [], language: 'English', format: 'PDF' };

      service.searchBooks('single', true).subscribe(books => {
        expect(Array.isArray(books)).toBe(true);
        expect(books.length).toBe(1);
        expect(books[0].title).toBe('Single Book');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/anna/book'));
      expect(req.request.params.get('exact')).toBe('true');
      req.flush(singleBook);
    });
  });

  // ─── Anna's Archive Download Tests ────────────────────────────────────────

  describe('downloadMember', () => {
    it('should download member book with title', () => {
      const mockBlob = new Blob(['book content'], { type: 'application/epub+zip' });

      service.downloadMember('abc123', 'Test Book').subscribe(response => {
        expect(response).toBeInstanceOf(Blob);
      });

      const req = httpMock.expectOne(req => req.url.includes('/download/member'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.has('coverUrl')).toBe(false);
      expect(req.request.responseType).toBe('blob');
      req.flush(mockBlob);
    });

    it('should download member book with cover URL', () => {
      const mockBlob = new Blob(['book content'], { type: 'application/epub+zip' });

      service.downloadMember('abc123', 'Test Book', 'https://covers.example.com/cover.jpg').subscribe(response => {
        expect(response).toBeInstanceOf(Blob);
      });

      const req = httpMock.expectOne(req => req.url.includes('/download/member'));
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      req.flush(mockBlob);
    });
  });

  // ─── Send to Library Tests ────────────────────────────────────────

  describe('sendToLibrary', () => {
    it('should send book to library with minimal parameters', () => {
      const mockResponse = { success: true, message: 'Saved to library' };

      service.sendToLibrary('abc123', 'Test Book').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/send-to-library'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('Test Book');
      req.flush(mockResponse);
    });

    it('should send book to library with all parameters', () => {
      const mockResponse = { success: true, message: 'Saved to library' };

      service.sendToLibrary(
        'abc123',
        'Test Book',
        'https://covers.example.com/cover.jpg',
        'Test Author',
        'EPUB',
        '2.5 MB',
        'anna',
        'A great book about testing'
      ).subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/send-to-library'));
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      expect(req.request.params.get('authors')).toBe('Test Author');
      expect(req.request.params.get('format')).toBe('EPUB');
      expect(req.request.params.get('fileSize')).toBe('2.5 MB');
      expect(req.request.params.get('source')).toBe('anna');
      expect(req.request.params.get('description')).toBe('A great book about testing');
      req.flush(mockResponse);
    });
  });

  // ─── Send to Boox Tests ────────────────────────────────────────

  describe('sendToBoox', () => {
    it('should send book to Boox with title', () => {
      const mockResponse = {
        success: true,
        dropboxPath: '/Books/test.epub',
        accountFastInfo: { downloadsLeft: 45, downloadsPerDay: 100 }
      };

      service.sendToBoox('abc123', 'Test Book').subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.accountFastInfo?.downloadsLeft).toBe(45);
      });

      const req = httpMock.expectOne(req => req.url.includes('/send-to-boox'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('Test Book');
      req.flush(mockResponse);
    });

    it('should send book to Boox with cover URL', () => {
      const mockResponse = { success: true, accountFastInfo: null };

      service.sendToBoox('abc123', 'Test Book', 'https://covers.example.com/cover.jpg').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/send-to-boox'));
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      req.flush(mockResponse);
    });
  });

  // ─── Send to Kindle Tests ────────────────────────────────────────

  describe('sendToKindle', () => {
    it('should send book to dad\'s Kindle', () => {
      const mockResponse = {
        success: true,
        message: 'Sent to Kindle',
        accountFastInfo: { downloadsLeft: 40, downloadsPerDay: 100 }
      };

      service.sendToKindle('abc123', 'Test Book', 'dad').subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/send-to-kindle'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('target')).toBe('dad');
      req.flush(mockResponse);
    });

    it('should send book to mom\'s Kindle with cover URL', () => {
      const mockResponse = { success: true, accountFastInfo: null };

      service.sendToKindle('abc123', 'Test Book', 'mom', 'https://covers.example.com/cover.jpg').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/send-to-kindle'));
      expect(req.request.params.get('target')).toBe('mom');
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      req.flush(mockResponse);
    });
  });

  // ─── LibGen Search Tests ────────────────────────────────────────

  describe('searchBooksLibGen', () => {
    it('should search books on LibGen with exact parameter', () => {
      const mockBooks = [
        { title: 'LibGen Book', md5: 'libgen123', source: 'libgen', format: 'PDF' }
      ];

      service.searchBooksLibGen('test', false).subscribe(books => {
        expect(books.length).toBe(1);
        expect(books[0].title).toBe('LibGen Book');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/libgen/book'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('name')).toBe('test');
      expect(req.request.params.get('exact')).toBe('false');
      req.flush(mockBooks);
    });

    it('should convert single LibGen result to array', () => {
      const singleBook = { title: 'Single LibGen Book', md5: 'lg123', format: 'PDF' };

      service.searchBooksLibGen('single', true).subscribe(books => {
        expect(Array.isArray(books)).toBe(true);
        expect(books.length).toBe(1);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/libgen/book'));
      req.flush(singleBook);
    });
  });

  // ─── LibGen Download Tests ────────────────────────────────────────

  describe('downloadMemberLibGen', () => {
    it('should download member book from LibGen', () => {
      const mockBlob = new Blob(['libgen content'], { type: 'application/pdf' });

      service.downloadMemberLibGen('libgen123', 'LibGen Book').subscribe(response => {
        expect(response).toBeInstanceOf(Blob);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/libgen') && req.url.includes('/download/member'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('LibGen Book');
      expect(req.request.responseType).toBe('blob');
      req.flush(mockBlob);
    });

    it('should download member book from LibGen with cover URL', () => {
      const mockBlob = new Blob(['libgen content'], { type: 'application/pdf' });

      service.downloadMemberLibGen('libgen123', 'LibGen Book', 'https://covers.example.com/cover.jpg').subscribe();

      const req = httpMock.expectOne(req => req.url.includes('/api/libgen') && req.url.includes('/download/member'));
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      req.flush(mockBlob);
    });
  });

  // ─── LibGen Send to Library Tests ────────────────────────────────────────

  describe('sendToLibraryLibGen', () => {
    it('should send LibGen book to library with all parameters', () => {
      const mockResponse = { success: true, message: 'Saved to library from LibGen' };

      service.sendToLibraryLibGen(
        'libgen123',
        'LibGen Book',
        'https://covers.example.com/cover.jpg',
        'LibGen Author',
        'PDF',
        '2.5 MB',
        'libgen',
        'A LibGen book description'
      ).subscribe(response => {
        expect(response.success).toBe(true);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/libgen') && req.url.includes('/send-to-library'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('title')).toBe('LibGen Book');
      expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
      expect(req.request.params.get('authors')).toBe('LibGen Author');
      expect(req.request.params.get('format')).toBe('PDF');
      expect(req.request.params.get('fileSize')).toBe('2.5 MB');
      expect(req.request.params.get('source')).toBe('libgen');
      expect(req.request.params.get('description')).toBe('A LibGen book description');
      req.flush(mockResponse);
    });
  });

  // ─── Status & Health Tests ────────────────────────────────────────

  describe('getDownloadStatus', () => {
    it('should get download status with account info', () => {
      const mockResponse = {
        accountFastInfo: { downloadsLeft: 50, downloadsPerDay: 100 }
      };

      service.getDownloadStatus().subscribe(response => {
        expect(response.accountFastInfo?.downloadsLeft).toBe(50);
        expect(response.accountFastInfo?.downloadsPerDay).toBe(100);
      });

      const req = httpMock.expectOne(req => req.url.includes('/download-status'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  describe('getSlumHealth', () => {
    it('should get SLUM health status', () => {
      const mockResponse = [
        { name: "Anna's Archive ORG", health: '95%', cert_exp: '90 days' },
        { name: "Anna's Archive SE", health: '92%', cert_exp: '85 days' }
      ];

      service.getSlumHealth().subscribe(response => {
        expect(response).toEqual(mockResponse);
      });

      const req = httpMock.expectOne(req => req.url.includes('/slum-health'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  describe('getMirrorHealth', () => {
    it('should get mirror health status', () => {
      const mockResponse = [
        { extension: 'pm', health: 88 },
        { extension: 'in', health: 85 }
      ];

      service.getMirrorHealth().subscribe(response => {
        expect(response).toEqual(mockResponse);
      });

      const req = httpMock.expectOne(req => req.url.includes('/mirror-health'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });
  });

  // ─── Cover Lookup Tests ────────────────────────────────────────

  describe('fetchCover', () => {
    it('should fetch cover with title and author', () => {
      const mockResponse = { coverUrl: 'https://covers.example.com/found-cover.jpg' };

      service.fetchCover('Test Book', 'Test Author').subscribe(response => {
        expect(response.coverUrl).toBe('https://covers.example.com/found-cover.jpg');
      });

      const req = httpMock.expectOne(req => req.url.includes('/book/cover'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('author')).toBe('Test Author');
      req.flush(mockResponse);
    });

    it('should fetch cover without author', () => {
      const mockResponse = { coverUrl: null };

      service.fetchCover('Unknown Book').subscribe(response => {
        expect(response.coverUrl).toBeNull();
      });

      const req = httpMock.expectOne(req => req.url.includes('/book/cover'));
      expect(req.request.params.get('title')).toBe('Unknown Book');
      expect(req.request.params.has('author')).toBe(false);
      req.flush(mockResponse);
    });
  });

  // ─── Description Lookup Tests ────────────────────────────────────────

  describe('fetchDescriptionFromGoogleBooks', () => {
    it('should fetch description from Google Books with author', () => {
      const mockResponse = { description: 'A description from Google Books.' };

      service.fetchDescriptionFromGoogleBooks('Test Book', 'Test Author').subscribe(response => {
        expect(response.description).toBe('A description from Google Books.');
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/google-books'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('author')).toBe('Test Author');
      req.flush(mockResponse);
    });

    it('should fetch description from Google Books without author', () => {
      const mockResponse = { description: null };

      service.fetchDescriptionFromGoogleBooks('Unknown Book').subscribe(response => {
        expect(response.description).toBeNull();
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/google-books'));
      expect(req.request.params.has('author')).toBe(false);
      req.flush(mockResponse);
    });
  });

  describe('fetchDescriptionFromOpenLibrary', () => {
    it('should fetch description from OpenLibrary with author', () => {
      const mockResponse = { description: 'A description from OpenLibrary.' };

      service.fetchDescriptionFromOpenLibrary('Test Book', 'Test Author').subscribe(response => {
        expect(response.description).toBe('A description from OpenLibrary.');
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/openlibrary'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('author')).toBe('Test Author');
      req.flush(mockResponse);
    });

    it('should fetch description from OpenLibrary without author', () => {
      const mockResponse = { description: null };

      service.fetchDescriptionFromOpenLibrary('Unknown Book').subscribe(response => {
        expect(response.description).toBeNull();
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/openlibrary'));
      expect(req.request.params.has('author')).toBe(false);
      req.flush(mockResponse);
    });
  });

  describe('fetchDescriptionFromGPT4', () => {
    it('should fetch description from GPT-4 with author', () => {
      const mockResponse = { description: 'An AI-generated description of the book.' };

      service.fetchDescriptionFromGPT4('Test Book', 'Test Author').subscribe(response => {
        expect(response.description).toBe('An AI-generated description of the book.');
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/gpt'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('title')).toBe('Test Book');
      expect(req.request.params.get('author')).toBe('Test Author');
      req.flush(mockResponse);
    });

    it('should fetch description from GPT-4 without author', () => {
      const mockResponse = { description: 'AI description without author context.' };

      service.fetchDescriptionFromGPT4('Mystery Book').subscribe(response => {
        expect(response.description).toBe('AI description without author context.');
      });

      const req = httpMock.expectOne(req => req.url.includes('/description/gpt'));
      expect(req.request.params.has('author')).toBe(false);
      req.flush(mockResponse);
    });
  });
});

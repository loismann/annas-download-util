import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { AnnaArchiveApiService } from './anna-archive-api.service';

/**
 * Basic smoke tests for AnnaArchiveApiService
 * These tests verify the service can be created and makes correct HTTP calls
 */
describe('AnnaArchiveApiService', () => {
  let service: AnnaArchiveApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [AnnaArchiveApiService]
    });

    service = TestBed.inject(AnnaArchiveApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should search books with exact parameter', () => {
    const mockBooks = [
      {
        title: 'Test Book',
        md5: 'abc123',
        authors: ['Author'],
        language: 'English',
        format: 'EPUB',
        source: '/lgli',
        fileSize: '1MB',
        bookType: 'Book',
        publisher: 'Publisher',
        year: 2023,
        isbn: '978-1234567890',
        coverCandidates: []
      }
    ];

    service.searchBooks('test', false).subscribe(books => {
      expect(books.length).toBe(1);
      expect(books[0].title).toBe('Test Book');
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/book'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('name')).toBe('test');
    expect(req.request.params.get('exact')).toBe('false');
    req.flush(mockBooks);
  });

  it('should send book to Boox', () => {
    const mockResponse = { success: true, dropboxPath: '/Books/test.epub' };

    service.sendToBoox('abc123', 'Test Book').subscribe(response => {
      expect(response.success).toBe(true);
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-boox'));
    expect(req.request.method).toBe('POST');
    req.flush(mockResponse);
  });

  it('should send book to Kindle', () => {
    const mockResponse = { success: true };

    service.sendToKindle('abc123', 'Test Book', 'dad').subscribe(response => {
      expect(response.success).toBe(true);
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-kindle'));
    expect(req.request.method).toBe('POST');
    req.flush(mockResponse);
  });

  // ─── Anna Download Endpoint Tests ────────────────────────────────────────

  it('should send book to library with all parameters', () => {
    const mockResponse = { success: true, message: 'Saved to library' };

    service.sendToLibrary(
      'abc123',
      'Test Book',
      'https://covers.example.com/cover.jpg',
      'Test Author',
      'EPUB',
      '1.5 MB',
      'anna',
      'A test book description'
    ).subscribe(response => {
      expect(response.success).toBe(true);
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-library'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
    expect(req.request.params.get('authors')).toBe('Test Author');
    expect(req.request.params.get('format')).toBe('EPUB');
    expect(req.request.params.get('fileSize')).toBe('1.5 MB');
    expect(req.request.params.get('source')).toBe('anna');
    expect(req.request.params.get('description')).toBe('A test book description');
    req.flush(mockResponse);
  });

  it('should send book to library with minimal parameters', () => {
    const mockResponse = { success: true };

    service.sendToLibrary('abc123', 'Test Book').subscribe(response => {
      expect(response.success).toBe(true);
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-library'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.has('coverUrl')).toBe(false);
    req.flush(mockResponse);
  });

  it('should download member book with cover replacement', () => {
    const mockBlob = new Blob(['test content'], { type: 'application/epub+zip' });

    service.downloadMember('abc123', 'Test Book', 'https://covers.example.com/cover.jpg').subscribe(response => {
      expect(response).toBeInstanceOf(Blob);
    });

    const req = httpMock.expectOne(req => req.url.includes('/download/member'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
    expect(req.request.responseType).toBe('blob');
    req.flush(mockBlob);
  });

  it('should download member book without cover', () => {
    const mockBlob = new Blob(['test content'], { type: 'application/epub+zip' });

    service.downloadMember('abc123', 'Test Book').subscribe(response => {
      expect(response).toBeInstanceOf(Blob);
    });

    const req = httpMock.expectOne(req => req.url.includes('/download/member'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.has('coverUrl')).toBe(false);
    req.flush(mockBlob);
  });

  it('should fetch GPT-4 description with author', () => {
    const mockResponse = { description: 'A fascinating adventure story.' };

    service.fetchDescriptionFromGPT4('Test Book', 'Test Author').subscribe(response => {
      expect(response.description).toBe('A fascinating adventure story.');
    });

    const req = httpMock.expectOne(req => req.url.includes('/description/gpt'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.get('author')).toBe('Test Author');
    req.flush(mockResponse);
  });

  it('should fetch GPT-4 description without author', () => {
    const mockResponse = { description: 'A fascinating adventure story.' };

    service.fetchDescriptionFromGPT4('Test Book').subscribe(response => {
      expect(response.description).toBe('A fascinating adventure story.');
    });

    const req = httpMock.expectOne(req => req.url.includes('/description/gpt'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.has('author')).toBe(false);
    req.flush(mockResponse);
  });

  it('should get download status', () => {
    const mockResponse = { accountFastInfo: { downloadsLeft: 50, downloadsPerDay: 100 } };

    service.getDownloadStatus().subscribe(response => {
      expect(response.accountFastInfo?.downloadsLeft).toBe(50);
      expect(response.accountFastInfo?.downloadsPerDay).toBe(100);
    });

    const req = httpMock.expectOne(req => req.url.includes('/download-status'));
    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });

  it('should send to Kindle with target parameter', () => {
    const mockResponse = { success: true, message: "Sent to Dad's Kindle" };

    service.sendToKindle('abc123', 'Test Book', 'dad', 'https://covers.example.com/cover.jpg').subscribe(response => {
      expect(response.success).toBe(true);
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-kindle'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('target')).toBe('dad');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
    req.flush(mockResponse);
  });

  it('should send to Boox with cover URL', () => {
    const mockResponse = { success: true, dropboxPath: '/Books/test.epub' };

    service.sendToBoox('abc123', 'Test Book', 'https://covers.example.com/cover.jpg').subscribe(response => {
      expect(response.success).toBe(true);
      expect(response.dropboxPath).toBe('/Books/test.epub');
    });

    const req = httpMock.expectOne(req => req.url.includes('/send-to-boox'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('Test Book');
    expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
    req.flush(mockResponse);
  });

  // ─── LibGen Endpoint Tests ────────────────────────────────────────

  it('should search books on LibGen with exact parameter', () => {
    const mockBooks = [
      {
        title: 'LibGen Book',
        md5: 'libgen123',
        authors: ['LibGen Author'],
        language: 'English',
        format: 'PDF',
        source: 'libgen',
        fileSize: '2MB',
        bookType: 'Book',
        publisher: 'Publisher',
        year: 2022,
        isbn: '978-0000000000',
        coverCandidates: []
      }
    ];

    service.searchBooksLibGen('test', false).subscribe(books => {
      expect(books.length).toBe(1);
      expect(books[0].title).toBe('LibGen Book');
      expect(books[0].source).toBe('libgen');
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/libgen/book'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('name')).toBe('test');
    expect(req.request.params.get('exact')).toBe('false');
    req.flush(mockBooks);
  });

  it('should send book to library from LibGen with all parameters', () => {
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

  it('should download member book from LibGen with cover replacement', () => {
    const mockBlob = new Blob(['libgen content'], { type: 'application/pdf' });

    service.downloadMemberLibGen('libgen123', 'LibGen Book', 'https://covers.example.com/cover.jpg').subscribe(response => {
      expect(response).toBeInstanceOf(Blob);
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/libgen') && req.url.includes('/download/member'));
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('title')).toBe('LibGen Book');
    expect(req.request.params.get('coverUrl')).toBe('https://covers.example.com/cover.jpg');
    expect(req.request.responseType).toBe('blob');
    req.flush(mockBlob);
  });

  // ─── Cover & Description Lookup Tests ────────────────────────────────────────

  it('should fetch cover with author', () => {
    const mockResponse = { coverUrl: 'https://covers.example.com/found-cover.jpg' };

    service.fetchCover('Test Book', 'Test Author').subscribe(response => {
      expect(response.coverUrl).toBe('https://covers.example.com/found-cover.jpg');
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/book/cover'));
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

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/book/cover'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('title')).toBe('Unknown Book');
    expect(req.request.params.has('author')).toBe(false);
    req.flush(mockResponse);
  });

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
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('title')).toBe('Unknown Book');
    expect(req.request.params.has('author')).toBe(false);
    req.flush(mockResponse);
  });

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
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('title')).toBe('Unknown Book');
    expect(req.request.params.has('author')).toBe(false);
    req.flush(mockResponse);
  });

  // ─── Health Check Tests ────────────────────────────────────────

  it('should get SLUM health status', () => {
    const mockResponse = [
      { name: "Anna's Archive ORG", health: '95%', cert_exp: '90 days' },
      { name: "Anna's Archive SE", health: '92%', cert_exp: '85 days' }
    ];

    service.getSlumHealth().subscribe(response => {
      expect(response).toEqual(mockResponse);
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/slum-health'));
    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });

  it('should get mirror health status', () => {
    const mockResponse = [
      { extension: 'pm', health: 88 },
      { extension: 'in', health: 85 }
    ];

    service.getMirrorHealth().subscribe(response => {
      expect(response).toEqual(mockResponse);
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/mirror-health'));
    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });
});

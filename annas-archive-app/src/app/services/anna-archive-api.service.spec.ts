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

  it('should be created', () => {
    expect(service).toBeTruthy();
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

  it('should get Dropbox EPUBs', () => {
    const mockEpubs = [{ id: '1', name: 'Book.epub', path: '/Books/Book.epub', size: 1000, serverModified: '2023-01-01' }];

    service.getDropboxEpubs().subscribe(epubs => {
      expect(epubs.length).toBe(1);
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/anna/dropbox/epubs'));
    expect(req.request.method).toBe('GET');
    req.flush(mockEpubs);
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

  it('should get token usage', () => {
    const mockUsage = { promptTokens: 1000, completionTokens: 500, totalTokens: 1500 };

    service.getTokenUsage().subscribe(usage => {
      expect(usage.totalTokens).toBe(1500);
    });

    const req = httpMock.expectOne(req => req.url.includes('/api/ai/usage'));
    expect(req.request.method).toBe('GET');
    req.flush(mockUsage);
  });
});

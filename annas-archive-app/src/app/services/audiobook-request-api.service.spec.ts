import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AudiobookRequestApiService, ListenarrIntegrationStatus } from './audiobook-request-api.service';

describe('AudiobookRequestApiService', () => {
  let service: AudiobookRequestApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), AudiobookRequestApiService]
    });
    service = TestBed.inject(AudiobookRequestApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads the safe integration status', () => {
    const status = { enabled: true, readOnlyGatePassed: true } as ListenarrIntegrationStatus;
    service.getStatus().subscribe(result => expect(result).toEqual(status));

    const request = http.expectOne(req => req.url.endsWith('/api/audiobook-requests/status'));
    expect(request.request.method).toBe('GET');
    request.flush(status);
  });

  it('encodes search term, region, and optional language as query parameters', () => {
    service.search('Pride & Prejudice', 'uk', 'english').subscribe();

    const request = http.expectOne(req => req.url.endsWith('/api/audiobook-requests/search'));
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('term')).toBe('Pride & Prejudice');
    expect(request.request.params.get('region')).toBe('uk');
    expect(request.request.params.get('language')).toBe('english');
    request.flush({ query: 'Pride & Prejudice', region: 'uk', language: 'english', totalResults: 0, results: [] });
  });

  // The body carries an opaque token plus one flag, and the distinction between
  // them is the point. Everything the server decided — which edition, which
  // region, whether to auto-search — rides inside the token where the browser
  // cannot touch it. `acceptNoReleases` is the one thing that is genuinely the
  // user's to send, because it reports a human answering a warning.
  it('confirms with an opaque preview token and nothing describing the destination', () => {
    const token = 'A'.repeat(64);
    service.confirmRequest(token).subscribe();

    const request = http.expectOne(req => req.url.endsWith('/api/audiobook-requests'));
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ previewToken: token, acceptNoReleases: false });
    expect(JSON.stringify(request.request.body)).not.toContain('destinationPath');
    request.flush({ listenarrId: 42, asin: 'B012345678', title: 'Book', status: 'Monitored', alreadyExisted: false, requesterAdded: true });
  });

  it('reports an acknowledged no-releases warning back to the server', () => {
    const token = 'A'.repeat(64);
    service.confirmRequest(token, true).subscribe();

    const request = http.expectOne(req => req.url.endsWith('/api/audiobook-requests'));
    expect(request.request.body).toEqual({ previewToken: token, acceptNoReleases: true });
    request.flush({ listenarrId: 42, asin: 'B012345678', title: 'Book', status: 'Monitored', alreadyExisted: false, requesterAdded: true });
  });

  it('grabs a release using only the app selection token', () => {
    const token = 'B'.repeat(64);
    service.grabRelease(42, token).subscribe();

    const request = http.expectOne(req => req.url.includes('/42/releases/') && req.url.endsWith('/grab'));
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({});
    expect(request.request.url).not.toContain('magnet:');
    request.flush({ listenarrId: 42, asin: 'B012345678', downloadId: 'download-1', status: 'Queued' });
  });
});

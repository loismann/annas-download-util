import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { errorInterceptor } from './error.interceptor';
import { LoggerService } from '../services/logger.service';

describe('errorInterceptor', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'warn', 'error']);

    TestBed.configureTestingModule({
      providers: [
        { provide: LoggerService, useValue: mockLogger },
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting()
      ]
    });

    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should pass through successful requests', () => {
    httpClient.get('/api/test').subscribe(response => {
      expect(response).toEqual({ data: 'success' });
    });

    const req = httpMock.expectOne('/api/test');
    req.flush({ data: 'success' });
    expect(mockLogger.error).not.toHaveBeenCalled();
  });

  describe('error handling by status code', () => {
    it('should handle network error (status 0)', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(0);
          expect(mockLogger.error).toHaveBeenCalled();
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    });

    it('should handle 400 validation error', (done) => {
      httpClient.post('/api/test', {}).subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(400);
          expect(mockLogger.warn).toHaveBeenCalled();
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Validation failed', details: { field: ['required'] } }, { status: 400, statusText: 'Bad Request' });
    });

    it('should handle 401 unauthorized', (done) => {
      httpClient.get('/api/protected').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(401);
          expect(mockLogger.warn).toHaveBeenCalledWith(jasmine.stringContaining('Unauthorized'));
          done();
        }
      });

      const req = httpMock.expectOne('/api/protected');
      req.flush({ error: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    });

    it('should handle 403 forbidden', (done) => {
      httpClient.get('/api/admin').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(403);
          expect(mockLogger.warn).toHaveBeenCalledWith(jasmine.stringContaining('Forbidden'));
          done();
        }
      });

      const req = httpMock.expectOne('/api/admin');
      req.flush({ error: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    });

    it('should handle 404 not found', (done) => {
      httpClient.get('/api/missing').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(404);
          expect(mockLogger.warn).toHaveBeenCalledWith(jasmine.stringContaining('not found'));
          done();
        }
      });

      const req = httpMock.expectOne('/api/missing');
      req.flush({ error: 'Not found' }, { status: 404, statusText: 'Not Found' });
    });

    it('should handle 429 rate limited', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(429);
          expect(mockLogger.warn).toHaveBeenCalledWith(jasmine.stringContaining('Rate limited'));
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Rate limited', details: { retryAfter: ['60'] } }, { status: 429, statusText: 'Too Many Requests' });
    });

    it('should handle 500 server error', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(500);
          expect(mockLogger.error).toHaveBeenCalled();
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Internal server error' }, { status: 500, statusText: 'Internal Server Error' });
    });

    it('should handle 502 bad gateway', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(502);
          expect(mockLogger.error).toHaveBeenCalled();
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Bad gateway' }, { status: 502, statusText: 'Bad Gateway' });
    });

    it('should handle 503 service unavailable', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.status).toBe(503);
          expect(mockLogger.error).toHaveBeenCalled();
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Service unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    });
  });

  describe('API error extraction', () => {
    it('should extract error from ApiError format', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.error.error).toBe('Custom API error');
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ error: 'Custom API error', errorCode: 'CUSTOM_ERROR' }, { status: 400, statusText: 'Bad Request' });
    });

    it('should extract error from message field', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.error.error).toBe('Error from message field');
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush({ message: 'Error from message field' }, { status: 400, statusText: 'Bad Request' });
    });

    it('should extract error from string body', (done) => {
      httpClient.get('/api/test').subscribe({
        error: (err: HttpErrorResponse) => {
          expect(err.error.error).toBe('Plain string error');
          done();
        }
      });

      const req = httpMock.expectOne('/api/test');
      req.flush('Plain string error', { status: 400, statusText: 'Bad Request' });
    });
  });
});

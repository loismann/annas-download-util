import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { GamingApiService, GamingToggleResponse, GamingStatusResponse } from './gaming-api.service';
import { LoggerService } from './logger.service';

describe('GamingApiService', () => {
  let service: GamingApiService;
  let httpMock: HttpTestingController;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'debug', 'error', 'warn']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        GamingApiService,
        { provide: LoggerService, useValue: mockLogger }
      ]
    });

    service = TestBed.inject(GamingApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  describe('toggleGamingPC', () => {
    it('should send wake command (action=1)', () => {
      const mockResponse: GamingToggleResponse = {
        success: true,
        action: 'wake',
        message: 'Wake-on-LAN packet sent',
        output: 'PC is waking up...'
      };

      service.toggleGamingPC(1).subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.action).toBe('wake');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/toggle'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('action')).toBe('1');
      req.flush(mockResponse);
    });

    it('should send sleep command (action=2)', () => {
      const mockResponse: GamingToggleResponse = {
        success: true,
        action: 'sleep',
        message: 'Sleep command sent'
      };

      service.toggleGamingPC(2).subscribe(response => {
        expect(response.success).toBe(true);
        expect(response.action).toBe('sleep');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/toggle'));
      expect(req.request.method).toBe('POST');
      expect(req.request.params.get('action')).toBe('2');
      req.flush(mockResponse);
    });

    it('should handle error response', () => {
      const mockResponse: GamingToggleResponse = {
        success: false,
        action: 'wake',
        message: 'Failed to wake PC',
        error: 'Connection timeout'
      };

      service.toggleGamingPC(1).subscribe(response => {
        expect(response.success).toBe(false);
        expect(response.error).toBe('Connection timeout');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/toggle'));
      req.flush(mockResponse);
    });
  });

  describe('getGamingPCStatus', () => {
    it('should return online status', () => {
      const mockResponse: GamingStatusResponse = {
        isOnline: true,
        ipAddress: '192.168.0.80',
        lastChecked: '2024-01-15T10:30:00Z'
      };

      service.getGamingPCStatus().subscribe(response => {
        expect(response.isOnline).toBe(true);
        expect(response.ipAddress).toBe('192.168.0.80');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/status'));
      expect(req.request.method).toBe('GET');
      req.flush(mockResponse);
    });

    it('should return offline status', () => {
      const mockResponse: GamingStatusResponse = {
        isOnline: false,
        ipAddress: '192.168.0.80',
        lastChecked: '2024-01-15T10:30:00Z'
      };

      service.getGamingPCStatus().subscribe(response => {
        expect(response.isOnline).toBe(false);
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/status'));
      req.flush(mockResponse);
    });

    it('should handle error response', () => {
      const mockResponse: GamingStatusResponse = {
        isOnline: false,
        ipAddress: '192.168.0.80',
        lastChecked: '2024-01-15T10:30:00Z',
        error: 'Host unreachable'
      };

      service.getGamingPCStatus().subscribe(response => {
        expect(response.error).toBe('Host unreachable');
      });

      const req = httpMock.expectOne(req => req.url.includes('/api/gaming/status'));
      req.flush(mockResponse);
    });
  });
});

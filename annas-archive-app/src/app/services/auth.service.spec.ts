import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { AuthService } from './auth.service';
import { PLATFORM_ID } from '@angular/core';

/**
 * Basic smoke tests for AuthService
 * These tests verify authentication and token management works correctly
 */
describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        AuthService,
        { provide: PLATFORM_ID, useValue: 'browser' }
      ]
    });

    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
    localStorage.clear();
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('should login and store token', (done) => {
    const mockResponse = {
      token: 'test-token',
      name: 'Test User',
      isAdmin: false,
      expiresAt: '2025-12-31T00:00:00Z'
    };

    service.login('test-code').subscribe(response => {
      expect(response.token).toBe('test-token');
      expect(service.getToken()).toBe('test-token');
      expect(service.getName()).toBe('Test User');
      expect(service.isAuthenticated()).toBe(true);
      done();
    });

    const req = httpMock.expectOne(req => req.url.includes('/login'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ code: 'test-code' });
    req.flush(mockResponse);
  });

  it('should logout and clear token', (done) => {
    const mockResponse = {
      token: 'test-token',
      name: 'Test User',
      isAdmin: false,
      expiresAt: '2025-12-31T00:00:00Z'
    };

    service.login('test-code').subscribe(() => {
      service.logout();
      expect(service.getToken()).toBeNull();
      expect(service.getName()).toBeNull();
      expect(service.isAuthenticated()).toBe(false);
      done();
    });

    const req = httpMock.expectOne(req => req.url.includes('/login'));
    req.flush(mockResponse);
  });

  it('should return null token when not authenticated', () => {
    expect(service.getToken()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });

  it('should track admin status', (done) => {
    const mockResponse = {
      token: 'test-token',
      name: 'Admin User',
      isAdmin: true,
      expiresAt: '2025-12-31T00:00:00Z'
    };

    service.login('admin-code').subscribe(() => {
      expect(service.isAdmin()).toBe(true);
      done();
    });

    const req = httpMock.expectOne(req => req.url.includes('/login'));
    req.flush(mockResponse);
  });

  it('should emit authentication state changes', (done) => {
    let emissionCount = 0;

    service.isAuthenticated$.subscribe(isAuth => {
      emissionCount++;
      if (emissionCount === 1) {
        expect(isAuth).toBe(false);
      } else if (emissionCount === 2) {
        expect(isAuth).toBe(true);
        done();
      }
    });

    setTimeout(() => {
      service.login('test-code').subscribe();
      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush({
        token: 'token',
        name: 'User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      });
    }, 100);
  });

  it('should fetch user activity', (done) => {
    const mockActivity = [
      { initial: 'M', userName: 'Mom', minutesAgo: 5, isFullTone: true, isHalfTone: false },
      { initial: 'D', userName: 'Dad', minutesAgo: 45, isFullTone: false, isHalfTone: true }
    ];

    service.getUserActivity().subscribe(activity => {
      expect(activity.length).toBe(2);
      expect(activity[0].initial).toBe('M');
      expect(activity[0].isFullTone).toBe(true);
      expect(activity[1].initial).toBe('D');
      expect(activity[1].isHalfTone).toBe(true);
      done();
    });

    const req = httpMock.expectOne(req => req.url.includes('/user-activity'));
    expect(req.request.method).toBe('GET');
    req.flush(mockActivity);
  });

  it('should return empty array when no user activity', (done) => {
    service.getUserActivity().subscribe(activity => {
      expect(activity).toEqual([]);
      done();
    });

    const req = httpMock.expectOne(req => req.url.includes('/user-activity'));
    req.flush([]);
  });
});

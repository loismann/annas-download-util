import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { AuthService, buildCoverCookie, COVER_COOKIE_NAME, COVER_COOKIE_PATH } from './auth.service';
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

  describe('Token Management and JWT Decoding', () => {
    it('should decode valid JWT and extract userId from nameid claim', (done) => {
      // Create a valid JWT with nameid claim
      const payload = { nameid: 'user-123', exp: Math.floor(Date.now() / 1000) + 3600 };
      const encodedPayload = btoa(JSON.stringify(payload));
      const fakeJwt = `header.${encodedPayload}.signature`;

      const mockResponse = {
        token: fakeJwt,
        name: 'Test User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('test-code').subscribe(() => {
        expect(service.getUserId()).toBe('user-123');
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });

    it('should extract userId from full claim URI', (done) => {
      // Some JWTs use the full claim URI
      const payload = {
        'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': 'user-456'
      };
      const encodedPayload = btoa(JSON.stringify(payload));
      const fakeJwt = `header.${encodedPayload}.signature`;

      const mockResponse = {
        token: fakeJwt,
        name: 'Test User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('test-code').subscribe(() => {
        expect(service.getUserId()).toBe('user-456');
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });

    it('should return null userId for invalid JWT format', () => {
      // Manually set an invalid token
      localStorage.setItem('auth_token', 'invalid-token-without-dots');

      expect(service.getUserId()).toBeNull();
    });

    it('should return null userId for JWT with invalid base64', () => {
      // JWT with invalid base64 in payload
      localStorage.setItem('auth_token', 'header.!!!invalid-base64!!!.signature');

      expect(service.getUserId()).toBeNull();
    });

    it('should return null userId for JWT without nameid claim', (done) => {
      // JWT with payload that lacks nameid
      const payload = { sub: 'some-subject', name: 'User' };
      const encodedPayload = btoa(JSON.stringify(payload));
      const fakeJwt = `header.${encodedPayload}.signature`;

      const mockResponse = {
        token: fakeJwt,
        name: 'Test User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('test-code').subscribe(() => {
        expect(service.getUserId()).toBeNull();
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });

    it('should return null userId when no token exists', () => {
      expect(service.getToken()).toBeNull();
      expect(service.getUserId()).toBeNull();
    });
  });

  /**
   * The cookie that lets `<img src>` fetch an ebook cover. Its name and path are
   * a wire contract with `ServiceConfiguration.LibraryCoverCookieName` /
   * `.LibraryCoverCookiePath`; a mismatch does not throw anywhere, it just turns
   * every cover into the placeholder, so it is pinned on both sides.
   *
   * These assert the cookie *string*, not a write-then-read round trip. The
   * cookie is scoped to `/api/library/cover` and the Karma page is served at
   * `/`, so `document.cookie` cannot see it — which is the path scoping working
   * as intended, not a bug to work around.
   */
  describe('Ebook cover cookie', () => {
    it('carries the token under the name the backend reads', () => {
      expect(buildCoverCookie('abc.def.ghi', false))
        .toContain(`${COVER_COOKIE_NAME}=abc.def.ghi`);
    });

    it('url-encodes a token containing cookie-delimiter characters', () => {
      expect(buildCoverCookie('a;b c', false)).toContain('a%3Bb%20c');
    });

    // The scope is what stops this being a second ambient credential: without it
    // the browser would attach the token to every request in the app.
    it('scopes the cookie to the cover route', () => {
      expect(buildCoverCookie('t', false)).toContain(`path=${COVER_COOKIE_PATH}`);
      expect(COVER_COOKIE_PATH).toBe('/api/library/cover');
    });

    it('is SameSite=Strict', () => {
      expect(buildCoverCookie('t', false)).toContain('SameSite=Strict');
    });

    // Local dev runs over plain http, where a Secure cookie would be dropped
    // silently and every cover would 404 to the placeholder.
    it('sets Secure only over https', () => {
      expect(buildCoverCookie('t', true)).toContain('Secure');
      expect(buildCoverCookie('t', false)).not.toContain('Secure');
    });

    // An expiry has to repeat path and name exactly or the browser treats it as
    // a different cookie and leaves the original in place.
    it('expires with the same name and path it was written under', () => {
      const expired = buildCoverCookie('', false, true);

      expect(expired).toContain('Max-Age=0');
      expect(expired).toContain(`${COVER_COOKIE_NAME}=`);
      expect(expired).toContain(`path=${COVER_COOKIE_PATH}`);
    });

    it('does not set Max-Age on a normal write', () => {
      expect(buildCoverCookie('t', false)).not.toContain('Max-Age');
    });
  });

  describe('Token Persistence', () => {
    it('should persist token across service re-instantiation', (done) => {
      const mockResponse = {
        token: 'persistent-token',
        name: 'Persistent User',
        isAdmin: true,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('test-code').subscribe(() => {
        // Verify token is stored
        expect(localStorage.getItem('auth_token')).toBe('persistent-token');
        expect(localStorage.getItem('auth_name')).toBe('Persistent User');
        expect(localStorage.getItem('auth_admin')).toBe('true');

        // Create new service instance (simulating page refresh)
        const newService = TestBed.inject(AuthService);
        expect(newService.getToken()).toBe('persistent-token');
        expect(newService.getName()).toBe('Persistent User');
        expect(newService.isAdmin()).toBe(true);
        expect(newService.isAuthenticated()).toBe(true);
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });

    it('should start authenticated if token exists in localStorage', () => {
      // Pre-populate localStorage
      localStorage.setItem('auth_token', 'existing-token');
      localStorage.setItem('auth_name', 'Existing User');
      localStorage.setItem('auth_admin', 'false');

      // Create new service instance
      const newService = TestBed.inject(AuthService);
      expect(newService.isAuthenticated()).toBe(true);
      expect(newService.getName()).toBe('Existing User');
    });

    it('should clear all auth data on logout', (done) => {
      const mockResponse = {
        token: 'token-to-clear',
        name: 'User',
        isAdmin: true,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('test-code').subscribe(() => {
        expect(localStorage.getItem('auth_token')).toBe('token-to-clear');

        service.logout();

        expect(localStorage.getItem('auth_token')).toBeNull();
        expect(localStorage.getItem('auth_name')).toBeNull();
        expect(localStorage.getItem('auth_admin')).toBeNull();
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });
  });

  describe('Login Error Handling', () => {
    it('should not store token on login failure', (done) => {
      service.login('invalid-code').subscribe({
        error: (err) => {
          expect(service.getToken()).toBeNull();
          expect(service.isAuthenticated()).toBe(false);
          done();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush({ error: 'Invalid code' }, { status: 401, statusText: 'Unauthorized' });
    });

    it('should handle server error during login', (done) => {
      service.login('test-code').subscribe({
        error: (err) => {
          expect(err.status).toBe(500);
          expect(service.isAuthenticated()).toBe(false);
          done();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush('Server Error', { status: 500, statusText: 'Internal Server Error' });
    });

    it('should handle network error during login', (done) => {
      service.login('test-code').subscribe({
        error: (err) => {
          expect(service.isAuthenticated()).toBe(false);
          done();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.error(new ProgressEvent('Network error'));
    });
  });

  describe('Authentication State Observable', () => {
    it('should emit false then true on login', (done) => {
      const emissions: boolean[] = [];

      service.isAuthenticated$.subscribe(isAuth => {
        emissions.push(isAuth);
        if (emissions.length === 2) {
          expect(emissions).toEqual([false, true]);
          done();
        }
      });

      service.login('test-code').subscribe();
      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush({
        token: 'token',
        name: 'User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      });
    });

    it('should emit false on logout', (done) => {
      const emissions: boolean[] = [];

      service.login('test-code').subscribe(() => {
        service.isAuthenticated$.subscribe(isAuth => {
          emissions.push(isAuth);
          if (emissions.length === 2 && emissions[1] === false) {
            expect(emissions).toEqual([true, false]);
            done();
          }
        });

        service.logout();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush({
        token: 'token',
        name: 'User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      });
    });

    it('should maintain observable state across multiple logins', (done) => {
      const emissions: boolean[] = [];

      service.isAuthenticated$.subscribe(isAuth => {
        emissions.push(isAuth);
      });

      // First login
      service.login('code-1').subscribe(() => {
        // Logout
        service.logout();

        // Second login
        service.login('code-2').subscribe(() => {
          expect(emissions).toEqual([false, true, false, true]);
          done();
        });

        const req2 = httpMock.expectOne(req => req.url.includes('/login'));
        req2.flush({
          token: 'token-2',
          name: 'User 2',
          isAdmin: false,
          expiresAt: '2025-12-31T00:00:00Z'
        });
      });

      const req1 = httpMock.expectOne(req => req.url.includes('/login'));
      req1.flush({
        token: 'token-1',
        name: 'User 1',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      });
    });
  });

  describe('Admin Status Edge Cases', () => {
    it('should default isAdmin to false when not logged in', () => {
      expect(service.isAdmin()).toBe(false);
    });

    it('should correctly track non-admin user', (done) => {
      const mockResponse = {
        token: 'token',
        name: 'Regular User',
        isAdmin: false,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('code').subscribe(() => {
        expect(service.isAdmin()).toBe(false);
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });

    it('should lose admin status after logout', (done) => {
      const mockResponse = {
        token: 'token',
        name: 'Admin User',
        isAdmin: true,
        expiresAt: '2025-12-31T00:00:00Z'
      };

      service.login('code').subscribe(() => {
        expect(service.isAdmin()).toBe(true);

        service.logout();

        expect(service.isAdmin()).toBe(false);
        done();
      });

      const req = httpMock.expectOne(req => req.url.includes('/login'));
      req.flush(mockResponse);
    });
  });

  describe('User Activity Error Handling', () => {
    it('should handle 401 error for user activity', (done) => {
      service.getUserActivity().subscribe({
        error: (err) => {
          expect(err.status).toBe(401);
          done();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/user-activity'));
      req.flush({ error: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    });

    it('should handle 500 error for user activity', (done) => {
      service.getUserActivity().subscribe({
        error: (err) => {
          expect(err.status).toBe(500);
          done();
        }
      });

      const req = httpMock.expectOne(req => req.url.includes('/user-activity'));
      req.flush('Server Error', { status: 500, statusText: 'Internal Server Error' });
    });
  });
});

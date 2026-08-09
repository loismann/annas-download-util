import { Injectable, PLATFORM_ID, Inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import { apiBase } from './api-base';

export interface LoginResponse {
  token: string;
  name: string;
  isAdmin: boolean;
  expiresAt: string;
}

export interface UserActivity {
  initial: string;
  userName: string;
  minutesAgo: number | null;
  isFullTone: boolean;   // Active within 30 min - full color
  isHalfTone: boolean;   // Active 30-60 min - half-toned
  lastAction: string | null;       // Broad category of their last request, e.g. "Reading a book"
  activeForMinutes: number | null; // How long their current unbroken activity streak has run
}

/**
 * Name and path of the ebook-cover cookie. A wire contract with
 * `ServiceConfiguration.LibraryCoverCookieName` / `.LibraryCoverCookiePath`.
 */
export const COVER_COOKIE_NAME = 'annas_cover_token';
export const COVER_COOKIE_PATH = '/api/library/cover';

/**
 * Builds the `document.cookie` string for the ebook-cover token.
 *
 * Separated from the assignment purely so it can be tested. It cannot be checked
 * by writing the cookie and reading it back: the cookie is scoped to
 * `/api/library/cover`, and a page served at `/` — which is every page, and the
 * Karma runner — is outside that path, so `document.cookie` will not show it.
 * That invisibility *is* the path scoping doing its job, so the thing worth
 * asserting is the string, not the round trip.
 */
export function buildCoverCookie(token: string, secure: boolean, expire = false): string {
  const attributes = [
    `path=${COVER_COOKIE_PATH}`,
    'SameSite=Strict',
    ...(secure ? ['Secure'] : []),
    ...(expire ? ['Max-Age=0'] : [])
  ];
  return `${COVER_COOKIE_NAME}=${encodeURIComponent(token)}; ${attributes.join('; ')}`;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  // No port override: the API listens on 5001 in local dev, which is also what
  // proxy.conf.json targets and what every other service resolves to. The 5050
  // here pointed local dev's login at nothing.
  private readonly baseUrl = apiBase() + '/api/auth';
  private readonly TOKEN_KEY = 'auth_token';
  private readonly NAME_KEY = 'auth_name';
  private readonly ADMIN_KEY = 'auth_admin';
  private isBrowser: boolean;

  private isAuthenticatedSubject!: BehaviorSubject<boolean>;
  public isAuthenticated$!: Observable<boolean>;

  constructor(
    private http: HttpClient,
    @Inject(PLATFORM_ID) platformId: Object,
    private logger: LoggerService
  ) {
    this.isBrowser = isPlatformBrowser(platformId);
    // Initialize AFTER isBrowser is set
    this.isAuthenticatedSubject = new BehaviorSubject<boolean>(this.hasToken());
    this.isAuthenticated$ = this.isAuthenticatedSubject.asObservable();

    // Tokens last 30 days, so almost every session live at the moment the cover
    // cookie shipped has a token in localStorage and no cookie. Re-writing it
    // here means those sessions heal on next page load instead of showing a grid
    // of placeholders until someone thinks to log out and back in.
    const existing = this.getToken();
    if (existing) this.writeCoverCookie(existing);
  }

  login(code: string): Observable<LoginResponse> {
    return this.http
      .post<LoginResponse>(`${this.baseUrl}/login`, { code })
      .pipe(
        tap(response => {
          this.setToken(response.token);
          this.setName(response.name);
          this.setAdmin(response.isAdmin);
          this.isAuthenticatedSubject.next(true);
        })
      );
  }

  logout(): void {
    this.clearToken();
    this.clearName();
    this.clearAdmin();
    this.isAuthenticatedSubject.next(false);
  }

  getToken(): string | null {
    if (!this.isBrowser) return null;
    return localStorage.getItem(this.TOKEN_KEY);
  }

  getName(): string | null {
    if (!this.isBrowser) return null;
    return localStorage.getItem(this.NAME_KEY);
  }

  isAuthenticated(): boolean {
    return this.hasToken();
  }

  private setToken(token: string): void {
    if (!this.isBrowser) return;
    localStorage.setItem(this.TOKEN_KEY, token);
    this.writeCoverCookie(token);
  }

  /**
   * Mirrors the bearer token into a cookie scoped to the ebook-cover route.
   *
   * `<img src>` cannot carry an Authorization header, and unlike audiobook or
   * photo-print thumbnails — whose URLs are assembled in TypeScript and can take
   * `?access_token=` — a library cover URL is minted server-side and handed out
   * inside a cached DTO. A cookie is the only mechanism that reaches it.
   *
   * `path` is what keeps this from widening anything: the browser sends this
   * cookie to `/api/library/cover/...` and nowhere else, so it is not a second
   * ambient credential. It is the same JWT already in localStorage, so it grants
   * nothing that value did not already grant.
   *
   * Not `HttpOnly` — it could not be written from here if it were, and the token
   * is readable from localStorage regardless, so that flag would buy nothing.
   * `Secure` is conditional because local dev runs over plain http on localhost.
   */
  private writeCoverCookie(token: string): void {
    document.cookie = buildCoverCookie(token, location.protocol === 'https:');
  }

  private clearCoverCookie(): void {
    document.cookie = buildCoverCookie('', false, true);
  }

  private setName(name: string): void {
    if (!this.isBrowser) return;
    localStorage.setItem(this.NAME_KEY, name);
  }

  private clearToken(): void {
    if (!this.isBrowser) return;
    localStorage.removeItem(this.TOKEN_KEY);
    this.clearCoverCookie();
  }

  private clearName(): void {
    if (!this.isBrowser) return;
    localStorage.removeItem(this.NAME_KEY);
  }

  private setAdmin(isAdmin: boolean): void {
    if (!this.isBrowser) return;
    localStorage.setItem(this.ADMIN_KEY, isAdmin.toString());
  }

  private clearAdmin(): void {
    if (!this.isBrowser) return;
    localStorage.removeItem(this.ADMIN_KEY);
  }

  isAdmin(): boolean {
    if (!this.isBrowser) return false;
    return localStorage.getItem(this.ADMIN_KEY) === 'true';
  }

  getUserId(): string | null {
    const token = this.getToken();
    if (!token) return null;

    try {
      // Decode JWT (it's base64 encoded, split by '.')
      const payload = token.split('.')[1];
      const decodedPayload = JSON.parse(atob(payload));
      // The userId is stored in the 'nameid' claim (ClaimTypes.NameIdentifier)
      return decodedPayload.nameid || decodedPayload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] || null;
    } catch (e) {
      this.logger.error('Failed to decode JWT token:', e);
      return null;
    }
  }

  private hasToken(): boolean {
    return !!this.getToken();
  }

  getUserActivity(): Observable<UserActivity[]> {
    return this.http.get<UserActivity[]>(`${this.baseUrl}/user-activity`);
  }

  /** Resolves the current session to one of the three household owner tags,
   * for library pages to default-filter to "your own stuff" on load — mirrors
   * LibraryHelpers.ResolveUserDisplayName server-side (substring match on the
   * configured display name, e.g. "Boo! (Mom)" -> "Mom"). isAdmin is checked
   * first as a direct shortcut for Paul, rather than relying on his display
   * name happening to contain "paul". */
  getOwnerName(): 'Paul' | 'Mom' | 'Dad' | null {
    if (this.isAdmin()) return 'Paul';

    const name = this.getName();
    if (!name) return null;

    const normalized = name.trim().toLowerCase();
    if (normalized.includes('mom')) return 'Mom';
    if (normalized.includes('dad')) return 'Dad';
    if (normalized.includes('paul')) return 'Paul';
    return null;
  }
}

import { Injectable, PLATFORM_ID, Inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';

export interface LoginRequest {
  code: string;
}

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
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly isLocalDev = typeof window !== 'undefined' && window.location.hostname === 'localhost';
  private readonly baseUrl = this.isLocalDev
    ? 'http://localhost:5050/api/auth'
    : 'https://fs01pfbooks.synology.me:5051/api/auth';
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
  }

  private setName(name: string): void {
    if (!this.isBrowser) return;
    localStorage.setItem(this.NAME_KEY, name);
  }

  private clearToken(): void {
    if (!this.isBrowser) return;
    localStorage.removeItem(this.TOKEN_KEY);
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
}

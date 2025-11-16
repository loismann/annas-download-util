import { Injectable, PLATFORM_ID, Inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';
import { tap } from 'rxjs/operators';

export interface LoginRequest {
  code: string;
}

export interface LoginResponse {
  token: string;
  name: string;
  isAdmin: boolean;
  expiresAt: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly baseUrl = 'http://fs01pfbooks.synology.me:5050/api/auth';
  private readonly TOKEN_KEY = 'auth_token';
  private readonly NAME_KEY = 'auth_name';
  private readonly ADMIN_KEY = 'auth_admin';
  private isBrowser: boolean;

  private isAuthenticatedSubject = new BehaviorSubject<boolean>(this.hasToken());
  public isAuthenticated$ = this.isAuthenticatedSubject.asObservable();

  constructor(
    private http: HttpClient,
    @Inject(PLATFORM_ID) platformId: Object
  ) {
    this.isBrowser = isPlatformBrowser(platformId);
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

  private hasToken(): boolean {
    return !!this.getToken();
  }
}

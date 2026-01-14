import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';

/* ─────────────── gaming PC control response ─────────────────────── */
export interface GamingToggleResponse {
  success: boolean;
  action: string;
  message: string;
  output?: string;
  error?: string;
}

/* ─────────────── gaming PC status response ─────────────────────── */
export interface GamingStatusResponse {
  isOnline: boolean;
  ipAddress: string;
  lastChecked: string;
  error?: string;
}

/**
 * Service for gaming PC remote control.
 * Handles wake-on-LAN and sleep commands, and status checks.
 */
@Injectable({ providedIn: 'root' })
export class GamingApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : 'https://fs01pfbooks.synology.me:5051';
  private readonly gamingBaseUrl = `${this.apiHost}/api/gaming`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[GamingApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /**
   * Toggle gaming PC (wake/sleep).
   * @param action 1 = wake (WOL), 2 = sleep
   */
  toggleGamingPC(action: 1 | 2): Observable<GamingToggleResponse> {
    const params = new HttpParams().set('action', action.toString());
    return this.http.post<GamingToggleResponse>(
      `${this.gamingBaseUrl}/toggle`,
      null,
      { params }
    );
  }

  /**
   * Check gaming PC status (online/offline).
   */
  getGamingPCStatus(): Observable<GamingStatusResponse> {
    return this.http.get<GamingStatusResponse>(
      `${this.gamingBaseUrl}/status`
    );
  }
}

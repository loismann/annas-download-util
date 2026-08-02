import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import {
  SpotifySearchResult,
  SpotifyPlaylist,
  SpotifyPlaylistItemsPage,
  SpotifyConnectionStatus,
  SpotifyAuthorizeResponse,
  SpotifyInventoryStatus,
  SpotifyLibraryAnalysis,
  SpotifyKnownMusicReport,
  SpotifyKnownMusicOverrideResult,
  SpotifyDiscoveryDraft,
  SpotifyDiscoveryDraftUpdate,
  CommandResponse,
  SpotifyPlan,
  SpotifyAuditEvent
} from '../spotifinator/spotifinator.models';
import { apiBase } from './api-base';

@Injectable({ providedIn: 'root' })
export class SpotifinatorApiService {
  private readonly apiHost = apiBase();
  private readonly baseUrl = `${this.apiHost}/api/spotify`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {}

  // ─── Direct API Calls ──────────────────────────────────────────────────────

  getConnection(): Observable<SpotifyConnectionStatus> {
    return this.http.get<SpotifyConnectionStatus>(`${this.baseUrl}/connection`);
  }

  beginAuthorization(forceDialog = false): Observable<SpotifyAuthorizeResponse> {
    return this.http.post<SpotifyAuthorizeResponse>(`${this.baseUrl}/connection/authorize`, {
      forceDialog
    });
  }

  disconnect(): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/connection`);
  }

  searchTracks(query: string, limit = 10): Observable<SpotifySearchResult> {
    return this.http.get<SpotifySearchResult>(`${this.baseUrl}/search`, {
      params: { q: query, limit: limit.toString() }
    }).pipe(
      tap(result => this.logger.log('[Spotifinator] Search results', { query, count: result.tracks.length }))
    );
  }

  getPlaylists(): Observable<SpotifyPlaylist[]> {
    return this.http.get<SpotifyPlaylist[]>(`${this.baseUrl}/playlists`).pipe(
      tap(playlists => this.logger.log('[Spotifinator] Playlists loaded', { count: playlists.length }))
    );
  }

  getPlaylist(playlistId: string): Observable<SpotifyPlaylist> {
    return this.http.get<SpotifyPlaylist>(`${this.baseUrl}/playlists/${encodeURIComponent(playlistId)}`);
  }

  getPlaylistItems(playlistId: string, offset = 0, limit = 50): Observable<SpotifyPlaylistItemsPage> {
    return this.http.get<SpotifyPlaylistItemsPage>(
      `${this.baseUrl}/playlists/${encodeURIComponent(playlistId)}/items`,
      { params: { offset: offset.toString(), limit: limit.toString() } }
    ).pipe(
      tap(page => this.logger.log('[Spotifinator] Playlist items loaded', {
        playlistId, count: page.items.length, access: page.access
      }))
    );
  }

  startInventoryRefresh(): Observable<SpotifyInventoryStatus> {
    return this.http.post<SpotifyInventoryStatus>(`${this.baseUrl}/inventory/refresh`, {});
  }

  getInventoryStatus(): Observable<SpotifyInventoryStatus> {
    return this.http.get<SpotifyInventoryStatus>(`${this.baseUrl}/inventory/status`);
  }

  getAnalysis(): Observable<SpotifyLibraryAnalysis> {
    return this.http.get<SpotifyLibraryAnalysis>(`${this.baseUrl}/analysis`);
  }

  getKnownMusic(): Observable<SpotifyKnownMusicReport> {
    return this.http.get<SpotifyKnownMusicReport>(`${this.baseUrl}/known-music`);
  }

  setKnownMusicOverride(
    kind: 'artist' | 'track', name: string, known: boolean, artist?: string
  ): Observable<SpotifyKnownMusicOverrideResult> {
    return this.http.put<SpotifyKnownMusicOverrideResult>(`${this.baseUrl}/known-music/override`, {
      kind, name, known, artist
    });
  }

  getDiscoveryDraft(draftId: string): Observable<SpotifyDiscoveryDraft> {
    return this.http.get<SpotifyDiscoveryDraft>(`${this.baseUrl}/drafts/${encodeURIComponent(draftId)}`);
  }

  getSavedDiscoveryDrafts(): Observable<SpotifyDiscoveryDraft[]> {
    return this.http.get<SpotifyDiscoveryDraft[]>(`${this.baseUrl}/drafts`);
  }

  updateDiscoveryDraft(
    draftId: string, update: SpotifyDiscoveryDraftUpdate
  ): Observable<SpotifyDiscoveryDraft> {
    return this.http.patch<SpotifyDiscoveryDraft>(
      `${this.baseUrl}/drafts/${encodeURIComponent(draftId)}`, update);
  }

  // ─── Change plans ──────────────────────────────────────────────────────────

  getPlan(planId: string): Observable<SpotifyPlan> {
    return this.http.get<SpotifyPlan>(`${this.baseUrl}/plans/${planId}`);
  }

  /**
   * Executes the plan. `highImpactAcknowledged` is a separate flag rather than
   * part of the plan itself so that a replace or merge cannot be confirmed by the
   * same click that confirms an ordinary add.
   */
  confirmPlan(planId: string, highImpactAcknowledged = false): Observable<SpotifyPlan> {
    return this.http.post<SpotifyPlan>(`${this.baseUrl}/plans/${planId}/confirm`, {
      highImpactAcknowledged
    }).pipe(
      tap(plan => this.logger.log('[Spotifinator] Plan executed', { planId, status: plan.status }))
    );
  }

  cancelPlan(planId: string): Observable<SpotifyPlan> {
    return this.http.post<SpotifyPlan>(`${this.baseUrl}/plans/${planId}/cancel`, {});
  }

  /** Returns a *new* plan that still needs confirming — undo is reviewed too. */
  undoPlan(planId: string): Observable<SpotifyPlan> {
    return this.http.post<SpotifyPlan>(`${this.baseUrl}/plans/${planId}/undo`, {});
  }

  getAudit(planId?: string, limit = 100): Observable<SpotifyAuditEvent[]> {
    const params: Record<string, string> = { limit: limit.toString() };
    if (planId) params['planId'] = planId;
    return this.http.get<SpotifyAuditEvent[]>(`${this.baseUrl}/audit`, { params });
  }

  // ─── Conversation ──────────────────────────────────────────────────────────

  /**
   * `playlistId` pins a playlist the user picked from a disambiguation card, so
   * the next turn does not re-ask which "Chill" they meant.
   */
  processCommand(
    userMessage: string, playlistId?: string, offset?: number, draftId?: string
  ): Observable<CommandResponse> {
    return this.http.post<CommandResponse>(`${this.baseUrl}/command`, {
      message: userMessage,
      playlistId,
      offset,
      draftId
    }).pipe(
      tap(response => this.logger.log('[Spotifinator] Command processed', {
        action: response.action,
        confidence: response.confidence
      }))
    );
  }
}

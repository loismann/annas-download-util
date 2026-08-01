import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import {
  SpotifySearchResult,
  SpotifyPlaylist,
  SpotifyConnectionStatus,
  SpotifyAuthorizeResponse,
  CommandResponse
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

  createPlaylist(name: string, description?: string, isPublic = false): Observable<SpotifyPlaylist> {
    return this.http.post<SpotifyPlaylist>(`${this.baseUrl}/playlists`, {
      name,
      description,
      public: isPublic
    }).pipe(
      tap(playlist => this.logger.log('[Spotifinator] Playlist created', { id: playlist.id, name: playlist.name }))
    );
  }

  addTracksToPlaylist(playlistId: string, trackUris: string[]): Observable<{ success: boolean; added: number }> {
    return this.http.post<{ success: boolean; added: number }>(
      `${this.baseUrl}/playlists/${playlistId}/items`,
      { playlistId, trackUris }
    ).pipe(
      tap(result => this.logger.log('[Spotifinator] Tracks added', { playlistId, count: result.added }))
    );
  }

  removeTracksFromPlaylist(playlistId: string, trackUris: string[]): Observable<{ success: boolean; removed: number }> {
    return this.http.request<{ success: boolean; removed: number }>(
      'DELETE',
      `${this.baseUrl}/playlists/${playlistId}/items`,
      { body: { playlistId, trackUris } }
    ).pipe(
      tap(result => this.logger.log('[Spotifinator] Tracks removed', { playlistId, count: result.removed }))
    );
  }

  // ─── AI Command Processing ─────────────────────────────────────────────────

  processCommand(userMessage: string, conversationContext?: string): Observable<CommandResponse> {
    return this.http.post<CommandResponse>(`${this.baseUrl}/command`, {
      message: userMessage,
      context: conversationContext
    }).pipe(
      tap(response => this.logger.log('[Spotifinator] Command processed', {
        action: response.parsed.action,
        confidence: response.parsed.confidence
      }))
    );
  }
}

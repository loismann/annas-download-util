import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';
import {
  SpotifySearchResult,
  SpotifyPlaylist,
  CommandResponse
} from '../spotifinator/spotifinator.models';

@Injectable({ providedIn: 'root' })
export class SpotifinatorApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : '';
  private readonly baseUrl = `${this.apiHost}/api/spotify`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {}

  // ─── Direct API Calls ──────────────────────────────────────────────────────

  searchTracks(query: string, limit = 20): Observable<SpotifySearchResult> {
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
      `${this.baseUrl}/playlists/${playlistId}/tracks`,
      { playlistId, trackUris }
    ).pipe(
      tap(result => this.logger.log('[Spotifinator] Tracks added', { playlistId, count: result.added }))
    );
  }

  removeTracksFromPlaylist(playlistId: string, trackUris: string[]): Observable<{ success: boolean; removed: number }> {
    return this.http.request<{ success: boolean; removed: number }>(
      'DELETE',
      `${this.baseUrl}/playlists/${playlistId}/tracks`,
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

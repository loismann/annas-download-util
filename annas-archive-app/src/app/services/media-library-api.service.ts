import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MediaLookupResult } from './media-search-api.service';

export interface EpisodeInfo {
  id: number;
  seasonNumber: number;
  episodeNumber: number;
  title?: string;
  hasFile?: boolean;
  airDate?: string;
}

export interface WatchResponse {
  embedUrl: string;
}

/**
 * Client for "what's downloaded, how do I watch it" — distinct from
 * MediaSearchApiService (search/add). Sonarr/Radarr are the source of truth
 * for download status (hasFile per episode/movie); Jellyfin is only asked
 * at watch-time to resolve a playable embed URL. See MediaLibraryEndpoints.cs.
 */
@Injectable({ providedIn: 'root' })
export class MediaLibraryApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev ? 'http://localhost:5001' : '';
  private readonly baseUrl = `${this.apiHost}/api/media`;

  constructor(private http: HttpClient) {}

  getDownloadedTv(): Observable<MediaLookupResult[]> {
    return this.http.get<MediaLookupResult[]>(`${this.baseUrl}/tv/downloaded`);
  }

  getSeriesEpisodes(seriesId: number): Observable<EpisodeInfo[]> {
    return this.http.get<EpisodeInfo[]>(`${this.baseUrl}/tv/${seriesId}/episodes`);
  }

  watchTv(tvdbId: number, season: number, episode: number): Observable<WatchResponse> {
    const params = new HttpParams()
      .set('tvdbId', tvdbId.toString())
      .set('season', season.toString())
      .set('episode', episode.toString());
    return this.http.get<WatchResponse>(`${this.baseUrl}/tv/watch`, { params });
  }

  getDownloadedMovies(): Observable<MediaLookupResult[]> {
    return this.http.get<MediaLookupResult[]>(`${this.baseUrl}/movies/downloaded`);
  }

  watchMovie(tmdbId: number): Observable<WatchResponse> {
    const params = new HttpParams().set('tmdbId', tmdbId.toString());
    return this.http.get<WatchResponse>(`${this.baseUrl}/movies/watch`, { params });
  }

  /** Removes the whole series from Sonarr and deletes all its files. */
  deleteSeries(seriesId: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/tv/${seriesId}`);
  }

  /** Deletes just one season's files, leaving the rest of the series intact. */
  deleteSeason(seriesId: number, seasonNumber: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/tv/${seriesId}/season/${seasonNumber}`);
  }

  /** Removes the movie from Radarr entirely and deletes its file. */
  deleteMovie(movieId: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/movies/${movieId}`);
  }
}

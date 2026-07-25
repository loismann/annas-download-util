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

/** One release Radarr/Sonarr's indexers found for a movie/season — raw
 * passthrough from their own interactive-search response, so "rejections"
 * (why the quality profile would normally skip this one) and "rejected"
 * ride along untouched for the picker UI to explain the choice to the user. */
export interface ReleaseInfo {
  guid: string;
  indexerId: number;
  title: string;
  size: number;
  indexer?: string;
  protocol?: 'torrent' | 'usenet';
  seeders?: number;
  leechers?: number;
  ageHours?: number;
  rejected?: boolean;
  rejections?: string[];
  quality?: { quality?: { name?: string } };
  [key: string]: unknown;
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

  /** Full replace of a downloaded show's owners + genre tags. */
  setTvMetadata(seriesId: number, owners: string[], genres: string[]): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/tv/${seriesId}/metadata`, { owners, genres });
  }

  /** Full replace of a downloaded movie's owners + genre tags. */
  setMovieMetadata(movieId: number, owners: string[], genres: string[]): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/movies/${movieId}/metadata`, { owners, genres });
  }

  /** Favorite/unfavorite a show on behalf of whoever's logged in — the acting owner is
   *  resolved server-side from the session, not passed from here. */
  setTvFavorite(seriesId: number, favorited: boolean): Observable<{ success: boolean; favorites: string[] }> {
    return this.http.post<{ success: boolean; favorites: string[] }>(`${this.baseUrl}/tv/${seriesId}/favorite`, { favorited });
  }

  /** Favorite/unfavorite a movie on behalf of whoever's logged in — same semantics as setTvFavorite. */
  setMovieFavorite(movieId: number, favorited: boolean): Observable<{ success: boolean; favorites: string[] }> {
    return this.http.post<{ success: boolean; favorites: string[] }>(`${this.baseUrl}/movies/${movieId}/favorite`, { favorited });
  }

  /** Radarr's own interactive search for a movie — includes releases its
   * quality profile would normally reject (e.g. too large), so the user can
   * grab one manually when nothing smaller is available. */
  searchMovieReleases(movieId: number): Observable<ReleaseInfo[]> {
    return this.http.get<ReleaseInfo[]>(`${this.baseUrl}/movies/${movieId}/releases`);
  }

  /** Force-grabs one specific release regardless of the quality profile's
   * normal rejections — pass back the exact object from searchMovieReleases. */
  grabMovieRelease(movieId: number, release: ReleaseInfo): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/movies/${movieId}/releases/grab`, release);
  }

  /** Sonarr's own interactive search for a season — same rejection info as
   * searchMovieReleases, scoped to one season rather than the whole series. */
  searchSeasonReleases(seriesId: number, seasonNumber: number): Observable<ReleaseInfo[]> {
    return this.http.get<ReleaseInfo[]>(`${this.baseUrl}/tv/${seriesId}/season/${seasonNumber}/releases`);
  }

  /** Force-grabs one specific season release — pass back the exact object
   * from searchSeasonReleases. */
  grabSeasonRelease(seriesId: number, seasonNumber: number, release: ReleaseInfo): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/tv/${seriesId}/season/${seasonNumber}/releases/grab`, release);
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';

/**
 * A Sonarr/Radarr lookup result, passed through from their APIs mostly
 * unmodified. Only a few fields are read directly here for display —
 * everything else rides along untouched and gets sent straight back to
 * addTvShow/addMovie, since Sonarr/Radarr's "add" endpoints expect the
 * exact object their own lookup endpoint returned.
 */
export interface MediaSeasonInfo {
  seasonNumber: number;
  monitored?: boolean;
  statistics?: { episodeCount?: number; totalEpisodeCount?: number; episodeFileCount?: number };
}

export interface MediaLookupResult {
  title: string;
  year?: number;
  overview?: string;
  images?: { coverType: string; url?: string; remoteUrl?: string }[];
  tvdbId?: number;
  tmdbId?: number;
  /** TV only — absent for movies. */
  seasons?: MediaSeasonInfo[];
  /** Only present after a successful add — Sonarr/Radarr's own record ID,
   * used to match this item against queue entries for progress polling. */
  id?: number;
  /** Who requested this (zero or more of "Paul"/"Mom"/"Dad") — seeded
   * server-side at add-time, editable afterward. See MediaMetadataService. */
  owners?: string[];
  /** User-created genre tags, independent of Sonarr/Radarr's own read-only
   * `genres` field (which rides along untouched via the index signature
   * below, since it's whatever TheTVDB/TMDB reports). */
  customGenres?: string[];
  [key: string]: unknown;
}

export interface MediaQueueItem {
  title?: string;
  status?: string;
  sizeleft?: number;
  size?: number;
  timeleft?: string;
  trackedDownloadStatus?: string;
  seriesId?: number;
  movieId?: number;
}

export interface MediaQueueResponse {
  tv: { records?: MediaQueueItem[] };
  movies: { records?: MediaQueueItem[] };
}

/**
 * Service for the TV/movie search-and-acquire page — a thin client for our
 * own backend's proxy in front of Sonarr and Radarr (see
 * MediaRequestEndpoints.cs). Mirrors BookSearchApiService's shape.
 */
@Injectable({ providedIn: 'root' })
export class MediaSearchApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev ? 'http://localhost:5001' : '';
  private readonly baseUrl = `${this.apiHost}/api/media`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {}

  searchTv(term: string): Observable<MediaLookupResult[]> {
    const params = new HttpParams().set('term', term);
    this.logger.log('[MediaSearchApiService] searchTv', { term });
    return this.http.get<MediaLookupResult[]>(`${this.baseUrl}/tv/search`, { params });
  }

  addTvShow(series: MediaLookupResult, selectedSeasons?: number[]): Observable<MediaLookupResult> {
    return this.http.post<MediaLookupResult>(`${this.baseUrl}/tv/add`, {
      series,
      selectedSeasons: selectedSeasons ?? null
    });
  }

  /** Every series already added in Sonarr — used to cross-reference search
   * results so already-requested shows/seasons don't look like a blank slate. */
  getTvLibrary(): Observable<MediaLookupResult[]> {
    return this.http.get<MediaLookupResult[]>(`${this.baseUrl}/tv/library`);
  }

  /** Adds seasons to a series that's already in Sonarr, instead of re-adding
   * it from scratch — merges with whatever's already monitored. */
  updateTvSeasons(seriesId: number, selectedSeasons: number[]): Observable<MediaLookupResult> {
    return this.http.post<MediaLookupResult>(`${this.baseUrl}/tv/update-seasons`, {
      seriesId,
      selectedSeasons
    });
  }

  searchMovies(term: string): Observable<MediaLookupResult[]> {
    const params = new HttpParams().set('term', term);
    this.logger.log('[MediaSearchApiService] searchMovies', { term });
    return this.http.get<MediaLookupResult[]>(`${this.baseUrl}/movies/search`, { params });
  }

  addMovie(movie: MediaLookupResult): Observable<MediaLookupResult> {
    return this.http.post<MediaLookupResult>(`${this.baseUrl}/movies/add`, movie);
  }

  getQueue(): Observable<MediaQueueResponse> {
    return this.http.get<MediaQueueResponse>(`${this.baseUrl}/queue`);
  }
}

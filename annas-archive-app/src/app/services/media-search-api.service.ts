import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';
import { apiBase } from './api-base';

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
  /** Which household member(s) have favorited this item ("Paul"/"Mom"/"Dad"). See MediaMetadataService. */
  favorites?: string[];
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

/** One row of a bulk-import list — Title+Year find the movie in Radarr,
 * Genres/Owner classify it once matched. Owner must be a household member
 * name (see HOUSEHOLD_OWNERS) — same constraint as the single-item editor. */
export interface BulkImportMovieRow {
  title: string;
  year: number | null;
  genres: string[];
  owner: string | null;
}

/** Status is one of: added, already-existed, not-found, ambiguous, invalid, error. */
export interface BulkImportMovieResult {
  title: string;
  year?: number;
  status: 'added' | 'already-existed' | 'not-found' | 'ambiguous' | 'invalid' | 'error';
  message?: string;
  movieId?: number;
}

/**
 * Service for the TV/movie search-and-acquire page — a thin client for our
 * own backend's proxy in front of Sonarr and Radarr (see
 * MediaRequestEndpoints.cs). Mirrors BookSearchApiService's shape.
 */
@Injectable({ providedIn: 'root' })
export class MediaSearchApiService {
  private readonly apiHost = apiBase();
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

  /** `dateNightPool` registers each movie as a catalog record only — unmonitored,
   * no search, tagged `date-night-pool` — so a long list can be added without any
   * of it downloading. See DOCS/features/DATE_NIGHT.md. */
  bulkImportMovies(rows: BulkImportMovieRow[], dateNightPool = false): Observable<BulkImportMovieResult[]> {
    this.logger.log('[MediaSearchApiService] bulkImportMovies', { count: rows.length, dateNightPool });
    return this.http.post<BulkImportMovieResult[]>(`${this.baseUrl}/movies/bulk-import`, { rows, dateNightPool });
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, BehaviorSubject, of } from 'rxjs';
import { tap } from 'rxjs/operators';
import { LoggerService } from './logger.service';

/* ─────────────── Video library response shapes ──────────────── */
export interface VideoDto {
  fileName: string;
  title: string;
  channel: string;
  duration: string;
  durationSeconds: number | null;
  format: string;
  resolution: string | null;
  fileSize: string;
  thumbnailUrl: string | null;
  description: string | null;
  primaryGenre: string | null;
  tags: string[];
  playlist: string | null;
  youTubeId: string | null;
  personalRating: number | null;
  bookmarked: boolean | null;
  downloadedAt: string | null;
  publishedAt: string | null;
}

export interface VideoMetadataUpdate {
  primaryGenre: string | null;
  tags: string[] | null;
  playlist: string | null;
  title: string | null;
  channel: string | null;
}

export interface VideoRatingsUpdate {
  personalRating?: number | null;
  bookmarked?: boolean | null;
}

export interface VideosPaginatedResponse {
  videos: VideoDto[];
  totalCount: number;
  skip: number;
  take: number;
}

/**
 * Service for video library operations.
 * Handles video listing, metadata management, and ratings.
 */
@Injectable({ providedIn: 'root' })
export class VideoLibraryApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : '';
  private readonly baseUrl = `${this.apiHost}/api/video-library`;

  // Client-side cache for videos
  private cachedVideos$ = new BehaviorSubject<VideoDto[] | null>(null);
  private cacheTimestamp: number | null = null;
  private readonly cacheMaxAgeMs = 5 * 60 * 1000; // 5 minutes

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {
    if (this.isLocalDev) {
      this.logger.log('[VideoLibraryApiService] LOCAL DEV MODE - Using localhost API endpoints');
    }
  }

  /**
   * Invalidate the client-side video cache.
   * Call this after any operation that modifies videos.
   */
  invalidateCache(): void {
    this.cachedVideos$.next(null);
    this.cacheTimestamp = null;
    this.logger.log('[VideoLibraryApiService] Cache invalidated');
  }

  /**
   * Check if cache is valid (exists and not expired).
   */
  private isCacheValid(): boolean {
    if (!this.cachedVideos$.value || !this.cacheTimestamp) {
      return false;
    }
    const age = Date.now() - this.cacheTimestamp;
    return age < this.cacheMaxAgeMs;
  }

  /* ══════════════════════════════════════════════════════════════
     VIDEO LIBRARY ENDPOINTS
     ══════════════════════════════════════════════════════════════ */

  /**
   * Get all videos in the library.
   * Uses client-side caching to avoid re-fetching on page navigation.
   */
  getVideos(): Observable<VideoDto[]> {
    // Return cached data if valid
    if (this.isCacheValid()) {
      this.logger.log('[VideoLibraryApiService] Returning cached videos', { count: this.cachedVideos$.value?.length });
      return of(this.cachedVideos$.value!);
    }

    // Fetch from server and update cache
    this.logger.log('[VideoLibraryApiService] Fetching videos from server');
    return this.http.get<VideoDto[]>(`${this.baseUrl}/videos`).pipe(
      tap(videos => {
        this.cachedVideos$.next(videos);
        this.cacheTimestamp = Date.now();
        this.logger.log('[VideoLibraryApiService] Cache updated', { count: videos.length });
      })
    );
  }

  /**
   * Get videos with pagination support.
   * Use this for initial load to get videos faster.
   */
  getVideosPaginated(
    skip = 0,
    take = 50,
    sortBy: 'title' | 'channel' | 'duration' | 'rating' | 'date' = 'date',
    sortDesc = true
  ): Observable<VideosPaginatedResponse> {
    const params = new HttpParams()
      .set('skip', skip.toString())
      .set('take', take.toString())
      .set('sortBy', sortBy)
      .set('sortDesc', sortDesc.toString());

    this.logger.log('[VideoLibraryApiService] Fetching paginated videos', { skip, take, sortBy, sortDesc });
    return this.http.get<VideosPaginatedResponse>(`${this.baseUrl}/videos`, { params });
  }

  /**
   * Update video metadata (genre, tags, playlist, title, channel).
   */
  updateVideoMetadata(fileName: string, metadata: VideoMetadataUpdate): Observable<{ success: boolean; message: string }> {
    return this.http.patch<{ success: boolean; message: string }>(
      `${this.baseUrl}/video/${encodeURIComponent(fileName)}/metadata`,
      metadata
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Update video ratings (personal rating and bookmark).
   */
  updateVideoRatings(fileName: string, ratings: VideoRatingsUpdate): Observable<{ success: boolean; message: string }> {
    return this.http.patch<{ success: boolean; message: string }>(
      `${this.baseUrl}/video/${encodeURIComponent(fileName)}/ratings`,
      ratings
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Delete a video from the library.
   */
  deleteVideo(fileName: string): Observable<{ success: boolean }> {
    return this.http.delete<{ success: boolean }>(
      `${this.baseUrl}/video/${encodeURIComponent(fileName)}`
    ).pipe(
      tap(() => this.invalidateCache())
    );
  }

  /**
   * Get thumbnail URL for a video.
   */
  getThumbnailUrl(path: string): string {
    if (!path) return '';
    if (path.startsWith('http')) return path;
    return `${this.baseUrl}/thumbnail/${encodeURIComponent(path)}`;
  }

  /**
   * Get video stream URL for in-browser playback.
   */
  getVideoStreamUrl(fileName: string): string {
    return `${this.baseUrl}/video/${encodeURIComponent(fileName)}/stream`;
  }
}

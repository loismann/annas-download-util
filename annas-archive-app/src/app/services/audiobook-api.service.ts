import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuthService } from './auth.service';

export interface AudiobookChapter {
  id: number;
  start: number;
  end: number;
  title: string;
}

export interface AudiobookAudioFile {
  ino: string;
  duration?: number;
}

/** Confirmed against a live Audiobookshelf instance (2.35.1) — the catalog
 *  list endpoint only returns numTracks/numAudioFiles/numChapters (counts),
 *  NOT the audioFiles/chapters arrays themselves; those only appear on the
 *  single-item detail endpoint (?expanded=1), which is why AudiobooksComponent
 *  fetches full detail before opening the player rather than using the list
 *  item directly. */
export interface AudiobookMediaMetadata {
  title?: string;
  subtitle?: string;
  authorName?: string;
  narratorName?: string;
  seriesName?: string;
  publishedYear?: string;
  description?: string;
}

export interface AudiobookMedia {
  metadata: AudiobookMediaMetadata;
  coverPath?: string | null;
  duration?: number;
  numAudioFiles?: number;
  numChapters?: number;
  audioFiles?: AudiobookAudioFile[]; // only present on the detail (?expanded=1) response
  chapters?: AudiobookChapter[];      // only present on the detail (?expanded=1) response
}

/** Raw Audiobookshelf item, merged server-side with our own
 *  owners/customGenres/favorites/progress — see AudiobookLibraryEndpoints.cs.
 *  Audiobookshelf nests almost everything under `media` (media.metadata.title,
 *  media.audioFiles, etc.) rather than flat top-level fields — mirrors how
 *  MediaLookupResult (TV/movies) is raw Sonarr/Radarr passthrough rather than
 *  a normalized shape; use the helper functions in audiobooks.component.ts
 *  (titleOf/authorOf/etc.) instead of assuming flat fields exist. */
export interface AudiobookItem {
  id: string;
  path?: string;
  isMissing?: boolean;
  media: AudiobookMedia;
  owners: string[];
  customGenres: string[];
  favorites: string[];
  progress?: Record<string, number>;
  [key: string]: unknown;
}

/**
 * Client for the Audiobooks library — same "query the specialized tool's
 * catalog, merge in our own owners/genres/favorites" shape as
 * MediaLibraryApiService (TV/movies via Sonarr/Radarr), backed by
 * Audiobookshelf instead. Kept separate from MediaLibraryApiService since
 * that one is tightly scoped to Sonarr/Radarr concepts (release search/grab,
 * episode/queue polling) that don't apply here.
 */
@Injectable({ providedIn: 'root' })
export class AudiobookApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev ? 'http://localhost:5001' : '';
  private readonly baseUrl = `${this.apiHost}/api/audiobooks`;

  constructor(private http: HttpClient, private authService: AuthService) {}

  getCatalog(): Observable<AudiobookItem[]> {
    return this.http.get<AudiobookItem[]>(this.baseUrl);
  }

  getItem(id: string): Observable<AudiobookItem> {
    return this.http.get<AudiobookItem>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  /** Full replace of an audiobook's owners + genre tags. */
  setMetadata(id: string, owners: string[], genres: string[]): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/${encodeURIComponent(id)}/metadata`, { owners, genres });
  }

  /** Favorite/unfavorite on behalf of whoever's logged in — the acting owner
   *  is resolved server-side from the session, not passed from here. */
  setFavorite(id: string, favorited: boolean): Observable<{ success: boolean; favorites: string[] }> {
    return this.http.post<{ success: boolean; favorites: string[] }>(`${this.baseUrl}/${encodeURIComponent(id)}/favorite`, { favorited });
  }

  /** Saves the current playback position for whoever's logged in, so
   *  reopening the book later resumes where they left off. */
  saveProgress(id: string, positionSeconds: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${encodeURIComponent(id)}/progress`, { positionSeconds });
  }

  /** Same-origin stream URL for a native <audio> element — includes the
   *  session token as a query param since <audio src> can't carry an
   *  Authorization header (see the OnMessageReceived handler in
   *  ServiceConfiguration.cs, scoped to just this route shape). */
  getStreamUrl(id: string, ino: string): string {
    const token = this.authService.getToken() ?? '';
    return `${this.baseUrl}/${encodeURIComponent(id)}/stream/${encodeURIComponent(ino)}?access_token=${encodeURIComponent(token)}`;
  }

  /** Same-origin cover URL for a plain <img src>, same token-param reasoning as getStreamUrl. */
  getCoverUrl(id: string): string {
    const token = this.authService.getToken() ?? '';
    return `${this.baseUrl}/${encodeURIComponent(id)}/cover?access_token=${encodeURIComponent(token)}`;
  }
}

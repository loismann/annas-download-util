import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuthService } from './auth.service';
import { apiBase } from './api-base';

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
  /** True when a user-picked cover override exists — see AudiobookLibraryEndpoints.ApplyMetadata. */
  hasCustomCover?: boolean;
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
  private readonly apiHost = apiBase();
  private readonly baseUrl = `${this.apiHost}/api/audiobooks`;

  constructor(private http: HttpClient, private authService: AuthService) {}

  getCatalog(): Observable<AudiobookItem[]> {
    return this.http.get<AudiobookItem[]>(this.baseUrl);
  }

  getItem(id: string): Observable<AudiobookItem> {
    return this.http.get<AudiobookItem>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  /** Cascades to Audiobookshelf (removes the audio files from disk, not just the
   *  catalog entry) and cleans up everything we track for the item. Permanent. */
  deleteItem(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  /** Full replace of an audiobook's owners + genre tags; title is optional and only
   *  applied when provided (a title override, distinct from Audiobookshelf's own —
   *  see AudiobookLibraryEndpoints.ApplyMetadata). Omitting it leaves any existing
   *  title override untouched, it does not clear one. */
  setMetadata(id: string, owners: string[], genres: string[], title?: string): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/${encodeURIComponent(id)}/metadata`, { owners, genres, title });
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

  /** Same-origin cover URL for a plain <img src>, same token-param reasoning as
   *  getStreamUrl. cacheBustVersion should change after a cover override is saved —
   *  cover responses carry a 1-day Cache-Control (see the cover-stampede fix), so
   *  without a differing URL the browser would keep showing the stale cached image. */
  getCoverUrl(id: string, cacheBustVersion?: number): string {
    const token = this.authService.getToken() ?? '';
    const version = cacheBustVersion ? `&v=${cacheBustVersion}` : '';
    return `${this.baseUrl}/${encodeURIComponent(id)}/cover?access_token=${encodeURIComponent(token)}${version}`;
  }

  /** Downloads the given URL and stores it as this audiobook's cover override —
   *  never touches Audiobookshelf's own cover storage; see AudiobookLibraryEndpoints.cs. */
  setCoverUrl(id: string, coverUrl: string): Observable<{ success: boolean }> {
    return this.http.post<{ success: boolean }>(`${this.baseUrl}/${encodeURIComponent(id)}/cover`, { coverUrl });
  }
}

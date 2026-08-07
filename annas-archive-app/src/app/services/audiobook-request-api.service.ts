import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { apiBase } from './api-base';

export type AudiobookAvailability = 'owned' | 'requested' | 'available';

export interface ListenarrIntegrationStatus {
  enabled: boolean;
  configured: boolean;
  reachable: boolean;
  ready: boolean;
  readyStatus?: string;
  databaseConnected: boolean;
  migrationsCurrent: boolean;
  serviceHealth?: string;
  version?: string;
  rootFolderCount: number;
  qualityProfileCount: number;
  enabledIndexerCount: number;
  enabledDownloadClientCount: number;
  libraryItemCount: number;
  readOnlyGatePassed: boolean;
  gateFailures: string[];
}

export interface AudiobookSeriesMembership {
  asin?: string;
  name?: string;
  position?: string;
}

export interface AudiobookSearchResult {
  asin: string;
  title: string;
  subtitle?: string;
  authors: string[];
  narrators: string[];
  publisher?: string;
  releaseDate?: string;
  language?: string;
  format?: string;
  runtimeMinutes?: number;
  imageUrl?: string;
  genres: string[];
  series: AudiobookSeriesMembership[];
  availability: AudiobookAvailability;
  availabilityReason?: string;
  ownedAudiobookshelfId?: string;
  listenarrId?: number;
  requestTracked: boolean;
}

export interface AudiobookSearchResponse {
  query: string;
  region: string;
  language?: string;
  totalResults: number;
  results: AudiobookSearchResult[];
}

export interface AudiobookRequestPreview {
  previewToken: string;
  expiresAt: string;
  asin: string;
  title: string;
  authors: string[];
  narrators: string[];
  language?: string;
  format?: string;
  abridged: boolean;
  qualityProfile: string;
  /** Decided by the server and carried inside the preview token — the browser
   * reports it, it never requests it. */
  autoSearch: boolean;
  autoSearchReason: string;
  alreadyRequested: boolean;
  /** False when no indexer carried anything for this book at preview time. */
  releasesAvailable: boolean;
}

/* ─────────────── AI discovery (phase 5) ─────────────────────────────── */

export type AudiobookResolution = 'resolved' | 'ambiguous' | 'notFound';

export interface AudiobookDiscoveryResult {
  resolution: AudiobookResolution;
  suggestedTitle: string;
  suggestedAuthor?: string;
  /** The model's recommendation reason — shown only for AI results. */
  reason?: string;
  /** Present only when exactly one real edition was matched. */
  match?: AudiobookSearchResult;
  /** Editions to choose between when the suggestion stayed ambiguous. */
  choices: AudiobookSearchResult[];
  resolutionNote?: string;
  /** A narrator the user named in their query. Sent back with the request so
   * the server keeps it on manual release review. */
  narratorPreference?: string;
}

export interface AudiobookDiscoveryResponse {
  summary?: string;
  region: string;
  resolvedCount: number;
  ambiguousCount: number;
  notFoundCount: number;
  ownedCount: number;
  results: AudiobookDiscoveryResult[];
}

/* ─────────────── Series requests (phase 6) ──────────────────────────── */

export type AudiobookSeriesClassification =
  'owned' | 'requested' | 'requestable' | 'ambiguous' | 'unavailable';

export interface AudiobookSeriesMemberPreview {
  classification: AudiobookSeriesClassification;
  position?: string;
  title: string;
  asin?: string;
  edition?: AudiobookSearchResult;
  note?: string;
}

export interface AudiobookSeriesPreview {
  previewToken: string;
  expiresAt: string;
  seriesAsin: string;
  seriesName?: string;
  region: string;
  ownedCount: number;
  requestedCount: number;
  requestableCount: number;
  unavailableCount: number;
  requestCeiling: number;
  exceedsCeiling: boolean;
  members: AudiobookSeriesMemberPreview[];
}

export interface AudiobookSeriesRequestOutcome {
  asin: string;
  title: string;
  outcome: 'requested' | 'alreadyRequested' | 'failed';
  listenarrId?: number;
  error?: string;
}

export interface AudiobookSeriesConfirmResult {
  seriesAsin: string;
  requestedCount: number;
  alreadyExistedCount: number;
  failedCount: number;
  outcomes: AudiobookSeriesRequestOutcome[];
}

export interface AudiobookRequestResult {
  listenarrId: number;
  asin: string;
  title: string;
  status: string;
  alreadyExisted: boolean;
  requesterAdded: boolean;
}

export interface AudiobookReleaseOption {
  selectionToken: string;
  expiresAt: string;
  title: string;
  source: string;
  downloadType: string;
  format?: string;
  quality?: string;
  language?: string;
  size: number;
  seeders?: number;
  leechers?: number;
  grabs: number;
  files: number;
  score: number;
}

export interface AudiobookReleaseSearchResponse {
  listenarrId: number;
  asin: string;
  title: string;
  releases: AudiobookReleaseOption[];
}

export interface AudiobookReleaseGrabResult {
  listenarrId: number;
  asin: string;
  downloadId: string;
  status: string;
}

export interface AudiobookRequestRemoval {
  listenarrId: number;
  removedFromListenarr: boolean;
  remainingRequesters: number;
}

export type AudiobookRequestState =
  'Monitored' | 'Searching' | 'Queued' | 'Downloading' | 'Paused' | 'Processing' | 'Importing' |
  'ImportBlocked' | 'ReadyToScan' | 'InLibrary' | 'Failed' | 'Canceled';

export interface AudiobookRequestStatus {
  listenarrId: number;
  asin: string;
  title: string;
  state: AudiobookRequestState;
  progress: number;
  downloadId?: string;
  totalSize?: number;
  downloadedSize?: number;
  downloadClient?: string;
  error?: string;
  importBlockMessages: string[];
  audiobookshelfItemId?: string;
  canCancel: boolean;
  canRetryImport: boolean;
  updatedAt: string;
}

/**
 * Listenarr-backed audiobook search, discovery, and requests. Kept separate
 * from AudiobookApiService, which owns the playable Audiobookshelf library:
 * the two answer different questions about different systems.
 */
@Injectable({ providedIn: 'root' })
export class AudiobookRequestApiService {
  private readonly baseUrl = `${apiBase()}/api/audiobook-requests`;
  /** AI discovery lives under the shared /api/ai prefix like every other AI
   * endpoint, but its response is audiobook-shaped, so it belongs here. */
  private readonly discoverUrl = `${apiBase()}/api/ai/audiobook-search`;

  constructor(private http: HttpClient) {}

  getStatus(): Observable<ListenarrIntegrationStatus> {
    return this.http.get<ListenarrIntegrationStatus>(`${this.baseUrl}/status`);
  }

  search(term: string, region = 'us', language?: string): Observable<AudiobookSearchResponse> {
    let params = new HttpParams().set('term', term).set('region', region);
    if (language?.trim()) params = params.set('language', language.trim());
    return this.http.get<AudiobookSearchResponse>(`${this.baseUrl}/search`, { params });
  }

  discover(query: string, count?: number, region = 'us'): Observable<AudiobookDiscoveryResponse> {
    return this.http.post<AudiobookDiscoveryResponse>(this.discoverUrl, { query, count, region });
  }

  previewRequest(
    asin: string,
    region: string,
    narratorPreference?: string,
    languagePreference?: string
  ): Observable<AudiobookRequestPreview> {
    return this.http.post<AudiobookRequestPreview>(`${this.baseUrl}/preview`, {
      asin,
      region,
      narratorPreference,
      languagePreference
    });
  }

  previewSeries(seriesAsin: string, region: string): Observable<AudiobookSeriesPreview> {
    return this.http.post<AudiobookSeriesPreview>(`${this.baseUrl}/series/preview`, { seriesAsin, region });
  }

  confirmSeries(
    previewToken: string,
    asins: string[],
    confirmLarge = false
  ): Observable<AudiobookSeriesConfirmResult> {
    return this.http.post<AudiobookSeriesConfirmResult>(`${this.baseUrl}/series/confirm`, {
      previewToken,
      asins,
      confirmLarge
    });
  }

  /** `acceptNoReleases` is required by the server whenever the preview found
   *  none — showing the warning is not enough, it has to come back acknowledged. */
  confirmRequest(previewToken: string, acceptNoReleases = false): Observable<AudiobookRequestResult> {
    return this.http.post<AudiobookRequestResult>(this.baseUrl, { previewToken, acceptNoReleases });
  }

  searchReleases(listenarrId: number): Observable<AudiobookReleaseSearchResponse> {
    return this.http.get<AudiobookReleaseSearchResponse>(`${this.baseUrl}/${listenarrId}/releases`);
  }

  grabRelease(listenarrId: number, selectionToken: string): Observable<AudiobookReleaseGrabResult> {
    return this.http.post<AudiobookReleaseGrabResult>(
      `${this.baseUrl}/${listenarrId}/releases/${encodeURIComponent(selectionToken)}/grab`,
      {}
    );
  }

  getRequestStatus(listenarrId: number): Observable<AudiobookRequestStatus> {
    return this.http.get<AudiobookRequestStatus>(`${this.baseUrl}/${listenarrId}`);
  }

  /**
   * Every request of mine that hasn't landed in the library yet. The library
   * page needs this because getRequestStatus() takes a Listenarr id, and once
   * you leave the search page that id is gone — which is why an in-progress
   * download used to be invisible everywhere.
   */
  listMyRequests(): Observable<AudiobookRequestStatus[]> {
    return this.http.get<AudiobookRequestStatus[]>(`${this.baseUrl}/mine`);
  }

  /** Hides one request from my library view. Per-person; doesn't cancel it. */
  dismissRequest(listenarrId: number): Observable<{ listenarrId: number; dismissed: boolean }> {
    return this.http.post<{ listenarrId: number; dismissed: boolean }>(
      `${this.baseUrl}/${listenarrId}/dismiss`,
      {}
    );
  }

  cancelRequest(listenarrId: number): Observable<{ listenarrId: number; status: string }> {
    return this.http.post<{ listenarrId: number; status: string }>(
      `${this.baseUrl}/${listenarrId}/cancel`,
      { removeFromClient: true }
    );
  }

  /** Undo a request. Only removes the Listenarr entry when the caller was the
   * last person wanting it; refused once the book has reached the library. */
  removeRequest(listenarrId: number): Observable<AudiobookRequestRemoval> {
    return this.http.delete<AudiobookRequestRemoval>(`${this.baseUrl}/${listenarrId}`);
  }

  retryImport(listenarrId: number): Observable<{ listenarrId: number; status: string }> {
    return this.http.post<{ listenarrId: number; status: string }>(
      `${this.baseUrl}/${listenarrId}/retry-import`,
      {}
    );
  }
}

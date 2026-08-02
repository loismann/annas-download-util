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
  autoSearch: false;
  alreadyRequested: boolean;
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

export type AudiobookRequestState =
  'Monitored' | 'Queued' | 'Downloading' | 'Paused' | 'Processing' | 'Importing' |
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

@Injectable({ providedIn: 'root' })
export class AudiobookRequestApiService {
  private readonly baseUrl = `${apiBase()}/api/audiobook-requests`;

  constructor(private http: HttpClient) {}

  getStatus(): Observable<ListenarrIntegrationStatus> {
    return this.http.get<ListenarrIntegrationStatus>(`${this.baseUrl}/status`);
  }

  search(term: string, region = 'us', language?: string): Observable<AudiobookSearchResponse> {
    let params = new HttpParams().set('term', term).set('region', region);
    if (language?.trim()) params = params.set('language', language.trim());
    return this.http.get<AudiobookSearchResponse>(`${this.baseUrl}/search`, { params });
  }

  previewRequest(asin: string, region: string): Observable<AudiobookRequestPreview> {
    return this.http.post<AudiobookRequestPreview>(`${this.baseUrl}/preview`, { asin, region });
  }

  confirmRequest(previewToken: string): Observable<AudiobookRequestResult> {
    return this.http.post<AudiobookRequestResult>(this.baseUrl, { previewToken });
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

  cancelRequest(listenarrId: number): Observable<{ listenarrId: number; status: string }> {
    return this.http.post<{ listenarrId: number; status: string }>(
      `${this.baseUrl}/${listenarrId}/cancel`,
      { removeFromClient: true }
    );
  }

  retryImport(listenarrId: number): Observable<{ listenarrId: number; status: string }> {
    return this.http.post<{ listenarrId: number; status: string }>(
      `${this.baseUrl}/${listenarrId}/retry-import`,
      {}
    );
  }
}

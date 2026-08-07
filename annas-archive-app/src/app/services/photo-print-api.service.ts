import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { apiBase } from './api-base';
import { AuthService } from './auth.service';

export interface PhotoPrintStatus {
  configured: boolean;
  reachable: boolean;
  pickupZip: string;
  maxPrintsPerRun: number;
}

export interface PrintSizeOption {
  code: string;
  name: string;
  shortInches: number;
  longInches: number;
  isSquare: boolean;
}

export interface PhotoAsset {
  id: string;
  fileName: string;
  /** Capture time, not upload time — see ImmichService. */
  takenAt: string;
  width: number;
  height: number;
  isFavorite: boolean;
}

export interface PhotoPage {
  total: number;
  nextPage: number | null;
  items: PhotoAsset[];
}

export interface PrintRunItem {
  itemId: string;
  assetId: string;
  fileName: string;
  sizeCode: string;
  quantity: number;
  status: 'Pending' | 'Prepared' | 'Uploaded' | 'Failed';
  effectiveDpi: number | null;
  belowQualityFloor: boolean;
  error: string | null;
}

export interface PrintRun {
  runId: string;
  status: string;
  pickupZip: string | null;
  outputDirectory: string | null;
  screenshotPath: string | null;
  error: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface PrintRunDetail {
  run: PrintRun;
  totalPrints: number;
  items: PrintRunItem[];
}

export interface PrepareResult {
  runId: string;
  prepared: number;
  failed: number;
  belowQualityFloor: number;
}

export interface PhotoQuery {
  takenAfter?: string;
  takenBefore?: string;
  favoritesOnly?: boolean;
  page?: number;
  size?: number;
}

/**
 * Immich -> CVS pickup prints. See
 * DOCS/features/google-photos-cvs-print-automation-spec.md.
 */
@Injectable({ providedIn: 'root' })
export class PhotoPrintApiService {
  private readonly baseUrl = `${apiBase()}/api/photo-print`;

  constructor(private http: HttpClient, private auth: AuthService) {}

  getStatus(): Observable<PhotoPrintStatus> {
    return this.http.get<PhotoPrintStatus>(`${this.baseUrl}/status`);
  }

  getSizes(): Observable<PrintSizeOption[]> {
    return this.http.get<PrintSizeOption[]>(`${this.baseUrl}/sizes`);
  }

  browsePhotos(query: PhotoQuery = {}): Observable<PhotoPage> {
    let params = new HttpParams();
    if (query.takenAfter) params = params.set('takenAfter', query.takenAfter);
    if (query.takenBefore) params = params.set('takenBefore', query.takenBefore);
    if (query.favoritesOnly) params = params.set('favoritesOnly', 'true');
    if (query.page) params = params.set('page', String(query.page));
    if (query.size) params = params.set('size', String(query.size));
    return this.http.get<PhotoPage>(`${this.baseUrl}/photos`, { params });
  }

  /**
   * Proxied through our API rather than hitting Immich directly, so the Immich
   * key never reaches the browser.
   *
   * The token rides in the query string because a native <img> cannot send an
   * Authorization header — same scoped fallback the audiobook covers use, and
   * the matching allowlist entry is in ServiceConfiguration's JwtBearerEvents.
   */
  thumbnailUrl(assetId: string): string {
    const token = this.auth.getToken() ?? '';
    return `${this.baseUrl}/photos/${encodeURIComponent(assetId)}/thumbnail`
      + `?access_token=${encodeURIComponent(token)}`;
  }

  createRun(): Observable<{ runId: string }> {
    return this.http.post<{ runId: string }>(`${this.baseUrl}/runs`, {});
  }

  getRun(runId: string): Observable<PrintRunDetail> {
    return this.http.get<PrintRunDetail>(`${this.baseUrl}/runs/${runId}`);
  }

  listRuns(): Observable<PrintRun[]> {
    return this.http.get<PrintRun[]>(`${this.baseUrl}/runs`);
  }

  addItem(
    runId: string, assetId: string, fileName: string, sizeCode: string, quantity: number
  ): Observable<{ runId: string }> {
    return this.http.post<{ runId: string }>(`${this.baseUrl}/runs/${runId}/items`, {
      assetId, fileName, sizeCode, quantity
    });
  }

  removeItem(runId: string, itemId: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/runs/${runId}/items/${itemId}`);
  }

  prepare(runId: string): Observable<PrepareResult> {
    return this.http.post<PrepareResult>(`${this.baseUrl}/runs/${runId}/prepare`, {});
  }

  cancelRun(runId: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/runs/${runId}/cancel`, {});
  }
}

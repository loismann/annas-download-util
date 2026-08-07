import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { apiBase } from './api-base';

export interface StorageStats {
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;
  percentFull: number;
  moviesBytes: number;
  tvBytes: number;
  booksBytes: number;
  audiobooksBytes: number;
  photosBytes: number;
  /** Everything no category claims — downloads, Docker images, backups. Derived by subtraction. */
  otherBytes: number;
}

@Injectable({ providedIn: 'root' })
export class SystemStatsApiService {
  private readonly apiHost = apiBase();

  constructor(private http: HttpClient) {}

  getStorageStats(): Observable<StorageStats> {
    return this.http.get<StorageStats>(`${this.apiHost}/api/system/storage`);
  }
}

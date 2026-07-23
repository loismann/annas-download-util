import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface StorageStats {
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;
  percentFull: number;
  moviesBytes: number;
  tvBytes: number;
  booksBytes: number;
}

@Injectable({ providedIn: 'root' })
export class SystemStatsApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev ? 'http://localhost:5001' : '';

  constructor(private http: HttpClient) {}

  getStorageStats(): Observable<StorageStats> {
    return this.http.get<StorageStats>(`${this.apiHost}/api/system/storage`);
  }
}

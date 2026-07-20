import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, timeout } from 'rxjs';
import {
  VideoInfo,
  StartDownloadRequest,
  DownloadJob,
  DownloadProgressEvent,
} from './youtube.models';

@Injectable({ providedIn: 'root' })
export class YouTubeApiService {
  private readonly isLocalDev = window.location.hostname === 'localhost';
  private readonly apiHost = this.isLocalDev
    ? 'http://localhost:5001'
    : '';
  private readonly baseUrl = `${this.apiHost}/api/youtube`;

  constructor(private http: HttpClient) {}

  getVideoInfo(url: string): Observable<VideoInfo> {
    // yt-dlp can take 30-60+ seconds to fetch video info, use a 2-minute timeout
    return this.http.get<VideoInfo>(`${this.baseUrl}/formats`, {
      params: { url },
    }).pipe(timeout(120000));
  }

  startDownload(request: StartDownloadRequest): Observable<{ jobId: string }> {
    return this.http.post<{ jobId: string }>(
      `${this.baseUrl}/download/start`,
      request
    );
  }

  getJobStatus(jobId: string): Observable<DownloadJob> {
    return this.http.get<DownloadJob>(
      `${this.baseUrl}/download/${jobId}/status`
    );
  }

  cancelDownload(jobId: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(
      `${this.baseUrl}/download/${jobId}/cancel`,
      {}
    );
  }

  getDownloadHistory(): Observable<DownloadJob[]> {
    return this.http.get<DownloadJob[]>(`${this.baseUrl}/downloads`);
  }

  deleteDownload(jobId: string): Observable<{ message: string }> {
    return this.http.delete<{ message: string }>(
      `${this.baseUrl}/downloads/${jobId}`
    );
  }

  streamProgress(jobId: string): Observable<DownloadProgressEvent> {
    return new Observable((observer) => {
      const eventSource = new EventSource(
        `${this.baseUrl}/download/${jobId}/stream`,
        { withCredentials: true }
      );

      eventSource.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data) as DownloadProgressEvent;
          observer.next(data);
        } catch (e) {
          console.error('Failed to parse SSE data:', e);
        }
      };

      eventSource.addEventListener('complete', (event) => {
        try {
          const data = JSON.parse(
            (event as MessageEvent).data
          ) as DownloadProgressEvent;
          observer.next(data);
          observer.complete();
        } catch (e) {
          observer.complete();
        }
        eventSource.close();
      });

      eventSource.onerror = (error) => {
        console.error('SSE error:', error);
        eventSource.close();
        observer.error(error);
      };

      return () => {
        eventSource.close();
      };
    });
  }
}

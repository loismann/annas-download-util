import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatDividerModule } from '@angular/material/divider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Subscription, interval } from 'rxjs';
import { switchMap, takeWhile } from 'rxjs/operators';
import { YouTubeApiService } from './youtube-api.service';
import {
  VideoInfo,
  VideoFormat,
  DownloadJob,
} from './youtube.models';

type ViewState = 'idle' | 'fetching' | 'selecting' | 'downloading' | 'error';

@Component({
  selector: 'app-youtube-downloader',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatInputModule,
    MatButtonModule,
    MatSelectModule,
    MatProgressBarModule,
    MatIconModule,
    MatListModule,
    MatDividerModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './youtube-downloader.component.html',
  styleUrl: './youtube-downloader.component.scss',
})
export class YouTubeDownloaderComponent implements OnInit, OnDestroy {
  url = '';
  viewState: ViewState = 'idle';
  errorMessage = '';

  videoInfo: VideoInfo | null = null;
  selectedFormat: VideoFormat | null = null;

  currentJob: DownloadJob | null = null;
  progressSubscription: Subscription | null = null;

  downloadHistory: DownloadJob[] = [];

  constructor(private youtubeApi: YouTubeApiService) {}

  ngOnInit(): void {
    this.loadHistory();
  }

  ngOnDestroy(): void {
    this.progressSubscription?.unsubscribe();
  }

  loadHistory(): void {
    this.youtubeApi.getDownloadHistory().subscribe({
      next: (jobs) => {
        this.downloadHistory = jobs;
      },
      error: (err) => {
        console.error('Failed to load history:', err);
      },
    });
  }

  fetchVideoInfo(): void {
    if (!this.url.trim()) return;

    this.viewState = 'fetching';
    this.errorMessage = '';
    this.videoInfo = null;
    this.selectedFormat = null;

    this.youtubeApi.getVideoInfo(this.url).subscribe({
      next: (info) => {
        this.videoInfo = info;
        this.viewState = 'selecting';
        // Pre-select best quality
        if (info.formats.length > 0) {
          this.selectedFormat = info.formats[0];
        }
      },
      error: (err) => {
        this.viewState = 'error';
        this.errorMessage = err.error?.error || 'Failed to fetch video info';
      },
    });
  }

  get videoFormats(): VideoFormat[] {
    return this.videoInfo?.formats.filter((f) => !f.isAudioOnly) || [];
  }

  get audioFormats(): VideoFormat[] {
    return this.videoInfo?.formats.filter((f) => f.isAudioOnly) || [];
  }

  startDownload(): void {
    if (!this.selectedFormat) return;

    this.viewState = 'downloading';
    this.currentJob = null;

    this.youtubeApi
      .startDownload({
        url: this.url,
        formatId: this.selectedFormat.formatId,
      })
      .subscribe({
        next: (response) => {
          this.subscribeToProgress(response.jobId);
        },
        error: (err) => {
          this.viewState = 'error';
          this.errorMessage = err.error?.error || 'Failed to start download';
        },
      });
  }

  private subscribeToProgress(jobId: string): void {
    this.progressSubscription?.unsubscribe();

    // Poll for status every second until download completes
    // (SSE doesn't work with JWT auth since EventSource can't set headers)
    this.progressSubscription = interval(1000)
      .pipe(
        switchMap(() => this.youtubeApi.getJobStatus(jobId)),
        takeWhile(
          (job) =>
            job.status === 'queued' || job.status === 'downloading',
          true // include the final emission
        )
      )
      .subscribe({
        next: (job) => {
          this.currentJob = job;

          if (
            job.status === 'complete' ||
            job.status === 'failed' ||
            job.status === 'cancelled'
          ) {
            this.onDownloadComplete(job);
          }
        },
        error: (err) => {
          console.error('Failed to poll status:', err);
        },
      });
  }

  private onDownloadComplete(job: DownloadJob): void {
    this.viewState = 'idle';
    this.loadHistory();

    if (job.status === 'failed' && job.error) {
      this.errorMessage = job.error;
    }
  }

  cancelDownload(): void {
    if (!this.currentJob) return;

    this.youtubeApi.cancelDownload(this.currentJob.jobId).subscribe({
      next: () => {
        this.viewState = 'idle';
        this.currentJob = null;
        this.loadHistory();
      },
      error: (err) => {
        console.error('Failed to cancel:', err);
      },
    });
  }

  deleteFromHistory(job: DownloadJob): void {
    this.youtubeApi.deleteDownload(job.jobId).subscribe({
      next: () => {
        this.downloadHistory = this.downloadHistory.filter(
          (j) => j.jobId !== job.jobId
        );
      },
      error: (err) => {
        console.error('Failed to delete:', err);
      },
    });
  }

  resetForm(): void {
    this.url = '';
    this.viewState = 'idle';
    this.errorMessage = '';
    this.videoInfo = null;
    this.selectedFormat = null;
    this.currentJob = null;
    this.progressSubscription?.unsubscribe();
  }

  getStatusIcon(status: string): string {
    switch (status) {
      case 'complete':
        return 'check_circle';
      case 'failed':
        return 'error';
      case 'cancelled':
        return 'cancel';
      case 'downloading':
        return 'downloading';
      default:
        return 'hourglass_empty';
    }
  }

  getStatusColor(status: string): string {
    switch (status) {
      case 'complete':
        return 'green';
      case 'failed':
        return 'red';
      case 'cancelled':
        return 'orange';
      default:
        return 'inherit';
    }
  }
}

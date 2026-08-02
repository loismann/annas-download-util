import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import {
  AudiobookRequestApiService,
  AudiobookRequestStatus,
  AudiobookSearchResult,
  ListenarrIntegrationStatus
} from '../services/audiobook-request-api.service';
import { LoggerService } from '../services/logger.service';
import { AudiobookRequestConfirmDialogComponent } from './audiobook-request-confirm-dialog/audiobook-request-confirm-dialog.component';
import { AudiobookReleasePickerComponent } from './audiobook-release-picker/audiobook-release-picker.component';
import { ConfirmDialogComponent } from '../components/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-audiobook-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatCardModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatProgressBarModule,
    MatSelectModule
  ],
  templateUrl: './audiobook-search.component.html',
  styleUrl: './audiobook-search.component.scss'
})
export class AudiobookSearchComponent implements OnInit, OnDestroy {
  searchTerm = '';
  region = 'us';
  language = '';
  loading = false;
  statusLoading = true;
  error: string | null = null;
  status: ListenarrIntegrationStatus | null = null;
  results: AudiobookSearchResult[] = [];
  totalResults = 0;
  previewingAsin: string | null = null;
  loadingReleasesFor: number | null = null;
  cardMessages: Record<string, string> = {};
  cardErrors: Record<string, string> = {};
  requestStatuses: Record<string, AudiobookRequestStatus> = {};
  mutatingRequestId: number | null = null;
  private pollTimer: number | null = null;

  readonly regions = [
    { value: 'us', label: 'United States' },
    { value: 'uk', label: 'United Kingdom' },
    { value: 'ca', label: 'Canada' },
    { value: 'au', label: 'Australia' },
    { value: 'de', label: 'Germany' },
    { value: 'fr', label: 'France' },
    { value: 'it', label: 'Italy' },
    { value: 'in', label: 'India' },
    { value: 'jp', label: 'Japan' },
    { value: 'es', label: 'Spain' },
    { value: 'br', label: 'Brazil' }
  ];

  constructor(
    private api: AudiobookRequestApiService,
    private logger: LoggerService,
    private dialog: MatDialog
  ) {}

  ngOnInit(): void {
    this.loadStatus();
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  loadStatus(): void {
    this.statusLoading = true;
    this.api.getStatus().subscribe({
      next: status => {
        this.status = status;
        this.statusLoading = false;
      },
      error: err => {
        this.logger.error('[AudiobookSearch] Listenarr status failed', err);
        this.status = null;
        this.statusLoading = false;
      }
    });
  }

  search(): void {
    const term = this.searchTerm.trim();
    if (term.length < 2 || this.loading || this.searchDisabled) return;

    this.loading = true;
    this.error = null;
    this.results = [];
    this.totalResults = 0;
    this.requestStatuses = {};
    this.stopPolling();

    this.api.search(term, this.region, this.language || undefined).subscribe({
      next: response => {
        this.results = response.results;
        this.totalResults = response.totalResults;
        this.loading = false;
        this.refreshTrackedStatuses();
      },
      error: err => {
        this.logger.error('[AudiobookSearch] search failed', err);
        this.error = err?.error?.error || 'Audiobook search is temporarily unavailable.';
        this.loading = false;
      }
    });
  }

  get searchDisabled(): boolean {
    return this.statusLoading || !this.status?.enabled || !this.status.readOnlyGatePassed;
  }

  get statusMessage(): string | null {
    if (this.statusLoading) return null;
    if (!this.status) return 'Listenarr status is unavailable.';
    if (!this.status.enabled) return 'Audiobook discovery is installed but not enabled.';
    if (!this.status.readOnlyGatePassed) {
      return this.status.gateFailures?.join(' ') || 'Listenarr setup is incomplete.';
    }
    return null;
  }

  availabilityLabel(result: AudiobookSearchResult): string {
    if (result.availability === 'owned') return 'In your library';
    if (result.availability === 'requested') return 'Already requested';
    return 'Available';
  }

  runtimeLabel(minutes?: number): string | null {
    if (!minutes || minutes < 1) return null;
    const hours = Math.floor(minutes / 60);
    const remainder = minutes % 60;
    return hours > 0 ? `${hours}h ${remainder}m` : `${remainder}m`;
  }

  onImageError(event: Event): void {
    const image = event.target as HTMLImageElement;
    image.src = '/assets/placeholder.jpg';
  }

  reviewRequest(result: AudiobookSearchResult): void {
    if (this.previewingAsin || result.availability === 'owned') return;
    this.previewingAsin = result.asin;
    delete this.cardErrors[result.asin];
    this.api.previewRequest(result.asin, this.region).subscribe({
      next: preview => {
        this.previewingAsin = null;
        this.dialog.open(AudiobookRequestConfirmDialogComponent, {
          data: preview,
          width: '560px',
          maxWidth: '96vw'
        }).afterClosed().subscribe(confirmed => {
          if (!confirmed) return;
          result.availability = 'requested';
          result.listenarrId = confirmed.listenarrId;
          result.requestTracked = true;
          result.availabilityReason = confirmed.alreadyExisted
            ? 'This edition was already in Listenarr; your requester attribution is saved.'
            : 'Added to Listenarr as monitored. No automatic download was started.';
          this.cardMessages[result.asin] = 'Request saved. You can now review available releases.';
          this.refreshRequestStatus(result);
        });
      },
      error: err => {
        this.previewingAsin = null;
        this.logger.error('[AudiobookSearch] request preview failed', err);
        this.cardErrors[result.asin] = err?.error?.error || 'The request could not be reviewed.';
      }
    });
  }

  chooseRelease(result: AudiobookSearchResult): void {
    if (!result.listenarrId || !result.requestTracked || this.loadingReleasesFor) return;
    const id = result.listenarrId;
    this.loadingReleasesFor = id;
    delete this.cardErrors[result.asin];
    this.api.searchReleases(id).subscribe({
      next: response => {
        this.loadingReleasesFor = null;
        this.dialog.open(AudiobookReleasePickerComponent, {
          data: response,
          width: '820px',
          maxWidth: '98vw',
          maxHeight: '92vh'
        }).afterClosed().subscribe(grabbed => {
          if (!grabbed) return;
          this.cardMessages[result.asin] = 'The selected release was sent to the download client.';
          result.availabilityReason = 'Queued for download.';
          this.refreshRequestStatus(result);
        });
      },
      error: err => {
        this.loadingReleasesFor = null;
        this.logger.error('[AudiobookSearch] release search failed', err);
        this.cardErrors[result.asin] = err?.error?.error || 'Release search is temporarily unavailable.';
      }
    });
  }

  cancelRequest(result: AudiobookSearchResult): void {
    const status = this.requestStatuses[result.asin];
    if (!result.listenarrId || !status?.canCancel || this.mutatingRequestId) return;
    this.dialog.open(ConfirmDialogComponent, {
      width: '460px',
      data: {
        title: 'Cancel this audiobook download?',
        message: 'This removes the active job from the download client. Temporary data is handled according to that client’s cleanup settings. The monitored Listenarr library entry is kept.',
        confirmText: 'Cancel download',
        isDanger: true
      }
    }).afterClosed().subscribe(confirmed => {
      if (!confirmed || !result.listenarrId) return;
      this.mutatingRequestId = result.listenarrId;
      this.api.cancelRequest(result.listenarrId).subscribe({
        next: () => {
          this.mutatingRequestId = null;
          this.cardMessages[result.asin] = 'The active download was canceled. The monitored request remains in Listenarr.';
          this.refreshRequestStatus(result);
        },
        error: err => {
          this.mutatingRequestId = null;
          this.cardErrors[result.asin] = err?.error?.error || 'The download could not be canceled.';
        }
      });
    });
  }

  retryImport(result: AudiobookSearchResult): void {
    const status = this.requestStatuses[result.asin];
    if (!result.listenarrId || !status?.canRetryImport || this.mutatingRequestId) return;
    this.mutatingRequestId = result.listenarrId;
    this.api.retryImport(result.listenarrId).subscribe({
      next: () => {
        this.mutatingRequestId = null;
        this.cardMessages[result.asin] = 'Listenarr is retrying the import.';
        this.refreshRequestStatus(result);
      },
      error: err => {
        this.mutatingRequestId = null;
        this.cardErrors[result.asin] = err?.error?.error || 'The import could not be retried.';
      }
    });
  }

  stateLabel(state: string): string {
    return ({
      Monitored: 'Waiting for release selection',
      Queued: 'Queued',
      Downloading: 'Downloading',
      Paused: 'Paused',
      Processing: 'Download complete — processing',
      Importing: 'Importing into the audiobook library',
      ImportBlocked: 'Import needs attention',
      ReadyToScan: 'Imported — waiting for Audiobookshelf',
      InLibrary: 'Ready in Audiobookshelf',
      Failed: 'Download failed',
      Canceled: 'Download canceled'
    } as Record<string, string>)[state] || state;
  }

  canChooseRelease(result: AudiobookSearchResult): boolean {
    const state = this.requestStatuses[result.asin]?.state;
    return !state || state === 'Monitored' || state === 'Canceled' || state === 'Failed';
  }

  private refreshTrackedStatuses(): void {
    this.results.filter(result => result.requestTracked && result.listenarrId)
      .forEach(result => this.refreshRequestStatus(result));
  }

  private refreshRequestStatus(result: AudiobookSearchResult): void {
    if (!result.listenarrId) return;
    this.api.getRequestStatus(result.listenarrId).subscribe({
      next: status => {
        this.requestStatuses[result.asin] = status;
        if (status.state === 'InLibrary') {
          result.availability = 'owned';
          result.ownedAudiobookshelfId = status.audiobookshelfItemId;
        }
        this.syncPolling();
      },
      error: err => {
        this.logger.error('[AudiobookSearch] progress refresh failed', err);
        this.syncPolling();
      }
    });
  }

  private syncPolling(): void {
    const activeStates = new Set(['Queued', 'Downloading', 'Paused', 'Processing', 'Importing', 'ReadyToScan']);
    const hasActive = Object.values(this.requestStatuses).some(status => activeStates.has(status.state));
    if (!hasActive) {
      this.stopPolling();
      return;
    }
    if (this.pollTimer !== null) return;
    this.pollTimer = window.setInterval(() => {
      this.results.filter(result => {
        const status = this.requestStatuses[result.asin];
        return !!result.listenarrId && !!status && activeStates.has(status.state);
      }).forEach(result => this.refreshRequestStatus(result));
    }, 10_000);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) window.clearInterval(this.pollTimer);
    this.pollTimer = null;
  }
}

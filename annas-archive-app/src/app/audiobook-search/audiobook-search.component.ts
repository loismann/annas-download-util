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
  AudiobookDiscoveryResult,
  AudiobookRequestApiService,
  AudiobookRequestStatus,
  AudiobookResolution,
  AudiobookSearchResult,
  ListenarrIntegrationStatus
} from '../services/audiobook-request-api.service';
import { LoggerService } from '../services/logger.service';
import { AudiobookRequestConfirmDialogComponent } from './audiobook-request-confirm-dialog/audiobook-request-confirm-dialog.component';
import { AudiobookReleasePickerComponent } from './audiobook-release-picker/audiobook-release-picker.component';
import { AudiobookEditionPickerComponent } from './audiobook-edition-picker/audiobook-edition-picker.component';
import { AudiobookSeriesRequestDialogComponent } from './audiobook-series-request-dialog/audiobook-series-request-dialog.component';
import { ConfirmDialogComponent } from '../components/confirm-dialog/confirm-dialog.component';

/**
 * One card on the page. Catalog search produces entries that are already
 * resolved; AI discovery can also produce ambiguous entries (the user must
 * pick an edition first) and not-found entries (nothing is requestable).
 * Both modes share this shape so the template has one card, not two.
 */
export interface AudiobookResultEntry {
  /** Stable per-card key. It deliberately survives an edition choice so a
   * card never jumps position while its progress is being polled. */
  key: string;
  resolution: AudiobookResolution;
  suggestedTitle: string;
  suggestedAuthor?: string;
  /** Only set for AI results. */
  aiReason?: string;
  note?: string;
  /** A narrator the user named. Carried back into the request so an
   * automatic release match cannot quietly ignore it. */
  narratorPreference?: string;
  result: AudiobookSearchResult | null;
  choices: AudiobookSearchResult[];
}

const ACTIVE_STATES = new Set([
  'Searching', 'Queued', 'Downloading', 'Paused', 'Processing', 'Importing', 'ReadyToScan'
]);
const POLL_MS = 10_000;

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
  aiSearchExpanded = false;
  aiSearchQuery = '';
  aiSummary: string | null = null;
  loading = false;
  statusLoading = true;
  error: string | null = null;
  status: ListenarrIntegrationStatus | null = null;
  entries: AudiobookResultEntry[] = [];
  totalResults = 0;
  hasSearched = false;
  requestingKey: string | null = null;
  loadingReleasesFor: number | null = null;
  seriesLoadingKey: string | null = null;
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

  toggleAiSearch(): void {
    this.aiSearchExpanded = !this.aiSearchExpanded;
    this.aiSearchQuery = this.aiSearchExpanded ? this.searchTerm.trim() : '';
  }

  /** Plain Enter submits the AI query; Shift+Enter inserts a newline. */
  onAiTextareaEnter(event: Event): void {
    if ((event as KeyboardEvent).shiftKey) return;
    event.preventDefault();
    this.search();
  }

  search(): void {
    if (this.loading || this.searchDisabled) return;
    this.aiSearchExpanded ? this.runAiSearch() : this.runCatalogSearch();
  }

  get searchDisabled(): boolean {
    return this.statusLoading || !this.status?.enabled || !this.status.readOnlyGatePassed;
  }

  get submitDisabled(): boolean {
    const query = this.aiSearchExpanded ? this.aiSearchQuery : this.searchTerm;
    return this.loading || this.searchDisabled || query.trim().length < 2;
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

  seriesAsin(entry: AudiobookResultEntry): string | null {
    return entry.result?.series?.[0]?.asin || null;
  }

  /** One action for the whole request path: the server decides during preview
   * whether this edition may be acquired automatically or has to go through
   * release review, and the dialog only appears in the second case. */
  requestBook(entry: AudiobookResultEntry): void {
    const result = entry.result;
    if (!result || this.requestingKey || result.availability === 'owned') return;

    this.requestingKey = entry.key;
    delete this.cardErrors[entry.key];
    this.api.previewRequest(
      result.asin,
      this.region,
      entry.narratorPreference,
      // A language typed into the filter box is an explicit preference too.
      this.aiSearchExpanded ? undefined : this.language.trim() || undefined
    ).subscribe({
      next: preview => {
        if (!preview.autoSearch) {
          this.requestingKey = null;
          this.dialog.open(AudiobookRequestConfirmDialogComponent, {
            data: preview,
            width: '560px',
            maxWidth: '96vw'
          }).afterClosed().subscribe(confirmed => {
            if (confirmed) this.applyRequest(entry, result, confirmed);
          });
          return;
        }

        this.api.confirmRequest(preview.previewToken).subscribe({
          next: confirmed => {
            this.requestingKey = null;
            this.applyRequest(entry, result, confirmed);
          },
          error: err => this.failCard(entry, err, 'The request could not be completed.')
        });
      },
      error: err => this.failCard(entry, err, 'The request could not be reviewed.')
    });
  }

  chooseEdition(entry: AudiobookResultEntry): void {
    if (!entry.choices.length) return;
    this.dialog.open(AudiobookEditionPickerComponent, {
      data: { suggestedTitle: entry.suggestedTitle, choices: entry.choices },
      width: '720px',
      maxWidth: '96vw',
      maxHeight: '90vh'
    }).afterClosed().subscribe((chosen: AudiobookSearchResult | undefined) => {
      if (!chosen) return;
      entry.result = chosen;
      entry.resolution = 'resolved';
      entry.note = 'You chose this edition.';
      if (chosen.requestTracked) this.refreshRequestStatus(entry);
    });
  }

  requestSeries(entry: AudiobookResultEntry): void {
    const seriesAsin = this.seriesAsin(entry);
    if (!seriesAsin || this.seriesLoadingKey) return;

    this.seriesLoadingKey = entry.key;
    delete this.cardErrors[entry.key];
    this.api.previewSeries(seriesAsin, this.region).subscribe({
      next: preview => {
        this.seriesLoadingKey = null;
        this.dialog.open(AudiobookSeriesRequestDialogComponent, {
          data: preview,
          width: '860px',
          maxWidth: '98vw',
          maxHeight: '92vh'
        }).afterClosed().subscribe(result => {
          if (!result) return;
          this.cardMessages[entry.key] =
            `${result.requestedCount} added, ${result.alreadyExistedCount} already requested` +
            (result.failedCount ? `, ${result.failedCount} failed.` : '.');
        });
      },
      error: err => {
        this.seriesLoadingKey = null;
        this.logger.error('[AudiobookSearch] series preview failed', err);
        this.cardErrors[entry.key] = err?.error?.error || 'The series could not be loaded.';
      }
    });
  }

  chooseRelease(entry: AudiobookResultEntry): void {
    const result = entry.result;
    if (!result?.listenarrId || !result.requestTracked || this.loadingReleasesFor) return;
    const id = result.listenarrId;
    this.loadingReleasesFor = id;
    delete this.cardErrors[entry.key];
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
          this.cardMessages[entry.key] = 'The selected release was sent to the download client.';
          result.availabilityReason = 'Queued for download.';
          this.refreshRequestStatus(entry);
        });
      },
      error: err => {
        this.loadingReleasesFor = null;
        this.logger.error('[AudiobookSearch] release search failed', err);
        this.cardErrors[entry.key] = err?.error?.error || 'Release search is temporarily unavailable.';
      }
    });
  }

  cancelRequest(entry: AudiobookResultEntry): void {
    const result = entry.result;
    const status = this.requestStatuses[entry.key];
    if (!result?.listenarrId || !status?.canCancel || this.mutatingRequestId) return;
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
          this.cardMessages[entry.key] = 'The active download was canceled. The monitored request remains in Listenarr.';
          this.refreshRequestStatus(entry);
        },
        error: err => {
          this.mutatingRequestId = null;
          this.cardErrors[entry.key] = err?.error?.error || 'The download could not be canceled.';
        }
      });
    });
  }

  /** Undo a request — the way out of a book that has no findable release. */
  removeRequest(entry: AudiobookResultEntry): void {
    const result = entry.result;
    if (!result?.listenarrId || !result.requestTracked || this.mutatingRequestId) return;
    this.dialog.open(ConfirmDialogComponent, {
      width: '460px',
      data: {
        title: 'Remove this request?',
        message: 'This takes the book off the wanted list and stops any download in progress. Nothing already in your library is affected, and you can request it again later.',
        confirmText: 'Remove request',
        isDanger: true
      }
    }).afterClosed().subscribe(confirmed => {
      if (!confirmed || !result.listenarrId) return;
      this.mutatingRequestId = result.listenarrId;
      this.api.removeRequest(result.listenarrId).subscribe({
        next: removal => {
          this.mutatingRequestId = null;
          delete this.requestStatuses[entry.key];
          result.availability = 'available';
          result.requestTracked = false;
          result.listenarrId = undefined;
          result.availabilityReason = undefined;
          this.cardMessages[entry.key] = removal.removedFromListenarr
            ? 'Request removed. You can request it again whenever you like.'
            : 'You were removed as a requester. Someone else still wants this book, so it stays on the list.';
          this.syncPolling();
        },
        error: err => {
          this.mutatingRequestId = null;
          this.cardErrors[entry.key] = err?.error?.error || 'The request could not be removed.';
        }
      });
    });
  }

  retryImport(entry: AudiobookResultEntry): void {
    const result = entry.result;
    const status = this.requestStatuses[entry.key];
    if (!result?.listenarrId || !status?.canRetryImport || this.mutatingRequestId) return;
    this.mutatingRequestId = result.listenarrId;
    this.api.retryImport(result.listenarrId).subscribe({
      next: () => {
        this.mutatingRequestId = null;
        this.cardMessages[entry.key] = 'Listenarr is retrying the import.';
        this.refreshRequestStatus(entry);
      },
      error: err => {
        this.mutatingRequestId = null;
        this.cardErrors[entry.key] = err?.error?.error || 'The import could not be retried.';
      }
    });
  }

  stateLabel(state: string): string {
    return ({
      Monitored: 'Waiting for release selection',
      Searching: 'Listenarr is searching for a release',
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

  /** Manual release choice stays available while an automatic search is
   * pending: an auto-search that finds nothing would otherwise leave the
   * request with no way forward. */
  canChooseRelease(entry: AudiobookResultEntry): boolean {
    const state = this.requestStatuses[entry.key]?.state;
    return !state || state === 'Monitored' || state === 'Searching' ||
      state === 'Canceled' || state === 'Failed';
  }

  private runCatalogSearch(): void {
    const term = this.searchTerm.trim();
    if (term.length < 2) return;

    this.beginSearch();
    this.api.search(term, this.region, this.language || undefined).subscribe({
      next: response => {
        this.entries = response.results.map(result => ({
          key: result.asin,
          resolution: 'resolved' as AudiobookResolution,
          suggestedTitle: result.title,
          suggestedAuthor: result.authors[0],
          result,
          choices: []
        }));
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

  private runAiSearch(): void {
    const query = this.aiSearchQuery.trim();
    if (query.length < 2) return;

    this.beginSearch();
    this.api.discover(query, undefined, this.region).subscribe({
      next: response => {
        this.aiSummary = response.summary || null;
        this.entries = this.toEntries(response.results);
        this.totalResults = response.results.length;
        this.loading = false;
        if (response.ownedCount > 0) {
          this.aiSummary = `${this.aiSummary ? this.aiSummary + ' ' : ''}` +
            `${response.ownedCount} of these are already in your library.`;
        }
        this.refreshTrackedStatuses();
      },
      error: err => {
        this.logger.error('[AudiobookSearch] AI discovery failed', err);
        this.error = err?.error?.error || 'AI discovery failed — try rephrasing the request.';
        this.loading = false;
      }
    });
  }

  private toEntries(results: AudiobookDiscoveryResult[]): AudiobookResultEntry[] {
    return results.map((result, index) => ({
      // AI suggestions can repeat a title, and an unresolved one has no ASIN
      // at all, so the index is what keeps card state unambiguous.
      key: `ai-${index}`,
      resolution: result.resolution,
      suggestedTitle: result.suggestedTitle,
      suggestedAuthor: result.suggestedAuthor,
      aiReason: result.reason,
      note: result.resolutionNote,
      narratorPreference: result.narratorPreference,
      result: result.match ?? null,
      choices: result.choices || []
    }));
  }

  private beginSearch(): void {
    this.loading = true;
    this.hasSearched = true;
    this.error = null;
    this.aiSummary = null;
    this.entries = [];
    this.totalResults = 0;
    this.requestStatuses = {};
    this.cardMessages = {};
    this.cardErrors = {};
    this.stopPolling();
  }

  private applyRequest(
    entry: AudiobookResultEntry,
    result: AudiobookSearchResult,
    confirmed: { listenarrId: number; alreadyExisted: boolean; status: string }
  ): void {
    result.availability = 'requested';
    result.listenarrId = confirmed.listenarrId;
    result.requestTracked = true;
    result.availabilityReason = confirmed.alreadyExisted
      ? 'This edition was already in Listenarr; your requester attribution is saved.'
      : confirmed.status === 'Searching'
        ? 'Added to Listenarr. It is searching for a release now.'
        : 'Added to Listenarr as monitored. No automatic download was started.';
    this.cardMessages[entry.key] = confirmed.status === 'Searching'
      ? 'Request saved. Listenarr is looking for a release.'
      : 'Request saved. You can now review available releases.';
    this.refreshRequestStatus(entry);
  }

  private failCard(entry: AudiobookResultEntry, err: unknown, fallback: string): void {
    this.requestingKey = null;
    this.logger.error('[AudiobookSearch] request failed', err);
    this.cardErrors[entry.key] = (err as { error?: { error?: string } })?.error?.error || fallback;
  }

  private refreshTrackedStatuses(): void {
    this.entries
      .filter(entry => entry.result?.requestTracked && entry.result.listenarrId)
      .forEach(entry => this.refreshRequestStatus(entry));
  }

  private refreshRequestStatus(entry: AudiobookResultEntry): void {
    const listenarrId = entry.result?.listenarrId;
    if (!listenarrId) return;
    this.api.getRequestStatus(listenarrId).subscribe({
      next: status => {
        this.requestStatuses[entry.key] = status;
        if (status.state === 'InLibrary' && entry.result) {
          entry.result.availability = 'owned';
          entry.result.ownedAudiobookshelfId = status.audiobookshelfItemId;
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
    const hasActive = Object.values(this.requestStatuses).some(status => ACTIVE_STATES.has(status.state));
    if (!hasActive) {
      this.stopPolling();
      return;
    }
    if (this.pollTimer !== null) return;
    this.pollTimer = window.setInterval(() => {
      this.entries
        .filter(entry => {
          const status = this.requestStatuses[entry.key];
          return !!entry.result?.listenarrId && !!status && ACTIVE_STATES.has(status.state);
        })
        .forEach(entry => this.refreshRequestStatus(entry));
    }, POLL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) window.clearInterval(this.pollTimer);
    this.pollTimer = null;
  }
}

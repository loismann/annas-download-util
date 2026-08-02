import { Component, OnDestroy, OnInit, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatListModule } from '@angular/material/list';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { Subject, timer } from 'rxjs';
import { switchMap, takeUntil, takeWhile } from 'rxjs/operators';
import { ActivatedRoute } from '@angular/router';

import { SpotifinatorApiService } from '../services/spotifinator-api.service';
import { LoggerService } from '../services/logger.service';
import { SpotifyPlaybackService } from '../services/spotify-playback.service';
import { PlanReviewDialogComponent } from './plan-review-dialog/plan-review-dialog.component';
import {
  ChatMessage,
  CommandData,
  SpotifyPlan,
  SpotifyPlanStep,
  SpotifyContentsAccess,
  SpotifyPlaybackState,
  SpotifyDevice,
  PlaybackMode,
  ViewState,
  SpotifyPlaylist,
  SpotifyPlaylistItem,
  SpotifyPlaylistItemsPage,
  SpotifyRecentPlaylistContext,
  SpotifyLibraryAnalysis,
  SpotifyPlaylistOverlap,
  SpotifyDuplicateItemGroup,
  SpotifyTopItems,
  SpotifySearchResult,
  SpotifyConnectionStatus,
  SpotifyInventoryStatus,
  SpotifyDiscoveryDraft
} from './spotifinator.models';

@Component({
  selector: 'app-spotifinator',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatInputModule,
    MatFormFieldModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatProgressBarModule,
    MatListModule,
    MatTooltipModule,
    MatDialogModule
  ],
  templateUrl: './spotifinator.component.html',
  styleUrl: './spotifinator.component.scss'
})
export class SpotifinatorComponent implements OnInit, OnDestroy, AfterViewChecked {
  @ViewChild('chatContainer') private chatContainer!: ElementRef;
  @ViewChild('messageInput') private messageInput!: ElementRef;

  // State
  viewState: ViewState = 'idle';
  userInput = '';
  messages: ChatMessage[] = [];
  errorMessage = '';
  connection: SpotifyConnectionStatus | null = null;
  connectionLoading = true;
  connectionActionPending = false;
  connectionNotice = '';
  inventoryStatus: SpotifyInventoryStatus | null = null;
  inventoryActionPending = false;
  activeDraft: SpotifyDiscoveryDraft | null = null;

  /**
   * Which pane wins when the screen is too narrow to show all three. Ignored
   * entirely on a wide screen — CSS only consults it under the breakpoints — so
   * there is no second layout to keep in step, just a preference the narrow
   * layouts read.
   */
  activePane: 'playlists' | 'library' | 'assistant' = 'library';

  // ─── library pane ─────────────────────────────────────────────────────────
  playlists: SpotifyPlaylist[] = [];
  playlistsLoading = false;
  playlistFilter = '';
  selectedPlaylist: SpotifyPlaylist | null = null;
  selectedItems: SpotifyPlaylistItem[] = [];
  selectedItemsTotal = 0;
  selectedItemsLoading = false;
  selectedItemsAccess: SpotifyContentsAccess = 'Available';

  // ─── playback ─────────────────────────────────────────────────────────────
  playback: SpotifyPlaybackState | null = null;
  playbackMode: PlaybackMode = 'unavailable';
  playbackProblem: string | null = null;
  playbackDevices: SpotifyDevice[] = [];
  savedDrafts: SpotifyDiscoveryDraft[] = [];
  draftActionPending = false;

  // The high-impact acknowledgement lives in PlanReviewDialogComponent now. Keeping
  // a copy here would let a plan be confirmed by a tick made against a different one.
  planActionPending: string | null = null;

  private destroy$ = new Subject<void>();
  private inventoryPollStop$ = new Subject<void>();
  private shouldScrollToBottom = false;

  /** Replayed when the user picks a playlist or pages, so the intent is not lost. */
  private lastMessage = '';
  private pendingAnalysisMessage = '';

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService,
    private route: ActivatedRoute,
    private playbackService: SpotifyPlaybackService,
    private dialog: MatDialog
  ) {
    this.addWelcomeMessage();
  }

  ngOnInit(): void {
    const oauthResult = this.route.snapshot.queryParamMap.get('spotify');
    if (oauthResult === 'connected') {
      this.connectionNotice = 'Spotify connected successfully.';
    } else if (oauthResult) {
      this.connectionNotice = `Spotify authorization did not complete (${oauthResult}).`;
    }

    this.loadConnection();
  }

  /**
   * Playback and the library only start once Spotify is actually connected —
   * otherwise the SDK asks for a token that cannot be issued and the device list is
   * a guaranteed 401.
   */
  private startLibraryAndPlayback(): void {
    if (this.libraryStarted || !this.canUseSpotify) return;
    this.libraryStarted = true;

    this.loadPlaylists();

    this.playbackService.mode.pipe(takeUntil(this.destroy$))
      .subscribe(mode => this.playbackMode = mode);
    this.playbackService.state.pipe(takeUntil(this.destroy$))
      .subscribe(state => this.playback = state);
    this.playbackService.problem.pipe(takeUntil(this.destroy$))
      .subscribe(problem => this.playbackProblem = problem);
    this.playbackService.devices.pipe(takeUntil(this.destroy$))
      .subscribe(devices => this.playbackDevices = devices);

    this.playbackService.initialize()
      .catch(error => this.logger.warn('[Spotifinator] Playback unavailable', error));
  }

  private libraryStarted = false;

  ngAfterViewChecked(): void {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.inventoryPollStop$.next();
    this.inventoryPollStop$.complete();
    this.playbackService.dispose();
  }

  // ─── Message Handling ──────────────────────────────────────────────────────

  onSubmit(): void {
    const message = this.userInput.trim();
    if (!message || this.viewState === 'processing' || !this.canUseSpotify) return;

    this.addUserMessage(message);
    this.userInput = '';
    this.processCommand(message);
  }

  get canUseSpotify(): boolean {
    if (!this.connection?.isConnected) return false;
    if (this.connection.state !== 'RateLimited') return true;
    return !!this.connection.rateLimitedUntil &&
      new Date(this.connection.rateLimitedUntil).getTime() <= Date.now();
  }

  loadConnection(): void {
    this.connectionLoading = true;
    this.api.getConnection().pipe(takeUntil(this.destroy$)).subscribe({
      next: (connection) => {
        this.connection = connection;
        this.connectionLoading = false;
        if (connection.isConnected) {
          this.loadInventoryStatus();
          this.loadSavedDrafts();
          this.loadActiveDraft();
          this.startLibraryAndPlayback();
        }
      },
      error: (err) => {
        this.connectionLoading = false;
        this.connectionNotice = err.error?.error || 'Could not load the Spotify connection status.';
        this.logger.error('[Spotifinator] Connection status failed:', err);
      }
    });
  }

  connectSpotify(forceDialog = false): void {
    this.connectionActionPending = true;
    this.api.beginAuthorization(forceDialog).pipe(takeUntil(this.destroy$)).subscribe({
      next: ({ authorizationUrl }) => window.location.assign(authorizationUrl),
      error: (err) => {
        this.connectionActionPending = false;
        this.connectionNotice = err.error?.error || 'Could not start Spotify authorization.';
        this.logger.error('[Spotifinator] Authorization start failed:', err);
      }
    });
  }

  disconnectSpotify(): void {
    if (!window.confirm('Disconnect Spotify from Spotifinator on this server?')) return;

    this.connectionActionPending = true;
    this.api.disconnect().pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.connectionActionPending = false;
        this.connectionNotice = 'Spotify disconnected from Spotifinator.';
        this.activeDraft = null;
        this.savedDrafts = [];
        localStorage.removeItem('spotifinator.activeDraftId');
        this.loadConnection();
      },
      error: (err) => {
        this.connectionActionPending = false;
        this.connectionNotice = err.error?.error || 'Could not disconnect Spotify.';
        this.logger.error('[Spotifinator] Disconnect failed:', err);
      }
    });
  }

  formatConnectionDate(value: string | null): string {
    return value ? new Date(value).toLocaleString() : 'Not yet';
  }

  loadInventoryStatus(): void {
    this.api.getInventoryStatus().pipe(takeUntil(this.destroy$)).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        if (this.inventoryIsRunning(status)) this.startInventoryPolling();
      },
      error: err => this.logger.error('[Spotifinator] Inventory status failed:', err)
    });
  }

  refreshInventory(): void {
    if (this.inventoryActionPending || !this.canUseSpotify) return;
    this.inventoryActionPending = true;
    this.api.startInventoryRefresh().pipe(takeUntil(this.destroy$)).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        this.inventoryActionPending = false;
        this.startInventoryPolling();
      },
      error: err => {
        this.inventoryActionPending = false;
        this.connectionNotice = err.error?.error || 'Could not start the library inventory.';
        this.logger.error('[Spotifinator] Inventory refresh failed:', err);
      }
    });
  }

  inventoryProgress(status: SpotifyInventoryStatus): number {
    return status.totalPlaylists > 0
      ? Math.round(status.processedPlaylists * 100 / status.totalPlaylists)
      : 0;
  }

  inventoryIsRunning(status: SpotifyInventoryStatus | null): boolean {
    return status?.state === 'Queued' || status?.state === 'Running';
  }

  private startInventoryPolling(): void {
    this.inventoryPollStop$.next();
    timer(0, 2000).pipe(
      switchMap(() => this.api.getInventoryStatus()),
      takeWhile(status => this.inventoryIsRunning(status), true),
      takeUntil(this.inventoryPollStop$),
      takeUntil(this.destroy$)
    ).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        if (!this.inventoryIsRunning(status)) {
          this.inventoryActionPending = false;
          if (this.pendingAnalysisMessage &&
              (status.state === 'Complete' || status.state === 'Partial')) {
            const message = this.pendingAnalysisMessage;
            this.pendingAnalysisMessage = '';
            this.processCommand(message);
          }
        }
      },
      error: err => {
        this.inventoryActionPending = false;
        this.logger.error('[Spotifinator] Inventory polling failed:', err);
      }
    });
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onSubmit();
    }
  }

  // ─── Command Processing ────────────────────────────────────────────────────

  private processCommand(message: string, playlistId?: string, offset?: number): void {
    this.viewState = 'processing';
    const pendingId = this.addPendingMessage();

    this.api.processCommand(message, playlistId, offset, this.activeDraft?.id).pipe(
      takeUntil(this.destroy$)
    ).subscribe({
      next: (response) => {
        this.removePendingMessage(pendingId);
        this.lastMessage = message;

        // A plan the assistant built is a decision, so it opens the review modal
        // and is kept out of the transcript — the reply sentence stays as context.
        // Without this a typed command and the draft button would review changes in
        // two different places.
        const pending = this.isPlan(response.data) && this.planIsPending(response.data);
        this.addAssistantMessage(response.message, pending ? null : response.data);
        if (pending) this.reviewPlan(response.data as SpotifyPlan);

        if (this.isDiscoveryDraft(response.data)) this.setActiveDraft(response.data);
        this.viewState = 'idle';
        if (response.action === 'analyze_playlist_library' && !this.isAnalysis(response.data))
          this.resumeAnalysisAfterInventory(message, response.data);
      },
      error: (err) => {
        this.removePendingMessage(pendingId);
        const errorMsg = err.error?.error || err.message || 'Something went wrong';
        this.addAssistantMessage(`Sorry, I encountered an error: ${errorMsg}`, null, true);
        this.viewState = 'error';
        this.errorMessage = errorMsg;
        this.logger.error('[Spotifinator] Command failed:', err);
      }
    });
  }

  // ─── Message Management ────────────────────────────────────────────────────

  private addWelcomeMessage(): void {
    this.messages.push({
      id: this.generateId(),
      role: 'assistant',
      content: 'Ask about your Spotify library, or describe the music you want me to build into a draft.',
      timestamp: new Date()
    });
  }

  private addUserMessage(content: string): void {
    this.messages.push({
      id: this.generateId(),
      role: 'user',
      content,
      timestamp: new Date()
    });
    this.shouldScrollToBottom = true;
  }

  private addPendingMessage(): string {
    const id = this.generateId();
    this.messages.push({
      id,
      role: 'assistant',
      content: '',
      timestamp: new Date(),
      pending: true
    });
    this.shouldScrollToBottom = true;
    return id;
  }

  private removePendingMessage(id: string): void {
    const index = this.messages.findIndex(m => m.id === id);
    if (index !== -1) {
      this.messages.splice(index, 1);
    }
  }

  private addAssistantMessage(
    content: string,
    data?: CommandData,
    isError = false
  ): void {
    this.messages.push({
      id: this.generateId(),
      role: 'assistant',
      content,
      timestamp: new Date(),
      data,
      error: isError
    });
    this.shouldScrollToBottom = true;
  }

  // ─── Data Type Guards ──────────────────────────────────────────────────────

  isSearchResult(data: unknown): data is SpotifySearchResult {
    return !!data && typeof data === 'object' && 'tracks' in data && Array.isArray((data as SpotifySearchResult).tracks);
  }

  isPlaylistArray(data: unknown): data is SpotifyPlaylist[] {
    return Array.isArray(data) && data.length > 0 && 'contentsAvailable' in data[0];
  }

  isPlaylist(data: unknown): data is SpotifyPlaylist {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'contentsAvailable' in data;
  }

  isItemsPage(data: unknown): data is SpotifyPlaylistItemsPage {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'access' in data;
  }

  isRecentContexts(data: unknown): data is SpotifyRecentPlaylistContext[] {
    return Array.isArray(data) && data.length > 0 && 'observedPlays' in data[0];
  }

  isAnalysis(data: unknown): data is SpotifyLibraryAnalysis {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'playlistsScanned' in data;
  }

  isTopItems(data: unknown): data is SpotifyTopItems {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'timeRange' in data;
  }

  isInventoryStatus(data: unknown): data is SpotifyInventoryStatus {
    return !!data && typeof data === 'object' && !Array.isArray(data) &&
      'processedPlaylists' in data && 'state' in data;
  }

  isDiscoveryDraft(data: unknown): data is SpotifyDiscoveryDraft {
    return !!data && typeof data === 'object' && !Array.isArray(data) &&
      'candidates' in data && 'desiredTrackCount' in data && 'userPrompts' in data;
  }

  candidateResolutionLabel(candidate: SpotifyDiscoveryDraft['candidates'][number]): string {
    // Numeric values keep already-persisted Phase 5 drafts readable across the
    // deployment that changes the API contract to string enum names.
    switch (candidate.resolution as unknown) {
      case 'Resolved': case 0: return 'Matched in Spotify catalog';
      case 'Ambiguous': case 1: return 'Multiple Spotify catalog matches';
      case 'NotFound': case 2: return 'No confident Spotify catalog match';
      default: return 'Spotify catalog status unavailable';
    }
  }

  removeDraftCandidate(draft: SpotifyDiscoveryDraft, candidateId: string): void {
    this.api.updateDiscoveryDraft(draft.id, { removeCandidateIds: [candidateId] })
      .pipe(takeUntil(this.destroy$)).subscribe({
        next: updated => this.setActiveDraft(updated),
        error: err => this.logger.error('[Spotifinator] Could not remove draft candidate:', err)
      });
  }

  moveDraftCandidate(draft: SpotifyDiscoveryDraft, candidateId: string, delta: number): void {
    const ids = draft.candidates.map(candidate => candidate.id);
    const from = ids.indexOf(candidateId);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= ids.length) return;
    [ids[from], ids[to]] = [ids[to], ids[from]];
    this.api.updateDiscoveryDraft(draft.id, { orderedCandidateIds: ids })
      .pipe(takeUntil(this.destroy$)).subscribe({
        next: updated => this.setActiveDraft(updated),
        error: err => this.logger.error('[Spotifinator] Could not reorder draft:', err)
      });
  }

  selectDraftAlternative(draft: SpotifyDiscoveryDraft, candidateId: string, trackId: string): void {
    this.api.updateDiscoveryDraft(draft.id, {
      candidateSelections: { [candidateId]: trackId }
    }).pipe(takeUntil(this.destroy$)).subscribe({
      next: updated => this.setActiveDraft(updated),
      error: err => this.logger.error('[Spotifinator] Could not select Spotify match:', err)
    });
  }

  saveActiveDraft(): void {
    if (!this.activeDraft || this.draftActionPending) return;
    this.draftActionPending = true;
    this.api.updateDiscoveryDraft(this.activeDraft.id, { saved: true })
      .pipe(takeUntil(this.destroy$)).subscribe({
        next: updated => {
          this.draftActionPending = false;
          this.setActiveDraft(updated);
          this.loadSavedDrafts();
        },
        error: err => {
          this.draftActionPending = false;
          this.logger.error('[Spotifinator] Could not save draft:', err);
        }
      });
  }

  closeActiveDraft(): void {
    this.activeDraft = null;
    localStorage.removeItem('spotifinator.activeDraftId');
  }

  /**
   * Whether the connection foldout should start open. Anything the user has to act
   * on opens it; a healthy connection stays collapsed and out of the way.
   */
  connectionNeedsAttention(): boolean {
    if (this.connectionLoading || !this.connection) return true;
    if (!this.connection.isConnected) return true;
    if (this.connection.missingScopes.length > 0) return true;
    return !!this.connection.warning || !!this.connection.lastError;
  }

  /** One line standing in for the whole panel while it is collapsed. */
  connectionSummaryLabel(): string {
    if (this.connectionLoading) return 'Checking…';
    if (!this.connection) return 'Not connected';
    if (!this.connection.isConnected) return 'Not connected — tap to connect';
    if (this.connection.missingScopes.length > 0) return 'Needs reauthorizing';
    if (this.connection.warning) return this.connection.warning;

    const unreadable = this.inventoryStatus?.unreadablePlaylists ?? 0;
    const total = this.inventoryStatus?.totalPlaylists ?? 0;

    if (total === 0) return 'Connected · inventory not refreshed yet';

    return unreadable > 0
      ? `Connected · ${total} playlists, ${unreadable} unreadable`
      : `Connected · ${total} playlists`;
  }

  /** Candidates that actually matched a Spotify track — the only ones creatable. */
  resolvedCandidateCount(draft: SpotifyDiscoveryDraft): number {
    return draft.candidates.filter(c => c.resolution === 'Resolved' && c.track).length;
  }

  /**
   * Turns the draft into a real playlist — via the plan flow, not directly. The
   * review opens as a modal right here, rather than as a card in the chat: the
   * button used to build a plan into a pane the user was not looking at, so it
   * looked like it had done nothing at all.
   */
  createDraftInSpotify(): void {
    if (!this.activeDraft || this.draftActionPending) return;
    if (this.resolvedCandidateCount(this.activeDraft) === 0) return;

    const draft = this.activeDraft;
    this.draftActionPending = true;

    this.api.buildCreateFromDraftPlan(draft.id, draft.name)
      .pipe(takeUntil(this.destroy$)).subscribe({
        next: plan => {
          this.draftActionPending = false;
          this.reviewPlan(plan);
        },
        error: err => {
          this.draftActionPending = false;
          // A refusal is a 400 carrying the real sentence: nothing resolved, no
          // name, over the ceiling. It is an answer, not a crash.
          this.addAssistantMessage(
            err.error?.error || 'That draft could not be turned into a playlist.', null, true);
        }
      });
  }

  /**
   * Throws the draft away for good. Unlike a playlist this really is a delete — the
   * draft has never touched Spotify — so it asks once here rather than going
   * through the plan flow.
   */
  deleteActiveDraft(): void {
    if (!this.activeDraft || this.draftActionPending) return;

    const draft = this.activeDraft;
    const resolved = this.resolvedCandidateCount(draft);
    const confirmed = confirm(
      `Delete the draft "${draft.name}"?\n\n`
      + `${draft.candidates.length} candidates (${resolved} matched) will be lost. `
      + 'Nothing on Spotify is affected — this draft was never a playlist.');

    if (!confirmed) return;

    this.draftActionPending = true;
    this.api.deleteDiscoveryDraft(draft.id).pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.draftActionPending = false;
        this.activeDraft = null;
        localStorage.removeItem('spotifinator.activeDraftId');
        this.savedDrafts = this.savedDrafts.filter(d => d.id !== draft.id);

        // Drop it from the transcript too, so a stale card cannot be re-opened.
        for (const message of this.messages) {
          if (this.isDiscoveryDraft(message.data) && message.data.id === draft.id)
            message.data = null;
        }

        this.addAssistantMessage(`Deleted the draft "${draft.name}". Spotify is untouched.`, null);
      },
      error: err => {
        this.draftActionPending = false;
        this.addAssistantMessage(
          err.error?.error || 'That draft could not be deleted.', null, true);
      }
    });
  }

  openSavedDraft(draft: SpotifyDiscoveryDraft): void {
    this.setActiveDraft(draft);
  }

  private loadSavedDrafts(): void {
    this.api.getSavedDiscoveryDrafts().pipe(takeUntil(this.destroy$)).subscribe({
      next: drafts => this.savedDrafts = drafts,
      error: err => this.logger.error('[Spotifinator] Could not load saved drafts:', err)
    });
  }

  private loadActiveDraft(): void {
    const draftId = localStorage.getItem('spotifinator.activeDraftId');
    if (!draftId) return;
    this.api.getDiscoveryDraft(draftId).pipe(takeUntil(this.destroy$)).subscribe({
      next: draft => this.setActiveDraft(draft),
      error: () => localStorage.removeItem('spotifinator.activeDraftId')
    });
  }

  private setActiveDraft(draft: SpotifyDiscoveryDraft): void {
    this.activeDraft = draft;
    localStorage.setItem('spotifinator.activeDraftId', draft.id);
    for (const message of this.messages) {
      if (this.isDiscoveryDraft(message.data) && message.data.id === draft.id)
        message.data = draft;
    }
    if (draft.savedAt) {
      const index = this.savedDrafts.findIndex(saved => saved.id === draft.id);
      if (index >= 0) this.savedDrafts[index] = draft;
    }
  }

  private resumeAnalysisAfterInventory(message: string, data: unknown): void {
    this.pendingAnalysisMessage = message;
    if (this.isInventoryStatus(data)) {
      this.updateInventoryStatusDisplays(data);
      if (this.inventoryIsRunning(data)) {
        this.startInventoryPolling();
        return;
      }
    }

    // The action is the stable contract. If a serializer/proxy changes the shape
    // of the embedded status, recover from the dedicated endpoint instead of
    // leaving a permanently queued chat card.
    this.api.getInventoryStatus().pipe(takeUntil(this.destroy$)).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        if (this.inventoryIsRunning(status)) this.startInventoryPolling();
        else if (status.state === 'Complete' || status.state === 'Partial') {
          const pending = this.pendingAnalysisMessage;
          this.pendingAnalysisMessage = '';
          if (pending) this.processCommand(pending);
        }
      },
      error: err => this.logger.error('[Spotifinator] Could not resume inventory polling:', err)
    });
  }

  private updateInventoryStatusDisplays(status: SpotifyInventoryStatus): void {
    this.inventoryStatus = status;
    for (const message of this.messages) {
      if (this.isInventoryStatus(message.data)) message.data = status;
    }
  }

  // ─── Rendering helpers ─────────────────────────────────────────────────────

  /**
   * Never renders a number when the count is unknown. The whole point of
   * `trackCount: number | null` is that "0 items" and "Spotify won't tell me"
   * must not look the same.
   */
  itemCountLabel(playlist: SpotifyPlaylist): string {
    if (!playlist.contentsAvailable || playlist.trackCount === null) {
      return 'Contents unavailable';
    }
    return playlist.trackCount === 1 ? '1 item' : `${playlist.trackCount} items`;
  }

  ownershipLabel(playlist: SpotifyPlaylist): string {
    if (playlist.isOwnedByUser) return 'Yours';
    if (playlist.isCollaborative) return 'Collaborative';
    return playlist.ownerName ? `Followed · ${playlist.ownerName}` : 'Followed';
  }

  inventoryLabel(playlist: SpotifyPlaylist): string {
    return playlist.inventoryAt
      ? `Inventoried ${new Date(playlist.inventoryAt).toLocaleString()}`
      : 'Not inventoried yet';
  }

  itemIcon(item: SpotifyPlaylistItem): string {
    switch (item.kind) {
      case 'Episode': return 'podcasts';
      case 'Local': return 'sd_storage';
      case 'Unavailable': return 'help_outline';
      default: return 'music_note';
    }
  }

  itemMeta(item: SpotifyPlaylistItem): string {
    if (item.kind === 'Unavailable') {
      return 'This item is no longer on Spotify';
    }

    const parts = [item.artists, item.albumName].filter(Boolean);
    if (item.kind === 'Local') parts.push('local file');
    if (item.durationMs > 0) parts.push(this.formatDuration(item.durationMs));
    return parts.join(' · ');
  }


  /**
   * Whether an analysis is safe to act on. False whenever any playlist could not
   * be read — the counts below it are then a floor, not a total, and the UI has to
   * say so before anyone treats a list of "empty" playlists as a delete list.
   */
  analysisIsComplete(analysis: SpotifyLibraryAnalysis): boolean {
    return analysis.unreadable.length === 0;
  }

  overlapLabel(overlap: SpotifyPlaylistOverlap): string {
    if (overlap.identical) return 'Identical';
    if (overlap.supersetOf) return 'One contains the other';
    return `${Math.round(overlap.overlap * 100)}% overlap`;
  }

  duplicateLabel(group: SpotifyDuplicateItemGroup): string {
    const where = `positions ${group.positions.map(p => p + 1).join(', ')}`;
    if (group.confidence === 'Exact') return `${group.label} — same Spotify item at ${where}`;
    if (group.confidence === 'Recording') return `${group.label} — same ISRC at ${where}`;
    return `${group.label} — possibly the same recording at ${where}`;
  }

  /** Picks a playlist from a disambiguation card and re-asks the same question. */
  selectPlaylist(playlist: SpotifyPlaylist): void {
    this.addUserMessage(playlist.name);
    this.processCommand(this.lastMessage || `what is in ${playlist.name}`, playlist.id);
  }

  /**
   * Pages straight off the items endpoint. Routing "show more" back through the
   * conversation would spend an AI call to re-derive an intent we already know,
   * and a re-classification could land on a different action entirely.
   */
  loadMoreItems(page: SpotifyPlaylistItemsPage): void {
    if (this.viewState === 'processing') return;

    this.viewState = 'processing';
    const pendingId = this.addPendingMessage();
    const nextOffset = page.offset + page.items.length;

    this.api.getPlaylistItems(page.playlistId, nextOffset, page.limit).pipe(
      takeUntil(this.destroy$)
    ).subscribe({
      next: (next) => {
        this.removePendingMessage(pendingId);
        const last = nextOffset + next.items.length;
        this.addAssistantMessage(`Showing ${nextOffset + 1}–${last} of ${next.total}:`, next);
        this.viewState = 'idle';
      },
      error: (err) => {
        this.removePendingMessage(pendingId);
        this.addAssistantMessage(
          `Sorry, I could not load more: ${err.error?.error || err.message}`, null, true);
        this.viewState = 'error';
        this.logger.error('[Spotifinator] Paging failed:', err);
      }
    });
  }

  // ─── Track Actions ─────────────────────────────────────────────────────────

  openInSpotify(url: string | null): void {
    if (url) {
      window.open(url, '_blank');
    }
  }

  formatDuration(ms: number): string {
    const minutes = Math.floor(ms / 60000);
    const seconds = Math.floor((ms % 60000) / 1000);
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  }

  // ─── Change plans ──────────────────────────────────────────────────────────

  isPlan(data: unknown): data is SpotifyPlan {
    return !!data && typeof data === 'object' && !Array.isArray(data) && 'preview' in data && 'steps' in data;
  }

  /** Only a plan still awaiting a decision can be acted on. */
  planIsPending(plan: SpotifyPlan): boolean {
    return plan.status === 'AwaitingConfirmation' || plan.status === 'Draft';
  }

  /** The receipt's icon. Deliberately three outcomes and no more — a row of status
   *  glyphs per step is what made the transcript unreadable. */
  planOutcomeIcon(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed': return 'check_circle';
      case 'PartiallyCompleted': return 'error_outline';
      case 'Failed': return 'cancel';
      default: return 'info_outline';
    }
  }

  /** Whether the step list is worth offering at all. A plan that simply worked has
   *  nothing to explain, so it gets no expander to ignore. */
  planHasTrouble(plan: SpotifyPlan): boolean {
    return plan.status === 'PartiallyCompleted'
      || plan.status === 'Failed'
      || plan.steps.some(step => step.status === 'Failed' || step.status === 'Skipped');
  }

  planStatusLabel(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed': return 'Done';
      case 'PartiallyCompleted': return 'Partly done';
      case 'Failed': return 'Failed';
      case 'Cancelled': return 'Cancelled';
      case 'Expired': return 'Expired — the playlist changed';
      case 'Reverted': return 'Undone';
      case 'Executing': return 'Running…';
      default: return 'Waiting for you';
    }
  }

  /**
   * The single door every change goes through, wherever it came from — the draft
   * panel, a typed command, or an undo.
   *
   * It has to be one function. When the review lived in the transcript, a plan built
   * from the draft panel put its decision in a pane the user was not looking at, and
   * the button appeared to do nothing. A modal cannot be missed, and routing every
   * source through here means no future entry point can reintroduce that.
   */
  reviewPlan(plan: SpotifyPlan): void {
    this.dialog.open(PlanReviewDialogComponent, {
      data: { plan },
      width: '540px',
      maxWidth: '94vw',
      autoFocus: false
    }).afterClosed().pipe(takeUntil(this.destroy$)).subscribe(executed => {
      // Undefined means cancelled or dismissed. Nothing happened, and saying so in
      // the transcript would be noise about a non-event.
      if (!executed) return;

      this.recordOutcome(executed);
      this.refreshAfterChange(executed);
    });
  }

  /**
   * One line in the transcript, not a panel.
   *
   * The plan is still attached so the receipt can offer Undo or "finish the rest"
   * when those genuinely apply — but the effects, warnings, steps and tick box all
   * did their job in the dialog and do not get a second showing.
   */
  private recordOutcome(plan: SpotifyPlan): void {
    this.addAssistantMessage(
      this.describeOutcome(plan), plan, plan.status === 'Failed');
  }

  /** A friendlier name than the enum for each step in the progress list. */
  planStepLabel(step: SpotifyPlanStep): string {
    const name = step.playlistName ? ` — ${step.playlistName}` : '';

    switch (step.kind) {
      case 'CreatePlaylist': return `Create the playlist${name}`;
      case 'AddItems': return `Add tracks${name}`;
      case 'RemoveItems': return `Remove tracks${name}`;
      case 'ReplaceItems': return `Replace the contents${name}`;
      case 'ReorderItems': return `Reorder${name}`;
      case 'ChangeDetails': return `Update the details${name}`;
      case 'VerifyPlaylistPopulated':
        return `Check everything arrived${name}`;
      case 'RemoveFromLibrary': return `Remove from your library${name}`;
      case 'AddToLibrary': return `Put back in your library${name}`;
      default: return step.kind + name;
    }
  }

  retryPlan(plan: SpotifyPlan): void {
    if (this.planActionPending || !plan.recovery?.canResume) return;

    this.planActionPending = plan.id;
    this.api.retryPlan(plan.id).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resumed) => {
        this.planActionPending = null;
        // Resuming needs no fresh review — the acknowledgement already given still
        // stands — so this runs and reports rather than reopening the dialog.
        this.recordOutcome(resumed);
        this.refreshAfterChange(resumed);
      },
      error: (err) => {
        this.planActionPending = null;
        this.addAssistantMessage(err.error?.error || 'That could not be picked back up.', null, true);
      }
    });
  }

  undoPlan(plan: SpotifyPlan): void {
    if (this.planActionPending) return;

    this.planActionPending = plan.id;
    this.api.undoPlan(plan.id).pipe(takeUntil(this.destroy$)).subscribe({
      next: (undo) => {
        this.planActionPending = null;
        // An undo is a change like any other, so it gets the same review.
        this.reviewPlan(undo);
      },
      error: (err) => {
        this.planActionPending = null;
        this.addAssistantMessage(err.error?.error || 'That cannot be undone.', null, true);
      }
    });
  }

  /**
   * A receipt, not a report. The full effects list was already read in the dialog;
   * repeating it here is the wall of text that made the chat unusable.
   */
  private describeOutcome(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed':
        return plan.preview.summary;
      case 'PartiallyCompleted':
        return `Partly done — ${plan.failure}`;
      case 'Failed':
        return `Nothing changed. ${plan.failure}`;
      default:
        return this.planStatusLabel(plan);
    }
  }

  /**
   * Puts the catalog back in step with what just happened.
   *
   * The server caches the playlist list for fifteen minutes, so without the forced
   * refresh a playlist you just created is invisible — which reads as the change
   * having failed. The open playlist is reloaded too, since a plan can add to or
   * empty the very list being looked at.
   */
  private refreshAfterChange(plan: SpotifyPlan): void {
    if (plan.status !== 'Completed' && plan.status !== 'PartiallyCompleted') return;

    this.loadPlaylists(true);

    const open = this.selectedPlaylist;
    if (open && plan.steps.some(step => step.playlistId === open.id)) {
      this.openPlaylist(open);
    }
  }

  // ─── Library pane ──────────────────────────────────────────────────────────

  /** `forceRefresh` bypasses the server's fifteen-minute cache — used after a change
   *  of ours, which the cache has no way to know about. */
  loadPlaylists(forceRefresh = false): void {
    this.playlistsLoading = true;
    this.api.getPlaylists(forceRefresh).pipe(takeUntil(this.destroy$)).subscribe({
      next: playlists => {
        this.playlistsLoading = false;
        // Yours first, then collaborative, then followed — the order you can
        // actually act on. Alphabetical within each group.
        this.playlists = [...playlists].sort((a, b) => {
          const rank = (p: SpotifyPlaylist) => p.isOwnedByUser ? 0 : p.isCollaborative ? 1 : 2;
          return rank(a) - rank(b) || a.name.localeCompare(b.name);
        });
      },
      error: err => {
        this.playlistsLoading = false;
        this.logger.error('[Spotifinator] Could not load playlists:', err);
      }
    });
  }

  get filteredPlaylists(): SpotifyPlaylist[] {
    const needle = this.playlistFilter.trim().toLowerCase();
    return needle
      ? this.playlists.filter(p => p.name.toLowerCase().includes(needle))
      : this.playlists;
  }

  /** Opens a playlist in the library pane. Distinct from selectPlaylist, which
   *  answers a disambiguation question the assistant asked. */
  openPlaylist(playlist: SpotifyPlaylist): void {
    // On a phone the rail is occupying the only column, so picking a playlist has
    // to hand that column over or the songs land somewhere off screen.
    this.activePane = 'library';
    this.selectedPlaylist = playlist;
    this.selectedItems = [];
    this.selectedItemsTotal = 0;
    this.selectedItemsAccess = 'Available';
    this.loadMoreSelectedItems();
  }

  loadMoreSelectedItems(): void {
    if (!this.selectedPlaylist || this.selectedItemsLoading) return;

    const playlist = this.selectedPlaylist;
    this.selectedItemsLoading = true;

    this.api.getPlaylistItems(playlist.id, this.selectedItems.length, 50)
      .pipe(takeUntil(this.destroy$)).subscribe({
        next: page => {
          this.selectedItemsLoading = false;
          // A slow response for a playlist the user has since navigated away from
          // must not overwrite what they are looking at now.
          if (this.selectedPlaylist?.id !== playlist.id) return;

          this.selectedItems = [...this.selectedItems, ...page.items];
          this.selectedItemsTotal = page.total;
          this.selectedItemsAccess = page.access;
        },
        error: err => {
          this.selectedItemsLoading = false;
          this.logger.error('[Spotifinator] Could not load playlist items:', err);
        }
      });
  }

  // ─── Playback ──────────────────────────────────────────────────────────────

  /** Local files and removed items have no URI, so nothing can play them. */
  canPlayItem(item: SpotifyPlaylistItem): boolean {
    return this.playbackMode !== 'unavailable'
      && !!item.uri
      && item.kind !== 'Local'
      && item.kind !== 'Unavailable';
  }

  itemUnplayableReason(item: SpotifyPlaylistItem): string {
    if (item.kind === 'Local') return 'Local files cannot be played through the Spotify API.';
    if (item.kind === 'Unavailable') return 'This item is no longer on Spotify.';
    if (!item.uri) return 'Spotify gave no playable address for this item.';
    return this.playDisabledReason() ?? '';
  }

  canPlayPlaylist(playlist: SpotifyPlaylist): boolean {
    return this.playbackMode !== 'unavailable' && !!playlist.uri;
  }

  /** Why the play buttons are dead, in words the user can act on. */
  playDisabledReason(): string | null {
    if (this.playbackMode !== 'unavailable') return null;

    return SpotifyPlaybackService.supportsLocalPlayback()
      ? 'Nothing to play on yet. Open Spotify somewhere and press "Check again".'
      : 'This device cannot play in the browser — Spotify does not allow it here. '
        + 'Open Spotify on your phone or a speaker and it will play there.';
  }

  playItem(item: SpotifyPlaylistItem): void {
    if (!this.canPlayItem(item)) return;

    // A track inside a playlist plays *in* that playlist, so what follows is the
    // next song rather than silence.
    const context = this.selectedPlaylist?.uri;
    this.playbackService.play(context
      ? { contextUri: context, offsetPosition: item.position }
      : { uris: [item.uri!] });
  }

  playPlaylist(playlist: SpotifyPlaylist): void {
    if (!this.canPlayPlaylist(playlist)) return;
    this.playbackService.play({ contextUri: playlist.uri! });
  }

  togglePlayPause(): void {
    if (this.playback?.isPlaying) this.playbackService.pause();
    else this.playbackService.play({});
  }

  skipNext(): void { this.playbackService.skipNext(); }
  skipPrevious(): void { this.playbackService.skipPrevious(); }

  toggleShuffle(): void {
    this.playbackService.setShuffle(!this.playback?.isShuffling);
  }

  /** Names the state the press will produce, not the one it is in — a toggle
   *  labelled "Shuffle" tells you nothing about which way it is pointing. */
  shuffleLabel(): string {
    return this.playback?.isShuffling ? 'Turn shuffle off' : 'Turn shuffle on';
  }

  refreshDevices(): void {
    this.playbackService.refreshDevices();
    this.playbackService.refreshState();
  }

  isTrackPlaying(item: SpotifyPlaylistItem): boolean {
    return !!item.uri && this.playback?.track?.uri === item.uri;
  }

  playbackProgressPercent(): number {
    const duration = this.playback?.track?.durationMs ?? 0;
    if (duration <= 0) return 0;
    return Math.min(100, ((this.playback?.progressMs ?? 0) / duration) * 100);
  }

  deviceIcon(type: string): string {
    switch (type.toLowerCase()) {
      case 'computer': return 'computer';
      case 'smartphone': return 'smartphone';
      case 'tablet': return 'tablet';
      case 'speaker': return 'speaker';
      case 'tv': case 'castvideo': return 'tv';
      case 'automobile': return 'directions_car';
      default: return 'devices';
    }
  }

  // ─── Utilities ─────────────────────────────────────────────────────────────

  private scrollToBottom(): void {
    try {
      if (this.chatContainer?.nativeElement) {
        this.chatContainer.nativeElement.scrollTop = this.chatContainer.nativeElement.scrollHeight;
      }
    } catch (err) {
      this.logger.error('[Spotifinator] Scroll error:', err);
    }
  }

  private generateId(): string {
    return `msg-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
  }

  focusInput(): void {
    this.messageInput?.nativeElement?.focus();
  }
}

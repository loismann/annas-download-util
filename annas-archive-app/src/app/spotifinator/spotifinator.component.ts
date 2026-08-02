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
import { Subject, timer } from 'rxjs';
import { switchMap, takeUntil, takeWhile } from 'rxjs/operators';
import { ActivatedRoute } from '@angular/router';

import { SpotifinatorApiService } from '../services/spotifinator-api.service';
import { LoggerService } from '../services/logger.service';
import {
  ChatMessage,
  CommandData,
  SpotifyPlan,
  SpotifyPlanStep,
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
    MatTooltipModule
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
  savedDrafts: SpotifyDiscoveryDraft[] = [];
  draftActionPending = false;

  /** Plan IDs the user has ticked the high-impact box for. */
  highImpactAcknowledged = new Set<string>();
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
    private route: ActivatedRoute
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
        this.addAssistantMessage(response.message, response.data);
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
   * button produces a review card showing the name and every track; nothing is
   * written until that card is confirmed.
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
          this.addAssistantMessage(
            'Here is what creating that would do. Nothing has changed yet — confirm it and I will build it.',
            plan);
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

  planIsBlocked(plan: SpotifyPlan): boolean {
    return plan.preview.requiresHighImpactAcknowledgement && !this.highImpactAcknowledged.has(plan.id);
  }

  toggleHighImpact(plan: SpotifyPlan, acknowledged: boolean): void {
    if (acknowledged) this.highImpactAcknowledged.add(plan.id);
    else this.highImpactAcknowledged.delete(plan.id);
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

  confirmPlan(plan: SpotifyPlan): void {
    if (this.planActionPending || this.planIsBlocked(plan)) return;

    this.planActionPending = plan.id;
    this.api.confirmPlan(plan.id, this.highImpactAcknowledged.has(plan.id))
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (executed) => {
          this.planActionPending = null;
          this.replacePlanInTranscript(executed);
          this.addAssistantMessage(this.describeOutcome(executed), executed);
        },
        error: (err) => {
          this.planActionPending = null;
          // 409 carries the real explanation — expired, drifted, or needing the
          // high-impact tick — and it is the sentence the user needs to read.
          this.addAssistantMessage(err.error?.error || 'That change could not be applied.', null, true);
        }
      });
  }

  cancelPlan(plan: SpotifyPlan): void {
    if (this.planActionPending) return;

    this.planActionPending = plan.id;
    this.api.cancelPlan(plan.id).pipe(takeUntil(this.destroy$)).subscribe({
      next: (cancelled) => {
        this.planActionPending = null;
        this.replacePlanInTranscript(cancelled);
        this.addAssistantMessage('Cancelled — nothing was changed.', null);
      },
      error: (err) => {
        this.planActionPending = null;
        this.addAssistantMessage(err.error?.error || 'Could not cancel that.', null, true);
      }
    });
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
        this.replacePlanInTranscript(resumed);
        this.addAssistantMessage(this.describeOutcome(resumed), resumed);
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
        this.addAssistantMessage(
          'Here is what undoing that would do. It needs confirming like any other change.', undo);
      },
      error: (err) => {
        this.planActionPending = null;
        this.addAssistantMessage(err.error?.error || 'That cannot be undone.', null, true);
      }
    });
  }

  private describeOutcome(plan: SpotifyPlan): string {
    switch (plan.status) {
      case 'Completed':
        return 'Done. ' + plan.preview.effects.join(' ');
      case 'PartiallyCompleted':
        return `Partly done — ${plan.failure} The steps that succeeded are listed above; `
             + 'the rest were not attempted.';
      case 'Failed':
        return `Nothing changed. ${plan.failure}`;
      default:
        return this.planStatusLabel(plan);
    }
  }

  /** Keeps the reviewed card in the transcript in step with what actually happened. */
  private replacePlanInTranscript(plan: SpotifyPlan): void {
    for (const message of this.messages) {
      if (this.isPlan(message.data) && message.data.id === plan.id) {
        message.data = plan;
      }
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

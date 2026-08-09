import { Component, OnDestroy, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
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

import { SpotifinatorApiService } from '../services/spotifinator-api.service';
import { LoggerService } from '../services/logger.service';
import { SpotifyPlaybackService } from '../services/spotify-playback.service';
import { PlanReviewDialogComponent } from './plan-review-dialog/plan-review-dialog.component';
import { SpotifyNowPlayingComponent } from './now-playing/now-playing.component';
import { SpotifyDraftPanelComponent } from './draft-panel/draft-panel.component';
import { SpotifyLibraryPaneComponent } from './library-pane/library-pane.component';
import { SpotifyConnectionPanelComponent } from './connection-panel/connection-panel.component';
import { SpotifinatorPresentation as Present } from './spotifinator.presentation';
import {
  ChatMessage,
  CommandData,
  SpotifyPlan,
  ViewState,
  SpotifyPlaylist,
  SpotifyPlaylistItemsPage,
  SpotifyPlaybackState,
  PlaybackMode,
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
    MatDialogModule,
    SpotifyNowPlayingComponent,
    SpotifyDraftPanelComponent,
    SpotifyLibraryPaneComponent,
    SpotifyConnectionPanelComponent
  ],
  templateUrl: './spotifinator.component.html',
  styleUrl: './spotifinator.component.scss'
})
export class SpotifinatorComponent implements OnDestroy, AfterViewChecked {
  @ViewChild('chatContainer') private chatContainer!: ElementRef;
  @ViewChild('messageInput') private messageInput!: ElementRef;
  @ViewChild(SpotifyLibraryPaneComponent) private libraryPane?: SpotifyLibraryPaneComponent;

  /**
   * Everything the template says about a value it was handed. Exposed as one
   * member because a template can only reach component members — see
   * `spotifinator.presentation.ts` for why none of it lives on this class.
   */
  readonly present = Present;

  // State
  viewState: ViewState = 'idle';
  userInput = '';
  messages: ChatMessage[] = [];
  errorMessage = '';
  connection: SpotifyConnectionStatus | null = null;
  inventoryStatus: SpotifyInventoryStatus | null = null;
  inventoryActionPending = false;
  inventoryError: string | null = null;
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

  // ─── playback ─────────────────────────────────────────────────────────────
  playback: SpotifyPlaybackState | null = null;
  playbackMode: PlaybackMode = 'unavailable';
  playbackProblem: string | null = null;

  // ─── drafts ───────────────────────────────────────────────────────────────
  savedDrafts: SpotifyDiscoveryDraft[] = [];

  // The high-impact acknowledgement lives in PlanReviewDialogComponent now. Keeping
  // a copy here would let a plan be confirmed by a tick made against a different one.
  planActionPending: string | null = null;

  /**
   * Ends in-flight *reads* when the component is destroyed.
   *
   * Reads only. Unsubscribing an HttpClient call aborts the request, so a write
   * routed through here is a user action that navigating away cancels — a
   * confirmed draft delete, a plan being resumed against Spotify, a reordered
   * candidate list. Classification is by the service method's HTTP verb, never
   * by its name.
   */
  private destroy$ = new Subject<void>();
  private inventoryPollStop$ = new Subject<void>();

  /**
   * Set in ngOnDestroy. Because writes are deliberately *not* cancelled, their
   * responses can land after the page is gone — so anything that puts something
   * on screen (a modal, a redirect) has to check this, or it arrives on top of
   * wherever the user navigated to instead.
   */
  private destroyed = false;
  private shouldScrollToBottom = false;

  /** Replayed when the user picks a playlist or pages, so the intent is not lost. */
  private lastMessage = '';
  private pendingAnalysisMessage = '';

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService,
    private playbackService: SpotifyPlaybackService,
    private dialog: MatDialog
  ) {
    this.addWelcomeMessage();
  }

  /** Everything downstream of the connection waits for the panel to report one. */
  onConnectionChanged(connection: SpotifyConnectionStatus): void {
    this.connection = connection;
    if (!connection.isConnected) return;

    this.loadInventoryStatus();
    this.loadSavedDrafts();
    this.loadActiveDraft();
    this.startLibraryAndPlayback();
  }

  /** Drafts belong to the account that is going away. */
  onDisconnected(): void {
    this.activeDraft = null;
    this.savedDrafts = [];
    localStorage.removeItem('spotifinator.activeDraftId');
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
    this.destroyed = true;
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

  loadInventoryStatus(): void {
    this.api.getInventoryStatus().pipe(takeUntil(this.destroy$)).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        if (Present.inventoryIsRunning(status)) this.startInventoryPolling();
      },
      error: err => this.logger.error('[Spotifinator] Inventory status failed:', err)
    });
  }

  refreshInventory(): void {
    if (this.inventoryActionPending || !this.canUseSpotify) return;
    this.inventoryActionPending = true;
    this.api.startInventoryRefresh().subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        this.inventoryActionPending = false;
        this.startInventoryPolling();
      },
      error: err => {
        this.inventoryActionPending = false;
        // The button is in the panel, so the answer has to appear there too —
        // a refresh that will not even start produces no status to render.
        this.inventoryError = err.error?.error || 'Could not start the library inventory.';
        this.logger.error('[Spotifinator] Inventory refresh failed:', err);
      }
    });
  }

  private startInventoryPolling(): void {
    this.inventoryPollStop$.next();
    timer(0, 2000).pipe(
      switchMap(() => this.api.getInventoryStatus()),
      takeWhile(status => Present.inventoryIsRunning(status), true),
      takeUntil(this.inventoryPollStop$),
      takeUntil(this.destroy$)
    ).subscribe({
      next: status => {
        this.updateInventoryStatusDisplays(status);
        if (!Present.inventoryIsRunning(status)) {
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

    this.api.processCommand(message, playlistId, offset, this.activeDraft?.id).subscribe({
      next: (response) => {
        this.removePendingMessage(pendingId);
        this.lastMessage = message;

        // A plan the assistant built is a decision, so it opens the review modal
        // and is kept out of the transcript — the reply sentence stays as context.
        // Without this a typed command and the draft button would review changes in
        // two different places.
        const pending = Present.isPlan(response.data) && Present.planIsPending(response.data);
        this.addAssistantMessage(response.message, pending ? null : response.data);
        if (pending) this.reviewPlan(response.data as SpotifyPlan);

        if (Present.isDiscoveryDraft(response.data)) this.setActiveDraft(response.data);
        this.viewState = 'idle';
        if (response.action === 'analyze_playlist_library' && !Present.isAnalysis(response.data))
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

  // ─── Discovery drafts ──────────────────────────────────────────────────────
  //
  // The panel itself does the writing; what stays here is everywhere else the
  // same draft appears — the transcript card, the saved list, and which draft
  // the next chat command is about.

  /**
   * A draft the panel changed. The sidebar refetch is only for a draft that has
   * just joined it: `setActiveDraft` already replaces an entry that is there,
   * and a save is the one edit that can add one.
   */
  onDraftChanged(draft: SpotifyDiscoveryDraft): void {
    const isNewToSidebar = !!draft.savedAt && !this.savedDrafts.some(d => d.id === draft.id);
    this.setActiveDraft(draft);
    if (isNewToSidebar) this.loadSavedDrafts();
  }

  /** A refusal the panel could not act on — it belongs in the transcript. */
  onDraftFailed(message: string): void {
    this.addAssistantMessage(message, null, true);
  }

  onDraftDeleted(draft: SpotifyDiscoveryDraft): void {
    this.activeDraft = null;
    localStorage.removeItem('spotifinator.activeDraftId');
    this.savedDrafts = this.savedDrafts.filter(d => d.id !== draft.id);

    // Drop it from the transcript too, so a stale card cannot be re-opened.
    for (const message of this.messages) {
      if (Present.isDiscoveryDraft(message.data) && message.data.id === draft.id)
        message.data = null;
    }

    this.addAssistantMessage(`Deleted the draft "${draft.name}". Spotify is untouched.`, null);
  }

  closeActiveDraft(): void {
    this.activeDraft = null;
    localStorage.removeItem('spotifinator.activeDraftId');
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
      if (Present.isDiscoveryDraft(message.data) && message.data.id === draft.id)
        message.data = draft;
    }
    if (draft.savedAt) {
      const index = this.savedDrafts.findIndex(saved => saved.id === draft.id);
      if (index >= 0) this.savedDrafts[index] = draft;
    }
  }

  private resumeAnalysisAfterInventory(message: string, data: unknown): void {
    this.pendingAnalysisMessage = message;
    if (Present.isInventoryStatus(data)) {
      this.updateInventoryStatusDisplays(data);
      if (Present.inventoryIsRunning(data)) {
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
        if (Present.inventoryIsRunning(status)) this.startInventoryPolling();
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
      if (Present.isInventoryStatus(message.data)) message.data = status;
    }
  }

  // ─── Rendering helpers ─────────────────────────────────────────────────────

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

  // ─── Change plans ──────────────────────────────────────────────────────────

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
    // Building an undo or a create-from-draft plan is a POST, and those are no
    // longer cancelled on destroy — so the reply can outlive the page. Opening
    // the review then would drop a Spotify modal over whatever came next.
    if (this.destroyed) return;

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
      Present.describeOutcome(plan), plan, plan.status === 'Failed');
  }

  retryPlan(plan: SpotifyPlan): void {
    if (this.planActionPending || !plan.recovery?.canResume) return;

    this.planActionPending = plan.id;
    this.api.retryPlan(plan.id).subscribe({
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
    this.api.undoPlan(plan.id).subscribe({
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

    // The open playlist is reloaded too, since a plan can add to or empty the
    // very list being looked at. Its identity has not changed, so an input alone
    // would not tell the pane anything happened.
    const open = this.selectedPlaylist;
    if (open && plan.steps.some(step => step.playlistId === open.id)) {
      this.libraryPane?.reload();
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

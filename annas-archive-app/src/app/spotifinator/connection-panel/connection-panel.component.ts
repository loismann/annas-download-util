import { Component, EventEmitter, Input, OnInit, Output, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { SpotifinatorApiService } from '../../services/spotifinator-api.service';
import { LoggerService } from '../../services/logger.service';
import { SpotifinatorPresentation as Present } from '../spotifinator.presentation';
import { SpotifyConnectionStatus, SpotifyInventoryStatus } from '../spotifinator.models';

/**
 * The account foldout at the top of the sidebar.
 *
 * Collapsed by default: it is the least interesting thing on the page once you
 * are connected, and it was occupying the most valuable space. The summary line
 * surfaces anything that needs action, so a problem is still visible shut.
 *
 * It owns the connection, because nothing else changes it. It does *not* own the
 * inventory: refreshing the library is what unblocks an analysis the assistant
 * is waiting to answer, so that state belongs to the page and only its controls
 * live here.
 */
@Component({
  selector: 'app-spotify-connection-panel',
  standalone: true,
  imports: [
    CommonModule, MatButtonModule, MatIconModule,
    MatProgressBarModule, MatProgressSpinnerModule
  ],
  templateUrl: './connection-panel.component.html',
  styleUrl: './connection-panel.component.scss'
})
export class SpotifyConnectionPanelComponent implements OnInit {
  @Input() inventoryStatus: SpotifyInventoryStatus | null = null;
  @Input() inventoryPending = false;

  /** A refresh the page could not even start; there is no status to show for it. */
  @Input() inventoryError: string | null = null;

  /** Every successful read of the connection, including the first. */
  @Output() connectionChanged = new EventEmitter<SpotifyConnectionStatus>();

  /** Spotify is gone; whatever else was keyed to that account has to go too. */
  @Output() disconnected = new EventEmitter<void>();

  @Output() refreshInventoryRequested = new EventEmitter<void>();

  readonly present = Present;

  connection: SpotifyConnectionStatus | null = null;
  loading = true;
  actionPending = false;
  notice = '';

  /** Reads only. The writes below must not be cancelled — see `destroy$` on the page. */
  private readonly destroyRef = inject(DestroyRef);

  /** Guards the redirect, which is the one thing that outlives this component. */
  private destroyed = false;

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService,
    private route: ActivatedRoute
  ) {
    this.destroyRef.onDestroy(() => (this.destroyed = true));
  }

  ngOnInit(): void {
    // Spotify sends the browser back here after the consent screen, and the
    // outcome is in the query string rather than in any response we can read.
    const oauthResult = this.route.snapshot.queryParamMap.get('spotify');
    if (oauthResult === 'connected') {
      this.notice = 'Spotify connected successfully.';
    } else if (oauthResult) {
      this.notice = `Spotify authorization did not complete (${oauthResult}).`;
    }

    this.load();
  }

  load(): void {
    this.loading = true;
    this.api.getConnection().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (connection) => {
        this.connection = connection;
        this.loading = false;
        this.connectionChanged.emit(connection);
      },
      error: (err) => {
        this.loading = false;
        this.notice = err.error?.error || 'Could not load the Spotify connection status.';
        this.logger.error('[Spotifinator] Connection status failed:', err);
      }
    });
  }

  connect(): void {
    this.actionPending = true;
    this.api.beginAuthorization(true).subscribe({
      // The POST reserves the PKCE state server-side, so it is not cancelled —
      // but the redirect it exists to produce must not fire at someone who has
      // already navigated somewhere else.
      next: ({ authorizationUrl }) => {
        if (!this.destroyed) this.navigateTo(authorizationUrl);
      },
      error: (err) => {
        this.actionPending = false;
        this.notice = err.error?.error || 'Could not start Spotify authorization.';
        this.logger.error('[Spotifinator] Authorization start failed:', err);
      }
    });
  }

  disconnect(): void {
    if (!window.confirm('Disconnect Spotify from Spotifinator on this server?')) return;

    this.actionPending = true;
    this.api.disconnect().subscribe({
      next: () => {
        this.actionPending = false;
        this.notice = 'Spotify disconnected from Spotifinator.';
        this.disconnected.emit();
        this.load();
      },
      error: (err) => {
        this.actionPending = false;
        this.notice = err.error?.error || 'Could not disconnect Spotify.';
        this.logger.error('[Spotifinator] Disconnect failed:', err);
      }
    });
  }

  /**
   * Leaving the app for Spotify's consent screen.
   *
   * A method rather than the call inline because `window.location.assign` cannot
   * be stubbed — it is non-configurable — and letting the real one run in a test
   * navigates the Karma page away and takes the whole run with it.
   */
  protected navigateTo(url: string): void {
    window.location.assign(url);
  }

  /**
   * Whether the foldout should start open. Anything the user has to act on opens
   * it; a healthy connection stays collapsed and out of the way.
   */
  needsAttention(): boolean {
    if (this.loading || !this.connection) return true;
    if (!this.connection.isConnected) return true;
    if (this.connection.missingScopes.length > 0) return true;
    return !!this.connection.warning || !!this.connection.lastError;
  }

  /** One line standing in for the whole panel while it is collapsed. */
  summaryLabel(): string {
    if (this.loading) return 'Checking…';
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
}

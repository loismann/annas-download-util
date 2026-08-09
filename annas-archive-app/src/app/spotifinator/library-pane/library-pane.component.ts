import { Component, Input, OnChanges, SimpleChanges, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SpotifinatorApiService } from '../../services/spotifinator-api.service';
import { LoggerService } from '../../services/logger.service';
import { SpotifyPlaybackService } from '../../services/spotify-playback.service';
import { SpotifinatorPresentation as Present } from '../spotifinator.presentation';
import {
  PlaybackMode, SpotifyContentsAccess, SpotifyPlaybackState,
  SpotifyPlaylist, SpotifyPlaylistItem
} from '../spotifinator.models';

const PAGE_SIZE = 50;

/**
 * What is inside the playlist you are looking at.
 *
 * The pane the feature was missing: before it, every song in the library was
 * reachable only by typing a sentence about it. Which playlist is open is the
 * page's business — it comes in as an input — and everything about that
 * playlist's contents is this component's.
 */
@Component({
  selector: 'app-spotify-library-pane',
  standalone: true,
  imports: [
    CommonModule, MatButtonModule, MatIconModule,
    MatProgressSpinnerModule, MatTooltipModule
  ],
  templateUrl: './library-pane.component.html',
  styleUrl: './library-pane.component.scss'
})
export class SpotifyLibraryPaneComponent implements OnChanges {
  /** Null until someone picks one; the pane then says so rather than sitting blank. */
  @Input() playlist: SpotifyPlaylist | null = null;

  @Input() playback: SpotifyPlaybackState | null = null;
  @Input() playbackMode: PlaybackMode = 'unavailable';

  readonly present = Present;

  items: SpotifyPlaylistItem[] = [];
  itemsTotal = 0;
  itemsLoading = false;
  itemsAccess: SpotifyContentsAccess = 'Available';

  /** Reads only — paging is a GET, and nothing here writes. */
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService,
    private playbackService: SpotifyPlaybackService
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['playlist']) this.reload();
  }

  /**
   * Starts the contents again from the top.
   *
   * Public because a change plan can add to or empty the very playlist being
   * looked at, and the page has no other way to say "that one moved under you".
   */
  reload(): void {
    this.items = [];
    this.itemsTotal = 0;
    this.itemsAccess = 'Available';
    this.itemsLoading = false;
    this.loadMore();
  }

  loadMore(): void {
    if (!this.playlist || this.itemsLoading) return;

    const playlist = this.playlist;
    this.itemsLoading = true;

    this.api.getPlaylistItems(playlist.id, this.items.length, PAGE_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: page => {
          this.itemsLoading = false;
          // A slow response for a playlist the user has since navigated away from
          // must not overwrite what they are looking at now.
          if (this.playlist?.id !== playlist.id) return;

          this.items = [...this.items, ...page.items];
          this.itemsTotal = page.total;
          this.itemsAccess = page.access;
        },
        error: err => {
          this.itemsLoading = false;
          this.logger.error('[Spotifinator] Could not load playlist items:', err);
        }
      });
  }

  openInSpotify(url: string | null): void {
    if (url) window.open(url, '_blank');
  }

  // ─── Playing what is in here ─────────────────────────────────────────────

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
    const context = this.playlist?.uri;
    this.playbackService.play(context
      ? { contextUri: context, offsetPosition: item.position }
      : { uris: [item.uri!] });
  }

  playPlaylist(playlist: SpotifyPlaylist): void {
    if (!this.canPlayPlaylist(playlist)) return;
    this.playbackService.play({ contextUri: playlist.uri! });
  }

  isTrackPlaying(item: SpotifyPlaylistItem): boolean {
    return !!item.uri && this.playback?.track?.uri === item.uri;
  }
}

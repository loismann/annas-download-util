import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SpotifyPlaybackService } from '../../services/spotify-playback.service';
import { SpotifinatorPresentation as Present } from '../spotifinator.presentation';
import { SpotifyPlaybackState } from '../spotifinator.models';

/**
 * The transport bar along the bottom of the Spotifinator card.
 *
 * State comes in rather than being subscribed to here: the track list above also
 * needs to know what is playing, so there is one subscription on the parent and
 * one source of truth. Commands go straight out to the playback service — they
 * are fire-and-forget, and the resulting state arrives back through that same
 * subscription.
 */
@Component({
  selector: 'app-spotify-now-playing',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './now-playing.component.html',
  styleUrl: './now-playing.component.scss'
})
export class SpotifyNowPlayingComponent {
  @Input() playback: SpotifyPlaybackState | null = null;

  /** Why there is nothing to play, when there is nothing to play. */
  @Input() problem: string | null = null;

  readonly present = Present;

  constructor(private playbackService: SpotifyPlaybackService) {}

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

  progressPercent(): number {
    const duration = this.playback?.track?.durationMs ?? 0;
    if (duration <= 0) return 0;
    return Math.min(100, ((this.playback?.progressMs ?? 0) / duration) * 100);
  }
}

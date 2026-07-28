import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { DateNightApiService } from '../../services/date-night-api.service';
import { MediaLibraryApiService } from '../../services/media-library-api.service';
import { JellyfinPlayerModalComponent, JellyfinPlayerModalData } from '../jellyfin-player-modal/jellyfin-player-modal.component';

export interface DateNightCountdownData {
  title: string;
  tmdbId?: number;
  showtimeUtc: string;
}

/**
 * The showtime popup — appears on both accounts within 10 minutes of the locked
 * slot (see AppComponent's showtime poll), counts down, and at zero offers a
 * single Start button. Playback itself is not new: it calls
 * MediaLibraryApiService.watchMovie + opens JellyfinPlayerModalComponent exactly
 * the way MediaLibraryComponent.playMovie() already does for the regular library.
 *
 * "Mark as watched" deliberately isn't here — it lives on the /date-night lobby
 * page instead (DateNightComponent), so it doesn't depend on this dialog still
 * being open/alive after the player modal takes over the screen.
 */
@Component({
  selector: 'app-date-night-countdown',
  standalone: true,
  imports: [CommonModule, MatDialogModule],
  template: `
    <div class="thtr-stage countdown-stage">
      <div class="thtr-inner">
        <button type="button" class="thtr-close" aria-label="Close" (click)="dismiss()">✕</button>

        <p class="thtr-eyebrow">{{ secondsLeft > 0 ? 'Now Showing In' : 'Now Showing' }}</p>
        <h1 class="thtr-title countdown-title">{{ data.title }}</h1>

        <div class="thtr-countdown-clock" [class.thtr-countdown-clock--zero]="secondsLeft <= 0">
          {{ clockLabel }}
        </div>

        <button *ngIf="secondsLeft <= 0" class="thtr-btn" (click)="start()">▶ Start</button>

        <p class="cancel-row">
          <button type="button" class="link-btn" (click)="cancel()">Cancel this date night</button>
        </p>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .countdown-stage { max-width: 380px; margin: 0 auto; }
    .countdown-title { font-size: 2.6rem !important; }
    .cancel-row { margin: 14px 0 0; }
    .link-btn {
      background: none; border: none; color: var(--thtr-parchment); opacity: .7;
      text-decoration: underline; font: inherit; cursor: pointer; padding: 4px;
    }
  `]
})
export class DateNightCountdownComponent implements OnInit, OnDestroy {
  secondsLeft = 0;
  private timer?: ReturnType<typeof setInterval>;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: DateNightCountdownData,
    private dialogRef: MatDialogRef<DateNightCountdownComponent>,
    private api: DateNightApiService,
    private mediaApi: MediaLibraryApiService,
    private dialog: MatDialog
  ) {}

  ngOnInit(): void {
    this.tick();
    this.timer = setInterval(() => this.tick(), 1000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private tick(): void {
    const target = new Date(this.data.showtimeUtc).getTime();
    this.secondsLeft = Math.max(0, Math.round((target - Date.now()) / 1000));
  }

  get clockLabel(): string {
    if (this.secondsLeft <= 0) return "Let's go!";
    const h = Math.floor(this.secondsLeft / 3600);
    const m = Math.floor((this.secondsLeft % 3600) / 60);
    const s = this.secondsLeft % 60;
    const pad = (n: number) => n.toString().padStart(2, '0');
    return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
  }

  dismiss(): void {
    this.dialogRef.close();
  }

  cancel(): void {
    this.api.cancelSchedule().subscribe();
    this.dialogRef.close();
  }

  start(): void {
    if (this.data.tmdbId == null) return;
    const tmdbId = this.data.tmdbId;

    this.mediaApi.watchMovie(tmdbId).subscribe({
      next: (resp) => {
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: {
            title: this.data.title,
            mode: resp.mode,
            embedUrl: resp.embedUrl,
            streamUrl: resp.mode === 'native'
              ? (resp.playbackMode === 'transcode' ? this.mediaApi.getMovieHlsMasterUrl(tmdbId) : this.mediaApi.getMovieStreamUrl(tmdbId))
              : undefined,
            isHls: resp.playbackMode === 'transcode',
            resumePositionSeconds: resp.resumePositionSeconds,
            durationSeconds: resp.durationSeconds,
            audioTracks: resp.audioTracks,
            subtitleTracks: resp.subtitleTracks,
            subtitleUrlFor: resp.mode === 'native' && resp.mediaSourceId
              ? (subtitleIndex) => this.mediaApi.getMovieSubtitleUrl(tmdbId, resp.mediaSourceId!, subtitleIndex)
              : undefined,
            saveProgress: resp.mode === 'native'
              ? (positionSeconds) => this.mediaApi.saveMovieProgress(tmdbId, positionSeconds).subscribe()
              : undefined
          }
        });
        this.dialogRef.close();
      },
      error: () => {
        // Jellyfin hasn't matched it yet, or the grab hasn't finished — leave the
        // countdown open with the Start button so they can try again in a moment.
      }
    });
  }
}

import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { DateNightApiService } from '../../services/date-night-api.service';
import { DateNightPlaybackService } from '../../services/date-night-playback.service';
import { formatCountdown, secondsUntil } from '../../date-night/countdown.util';

export interface DateNightCountdownData {
  title: string;
  tmdbId?: number;
  showtimeUtc: string;
}

/**
 * The showtime popup — appears on both accounts within 10 minutes of the locked
 * slot (see AppComponent's showtime poll), fills the viewport, counts down, and
 * at zero offers a single Play button. DateNightPlaybackService reuses the
 * regular library's Jellyfin watch/player path after the server validates the
 * one-hour start window.
 *
 * "Mark as watched" deliberately isn't here — it lives on the /date-night lobby
 * page instead (DateNightComponent), so it doesn't depend on this dialog still
 * being open/alive after the player modal takes over the screen.
 */
@Component({
  selector: 'app-date-night-countdown',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatIconModule],
  template: `
    <div class="thtr-stage countdown-stage">
      <div class="thtr-bulbs" aria-hidden="true">
        <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
      </div>
      <div class="thtr-inner">
        <button type="button" class="thtr-close" aria-label="Close" (click)="dismiss()">
          <mat-icon>close</mat-icon>
        </button>

        <p class="thtr-eyebrow">{{ secondsLeft > 0 ? 'Now Showing In' : 'Now Showing' }}</p>
        <h1 class="thtr-title countdown-title">{{ data.title }}</h1>

        <div class="thtr-countdown-clock" [class.thtr-countdown-clock--zero]="secondsLeft <= 0">
          {{ clockLabel }}
        </div>

        <button *ngIf="secondsLeft <= 0" class="thtr-btn" [disabled]="starting" (click)="start()">
          <mat-icon>play_arrow</mat-icon> {{ starting ? 'Starting…' : 'Play' }}
        </button>
        <p class="play-error" *ngIf="error">{{ error }}</p>

        <p class="cancel-row">
          <button type="button" class="thtr-link" (click)="cancel()">Cancel this date night</button>
        </p>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; width: 100vw; height: 100vh; }
    .countdown-stage {
      box-sizing: border-box; width: 100vw; max-width: none; min-height: 100vh;
      margin: 0; display: flex; align-items: center; justify-content: center;
    }
    .countdown-stage .thtr-inner {
      box-sizing: border-box;
      width: min(760px, calc(100vw - 72px));
      padding: clamp(64px, 8vh, 92px) clamp(28px, 6vw, 72px) clamp(38px, 6vh, 64px);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: clamp(18px, 3vh, 30px);
      overflow: visible;
    }
    .countdown-stage .thtr-eyebrow { margin: 0; }
    .countdown-title {
      max-width: 100%;
      margin: 0 !important;
      font-size: clamp(2.4rem, 6vw, 4.6rem) !important;
      line-height: .98 !important;
      text-wrap: balance;
    }
    .countdown-stage .thtr-countdown-clock {
      margin: 0;
      font-size: clamp(4.2rem, 10vw, 7.5rem);
      line-height: 1;
    }
    .cancel-row { margin: 0; }
    .play-error { color: #ff9b9b; margin: 0; }
    @media (max-width: 620px) {
      .countdown-stage .thtr-inner {
        width: calc(100vw - 40px);
        padding: 68px 20px 36px;
        gap: 18px;
      }
    }
  `]
})
export class DateNightCountdownComponent implements OnInit, OnDestroy {
  secondsLeft = 0;
  starting = false;
  error: string | null = null;
  private timer?: ReturnType<typeof setInterval>;
  private expiryRequested = false;
  readonly hBulbs = Array.from({ length: 256 });
  readonly vBulbs = Array.from({ length: 256 });

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: DateNightCountdownData,
    private dialogRef: MatDialogRef<DateNightCountdownComponent>,
    private api: DateNightApiService,
    private playback: DateNightPlaybackService
  ) {}

  ngOnInit(): void {
    this.tick();
    this.timer = setInterval(() => this.tick(), 1000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private tick(): void {
    this.secondsLeft = secondsUntil(this.data.showtimeUtc);
    const graceEnds = new Date(this.data.showtimeUtc).getTime() + 60 * 60 * 1000;
    if (!this.expiryRequested && Date.now() > graceEnds) {
      this.expiryRequested = true;
      // This poll performs the server-side missed-showtime transition. Close
      // only after asking it to persist that cancellation.
      this.api.checkShowtime().subscribe({
        next: () => this.dialogRef.close('expired'),
        error: () => { this.expiryRequested = false; }
      });
    }
  }

  get clockLabel(): string {
    return formatCountdown(this.secondsLeft);
  }

  dismiss(): void {
    this.dialogRef.close();
  }

  cancel(): void {
    this.error = null;
    this.api.cancelSchedule().subscribe({
      next: () => this.dialogRef.close('cancelled'),
      error: () => {
        this.error = 'Could not cancel the date night. Please try again.';
      }
    });
  }

  start(): void {
    if (this.starting) return;
    this.starting = true;
    this.error = null;
    this.playback.play(this.data.title, this.data.tmdbId).subscribe({
      next: () => {
        this.starting = false;
        this.dialogRef.close();
      },
      error: () => {
        this.starting = false;
        this.error = 'The movie is not ready in Jellyfin yet. Try Play again in a moment.';
      }
    });
  }
}

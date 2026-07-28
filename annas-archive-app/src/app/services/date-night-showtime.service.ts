import { Injectable } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { DateNightApiService } from './date-night-api.service';
import { LoggerService } from './logger.service';
import {
  DateNightCountdownComponent, DateNightCountdownData
} from '../components/date-night-countdown/date-night-countdown.component';

/**
 * Drives the showtime countdown popup — polled app-wide from AppComponent (see
 * its showtime subscription), same "one place owns the don't-stack-twice guard"
 * shape as DateNightAnnouncementService and LibraryReviewTriggerService.
 *
 * There's no push notification in this app, so polling while a tab happens to be
 * open is the only way this can appear "10 minutes before showtime, on both
 * accounts" — it can't fire if nobody has the app open, which is a known,
 * accepted limitation (see DOCS/DATE_NIGHT_FEATURE.md's technical constraints).
 */
@Injectable({ providedIn: 'root' })
export class DateNightShowtimeService {
  private dialogRef?: MatDialogRef<DateNightCountdownComponent>;
  /** Once dismissed for a given locked showtime, don't reopen for that same
   *  showtime again this session — otherwise the next poll (30-60s later) would
   *  just pop it right back up. Resets naturally: a new lock-in has a new
   *  showtimeUtc, and a page reload clears this entirely. */
  private dismissedForShowtimeUtc?: string;

  constructor(
    private dialog: MatDialog,
    private api: DateNightApiService,
    private logger: LoggerService
  ) {}

  checkAndMaybeShow(): void {
    if (this.dialogRef) return;

    this.api.checkShowtime().subscribe({
      next: status => {
        if (!status.imminent || !status.showtimeUtc) return;
        if (status.showtimeUtc === this.dismissedForShowtimeUtc) return;
        this.open(status.showtimeUtc);
      },
      // Fires on every poll tick for every authenticated user — a hiccup here
      // must stay silent, same reasoning as the announcement check.
      error: err => this.logger.log('[DateNightShowtime] check skipped', err)
    });
  }

  private open(showtimeUtc: string): void {
    // The poll endpoint only returns a movieId — title/tmdbId come from the
    // already-built cycle read rather than duplicating a Radarr lookup here.
    this.api.getCycle().subscribe({
      next: cycle => {
        const movie = cycle.movies.find(m => m.movieId === cycle.resolvedMovieId);
        if (!movie || this.dialogRef) return;

        this.dialogRef = this.dialog.open<DateNightCountdownComponent, DateNightCountdownData>(
          DateNightCountdownComponent,
          {
            data: { title: movie.title, tmdbId: movie.tmdbId, showtimeUtc },
            panelClass: 'thtr-dialog-panel'
          }
        );

        this.dialogRef.afterClosed().subscribe(() => {
          this.dialogRef = undefined;
          this.dismissedForShowtimeUtc = showtimeUtc;
        });
      },
      error: err => this.logger.log('[DateNightShowtime] could not load cycle for countdown', err)
    });
  }
}

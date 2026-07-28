import { Injectable } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { DateNightApiService } from './date-night-api.service';
import { LoggerService } from './logger.service';
import {
  DateNightAnnouncementComponent
} from '../components/date-night-announcement/date-night-announcement.component';

/**
 * Shows the one-time Date Night "coming soon" splash, mirroring
 * LibraryReviewTriggerService's shape (on-login check, one place owning the
 * "don't stack a second dialog" guard).
 *
 * Whether it should appear is decided entirely server-side and per person — the
 * backend knows who is asking and whether they've already dismissed it. Nothing
 * is cached in the browser on purpose: "has seen the announcement" is a fact
 * about the human, not the browser, so it has to follow Mom and Dad across
 * their own devices and the one they share for movie night. It also means Mom
 * dismissing it has no effect on Dad.
 */
@Injectable({ providedIn: 'root' })
export class DateNightAnnouncementService {
  private dialogRef?: MatDialogRef<DateNightAnnouncementComponent>;

  constructor(
    private dialog: MatDialog,
    private api: DateNightApiService,
    private logger: LoggerService
  ) {}

  /**
   * Automatic on-login check.
   *
   * @param preview Force it open for the current user without marking it seen —
   *                how Paul reviews it before it goes out. Admins never get it
   *                automatically.
   */
  checkAndMaybeShow(preview = false): void {
    if (this.dialogRef) return; // already open — never stack a second one

    this.api.getAnnouncement(preview).subscribe({
      next: announcement => {
        if (!announcement.shouldShow || this.dialogRef) return;
        this.open(announcement.posters, preview);
      },
      // A failure here must stay silent. This fires on every login for every
      // user, and a broken announcement is not worth an error in anyone's face.
      error: err => this.logger.log('[DateNightAnnouncement] check skipped', err)
    });
  }

  private open(posters: string[], preview: boolean): void {
    this.dialogRef = this.dialog.open(DateNightAnnouncementComponent, {
      data: { posters },
      // Deliberately dismissible only via the ✕ or the button: it's a one-time
      // message, and a stray backdrop click would burn the single showing.
      disableClose: true,
      // Full viewport on every device; the component scales the poster to fit
      // inside it so the whole bill is visible without scrolling.
      width: '100vw',
      maxWidth: '100vw',
      height: '100vh',
      maxHeight: '100vh',
      panelClass: 'thtr-dialog-panel'
    });

    this.dialogRef.afterClosed().subscribe(confirmed => {
      this.dialogRef = undefined;
      // A preview never counts as having seen it.
      if (!confirmed || preview) return;

      this.api.dismissAnnouncement().subscribe({
        error: err => this.logger.error('[DateNightAnnouncement] dismiss failed', err)
      });
    });
  }
}

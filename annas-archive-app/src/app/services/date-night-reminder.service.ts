import { Injectable } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { DateNightFlyerComponent, DateNightFlyerData } from '../components/date-night-flyer/date-night-flyer.component';
import {
  DateNightScheduleModalComponent, DateNightScheduleModalData
} from '../components/date-night-schedule-modal/date-night-schedule-modal.component';
import { CycleView, DateNightApiService } from './date-night-api.service';
import { AuthService } from './auth.service';
import { DateNightImpersonationService } from './date-night-impersonation.service';
import { LoggerService } from './logger.service';

/**
 * App-wide daily nudge for unfinished Date Night decisions. The backend owns
 * the Hawaii-day eligibility, so changing browsers or leaving a tab open over
 * midnight cannot create duplicate reminders. This service only decides when
 * it is polite to ask: never over another open dialog, and only for Mom/Dad.
 */
@Injectable({ providedIn: 'root' })
export class DateNightReminderService {
  private checking = false;

  constructor(
    private dialog: MatDialog,
    private api: DateNightApiService,
    private auth: AuthService,
    private impersonation: DateNightImpersonationService,
    private logger: LoggerService
  ) {}

  checkAndMaybeShow(): void {
    if (this.checking || this.dialog.openDialogs.length > 0) return;
    const person = this.person();
    if (person !== 'Mom' && person !== 'Dad') return;

    this.checking = true;
    this.api.getCycle().subscribe({
      next: cycle => {
        this.checking = false;
        if (this.dialog.openDialogs.length > 0) return;
        if (cycle.shouldShowFlyerToday) {
          this.openFlyer(cycle, person);
        } else if (cycle.shouldShowScheduleReminderToday) {
          this.openSchedule(cycle);
        }
      },
      error: err => {
        this.checking = false;
        this.logger.log('[DateNightReminder] check skipped', err);
      }
    });
  }

  private openFlyer(cycle: CycleView, person: 'Mom' | 'Dad'): void {
    this.dialog.open<DateNightFlyerComponent, DateNightFlyerData>(DateNightFlyerComponent, {
      data: { cycle, person },
      panelClass: 'thtr-dialog-panel'
    }).afterClosed().subscribe(result => {
      // Dad/Mom's last vote can resolve the movie and hand them directly to the
      // other person's pending time proposal in one continuous flow.
      if (result === 'respond-to-schedule') this.loadAndOpenSchedule();
    });
  }

  private loadAndOpenSchedule(): void {
    this.api.getCycle().subscribe({
      next: cycle => {
        if (cycle.shouldShowScheduleReminderToday && this.dialog.openDialogs.length === 0)
          this.openSchedule(cycle);
      },
      error: err => this.logger.log('[DateNightReminder] could not load schedule prompt', err)
    });
  }

  private openSchedule(cycle: CycleView): void {
    const proposedBy = cycle.schedule?.proposedBy;
    if (!proposedBy) return;
    this.dialog.open<DateNightScheduleModalComponent, DateNightScheduleModalData>(DateNightScheduleModalComponent, {
      data: { cycle, otherPersonLabel: proposedBy },
      panelClass: 'thtr-dialog-panel'
    });
  }

  private person(): 'Paul' | 'Mom' | 'Dad' | null {
    return this.impersonation.current() ?? this.auth.getOwnerName();
  }
}

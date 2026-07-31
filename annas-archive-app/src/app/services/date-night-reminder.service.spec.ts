import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { DateNightFlyerComponent } from '../components/date-night-flyer/date-night-flyer.component';
import { DateNightScheduleModalComponent } from '../components/date-night-schedule-modal/date-night-schedule-modal.component';
import { CycleView, DateNightApiService } from './date-night-api.service';
import { AuthService } from './auth.service';
import { DateNightImpersonationService } from './date-night-impersonation.service';
import { DateNightReminderService } from './date-night-reminder.service';
import { LoggerService } from './logger.service';

describe('DateNightReminderService', () => {
  const baseCycle: CycleView = {
    cycleId: '2026-08-03',
    status: 'Active',
    movies: [{ movieId: 1, title: 'The Feature' }],
    myVotes: {},
    shouldShowFlyerToday: false,
    shouldShowScheduleReminderToday: false,
    skipped: false
  };

  let dialog: jasmine.SpyObj<MatDialog> & { openDialogs: unknown[] };
  let api: jasmine.SpyObj<DateNightApiService>;
  let auth: jasmine.SpyObj<AuthService>;
  let impersonation: jasmine.SpyObj<DateNightImpersonationService>;
  let service: DateNightReminderService;

  beforeEach(() => {
    dialog = jasmine.createSpyObj('MatDialog', ['open']) as typeof dialog;
    dialog.openDialogs = [];
    dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);
    api = jasmine.createSpyObj('DateNightApiService', ['getCycle']);
    auth = jasmine.createSpyObj('AuthService', ['getOwnerName']);
    auth.getOwnerName.and.returnValue('Mom');
    impersonation = jasmine.createSpyObj('DateNightImpersonationService', ['current']);
    impersonation.current.and.returnValue(null);
    const logger = jasmine.createSpyObj<LoggerService>('LoggerService', ['log']);
    service = new DateNightReminderService(dialog, api, auth, impersonation, logger);
  });

  it('opens the once-daily movie-and-time flyer when the server says it is owed', () => {
    api.getCycle.and.returnValue(of({ ...baseCycle, shouldShowFlyerToday: true }));

    service.checkAndMaybeShow();

    expect(dialog.open).toHaveBeenCalled();
    expect(dialog.open.calls.mostRecent().args[0]).toBe(DateNightFlyerComponent);
  });

  it('opens the once-daily schedule response after voting is complete', () => {
    api.getCycle.and.returnValue(of({
      ...baseCycle,
      status: 'Resolved',
      shouldShowScheduleReminderToday: true,
      schedule: {
        status: 'AwaitingApproval',
        proposedBy: 'Dad',
        proposedSlots: [{ date: '2026-08-07', time: '19:00' }],
        acknowledgedBy: ['Dad']
      }
    }));

    service.checkAndMaybeShow();

    expect(dialog.open).toHaveBeenCalled();
    expect(dialog.open.calls.mostRecent().args[0]).toBe(DateNightScheduleModalComponent);
  });

  it('does not interrupt another open dialog', () => {
    dialog.openDialogs = [{} as any];

    service.checkAndMaybeShow();

    expect(api.getCycle).not.toHaveBeenCalled();
  });

  it('does not poll for Paul outside an explicit Mom/Dad dry run', () => {
    auth.getOwnerName.and.returnValue('Paul');

    service.checkAndMaybeShow();

    expect(api.getCycle).not.toHaveBeenCalled();
  });
});

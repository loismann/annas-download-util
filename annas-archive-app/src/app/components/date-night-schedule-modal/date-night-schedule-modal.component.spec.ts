import { MatDialogRef } from '@angular/material/dialog';
import { of, Subject } from 'rxjs';
import { DateNightApiService } from '../../services/date-night-api.service';
import {
  DateNightScheduleModalComponent,
  DateNightScheduleModalData
} from './date-night-schedule-modal.component';

describe('DateNightScheduleModalComponent', () => {
  const data: DateNightScheduleModalData = {
    otherPersonLabel: 'Mom',
    cycle: {
      cycleId: 'test',
      status: 'Resolved',
      movies: [],
      myVotes: {},
      resolvedMovieId: 42,
      shouldShowFlyerToday: false,
      shouldShowScheduleReminderToday: true,
      skipped: false,
      schedule: {
        status: 'AwaitingApproval',
        proposedBy: 'Mom',
        proposedSlots: [{ date: '2099-08-07', time: '19:00' }],
        acknowledgedBy: ['Mom']
      }
    }
  };

  let dialogRef: jasmine.SpyObj<MatDialogRef<DateNightScheduleModalComponent>>;
  let api: jasmine.SpyObj<DateNightApiService>;

  beforeEach(() => {
    dialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    api = jasmine.createSpyObj('DateNightApiService', [
      'acknowledgeSchedule', 'approveSchedule', 'proposeSchedule', 'cancelSchedule'
    ]);
    api.acknowledgeSchedule.and.returnValue(of({}));
  });

  it('serializes approval so a double tap sends only one request', () => {
    const response = new Subject<unknown>();
    api.approveSchedule.and.returnValue(response);
    const component = new DateNightScheduleModalComponent(data, dialogRef, api);
    component.selectedSlotIndex = 0;

    component.approve();
    component.approve();

    expect(component.acting).toBeTrue();
    expect(api.approveSchedule).toHaveBeenCalledOnceWith(data.cycle.schedule!.proposedSlots[0]);
    expect(dialogRef.close).not.toHaveBeenCalled();

    response.next({});
    response.complete();
    expect(dialogRef.close).toHaveBeenCalledOnceWith('acted');
  });

  it('re-enables the controls when confirmation genuinely fails', () => {
    const response = new Subject<unknown>();
    api.approveSchedule.and.returnValue(response);
    const component = new DateNightScheduleModalComponent(data, dialogRef, api);
    component.selectedSlotIndex = 0;

    component.approve();
    response.error(new Error('failed'));

    expect(component.acting).toBeFalse();
    expect(component.error).toContain('Please try again');
  });
});

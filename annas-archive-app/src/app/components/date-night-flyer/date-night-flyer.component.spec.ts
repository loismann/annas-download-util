import { MatDialogRef } from '@angular/material/dialog';
import { Subject, of, throwError } from 'rxjs';
import { DateNightApiService } from '../../services/date-night-api.service';
import { DateNightFlyerComponent, DateNightFlyerData } from './date-night-flyer.component';

describe('DateNightFlyerComponent', () => {
  const data: DateNightFlyerData = {
    person: 'Mom',
    cycle: {
      cycleId: 'test',
      status: 'Active',
      movies: [
        { movieId: 1, title: 'First' },
        { movieId: 2, title: 'Second' }
      ],
      myVotes: {},
      shouldShowFlyerToday: true,
      shouldShowScheduleReminderToday: false,
      skipped: false
    }
  };

  let dialogRef: jasmine.SpyObj<MatDialogRef<DateNightFlyerComponent>>;
  let api: jasmine.SpyObj<DateNightApiService>;

  beforeEach(() => {
    dialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    api = jasmine.createSpyObj('DateNightApiService', ['castVote', 'recordFlyerShown', 'proposeSchedule', 'setSkip']);
    api.recordFlyerShown.and.returnValue(of({}));
  });

  it('waits for the server before recording a vote or advancing', () => {
    const saved = new Subject<unknown>();
    api.castVote.and.returnValue(saved);
    const component = new DateNightFlyerComponent(data, dialogRef, api);

    component.vote(1, 'Up');

    expect(component.saving).toBeTrue();
    expect(component.voteFor(1)).toBeUndefined();
    expect(component.index).toBe(0);

    saved.next({});
    saved.complete();

    expect(component.saving).toBeFalse();
    expect(component.voteFor(1)).toBe('Up');
    expect(component.index).toBe(1);
  });

  it('keeps the flyer open and reports a failed vote', () => {
    api.castVote.and.returnValue(throwError(() => new Error('failed')));
    const component = new DateNightFlyerComponent(data, dialogRef, api);

    component.vote(1, 'Down');

    expect(component.saving).toBeFalse();
    expect(component.voteFor(1)).toBeUndefined();
    expect(component.error).toContain('did not save');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('keeps the first completed ballot in the flyer to collect possible times', () => {
    api.castVote.and.returnValue(of({}));
    const component = new DateNightFlyerComponent(data, dialogRef, api);
    component.myVotes = { 1: 'Up' };

    component.vote(2, 'Down');

    expect(component.allVoted).toBeTrue();
    expect(component.needsInitialProposal).toBeTrue();
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('sends the first voter’s many schedule options from the same flyer', () => {
    api.proposeSchedule.and.returnValue(of({}));
    const component = new DateNightFlyerComponent(data, dialogRef, api);
    const slots = [
      { date: '2099-08-01', time: '18:00' },
      { date: '2099-08-02', time: '19:30' }
    ];

    component.proposeTimes(slots);

    expect(api.proposeSchedule).toHaveBeenCalledOnceWith(slots);
    expect(dialogRef.close).toHaveBeenCalledOnceWith('proposal-sent');
  });

  it('hands the second voter directly to the existing schedule response', () => {
    const secondData: DateNightFlyerData = {
      person: 'Dad',
      cycle: {
        ...data.cycle,
        schedule: {
          status: 'AwaitingApproval',
          proposedBy: 'Mom',
          proposedSlots: [{ date: '2099-08-01', time: '18:00' }],
          acknowledgedBy: ['Mom']
        }
      }
    };
    api.castVote.and.returnValue(of({}));
    const component = new DateNightFlyerComponent(secondData, dialogRef, api);
    component.myVotes = { 1: 'Up' };

    component.vote(2, 'Up');

    expect(dialogRef.close).toHaveBeenCalledOnceWith('respond-to-schedule');
  });

  it('offers a real user a rain check and persists the selected skip', () => {
    api.setSkip.and.returnValue(of({}));
    const realData: DateNightFlyerData = {
      ...data,
      cycle: { ...data.cycle, cycleId: '2026-07-27' }
    };
    const component = new DateNightFlyerComponent(realData, dialogRef, api);

    component.skip('week');

    expect(api.setSkip).toHaveBeenCalledOnceWith('week');
    expect(dialogRef.close).toHaveBeenCalledOnceWith('skipped');
  });
});

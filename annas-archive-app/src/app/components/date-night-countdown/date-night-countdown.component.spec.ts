import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import { DateNightCountdownComponent } from './date-night-countdown.component';
import { DateNightApiService } from '../../services/date-night-api.service';
import { DateNightPlaybackService } from '../../services/date-night-playback.service';

/**
 * Characterization tests for the pre-showtime countdown popup.
 *
 * This is the one surface that turns a missed showtime into a cancellation, and
 * it does so from a timer rather than from anything the user did — so the tests
 * are mostly about when that fires, and about it firing exactly once.
 */
describe('DateNightCountdownComponent (characterization)', () => {
  let fixture: ComponentFixture<DateNightCountdownComponent>;
  let component: DateNightCountdownComponent;
  let api: jasmine.SpyObj<DateNightApiService>;
  let playback: jasmine.SpyObj<DateNightPlaybackService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<DateNightCountdownComponent>>;

  const HOUR = 60 * 60 * 1000;

  async function build(showtimeOffsetMs: number): Promise<void> {
    api = jasmine.createSpyObj<DateNightApiService>('DateNightApiService', ['checkShowtime', 'cancelSchedule']);
    api.checkShowtime.and.returnValue(of({ imminent: false } as any));
    api.cancelSchedule.and.returnValue(of({}));
    playback = jasmine.createSpyObj<DateNightPlaybackService>('DateNightPlaybackService', ['play']);
    playback.play.and.returnValue(of(undefined));
    dialogRef = jasmine.createSpyObj<MatDialogRef<DateNightCountdownComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [DateNightCountdownComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            title: 'Them!', tmdbId: 42,
            showtimeUtc: new Date(Date.now() + showtimeOffsetMs).toISOString()
          }
        },
        { provide: DateNightApiService, useValue: api },
        { provide: DateNightPlaybackService, useValue: playback }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DateNightCountdownComponent);
    component = fixture.componentInstance;
  }

  afterEach(() => fixture.destroy());

  describe('the clock', () => {
    it('should count down to the showtime', async () => {
      await build(10 * 60 * 1000);

      component.ngOnInit();

      expect(component.secondsLeft).toBeGreaterThan(0);
      expect(component.clockLabel).toMatch(/^\d{2}:\d{2}$/);
    });

    it('should say it is time rather than show zeros', async () => {
      await build(-1000);

      component.ngOnInit();

      expect(component.secondsLeft).toBe(0);
      expect(component.clockLabel).toBe("Let's go!");
    });

    it('should stop ticking when the popup closes', async () => {
      await build(10 * 60 * 1000);
      // Installed after the await: installing across one leaves the clock in
      // place if the suspended half never resumes, and every later test then
      // fails to install its own.
      jasmine.clock().install();
      try {
        component.ngOnInit();
        fixture.destroy();
        api.checkShowtime.calls.reset();

        jasmine.clock().tick(10 * 60 * 1000);

        expect(api.checkShowtime).not.toHaveBeenCalled();
      } finally {
        jasmine.clock().uninstall();
      }
    });
  });

  describe('the grace period', () => {
    it('should leave a showtime inside the hour alone', async () => {
      // Still watchable — the popup stays up offering Play.
      await build(-30 * 60 * 1000);

      component.ngOnInit();

      expect(api.checkShowtime).not.toHaveBeenCalled();
      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should ask the server to record the miss once the hour is up', async () => {
      // That poll is what performs the server-side cancellation, so it has to
      // happen before the popup closes rather than instead of it.
      await build(-HOUR - 60 * 1000);

      component.ngOnInit();

      expect(api.checkShowtime).toHaveBeenCalled();
      expect(dialogRef.close).toHaveBeenCalledWith('expired');
    });

    it('should ask only once', async () => {
      await build(-HOUR - 60 * 1000);
      jasmine.clock().install();
      try {
        component.ngOnInit();

        jasmine.clock().tick(5000);

        expect(api.checkShowtime).toHaveBeenCalledTimes(1);
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should try again on the next tick when the server could not be told', async () => {
      // Closing without the cancellation recorded would leave the week stuck
      // Locked on a showtime that never happened.
      await build(-HOUR - 60 * 1000);
      api.checkShowtime.and.returnValue(throwError(() => new Error('down')));
      jasmine.clock().install();
      try {
        component.ngOnInit();
        expect(dialogRef.close).not.toHaveBeenCalled();

        jasmine.clock().tick(1000);

        expect(api.checkShowtime.calls.count()).toBeGreaterThan(1);
      } finally {
        jasmine.clock().uninstall();
      }
    });
  });

  describe('starting the film', () => {
    beforeEach(async () => {
      await build(-60 * 1000);
      component.ngOnInit();
    });

    it('should play through the shared flow and close', () => {
      component.start();

      expect(playback.play).toHaveBeenCalledWith('Them!', 42);
      expect(dialogRef.close).toHaveBeenCalled();
      expect(component.starting).toBe(false);
    });

    it('should not start a second play over the first', () => {
      playback.play.and.returnValue(new Observable<void>(() => {}));

      component.start();
      component.start();

      expect(playback.play).toHaveBeenCalledTimes(1);
      expect(component.starting).toBe(true);
    });

    it('should blame Jellyfin and stay open when it is not ready', () => {
      playback.play.and.returnValue(throwError(() => new Error('404')));

      component.start();

      expect(component.error).toContain('Jellyfin');
      expect(component.starting).toBe(false);
      expect(dialogRef.close).not.toHaveBeenCalled();
    });
  });

  describe('cancelling', () => {
    beforeEach(async () => {
      await build(10 * 60 * 1000);
      component.ngOnInit();
    });

    it('should cancel and report it', () => {
      component.cancel();

      expect(api.cancelSchedule).toHaveBeenCalled();
      expect(dialogRef.close).toHaveBeenCalledWith('cancelled');
    });

    it('should stay open and say so when the cancel fails', () => {
      api.cancelSchedule.and.returnValue(throwError(() => new Error('down')));

      component.cancel();

      expect(component.error).toContain('Could not cancel');
      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should close with nothing on dismiss', () => {
      // Dismissing is "not now", not "call it off".
      component.dismiss();

      expect(api.cancelSchedule).not.toHaveBeenCalled();
      expect(dialogRef.close).toHaveBeenCalledWith();
    });

    it('should let a cancel already in flight survive the popup closing', () => {
      let aborted = false;
      api.cancelSchedule.and.returnValue(new Observable<unknown>(() => () => { aborted = true; }));
      component.cancel();

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });
});

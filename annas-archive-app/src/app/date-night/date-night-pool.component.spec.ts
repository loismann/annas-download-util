import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { DateNightPoolComponent } from './date-night-pool.component';
import { DateNightApiService } from '../services/date-night-api.service';
import { DateNightAnnouncementService } from '../services/date-night-announcement.service';

/**
 * Characterization tests for the Date Night pool page.
 *
 * Written as a review pass rather than as cover for a refactor, following the
 * book-reader lesson in ASSERTIONS. It found the same defect class the
 * audiobook grid had, with a worse cadence — see "timer lifecycle".
 */
describe('DateNightPoolComponent (characterization)', () => {
  let fixture: ComponentFixture<DateNightPoolComponent>;
  let component: DateNightPoolComponent;
  let api: jasmine.SpyObj<DateNightApiService>;

  /** A pool response with the test cycle locked to a slot far in the future. */
  function poolWithLockedSlot(): any {
    const future = new Date(Date.now() + 7 * 24 * 3600 * 1000);
    return {
      items: [],
      testCycle: {
        schedule: {
          status: 'Locked',
          lockedSlot: { date: future.toISOString().slice(0, 10), time: '19:00' }
        }
      }
    };
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<DateNightApiService>('DateNightApiService', [
      'getPool', 'clearSkip', 'goDark', 'goLive', 'resetAnnouncement',
      'resetDryRun', 'restoreMovie', 'startAvailabilityScan'
    ]);
    api.getPool.and.returnValue(of({ items: [] } as any));

    await TestBed.configureTestingModule({
      imports: [DateNightPoolComponent, NoopAnimationsModule],
      providers: [
        { provide: DateNightApiService, useValue: api },
        { provide: DateNightAnnouncementService, useValue: { checkAndMaybeShow: () => {} } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DateNightPoolComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('timer lifecycle', () => {
    /**
     * `refreshNow()` re-fetches the pool without waiting for the 15s poll, and
     * its response handler runs `syncTestCountdown`, which starts a 1-second
     * countdown interval when a test slot is locked.
     *
     * That fetch was not tied to the component's lifetime. `ngOnDestroy` clears
     * whatever timer exists at that moment, but a refresh that resolved
     * afterwards started a *new* one-second interval on a component that was
     * already gone — and nothing clears that.
     */
    it('should not start a countdown from a refresh that lands after destroy', () => {
      component.ngOnInit();

      // The 15s poll IS unsubscribed on destroy. refreshNow() is the one that
      // was not, so that is the path this exercises.
      const late = new Subject<any>();
      api.getPool.and.returnValue(late.asObservable());
      (component as any).refreshNow();

      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      fixture.destroy();
      late.next(poolWithLockedSlot());

      expect(setInterval).not.toHaveBeenCalled();
    });

    it('should not apply a refresh that lands after destroy', () => {
      component.ngOnInit();
      const late = new Subject<any>();
      api.getPool.and.returnValue(late.asObservable());
      (component as any).refreshNow();

      fixture.destroy();
      late.next({ items: [{ id: 'arrived-too-late' }] } as any);

      expect(component.data?.items ?? []).toEqual([]);
    });

    it('should stop polling the pool after destroy', () => {
      component.ngOnInit();
      const callsAtDestroy = api.getPool.calls.count();

      fixture.destroy();

      expect(api.getPool.calls.count()).toBe(callsAtDestroy);
    });

    // These two assert on the countdown timer itself rather than on a
    // window.setInterval spy: RxJS's own `interval(15000)` in the poll calls
    // setInterval too, so a spy installed before ngOnInit cannot tell the two
    // apart and would pass for the wrong reason.
    it('should run a countdown while a test slot is locked', () => {
      // The behaviour the guard above must not break.
      api.getPool.and.returnValue(of(poolWithLockedSlot()));

      component.ngOnInit();

      expect((component as any).testCountdownTimer).toBeDefined();
      expect(component.testSecondsLeft).toBeGreaterThan(0);
    });

    it('should not run a countdown when no slot is locked', () => {
      api.getPool.and.returnValue(of({ items: [], testCycle: null } as any));

      component.ngOnInit();

      expect((component as any).testCountdownTimer).toBeUndefined();
      expect(component.testSecondsLeft).toBe(0);
    });
  });

  describe('loading', () => {
    it('should clear a stale error once a load succeeds', () => {
      component.error = 'stale failure';

      component.ngOnInit();

      expect(component.error).toBeNull();
      expect(component.loading).toBe(false);
    });

    it('should report a reachability problem rather than a stack', () => {
      api.getPool.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('Radarr');
      expect(component.loading).toBe(false);
    });
  });
});

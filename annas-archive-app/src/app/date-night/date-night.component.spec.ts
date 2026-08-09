import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { Observable, Subject, config, of, throwError } from 'rxjs';

import { DateNightComponent } from './date-night.component';
import {
  CycleView, DateNightAnnouncement, DateNightApiService, ProposedSlot
} from '../services/date-night-api.service';
import { AuthService } from '../services/auth.service';
import { DateNightImpersonationService } from '../services/date-night-impersonation.service';
import { DateNightPlaybackService } from '../services/date-night-playback.service';

/**
 * Characterization tests for the Date Night lobby.
 *
 * Written as a review pass rather than as cover for a refactor, following the
 * book-reader lesson in ASSERTIONS. Like the audiobook grid and the pool page
 * before it, the pass found defects — two, both recorded below on the tests
 * that pin them: a stale error banner that no success ever cleared, and a
 * 15-second poll whose failures RxJS reported as unhandled errors.
 *
 * Most tests drive `ngOnInit()` directly rather than `detectChanges()`, the
 * same way the other two suites do: this component's behaviour lives in timers
 * and subscriptions, and rendering a 600-line template to reach them would only
 * add ways for the test to fail for reasons that are not about Date Night.
 */
describe('DateNightComponent (characterization)', () => {
  let fixture: ComponentFixture<DateNightComponent>;
  let component: DateNightComponent;
  let api: jasmine.SpyObj<DateNightApiService>;
  let playback: jasmine.SpyObj<DateNightPlaybackService>;
  let dialog: jasmine.SpyObj<MatDialog>;
  let impersonation: DateNightImpersonationService;
  let isAdmin: boolean;
  let ownerName: 'Paul' | 'Mom' | 'Dad' | null;

  // ─── Fixtures ────────────────────────────────────────────────────────

  function announcement(over: Partial<DateNightAnnouncement> = {}): DateNightAnnouncement {
    return { shouldShow: false, posters: [], live: false, ...over };
  }

  function cycle(over: Partial<CycleView> = {}): CycleView {
    return {
      cycleId: 'c1',
      status: 'Active',
      movies: [],
      myVotes: {},
      shouldShowFlyerToday: false,
      shouldShowScheduleReminderToday: false,
      skipped: false,
      ...over
    };
  }

  /**
   * A Hawaii slot that lands `msFromNow` from now.
   *
   * Slots are stored as Hawaii wall-clock with no offset and Hawaii is
   * permanently UTC-10, so the wall-clock value is the UTC instant minus ten
   * hours. Building it that way rather than hard-coding a date keeps these
   * tests from expiring, and keeps them honest in any local timezone.
   */
  function slotIn(msFromNow: number): ProposedSlot {
    const hawaii = new Date(Date.now() + msFromNow - 10 * 3600 * 1000).toISOString();
    return { date: hawaii.slice(0, 10), time: hawaii.slice(11, 16) };
  }

  function lockedCycle(slot: ProposedSlot, over: Partial<CycleView> = {}): CycleView {
    return cycle({
      status: 'Resolved',
      schedule: { status: 'Locked', lockedSlot: slot, proposedSlots: [], acknowledgedBy: [] },
      ...over
    });
  }

  /** Delays handed to setInterval, so the 1s countdown and the 15s poll can be told apart. */
  function intervalDelays(spy: jasmine.Spy): number[] {
    return spy.calls.allArgs().map(args => args[1] as number);
  }

  beforeEach(async () => {
    isAdmin = false;
    ownerName = 'Mom';

    api = jasmine.createSpyObj<DateNightApiService>('DateNightApiService', [
      'getAnnouncement', 'getCycle', 'proposeSchedule', 'cancelSchedule',
      'markWatched', 'retryDownload'
    ]);
    api.getAnnouncement.and.returnValue(of(announcement({ live: true })));
    api.getCycle.and.returnValue(of(cycle()));
    api.proposeSchedule.and.returnValue(of({}));
    api.cancelSchedule.and.returnValue(of({}));
    api.markWatched.and.returnValue(of({}));
    api.retryDownload.and.returnValue(of({}));

    playback = jasmine.createSpyObj<DateNightPlaybackService>('DateNightPlaybackService', ['play']);
    playback.play.and.returnValue(of(undefined));

    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);

    await TestBed.configureTestingModule({
      imports: [DateNightComponent, NoopAnimationsModule],
      providers: [
        { provide: DateNightApiService, useValue: api },
        { provide: DateNightPlaybackService, useValue: playback },
        { provide: MatDialog, useValue: dialog },
        { provide: AuthService, useValue: { isAdmin: () => isAdmin, getOwnerName: () => ownerName } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DateNightComponent);
    component = fixture.componentInstance;
    impersonation = TestBed.inject(DateNightImpersonationService);
    impersonation.set(null);
  });

  afterEach(() => fixture.destroy());

  // ─── The live gate ───────────────────────────────────────────────────

  describe('the live gate', () => {
    it('should ask for the announcement without consuming it', () => {
      // preview=true. The one-time dialog is triggered app-wide from
      // AppComponent; asking for it here too would burn Mom's only showing on a
      // page that does not display it.
      component.ngOnInit();

      expect(api.getAnnouncement).toHaveBeenCalledWith(true);
    });

    it('should not load a cycle while the feature is dark', () => {
      api.getAnnouncement.and.returnValue(of(announcement({ live: false, posters: ['p.jpg'] })));

      component.ngOnInit();

      expect(component.live).toBe(false);
      expect(component.posters).toEqual(['p.jpg']);
      expect(api.getCycle).not.toHaveBeenCalled();
    });

    it('should load the cycle once live', () => {
      component.ngOnInit();

      expect(component.live).toBe(true);
      expect(api.getCycle).toHaveBeenCalled();
    });

    it('should stop loading even when the announcement fails', () => {
      // A dead backend must not leave the page on a spinner forever.
      api.getAnnouncement.and.returnValue(throwError(() => new Error('down')));

      component.ngOnInit();

      expect(component.loading).toBe(false);
      expect(api.getCycle).not.toHaveBeenCalled();
    });

    it('should show the poster, not the lobby, while dark', () => {
      api.getAnnouncement.and.returnValue(of(announcement({ live: false })));

      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('app-date-night-poster')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('.lobby')).toBeNull();
    });

    it('should show the lobby once live', () => {
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.lobby')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('app-date-night-poster')).toBeNull();
    });
  });

  // ─── Impersonation ───────────────────────────────────────────────────

  describe('impersonation', () => {
    it('should let an admin drive the lobby while the feature is dark', () => {
      // The whole point of dry run: Paul can exercise phases 3-7 against a
      // separate test cycle without going live for Mom and Dad.
      isAdmin = true;
      ownerName = 'Paul';
      api.getAnnouncement.and.returnValue(of(announcement({ live: false })));
      impersonation.set('Mom');

      component.ngOnInit();

      expect(component.dryRun).toBe(true);
      expect(component.myName).toBe('Mom');
    });

    it('should never dry-run for a non-admin', () => {
      // Belt and braces on the gate that keeps Mom and Dad out of test state:
      // even with impersonation somehow set, a non-admin session is governed by
      // `live` alone.
      isAdmin = false;
      impersonation.set('Dad');

      expect(component.dryRun).toBe(false);
    });

    it('should fall back to the real identity when not impersonating', () => {
      ownerName = 'Dad';

      component.ngOnInit();

      expect(component.myName).toBe('Dad');
    });

    it('should reload the cycle when the admin switches who they are testing as', () => {
      isAdmin = true;
      ownerName = 'Paul';
      component.ngOnInit();
      const before = api.getCycle.calls.count();

      component.setViewAs('Dad');

      expect(component.isViewingAs('Dad')).toBe(true);
      expect(api.getCycle.calls.count()).toBe(before + 1);
    });
  });

  // ─── Loading the cycle ───────────────────────────────────────────────

  describe('loading the cycle', () => {
    it('should take the cycle and the skip state', () => {
      api.getCycle.and.returnValue(of(cycle({ skipped: true, status: 'NoMatch' })));

      component.ngOnInit();

      expect(component.cycle?.status).toBe('NoMatch');
      expect(component.skipped).toBe(true);
    });

    it('should report a load failure in plain language', () => {
      api.getCycle.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('this week');
    });

    /**
     * One of the two defects this pass found.
     *
     * Every action reports its own failure into `error` and every success ends
     * up back in `loadCycle`, but nothing ever cleared the banner. So a propose
     * that failed once left "Could not send that proposal." on screen through
     * the retry that worked, through the reload after it, and until the page
     * was navigated away from — telling the user their date night never got
     * sent while the lobby showed the proposal they had just sent.
     */
    it('should clear a stale error once a load succeeds', () => {
      component.error = 'Could not send that proposal.';

      component.ngOnInit();

      expect(component.error).toBeNull();
    });

    it('should clear the banner after a failed action finally succeeds', () => {
      // The same defect from the user's side rather than the field's.
      component.ngOnInit();
      api.cancelSchedule.and.returnValue(throwError(() => new Error('nope')));
      component.cancel();
      expect(component.error).toBe('Could not cancel.');

      api.cancelSchedule.and.returnValue(of({}));
      component.cancel();

      expect(component.error).toBeNull();
    });
  });

  // ─── The locked countdown ────────────────────────────────────────────

  describe('the locked countdown', () => {
    it('should count down to a showtime still ahead', () => {
      api.getCycle.and.returnValue(of(lockedCycle(slotIn(2 * 3600 * 1000))));

      component.ngOnInit();

      expect(component.lockedSecondsLeft).toBeGreaterThan(0);
      expect(component.lockedCountdownLabel).toMatch(/^\d+:\d{2}:\d{2}$/);
    });

    it('should not tick for a showtime that has passed', () => {
      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      api.getCycle.and.returnValue(of(lockedCycle(slotIn(-2 * 3600 * 1000))));

      component.ngOnInit();

      expect(intervalDelays(setInterval)).not.toContain(1000);
    });

    it('should not tick when nothing is locked', () => {
      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        schedule: { status: 'AwaitingApproval', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(intervalDelays(setInterval)).not.toContain(1000);
    });

    it('should say so rather than show zeros once the clock runs out', () => {
      component.lockedSecondsLeft = 0;

      expect(component.lockedCountdownLabel).toBe("Let's go!");
    });

    it('should stop both timers when the page goes', () => {
      const clearInterval = spyOn(window, 'clearInterval').and.callThrough();
      api.getCycle.and.returnValue(of(lockedCycle(slotIn(2 * 3600 * 1000))));
      component.ngOnInit();

      fixture.destroy();

      expect(clearInterval).toHaveBeenCalledTimes(2);
    });

    it('should not start a countdown from a read that lands after destroy', () => {
      // The defect class the audiobook grid and the pool page both had: the
      // response handler starts a timer, so an unguarded read that resolves
      // after destroy leaves a 1-second interval running on a dead component
      // with nothing left to clear it.
      const late = new Subject<CycleView>();
      api.getCycle.and.returnValue(late.asObservable());
      component.ngOnInit();
      const setInterval = spyOn(window, 'setInterval').and.callThrough();

      fixture.destroy();
      late.next(lockedCycle(slotIn(2 * 3600 * 1000)));

      expect(setInterval).not.toHaveBeenCalled();
      expect(component.cycle).toBeNull();
    });
  });

  // ─── The locked status poll ──────────────────────────────────────────

  describe('the locked status poll', () => {
    it('should poll while a showtime is locked', () => {
      // A Radarr grab can finish after lock-in, so "requested" has to become
      // "downloaded and ready" without a reload.
      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      api.getCycle.and.returnValue(of(lockedCycle(slotIn(2 * 3600 * 1000))));

      component.ngOnInit();

      expect(intervalDelays(setInterval)).toContain(15000);
    });

    it('should not poll when nothing is locked', () => {
      const setInterval = spyOn(window, 'setInterval').and.callThrough();

      component.ngOnInit();

      expect(intervalDelays(setInterval)).not.toContain(15000);
    });

    describe('once it is running', () => {
      beforeEach(() => {
        jasmine.clock().install();
        api.getCycle.and.returnValue(of(lockedCycle(slotIn(2 * 3600 * 1000))));
        component.ngOnInit();
      });

      afterEach(() => jasmine.clock().uninstall());

      it('should keep refreshing the winner card', () => {
        const before = api.getCycle.calls.count();

        jasmine.clock().tick(15000);

        expect(api.getCycle.calls.count()).toBe(before + 1);
      });

      it('should stand down once the week stops being locked', () => {
        api.getCycle.and.returnValue(of(cycle({
          status: 'Resolved',
          schedule: { status: 'Concluded', proposedSlots: [], acknowledgedBy: [] }
        })));
        jasmine.clock().tick(15000);
        const afterStandDown = api.getCycle.calls.count();

        jasmine.clock().tick(15000);

        expect(api.getCycle.calls.count()).toBe(afterStandDown);
      });

      /**
       * The second defect this pass found.
       *
       * This subscribe had a `next` and no `error`. RxJS treats that as nobody
       * handling the failure and reports it through `onUnhandledError` — so
       * every 15 seconds of a backend blip raised one, on a page a user would
       * plausibly leave open all evening waiting for a showtime.
       *
       * Asserted through RxJS's own hook rather than through component state,
       * because the component state is identical either way: without the
       * handler this test still finds a clean banner and an intact cycle. The
       * hook is the only thing that can tell the two apart.
       */
      it('should absorb a failed poll instead of raising an unhandled error', () => {
        const unhandled = jasmine.createSpy('onUnhandledError');
        const previous = config.onUnhandledError;
        config.onUnhandledError = unhandled;
        const stillShowing = component.cycle;

        try {
          api.getCycle.and.returnValue(throwError(() => new Error('backend blip')));
          jasmine.clock().tick(15000);
          jasmine.clock().tick(0); // RxJS reports unhandled errors on a timeout
        } finally {
          config.onUnhandledError = previous;
        }

        expect(unhandled).not.toHaveBeenCalled();
        // And it says nothing to the user: this is a background refresh, so the
        // card keeps what it has and the next tick tries again.
        expect(component.error).toBeNull();
        expect(component.cycle).toBe(stillShowing);
      });

      it('should stop polling when the page goes', () => {
        fixture.destroy();
        const afterDestroy = api.getCycle.calls.count();

        jasmine.clock().tick(60000);

        expect(api.getCycle.calls.count()).toBe(afterDestroy);
      });
    });
  });

  // ─── Auto-opened dialogs ─────────────────────────────────────────────

  describe('auto-opened dialogs', () => {
    it('should open the flyer when the day calls for it', () => {
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true, movies: [] })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should not stack the schedule modal on top of the flyer', () => {
      // One load, two things worth saying: whichever lost would open behind the
      // other and be dismissed unread. The flyer wins and the proposal waits.
      //
      // Modelled with a dialog that has not been closed yet, because that is
      // the case the rule is about — after it closes, handing over to the
      // proposal is wanted, and the test below pins that.
      dialog.open.and.returnValue({ afterClosed: () => new Subject<unknown>() } as any);
      api.getCycle.and.returnValue(of(cycle({
        shouldShowFlyerToday: true,
        shouldShowScheduleReminderToday: true,
        status: 'Resolved',
        schedule: { status: 'AwaitingApproval', proposedBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should reload once the flyer closes', () => {
      // The flyer is where votes are cast, so what is on screen behind it is
      // stale the moment it closes.
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true })));
      component.ngOnInit();

      expect(api.getCycle.calls.count()).toBe(2);
    });

    /**
     * The third and worst defect this pass found.
     *
     * Closing the flyer reloads the cycle, and the flag that opened it is only
     * turned off by the flyer's own fire-and-forget POST from ngOnInit. So the
     * two race, and when the POST lost — or failed — the reload reopened the
     * dialog the user had just dismissed. Closing it reopened it again. The
     * only way out was to leave the page.
     *
     * This test is what found it: with a synchronous afterClosed it did not
     * fail on an assertion, it ran the loop several hundred times before
     * Jasmine cut it off.
     */
    it('should not reopen the flyer the user just closed', () => {
      // The flag stays set, exactly as it does when the POST has not landed.
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should not reopen the schedule modal the user just closed', () => {
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        shouldShowScheduleReminderToday: true,
        schedule: { status: 'AwaitingApproval', proposedBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should still hand the flyer over to a pending proposal', () => {
      // The behaviour the latch must not cost: a last vote resolves the week,
      // and closing the flyer should walk straight into the time proposal
      // waiting behind it rather than making the user find it.
      api.getCycle.and.returnValues(
        of(cycle({ shouldShowFlyerToday: true })),
        of(cycle({
          status: 'Resolved',
          shouldShowScheduleReminderToday: true,
          schedule: { status: 'AwaitingApproval', proposedBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
        })),
        of(cycle())
      );

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(2);
    });

    it('should still open on request after the auto-open has had its turn', () => {
      // The latch is on opening by itself, not on the buttons.
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true })));
      component.ngOnInit();

      component.openFlyer();

      expect(dialog.open).toHaveBeenCalledTimes(2);
    });

    it('should let an admin see the flyer again as the other person', () => {
      isAdmin = true;
      ownerName = 'Paul';
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true })));
      impersonation.set('Mom');
      component.ngOnInit();
      expect(dialog.open).toHaveBeenCalledTimes(1);

      component.setViewAs('Dad');

      expect(dialog.open).toHaveBeenCalledTimes(2);
    });

    it('should refuse the flyer to anyone who is not Mom or Dad', () => {
      // It is a voting surface, and Paul has no vote.
      isAdmin = true;
      ownerName = 'Paul';
      api.getCycle.and.returnValue(of(cycle({ shouldShowFlyerToday: true })));

      component.ngOnInit();

      expect(dialog.open).not.toHaveBeenCalled();
    });

    it('should nudge the person whose turn it is on a proposal', () => {
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        shouldShowScheduleReminderToday: true,
        schedule: { status: 'AwaitingApproval', proposedBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should not nudge again on a day the backend has not cleared', () => {
      // The backend owns "once a day", so the page must take its answer rather
      // than re-deciding on every poll.
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        shouldShowScheduleReminderToday: false,
        schedule: { status: 'AwaitingApproval', proposedBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).not.toHaveBeenCalled();
    });

    it('should tell the person who did not cancel that the week is off', () => {
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        schedule: { status: 'Cancelled', cancelledBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).toHaveBeenCalledTimes(1);
    });

    it('should not tell the canceller what they just did', () => {
      ownerName = 'Dad';
      api.getCycle.and.returnValue(of(cycle({
        status: 'Resolved',
        schedule: { status: 'Cancelled', cancelledBy: 'Dad', proposedSlots: [], acknowledgedBy: [] }
      })));

      component.ngOnInit();

      expect(dialog.open).not.toHaveBeenCalled();
    });
  });

  // ─── Actions ─────────────────────────────────────────────────────────

  describe('actions', () => {
    beforeEach(() => component.ngOnInit());

    it('should send a proposal and reload', () => {
      const slots = [slotIn(24 * 3600 * 1000)];
      const before = api.getCycle.calls.count();

      component.propose(slots);

      expect(api.proposeSchedule).toHaveBeenCalledWith(slots);
      expect(api.getCycle.calls.count()).toBe(before + 1);
    });

    it('should report each action failure in its own words', () => {
      api.proposeSchedule.and.returnValue(throwError(() => new Error('x')));
      component.propose([]);
      expect(component.error).toBe('Could not send that proposal.');

      api.cancelSchedule.and.returnValue(throwError(() => new Error('x')));
      component.cancel();
      expect(component.error).toBe('Could not cancel.');

      api.markWatched.and.returnValue(throwError(() => new Error('x')));
      component.markWatched();
      expect(component.error).toBe('Could not mark that watched.');

      api.retryDownload.and.returnValue(throwError(() => new Error('x')));
      component.retryDownload();
      expect(component.error).toBe('Could not retry the Radarr download.');
    });

    it('should not let a second retry chase the first', () => {
      // Two in flight would have Radarr grab the same release twice.
      api.retryDownload.and.returnValue(new Subject<unknown>().asObservable());
      component.retryDownload();
      component.retryDownload();

      expect(api.retryDownload).toHaveBeenCalledTimes(1);
      expect(component.retryingDownload).toBe(true);
    });

    it('should free the retry button again once it settles', () => {
      component.retryDownload();

      expect(component.retryingDownload).toBe(false);
    });

    it('should start the movie through the shared playback flow', () => {
      const before = api.getCycle.calls.count();

      component.startMovie({ movieId: 1, title: 'Them!', tmdbId: 42 });

      expect(playback.play).toHaveBeenCalledWith('Them!', 42);
      expect(component.startingMovie).toBe(false);
      expect(api.getCycle.calls.count()).toBe(before + 1);
    });

    it('should not let a second Play chase the first', () => {
      playback.play.and.returnValue(new Subject<void>().asObservable());
      component.startMovie({ movieId: 1, title: 'Them!', tmdbId: 42 });
      component.startMovie({ movieId: 1, title: 'Them!', tmdbId: 42 });

      expect(playback.play).toHaveBeenCalledTimes(1);
    });

    it('should blame Jellyfin, not the user, when Play fails', () => {
      // The usual cause is the file not having landed yet, and trying again in
      // a minute is genuinely the fix.
      playback.play.and.returnValue(throwError(() => new Error('404')));

      component.startMovie({ movieId: 1, title: 'Them!', tmdbId: 42 });

      expect(component.error).toContain('Jellyfin');
      expect(component.startingMovie).toBe(false);
    });

    it('should let a write already in flight finish after destroy', () => {
      // Reads are tied to the component's lifetime; writes deliberately are
      // not. Unsubscribing an HttpClient call aborts the request, so guarding
      // these the same way would mean navigating away cancelled the date night
      // the user just called off.
      let aborted = false;
      api.cancelSchedule.and.returnValue(new Observable<unknown>(() => () => { aborted = true; }));
      component.cancel();

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── Showtime windows ────────────────────────────────────────────────

  describe('showtime windows', () => {
    it('should offer Play from the showtime until an hour after', () => {
      expect(component.canStartMovie(slotIn(5 * 60 * 1000))).toBe(false);
      expect(component.canStartMovie(slotIn(-5 * 60 * 1000))).toBe(true);
      expect(component.canStartMovie(slotIn(-65 * 60 * 1000))).toBe(false);
      expect(component.canStartMovie(undefined)).toBe(false);
    });

    it('should know when the showtime is behind us', () => {
      expect(component.showtimePassed(slotIn(60 * 60 * 1000))).toBe(false);
      expect(component.showtimePassed(slotIn(-60 * 60 * 1000))).toBe(true);
      expect(component.showtimePassed(undefined)).toBe(false);
    });

    it('should render a slot in Hawaii time, not the browser\'s', () => {
      // An admin dry-running from the mainland must still see the time Mom and
      // Dad will see.
      const shown = component.formatShowTime({ date: '2026-03-14', time: '19:00' });

      expect(shown).toContain('7:00');
    });

    it('should render the show date in Hawaii too', () => {
      const shown = component.formatShowDate({ date: '2026-03-14', time: '19:00' });

      expect(shown).toContain('March 14');
    });
  });

  // ─── Presentation helpers ────────────────────────────────────────────

  describe('presentation', () => {
    it('should find the movie the week settled on', () => {
      const c = cycle({
        resolvedMovieId: 2,
        movies: [{ movieId: 1, title: 'A' }, { movieId: 2, title: 'B' }]
      });

      expect(component.resolvedMovie(c)?.title).toBe('B');
    });

    it('should name the other half of the household', () => {
      ownerName = 'Mom';
      component.ngOnInit();

      expect(component.otherPerson()).toBe('Dad');
    });

    it('should report vote completeness without leaking the votes', () => {
      const c = cycle({
        movies: [{ movieId: 1, title: 'A', dadVote: 'Up' }, { movieId: 2, title: 'B' }],
        myVotes: { 1: 'Up', 2: 'Down' }
      });
      ownerName = 'Mom';
      component.ngOnInit();

      expect(component.myVotesComplete(c)).toBe(true);
      // Dad has voted on one of the two, so he is not done — and that is all
      // this ever says while the cycle is Active.
      expect(component.otherVotesComplete(c)).toBe(false);
    });

    it('should say a movie already on disk is ready', () => {
      const c = cycle({ resolvedMovieId: 1, movies: [{ movieId: 1, title: 'A', hasFile: true }] });

      expect(component.downloadStatusLabel(c)).toContain('ready');
      // Nothing to retry once the file is there.
      expect(component.canRetryDownload(c)).toBe(false);
    });

    it('should hide a broken poster rather than show a torn image', () => {
      const img = document.createElement('img');

      component.onPosterError({ target: img } as unknown as Event);

      expect(img.style.display).toBe('none');
    });
  });
});

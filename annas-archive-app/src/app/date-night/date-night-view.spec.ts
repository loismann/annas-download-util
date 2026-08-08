import { CycleMovieView, CycleView, ProposedSlot, ScheduleState } from '../services/date-night-api.service';
import { hawaiiSlotToUtcIso } from './countdown.util';
import {
  PlaybackWindowMs, canRetryDownload, canStartMovie, downloadStatusLabel, myVotesComplete,
  otherPerson, otherVoteFor, otherVotesComplete, resolvedMovie, shouldOpenScheduleModal,
  showtimePassed
} from './date-night-view';

/**
 * These were methods on a 946-line component. The two clock-dependent ones read
 * `Date.now()` directly, so the one-hour playback window could not be tested from
 * either side of its own edge — which is the only place it is interesting.
 */
describe('date-night-view', () => {
  const movie = (movieId: number, over: Partial<CycleMovieView> = {}): CycleMovieView => ({
    movieId, title: `Movie ${movieId}`, hasFile: false, monitored: false, ...over
  } as CycleMovieView);

  const cycle = (over: Partial<CycleView> = {}): CycleView => ({
    status: 'Active',
    movies: [movie(1), movie(2)],
    myVotes: {},
    shouldShowFlyerToday: false,
    shouldShowScheduleReminderToday: false,
    skipped: false,
    ...over
  } as CycleView);

  const schedule = (over: Partial<ScheduleState> = {}): ScheduleState => ({
    status: 'AwaitingProposal', proposedSlots: [], acknowledgedBy: [], ...over
  } as ScheduleState);

  describe('resolvedMovie', () => {
    it('finds the movie the week settled on', () => {
      const c = cycle({ resolvedMovieId: 2 });

      expect(resolvedMovie(c)?.movieId).toBe(2);
    });

    it('is undefined while the week is unresolved', () => {
      expect(resolvedMovie(cycle())).toBeUndefined();
    });

    /** A resolved id can outlive the movie it names if Radarr drops it mid-week. */
    it('is undefined when the resolved movie is no longer in the list', () => {
      expect(resolvedMovie(cycle({ resolvedMovieId: 99 }))).toBeUndefined();
    });
  });

  describe('who is who', () => {
    it('names the other half of the household', () => {
      expect(otherPerson('Mom')).toBe('Dad');
      expect(otherPerson('Dad')).toBe('Mom');
    });

    /** Nobody signed in is treated as Dad's view — the template only reads this for a label. */
    it('falls back to Mom for an unknown viewer', () => {
      expect(otherPerson(null)).toBe('Mom');
    });

    it('reads the other person\'s vote from the right field', () => {
      const m = movie(1, { momVote: 'Up', dadVote: 'Down' });

      expect(otherVoteFor(m, 'Mom')).toBe('Down');
      expect(otherVoteFor(m, 'Dad')).toBe('Up');
    });
  });

  describe('vote completeness', () => {
    it('is complete only once every movie has my vote', () => {
      expect(myVotesComplete(cycle({ myVotes: { 1: 'Up' } }))).toBeFalse();
      expect(myVotesComplete(cycle({ myVotes: { 1: 'Up', 2: 'Down' } }))).toBeTrue();
    });

    /**
     * Reports only *whether* the other person finished, never what they chose —
     * surfacing that before the week resolves would let one person answer to match.
     */
    it('reports the other person as finished without revealing their votes', () => {
      const c = cycle({ movies: [movie(1, { dadVote: 'Up' }), movie(2, { dadVote: 'Never' })] });

      expect(otherVotesComplete(c, 'Mom')).toBeTrue();
    });

    it('is incomplete while the other person has one left', () => {
      const c = cycle({ movies: [movie(1, { dadVote: 'Up' }), movie(2)] });

      expect(otherVotesComplete(c, 'Mom')).toBeFalse();
    });

    /** An empty ballot is vacuously complete; there is nothing left to answer. */
    it('treats a week with no movies as complete', () => {
      expect(myVotesComplete(cycle({ movies: [] }))).toBeTrue();
      expect(otherVotesComplete(cycle({ movies: [] }), 'Mom')).toBeTrue();
    });
  });

  describe('showtime window', () => {
    const slot: ProposedSlot = { date: '2026-07-31', time: '19:00' };
    const showtime = new Date(hawaiiSlotToUtcIso(slot)).getTime();

    it('has not passed a minute before the showtime', () => {
      expect(showtimePassed(slot, showtime - 60_000)).toBeFalse();
    });

    it('has passed a minute after the showtime', () => {
      expect(showtimePassed(slot, showtime + 60_000)).toBeTrue();
    });

    it('reports nothing passed when no slot is locked', () => {
      expect(showtimePassed(undefined, showtime)).toBeFalse();
      expect(canStartMovie(undefined, showtime)).toBeFalse();
    });

    /** Starting early defeats the point of agreeing a time. */
    it('does not offer Play before the showtime', () => {
      expect(canStartMovie(slot, showtime - 1)).toBeFalse();
    });

    it('offers Play from the showtime itself', () => {
      expect(canStartMovie(slot, showtime)).toBeTrue();
    });

    it('still offers Play at the very end of the window', () => {
      expect(canStartMovie(slot, showtime + PlaybackWindowMs)).toBeTrue();
    });

    /**
     * The far edge. Without it a week-old locked slot would keep offering to start a
     * movie nobody is waiting for.
     */
    it('stops offering Play one millisecond past the window', () => {
      expect(canStartMovie(slot, showtime + PlaybackWindowMs + 1)).toBeFalse();
    });
  });

  describe('downloadStatusLabel', () => {
    const locked = (downloadStatus?: string) =>
      cycle({ resolvedMovieId: 1, schedule: schedule({ status: 'Locked', downloadStatus } as Partial<ScheduleState>) });

    /** A file already on disk makes Radarr's status irrelevant. */
    it('reports the file as ready regardless of what Radarr thinks', () => {
      const c = cycle({
        resolvedMovieId: 1,
        movies: [movie(1, { hasFile: true })],
        schedule: schedule({ status: 'Locked', downloadStatus: 'Failed' } as Partial<ScheduleState>)
      });

      expect(downloadStatusLabel(c)).toBe('Downloaded and ready to play');
    });

    it('describes each Radarr stage in plain language', () => {
      expect(downloadStatusLabel(locked('Searching'))).toContain('Searching Radarr');
      expect(downloadStatusLabel(locked('Requested'))).toContain('downloading now');
      expect(downloadStatusLabel(locked('Monitoring'))).toContain('monitoring');
      expect(downloadStatusLabel(locked('Failed'))).toContain('could not start');
    });

    it('explains the gap between locking a time and the download starting', () => {
      expect(downloadStatusLabel(locked(undefined))).toBe('Waiting to start the Radarr download');
    });

    /** Nothing to say before a time is agreed. */
    it('says nothing while the schedule is not locked', () => {
      expect(downloadStatusLabel(cycle({ schedule: schedule({ status: 'AwaitingApproval' }) }))).toBeNull();
      expect(downloadStatusLabel(cycle())).toBeNull();
    });
  });

  describe('canRetryDownload', () => {
    const locked = (downloadStatus?: string) =>
      cycle({ resolvedMovieId: 1, schedule: schedule({ status: 'Locked', downloadStatus } as Partial<ScheduleState>) });

    it('offers a retry once Radarr has stopped trying', () => {
      expect(canRetryDownload(locked('Failed'))).toBeTrue();
      expect(canRetryDownload(locked('Monitoring'))).toBeTrue();
      expect(canRetryDownload(locked(undefined))).toBeTrue();
    });

    /** A second request while one is in flight has Radarr grab the same release twice. */
    it('does not offer a retry while a request is already in flight', () => {
      expect(canRetryDownload(locked('Searching'))).toBeFalse();
      expect(canRetryDownload(locked('Requested'))).toBeFalse();
    });

    it('does not offer a retry for a movie already on disk', () => {
      const c = cycle({
        resolvedMovieId: 1,
        movies: [movie(1, { hasFile: true })],
        schedule: schedule({ status: 'Locked', downloadStatus: 'Failed' } as Partial<ScheduleState>)
      });

      expect(canRetryDownload(c)).toBeFalse();
    });

    it('does not offer a retry before a showtime is locked', () => {
      expect(canRetryDownload(cycle({ schedule: schedule({ status: 'AwaitingApproval' }) }))).toBeFalse();
    });
  });

  describe('shouldOpenScheduleModal', () => {
    const resolved = (s: Partial<ScheduleState>, over: Partial<CycleView> = {}) =>
      cycle({ status: 'Resolved', schedule: schedule(s), ...over });

    it('opens for an unanswered proposal on the day the reminder is due', () => {
      const c = resolved({ status: 'AwaitingApproval' }, { shouldShowScheduleReminderToday: true });

      expect(shouldOpenScheduleModal(c, 'Mom')).toBeTrue();
    });

    it('stays shut on a day the reminder is not due', () => {
      const c = resolved({ status: 'AwaitingApproval' }, { shouldShowScheduleReminderToday: false });

      expect(shouldOpenScheduleModal(c, 'Mom')).toBeFalse();
    });

    it('tells the other person when a showtime was called off', () => {
      const c = resolved({ status: 'Cancelled', cancelledBy: 'Dad', acknowledgedBy: [] });

      expect(shouldOpenScheduleModal(c, 'Mom')).toBeTrue();
    });

    /** The canceller already knows. */
    it('does not announce a cancellation to whoever made it', () => {
      const c = resolved({ status: 'Cancelled', cancelledBy: 'Dad', acknowledgedBy: [] });

      expect(shouldOpenScheduleModal(c, 'Dad')).toBeFalse();
    });

    /** Without this the notice would reappear on every poll. */
    it('stops announcing a cancellation once it has been acknowledged', () => {
      const c = resolved({ status: 'Cancelled', cancelledBy: 'Dad', acknowledgedBy: ['Mom'] });

      expect(shouldOpenScheduleModal(c, 'Mom')).toBeFalse();
    });

    it('stays shut before the week resolves', () => {
      const c = cycle({ status: 'Active', schedule: schedule({ status: 'AwaitingApproval' }), shouldShowScheduleReminderToday: true });

      expect(shouldOpenScheduleModal(c, 'Mom')).toBeFalse();
    });

    it('stays shut when nobody is signed in', () => {
      const c = resolved({ status: 'AwaitingApproval' }, { shouldShowScheduleReminderToday: true });

      expect(shouldOpenScheduleModal(c, null)).toBeFalse();
    });

    it('stays shut when there is no schedule at all', () => {
      expect(shouldOpenScheduleModal(cycle({ status: 'Resolved' }), 'Mom')).toBeFalse();
    });
  });
});

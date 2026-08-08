import { CycleMovieView, CycleView, ProposedSlot } from '../services/date-night-api.service';
import { hawaiiSlotToUtcIso } from './countdown.util';

/**
 * What the Date Night screen shows, as pure functions of the cycle it was given.
 *
 * These lived on a 946-line component alongside a router, a playback service and ten
 * subscriptions. The two that turn on a clock — whether a showtime has passed, and
 * whether the Play button is live — read `Date.now()` directly, which meant the
 * one-hour window they implement could not be tested from either side of its own edge.
 * Both now take `now` as a parameter.
 */

/** How long after the showtime the Play button stays available. */
export const PlaybackWindowMs = 60 * 60 * 1000;

/** The movie the week settled on, if it has settled. */
export function resolvedMovie(cycle: CycleView): CycleMovieView | undefined {
  return cycle.movies.find(m => m.movieId === cycle.resolvedMovieId);
}

/** The other half of the household. */
export function otherPerson(myName: string | null): string {
  return myName === 'Mom' ? 'Dad' : 'Mom';
}

/**
 * The other person's vote on one movie. Which field that is depends on who is
 * looking, which is the only reason this is not a plain property read.
 */
export function otherVoteFor(movie: CycleMovieView, myName: string | null): string | undefined {
  return myName === 'Mom' ? movie.dadVote : movie.momVote;
}

/** Whether every one of this week's movies has my vote recorded. */
export function myVotesComplete(cycle: CycleView): boolean {
  return cycle.movies.every(m => cycle.myVotes[m.movieId] != null);
}

/**
 * Whether the other person has finished voting. Deliberately reports only
 * *whether* — surfacing what they voted before the week resolves would let one
 * person answer to match the other.
 */
export function otherVotesComplete(cycle: CycleView, myName: string | null): boolean {
  return cycle.movies.every(m => otherVoteFor(m, myName) != null);
}

export function showtimePassed(slot: ProposedSlot | undefined, now: number): boolean {
  if (!slot) return false;
  return new Date(hawaiiSlotToUtcIso(slot)).getTime() < now;
}

/**
 * Whether the Play button should be live: from the showtime until an hour after it.
 *
 * Not before, because starting early defeats the point of agreeing a time; not
 * indefinitely after, because a week-old locked slot would otherwise keep offering to
 * start a movie nobody is waiting for.
 */
export function canStartMovie(slot: ProposedSlot | undefined, now: number): boolean {
  if (!slot) return false;
  const showtime = new Date(hawaiiSlotToUtcIso(slot)).getTime();
  return now >= showtime && now <= showtime + PlaybackWindowMs;
}

/**
 * A plain-language line for where the download has got to, or null when there is
 * nothing worth saying. A movie already on disk short-circuits everything: Radarr's
 * status is irrelevant once the file is there.
 */
export function downloadStatusLabel(cycle: CycleView): string | null {
  if (resolvedMovie(cycle)?.hasFile) return 'Downloaded and ready to play';

  switch (cycle.schedule?.downloadStatus) {
    case 'Searching': return 'Searching Radarr for a release…';
    case 'Requested': return 'Sent to Radarr — downloading now';
    case 'Monitoring': return 'Radarr is monitoring it; no acceptable release was available yet';
    case 'Failed': return 'Radarr could not start this download';
    default:
      return cycle.schedule?.status === 'Locked'
        ? 'Waiting to start the Radarr download'
        : null;
  }
}

/**
 * Whether to offer a retry. Not while a search or request is already in flight —
 * a second request would have Radarr grab the same release twice.
 */
export function canRetryDownload(cycle: CycleView): boolean {
  if (resolvedMovie(cycle)?.hasFile) return false;
  if (cycle.schedule?.status !== 'Locked') return false;

  const status = cycle.schedule.downloadStatus;
  return status !== 'Searching' && status !== 'Requested';
}

/**
 * Whether to auto-open the schedule modal: a daily "your turn" for an unanswered
 * proposal, or the one-time "called off" notice for whoever did not cancel.
 *
 * The canceller is excluded because they already know, and the acknowledgement check
 * is what stops the notice reappearing on every poll.
 */
export function shouldOpenScheduleModal(cycle: CycleView, myName: string | null): boolean {
  if (cycle.status !== 'Resolved' || !myName) return false;

  const schedule = cycle.schedule;
  if (!schedule) return false;

  if (schedule.status === 'AwaitingApproval') return cycle.shouldShowScheduleReminderToday;

  return schedule.status === 'Cancelled'
    && schedule.cancelledBy !== myName
    && !schedule.acknowledgedBy.includes(myName);
}

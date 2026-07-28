import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LoggerService } from './logger.service';
import { apiBase } from './api-base';

/** One movie in the Date Night pool. `available` is null when it has never been
 * checked — distinct from false, which means it was checked and nothing grabbable
 * came back. See DOCS/DATE_NIGHT_FEATURE.md. */
export interface DateNightPoolItem {
  movieId: number;
  title: string;
  year?: number;
  overview?: string;
  posterUrl?: string;
  hasFile: boolean;
  monitored: boolean;
  available: boolean | null;
  grabbableReleases: number;
  rejectedReleases: number;
  checkedUtc?: string;
}

export interface DateNightPoolSummary {
  total: number;
  available: number;
  unavailable: number;
  unchecked: number;
  alreadyDownloaded: number;
}

export interface AvailabilityScanStatus {
  running: boolean;
  checked: number;
  total: number;
  startedUtc?: string;
  finishedUtc?: string;
  error?: string;
}

/** Per-person announcement state. Both null = they haven't loaded a page since
 * it went live. `shownUtc` set but `dismissedUtc` null = it appeared and they
 * closed the tab rather than acknowledging it, so it will show again. */
export interface AnnouncementRecipient {
  person: string;
  shownUtc?: string;
  dismissedUtc?: string;
}

/// One of this week's drawn movies, as both the admin panel and the real flyer need
/// it. `summary` is the AI pitch line — falls back to `overview` if generation failed.
export interface CycleMovieView {
  movieId: number;
  title: string;
  posterUrl?: string;
  tmdbId?: number;
  overview?: string;
  summary?: string;
  momVote?: string;
  dadVote?: string;
}

export interface ProposedSlot {
  date: string; // "yyyy-MM-dd", Hawaii
  time: string; // "HH:mm", Hawaii
}

/// The propose -> approve -> lock handshake for a resolved week's movie.
export interface ScheduleState {
  status: 'AwaitingProposal' | 'AwaitingApproval' | 'Locked' | 'Cancelled';
  proposedBy?: string;
  proposedSlots: ProposedSlot[];
  lockedSlot?: ProposedSlot;
  lockedUtc?: string;
}

export interface ShowtimeStatus {
  imminent: boolean;
  movieId?: number;
  showtimeUtc?: string;
}

/// A movie sitting in never-show or a disagreement cooling-off, with a way back.
export interface RecoverableMovie {
  movieId: number;
  title: string;
  reason: string;
  since: string;
}

export interface SkipState {
  skipUntilUtc?: string;
  setBy?: string;
  setUtc?: string;
}

/// Everything the admin "Weekly cycle" panel needs — see DateNightCycleService.
export interface CycleAdminView {
  cycleId?: string;
  status: string;
  deadlineUtc?: string;
  resolvedUtc?: string;
  movies: CycleMovieView[];
  resolvedMovieId?: number;
  schedule?: ScheduleState;
  skip: SkipState;
  live: boolean;
  neverShowCount: number;
  watchedCount: number;
  coolingOffCount: number;
  recoverable: RecoverableMovie[];
}

export interface DateNightPoolResponse {
  summary: DateNightPoolSummary;
  scan: AvailabilityScanStatus;
  items: DateNightPoolItem[];
  announcement: AnnouncementRecipient[];
  cycle: CycleAdminView | null;
}

/** The one-time "coming soon" splash: whether this person should see it, and
 * poster URLs from the pool to decorate it with. `live` tells the Date Night page
 * whether to render the poster (false) or the real lobby/flyer (true). */
export interface DateNightAnnouncement {
  shouldShow: boolean;
  posters: string[];
  live: boolean;
}

/// What Mom or Dad see when they check this week's draw.
export interface CycleView {
  cycleId?: string;
  status: string;
  deadlineUtc?: string;
  movies: CycleMovieView[];
  myVotes: Record<number, string>;
  resolvedMovieId?: number;
  resolvedTitle?: string;
  schedule?: ScheduleState;
  shouldShowFlyerToday: boolean;
  skipped: boolean;
}

@Injectable({ providedIn: 'root' })
export class DateNightApiService {
  private readonly baseUrl = `${apiBase()}/api/date-night`;

  constructor(
    private http: HttpClient,
    private logger: LoggerService
  ) {}

  getPool(): Observable<DateNightPoolResponse> {
    return this.http.get<DateNightPoolResponse>(`${this.baseUrl}/pool`);
  }

  /**
   * Starts the availability pre-pass and returns immediately — a full pass is
   * paced across hours, so progress is read back from getPool() rather than
   * from this call.
   *
   * @param force  Re-check movies whose result is still recent.
   * @param limit  Stop after this many checks, for a trial run.
   */
  startAvailabilityScan(force = false, limit?: number): Observable<unknown> {
    let params = new HttpParams().set('force', force);
    if (limit != null) params = params.set('limit', limit);
    this.logger.log('[DateNightApiService] startAvailabilityScan', { force, limit });
    return this.http.post(`${this.baseUrl}/availability/scan`, null, { params });
  }

  /** @param preview Force it to report shouldShow for the current user without
   *  marking it seen — how an admin reviews it before it goes out. */
  getAnnouncement(preview = false): Observable<DateNightAnnouncement> {
    const params = new HttpParams().set('preview', preview);
    return this.http.get<DateNightAnnouncement>(`${this.baseUrl}/announcement`, { params });
  }

  dismissAnnouncement(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/announcement/dismiss`, null);
  }

  // Person-facing cycle/schedule routes (Mom and Dad).

  getCycle(): Observable<CycleView> {
    return this.http.get<CycleView>(`${this.baseUrl}/cycle`);
  }

  castVote(movieId: number, vote: 'Up' | 'Down' | 'Never'): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/vote`, { movieId, vote });
  }

  setSkip(scope: 'week' | 'month'): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/skip`, { scope });
  }

  recordFlyerShown(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/flyer-shown`, null);
  }

  proposeSchedule(slots: ProposedSlot[]): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/schedule/propose`, { slots });
  }

  approveSchedule(slot: ProposedSlot): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/schedule/approve`, { slot });
  }

  cancelSchedule(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/schedule/cancel`, null);
  }

  /** Polled app-wide (see AppComponent) since there are no push notifications —
   *  this is how the countdown popup knows when to appear. */
  checkShowtime(): Observable<ShowtimeStatus> {
    return this.http.get<ShowtimeStatus>(`${this.baseUrl}/showtime-check`);
  }

  markWatched(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/mark-watched`, null);
  }

  // Admin-only cycle controls — how phases 3-7 are exercised end to end before the
  // real flyer/scheduling UI is live for Mom and Dad. See DOCS/DATE_NIGHT_FEATURE.md.

  forceIssueCycle(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/force-issue`, null);
  }

  resolveCycleNow(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/resolve-now`, null);
  }

  discardCycle(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/discard`, null);
  }

  castVoteAsAdmin(person: string, movieId: number, vote: 'Up' | 'Down' | 'Never'): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/vote`, { person, movieId, vote });
  }

  restoreMovie(movieId: number): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/restore/${movieId}`, null);
  }

  clearSkip(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/skip/clear`, null);
  }

  proposeScheduleAsAdmin(person: string, slots: ProposedSlot[]): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/schedule/propose`, { person, slots });
  }

  approveScheduleAsAdmin(person: string, slot: ProposedSlot): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/schedule/approve`, { person, slot });
  }

  cancelScheduleAsAdmin(person: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/schedule/cancel`, { person });
  }

  markWatchedAsAdmin(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/mark-watched`, null);
  }

  goLive(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/go-live`, null);
  }

  goDark(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/cycle/admin/go-dark`, null);
  }
}

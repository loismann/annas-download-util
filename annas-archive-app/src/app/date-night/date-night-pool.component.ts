import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { Subscription, interval, startWith, switchMap } from 'rxjs';
import {
  DateNightApiService,
  DateNightPoolItem,
  DateNightPoolResponse,
  ProposedSlot,
  ScheduleState
} from '../services/date-night-api.service';
import { DateNightAnnouncementService } from '../services/date-night-announcement.service';

type PoolFilter = 'all' | 'available' | 'unavailable' | 'unchecked';

/**
 * Admin view of the Date Night pool and its availability pre-pass.
 *
 * The pool is a few hundred obscure B-movies registered in Radarr as records only
 * — unmonitored and undownloaded — so this page is the only place they're visible;
 * they're deliberately kept out of the regular movie library, which would otherwise
 * fill up with hundreds of tiles nobody can watch.
 *
 * The headline numbers here are the gate on the rest of the feature: a weekly draw
 * of three movies only works if enough of the pool can actually be obtained. See
 * DOCS/DATE_NIGHT_FEATURE.md.
 */
@Component({
  selector: 'app-date-night-pool',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule,
    MatProgressSpinnerModule, MatProgressBarModule, MatButtonToggleModule
  ],
  template: `
    <div class="pool-page">
      <header class="pool-header">
        <h1>Date Night pool</h1>
        <p class="subtitle" *ngIf="data">
          {{ data.summary.total }} movies in the pool. Nothing here downloads until a date
          night is scheduled for it.
        </p>
        <!-- The leak-prevention gate: while dark, Mom and Dad see only the "coming
             soon" poster no matter what's built/deployed behind it. -->
        <p class="live-banner" *ngIf="data?.cycle as cycle" [class.is-live]="cycle.live">
          <span>{{ cycle.live ? 'LIVE for Mom & Dad' : 'Dark — Mom & Dad see only the poster' }}</span>
          <button mat-stroked-button *ngIf="!cycle.live" (click)="goLive()">Go live</button>
          <button mat-stroked-button *ngIf="cycle.live" (click)="goDark()">Go dark</button>
        </p>
      </header>

      <div *ngIf="loading && !data" class="loading"><mat-spinner diameter="32"></mat-spinner></div>
      <div *ngIf="error" class="error">{{ error }}</div>

      <ng-container *ngIf="data">
        <div class="summary-cards">
          <div class="card ok">
            <span class="value">{{ data.summary.available }}</span>
            <span class="label">can be downloaded</span>
          </div>
          <div class="card bad">
            <span class="value">{{ data.summary.unavailable }}</span>
            <span class="label">nothing found</span>
          </div>
          <div class="card muted">
            <span class="value">{{ data.summary.unchecked }}</span>
            <span class="label">not checked yet</span>
          </div>
          <div class="card">
            <span class="value">{{ data.summary.alreadyDownloaded }}</span>
            <span class="label">already on the server</span>
          </div>
        </div>

        <section class="scan-panel">
          <ng-container *ngIf="data.scan.running; else scanIdle">
            <p>
              Checking indexers — {{ data.scan.checked }} of {{ data.scan.total }}.
              This runs slowly on purpose (about {{ remainingLabel }} to go); you can leave this page.
            </p>
            <mat-progress-bar
              mode="determinate"
              [value]="data.scan.total ? (data.scan.checked / data.scan.total) * 100 : 0"
            ></mat-progress-bar>
          </ng-container>

          <ng-template #scanIdle>
            <p *ngIf="data.scan.error" class="error">Last scan ended early: {{ data.scan.error }}</p>
            <p *ngIf="data.scan.finishedUtc && !data.scan.error">
              Last checked {{ data.scan.finishedUtc | date: 'medium' }} — {{ data.scan.checked }} movies.
            </p>
            <div class="scan-actions">
              <button mat-raised-button color="primary" [disabled]="!data.summary.unchecked" (click)="startScan(false)">
                Check the {{ data.summary.unchecked }} unchecked
              </button>
              <button mat-stroked-button [disabled]="!data.summary.unchecked" (click)="startScan(false, 10)">
                Try 10 first
              </button>
              <button mat-stroked-button [disabled]="!data.summary.total" (click)="startScan(true)">
                Re-check everything
              </button>
              <button mat-stroked-button (click)="previewAnnouncement()">
                Preview the announcement
              </button>
            </div>
          </ng-template>
        </section>

        <!-- Answers "have Mom and Dad actually seen the announcement?" without
             having to ask them. Shown and dismissed are separate: closing the tab
             is not the same as acknowledging it, and only the latter stops it
             reappearing. -->
        <section class="announce-panel" *ngIf="data.announcement?.length">
          <h2>Announcement</h2>
          <div class="recipients">
            <div class="recipient" *ngFor="let r of data.announcement">
              <span class="who">{{ r.person }}</span>
              <span *ngIf="r.dismissedUtc" class="ok-text">
                Seen and closed — {{ r.dismissedUtc | date: 'MMM d, h:mm a' }}
              </span>
              <span *ngIf="!r.dismissedUtc && r.shownUtc" class="pending-text">
                Shown {{ r.shownUtc | date: 'MMM d, h:mm a' }}, not closed — will show again
              </span>
              <span *ngIf="!r.shownUtc" class="muted-text">Not seen yet</span>
            </div>
          </div>
        </section>

        <!-- Phase 3's testable surface: the real flyer/voting UI is phase 4, so this
             panel is how the weekly cycle state machine gets driven and checked —
             force a draw, vote as either person, resolve, discard, repeat. -->
        <section class="cycle-panel" *ngIf="data.cycle as cycle">
          <h2>Weekly cycle</h2>

          <p *ngIf="cycle.skip.skipUntilUtc" class="skip-banner">
            Skipped by {{ cycle.skip.setBy }} — resumes {{ cycle.skip.skipUntilUtc | date: 'medium' }}
            <button mat-button (click)="clearSkip()">Clear skip</button>
          </p>

          <p *ngIf="cycle.status !== 'None'">
            <span class="status-badge" [ngClass]="'status-' + cycle.status.toLowerCase()">{{ cycle.status }}</span>
            <span *ngIf="cycle.deadlineUtc" class="muted-text">
              &nbsp;deadline {{ cycle.deadlineUtc | date: 'EEE MMM d, h:mm a' }}
            </span>
          </p>
          <p *ngIf="cycle.status === 'None' && !cycle.skip.skipUntilUtc" class="muted-text">
            No cycle issued yet.
          </p>

          <table class="cycle-table" *ngIf="cycle.movies.length">
            <thead>
              <tr>
                <th>Movie</th>
                <th>Mom</th>
                <th>Dad</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let m of cycle.movies" [class.resolved-row]="m.movieId === cycle.resolvedMovieId">
                <td>
                  {{ m.title }}
                  <span *ngIf="m.movieId === cycle.resolvedMovieId" class="ok-text"> — picked!</span>
                  <p class="pitch-line muted-text" *ngIf="m.summary">"{{ m.summary }}"</p>
                </td>
                <td>
                  <span class="vote-mark">{{ voteEmoji(m.momVote) }}</span>
                  <span class="vote-buttons">
                    <button mat-icon-button (click)="voteAs('Mom', m.movieId, 'Up')" title="Thumbs up">👍</button>
                    <button mat-icon-button (click)="voteAs('Mom', m.movieId, 'Down')" title="Thumbs down">👎</button>
                    <button mat-icon-button (click)="voteAs('Mom', m.movieId, 'Never')" title="Never show again">🚫</button>
                  </span>
                </td>
                <td>
                  <span class="vote-mark">{{ voteEmoji(m.dadVote) }}</span>
                  <span class="vote-buttons">
                    <button mat-icon-button (click)="voteAs('Dad', m.movieId, 'Up')" title="Thumbs up">👍</button>
                    <button mat-icon-button (click)="voteAs('Dad', m.movieId, 'Down')" title="Thumbs down">👎</button>
                    <button mat-icon-button (click)="voteAs('Dad', m.movieId, 'Never')" title="Never show again">🚫</button>
                  </span>
                </td>
              </tr>
            </tbody>
          </table>

          <div class="cycle-actions">
            <button mat-stroked-button (click)="forceIssue()">Force-issue a test cycle</button>
            <button mat-stroked-button [disabled]="cycle.status !== 'Active'" (click)="resolveNow()">Resolve now</button>
            <button mat-stroked-button [disabled]="!cycle.cycleId" (click)="discardCycle()">Discard cycle</button>
          </div>

          <!-- Schedule handshake test controls (phase 5) — exercises propose ->
               approve -> lock -> (real Radarr grab) without needing a Mom/Dad login. -->
          <div class="schedule-panel" *ngIf="cycle.status === 'Resolved' && cycle.schedule as s">
            <h3>Schedule — {{ s.status }}</h3>

            <div *ngIf="s.status === 'AwaitingProposal'" class="btn-row">
              <button mat-stroked-button (click)="proposeAsAdmin('Mom')">Propose as Mom (tomorrow 7pm)</button>
              <button mat-stroked-button (click)="proposeAsAdmin('Dad')">Propose as Dad (tomorrow 7pm)</button>
            </div>

            <div *ngIf="s.status === 'AwaitingApproval'">
              <p class="muted-text">Proposed by {{ s.proposedBy }}: {{ formatSlots(s.proposedSlots) }}</p>
              <div class="btn-row">
                <button mat-stroked-button (click)="approveAsAdmin(s)">
                  Approve as {{ s.proposedBy === 'Mom' ? 'Dad' : 'Mom' }} (first slot)
                </button>
                <button mat-stroked-button (click)="cancelScheduleAsAdmin(s.proposedBy!)">Cancel</button>
              </div>
            </div>

            <div *ngIf="s.status === 'Locked'">
              <p class="ok-text">Locked: {{ s.lockedSlot ? formatSlots([s.lockedSlot]) : '' }}</p>
              <div class="btn-row">
                <button mat-stroked-button (click)="markWatchedAsAdmin()">Mark watched (cleanup)</button>
                <button mat-stroked-button (click)="cancelScheduleAsAdmin('Mom')">Cancel</button>
              </div>
            </div>

            <p *ngIf="s.status === 'Cancelled'" class="muted-text">Cancelled.</p>
          </div>

          <p class="cycle-list-summary muted-text">
            {{ cycle.neverShowCount }} never-show &middot; {{ cycle.watchedCount }} watched &middot;
            {{ cycle.coolingOffCount }} cooling off
          </p>

          <details class="recoverable" *ngIf="cycle.recoverable.length">
            <summary>Recoverable ({{ cycle.recoverable.length }})</summary>
            <div class="recoverable-row" *ngFor="let r of cycle.recoverable">
              <span>{{ r.title }} — {{ r.reason }} ({{ r.since | date: 'MMM d' }})</span>
              <button mat-button (click)="restoreMovie(r.movieId)">Restore</button>
            </div>
          </details>
        </section>

        <mat-button-toggle-group [(ngModel)]="filter" name="poolFilter" class="filter-toggle">
          <mat-button-toggle value="all">All ({{ data.summary.total }})</mat-button-toggle>
          <mat-button-toggle value="available">Available ({{ data.summary.available }})</mat-button-toggle>
          <mat-button-toggle value="unavailable">Nothing found ({{ data.summary.unavailable }})</mat-button-toggle>
          <mat-button-toggle value="unchecked">Unchecked ({{ data.summary.unchecked }})</mat-button-toggle>
        </mat-button-toggle-group>

        <table class="pool-table">
          <thead>
            <tr>
              <th>Title</th>
              <th>Year</th>
              <th>Availability</th>
              <th>Checked</th>
            </tr>
          </thead>
          <tbody>
            <tr *ngFor="let item of visibleItems">
              <td>{{ item.title }}</td>
              <td>{{ item.year ?? '—' }}</td>
              <td [ngSwitch]="item.available">
                <span *ngSwitchCase="true" class="ok-text">
                  {{ item.grabbableReleases }} release{{ item.grabbableReleases === 1 ? '' : 's' }}
                </span>
                <span *ngSwitchCase="false" class="bad-text">
                  <ng-container *ngIf="item.rejectedReleases > 0; else nothingAtAll">
                    {{ item.rejectedReleases }} found, all rejected by the quality profile
                  </ng-container>
                  <ng-template #nothingAtAll>nothing found</ng-template>
                </span>
                <span *ngSwitchDefault class="muted-text">not checked</span>
              </td>
              <td class="muted-text">{{ item.checkedUtc ? (item.checkedUtc | date: 'shortDate') : '—' }}</td>
            </tr>
          </tbody>
        </table>

        <p *ngIf="visibleItems.length === 0" class="empty-state">Nothing in this category.</p>
      </ng-container>
    </div>
  `,
  styles: [`
    .pool-page { padding: 24px; max-width: 1100px; margin: 0 auto; }
    .subtitle { opacity: 0.75; }
    .loading { display: flex; justify-content: center; padding: 48px; }
    .error { color: var(--mat-sys-error, #d32f2f); }
    .summary-cards { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; }
    .card {
      flex: 1 1 140px; padding: 12px 16px; border-radius: 8px;
      background: rgba(128, 128, 128, 0.12); display: flex; flex-direction: column;
    }
    .card .value { font-size: 1.8em; font-weight: 600; }
    .card .label { font-size: 0.85em; opacity: 0.75; }
    .card.ok .value { color: #2e7d32; }
    .card.bad .value { color: #d32f2f; }
    .card.muted .value { opacity: 0.6; }
    .scan-panel { margin: 16px 0 24px; padding: 16px; border-radius: 8px; background: rgba(128,128,128,0.08); }
    .scan-actions { display: flex; flex-wrap: wrap; gap: 8px; }
    .announce-panel { margin: 0 0 20px; padding: 12px 16px; border-radius: 8px; background: rgba(128,128,128,0.08); }
    .announce-panel h2 { margin: 0 0 8px; font-size: 1rem; }
    .recipients { display: flex; flex-wrap: wrap; gap: 8px 28px; }
    .recipient { display: flex; align-items: baseline; gap: 8px; font-size: 0.9em; }
    .recipient .who { font-weight: 600; min-width: 42px; }
    .pending-text { color: #b26a00; }

    .live-banner {
      display: inline-flex; align-items: center; gap: 10px; margin: 6px 0 0;
      padding: 6px 12px; border-radius: 6px; background: rgba(211,47,47,0.14); color: #d32f2f; font-size: 0.85em;
    }
    .live-banner.is-live { background: rgba(46,125,50,0.14); color: #2e7d32; }

    .cycle-panel { margin: 0 0 20px; padding: 12px 16px; border-radius: 8px; background: rgba(128,128,128,0.08); }
    .cycle-panel h2 { margin: 0 0 8px; font-size: 1rem; }
    .skip-banner {
      display: flex; align-items: center; gap: 8px; margin: 0 0 10px;
      padding: 8px 10px; border-radius: 6px; background: rgba(178,106,0,0.12); color: #b26a00; font-size: 0.9em;
    }
    .status-badge {
      display: inline-block; padding: 2px 10px; border-radius: 12px; font-size: 0.82em; font-weight: 600;
      background: rgba(128,128,128,0.18);
    }
    .status-active { background: rgba(25,118,210,0.18); color: #1565c0; }
    .status-resolved { background: rgba(46,125,50,0.18); color: #2e7d32; }
    .status-nomatch, .status-cancelled { background: rgba(211,47,47,0.14); color: #d32f2f; }
    .cycle-table { width: 100%; border-collapse: collapse; font-size: 0.92em; margin: 10px 0; }
    .cycle-table th, .cycle-table td { text-align: left; padding: 6px 8px; border-bottom: 1px solid rgba(128,128,128,0.2); }
    .cycle-table .resolved-row { background: rgba(46,125,50,0.08); }
    .vote-mark { display: inline-block; min-width: 1.4em; }
    .vote-buttons button { width: 30px; height: 30px; line-height: 30px; }
    .cycle-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; }
    .cycle-list-summary { margin: 8px 0 0; font-size: 0.85em; }
    .pitch-line { margin: 4px 0 0; font-style: italic; font-size: 0.88em; }
    .schedule-panel { margin: 12px 0 0; padding: 10px; border-radius: 6px; background: rgba(128,128,128,0.08); }
    .schedule-panel h3 { margin: 0 0 8px; font-size: 0.9em; }
    .recoverable { margin-top: 10px; font-size: 0.88em; }
    .recoverable summary { cursor: pointer; }
    .recoverable-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 0; }
    .filter-toggle { margin-bottom: 12px; flex-wrap: wrap; }
    .pool-table { width: 100%; border-collapse: collapse; font-size: 0.92em; }
    .pool-table th, .pool-table td { text-align: left; padding: 6px 8px; border-bottom: 1px solid rgba(128,128,128,0.2); }
    .ok-text { color: #2e7d32; }
    .bad-text { color: #d32f2f; }
    .muted-text { opacity: 0.6; }
    .empty-state { opacity: 0.7; padding: 24px 0; }
  `]
})
export class DateNightPoolComponent implements OnInit, OnDestroy {
  /** Matches DateNightAvailabilityService.PacingDelay — only used to turn the raw
   * remaining count into a human estimate, so an approximation is fine. */
  private static readonly SECONDS_PER_CHECK = 20;

  data: DateNightPoolResponse | null = null;
  loading = true;
  error: string | null = null;
  filter: PoolFilter = 'all';

  private poll?: Subscription;

  constructor(
    private api: DateNightApiService,
    private announcement: DateNightAnnouncementService
  ) {}

  ngOnInit(): void {
    // Polls rather than fetching once: a scan runs for hours in the background,
    // and the page is most useful when it shows that progress live.
    this.poll = interval(15000)
      .pipe(startWith(0), switchMap(() => this.api.getPool()))
      .subscribe({
        next: data => {
          this.data = data;
          this.loading = false;
          this.error = null;
        },
        error: () => {
          this.loading = false;
          this.error = 'Could not load the pool — is Radarr reachable?';
        }
      });
  }

  ngOnDestroy(): void {
    this.poll?.unsubscribe();
  }

  get visibleItems(): DateNightPoolItem[] {
    const items = this.data?.items ?? [];
    switch (this.filter) {
      case 'available': return items.filter(i => i.available === true);
      case 'unavailable': return items.filter(i => i.available === false);
      case 'unchecked': return items.filter(i => i.available === null);
      default: return items;
    }
  }

  get remainingLabel(): string {
    const scan = this.data?.scan;
    if (!scan) return '';
    const minutes = Math.ceil(
      ((scan.total - scan.checked) * DateNightPoolComponent.SECONDS_PER_CHECK) / 60
    );
    if (minutes < 60) return `${minutes} min`;
    return `${Math.round((minutes / 60) * 10) / 10} hr`;
  }

  /** Opens the one-time Mom/Dad splash for an admin without consuming anyone's
   *  single showing — the only way to check it before it goes out. */
  previewAnnouncement(): void {
    this.announcement.checkAndMaybeShow(true);
  }

  startScan(force: boolean, limit?: number): void {
    this.api.startAvailabilityScan(force, limit).subscribe({
      next: () => {
        if (this.data) this.data = { ...this.data, scan: { ...this.data.scan, running: true, checked: 0 } };
      },
      error: () => { this.error = 'Could not start the scan.'; }
    });
  }

  voteEmoji(vote?: string): string {
    switch (vote) {
      case 'Up': return '👍';
      case 'Down': return '👎';
      case 'Never': return '🚫';
      default: return '—';
    }
  }

  /** Every cycle action re-fetches immediately rather than waiting on the 15s poll —
   *  these are admin test actions meant to be driven click by click. */
  private refreshNow(): void {
    this.api.getPool().subscribe({ next: data => { this.data = data; } });
  }

  forceIssue(): void {
    this.api.forceIssueCycle().subscribe({
      next: () => this.refreshNow(),
      error: () => { this.error = 'Could not issue a cycle — check that eligible movies exist.'; }
    });
  }

  resolveNow(): void {
    this.api.resolveCycleNow().subscribe({ next: () => this.refreshNow() });
  }

  discardCycle(): void {
    this.api.discardCycle().subscribe({ next: () => this.refreshNow() });
  }

  voteAs(person: string, movieId: number, vote: 'Up' | 'Down' | 'Never'): void {
    this.api.castVoteAsAdmin(person, movieId, vote).subscribe({ next: () => this.refreshNow() });
  }

  restoreMovie(movieId: number): void {
    this.api.restoreMovie(movieId).subscribe({ next: () => this.refreshNow() });
  }

  clearSkip(): void {
    this.api.clearSkip().subscribe({ next: () => this.refreshNow() });
  }

  goLive(): void {
    this.api.goLive().subscribe({ next: () => this.refreshNow() });
  }

  goDark(): void {
    this.api.goDark().subscribe({ next: () => this.refreshNow() });
  }

  formatSlots(slots: ProposedSlot[]): string {
    return slots.map(s => `${s.date} ${s.time}`).join(', ');
  }

  proposeAsAdmin(person: 'Mom' | 'Dad'): void {
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);
    const slot: ProposedSlot = { date: tomorrow.toISOString().slice(0, 10), time: '19:00' };
    this.api.proposeScheduleAsAdmin(person, [slot]).subscribe({
      next: () => this.refreshNow(),
      error: () => { this.error = 'Could not propose that slot.'; }
    });
  }

  approveAsAdmin(schedule: ScheduleState): void {
    const approver = schedule.proposedBy === 'Mom' ? 'Dad' : 'Mom';
    const slot = schedule.proposedSlots[0];
    if (!slot) return;
    this.api.approveScheduleAsAdmin(approver, slot).subscribe({
      next: () => this.refreshNow(),
      error: () => { this.error = 'Could not approve that slot.'; }
    });
  }

  cancelScheduleAsAdmin(person: string): void {
    this.api.cancelScheduleAsAdmin(person).subscribe({ next: () => this.refreshNow() });
  }

  markWatchedAsAdmin(): void {
    this.api.markWatchedAsAdmin().subscribe({
      next: () => this.refreshNow(),
      error: () => { this.error = 'Could not mark that watched.'; }
    });
  }
}

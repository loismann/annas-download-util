import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { Subscription, interval, startWith, switchMap } from 'rxjs';
import {
  CycleAdminView,
  DateNightApiService,
  DateNightPoolItem,
  DateNightPoolResponse,
  ProposedSlot
} from '../services/date-night-api.service';
import { DateNightAnnouncementService } from '../services/date-night-announcement.service';
import { formatCountdown, formatHawaiiSlot, hawaiiSlotToUtcIso, secondsUntil } from './countdown.util';

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
 * of five movies only works if enough of the pool can actually be obtained. See
 * DOCS/DATE_NIGHT_FEATURE.md.
 */
@Component({
  selector: 'app-date-night-pool',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule,
    MatProgressSpinnerModule, MatProgressBarModule, MatButtonToggleModule, MatIconModule
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
              <button
                mat-button
                *ngIf="r.shownUtc"
                (click)="resetAnnouncement(r.person)"
                title="Clear their state as if they'd never seen it — e.g. after testing on their account"
              >Reset</button>
            </div>
          </div>
        </section>

        <!-- One testing surface. The live household cycle is still available below
             as a collapsed, read-only status check, but it is deliberately not
             presented as a second set of test controls. -->
        <section class="cycle-panel dry-run-panel" *ngIf="data.testCycle as testCycle">
          <h2><mat-icon>science</mat-icon> Test Date Night</h2>
          <p>
            This is the isolated test cycle used by “Testing as Mom” and “Testing as
            Dad” on the Date Night page. Both complete ballots are required; the test
            picks a mutual favorite automatically when the final vote is saved.
          </p>
          <p class="warning-text">
            Test bookkeeping is isolated, but confirming a showtime still performs
            the real Radarr download action.
          </p>

          <p *ngIf="testCycle.status !== 'None'">
            <span class="status-badge" [ngClass]="'status-' + testCycle.status.toLowerCase()">{{ testCycle.status }}</span>
          </p>
          <p *ngIf="testCycle.status === 'None'" class="muted-text">
            No test is running. Opening the Date Night page and choosing Mom or Dad
            starts one with five movies.
          </p>

          <table class="cycle-table" *ngIf="testCycle.movies.length">
            <thead>
              <tr>
                <th>Movie</th>
                <th>Mom</th>
                <th>Dad</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let m of testCycle.movies" [class.resolved-row]="m.movieId === testCycle.resolvedMovieId">
                <td>
                  {{ m.title }}
                  <span *ngIf="m.movieId === testCycle.resolvedMovieId" class="ok-text"> — picked!</span>
                </td>
                <td><span class="vote-mark"><mat-icon>{{ voteIconName(m.momVote) }}</mat-icon></span></td>
                <td><span class="vote-mark"><mat-icon>{{ voteIconName(m.dadVote) }}</mat-icon></span></td>
              </tr>
            </tbody>
          </table>

          <p class="muted-text" *ngIf="testCycle.status === 'Resolved' && testCycle.schedule as ts">
            Schedule: {{ ts.status }}
            <ng-container *ngIf="ts.status === 'Locked' && ts.lockedSlot"> — {{ formatSlots([ts.lockedSlot]) }}</ng-container>
          </p>
          <ng-container *ngIf="testCycle.status === 'Resolved' && testCycle.schedule?.status === 'Locked'">
            <p class="test-download-state">
              <mat-icon>{{ resolvedTestMovie(testCycle)?.hasFile ? 'download_done' : 'downloading' }}</mat-icon>
              {{ downloadStatusLabel(testCycle) }}
            </p>
            <div class="test-countdown" *ngIf="testCycle.schedule?.lockedSlot">
              {{ testSecondsLeft > 0 ? 'Showtime in ' + testCountdownLabel : 'Showtime now' }}
            </div>
          </ng-container>

          <div class="cycle-actions">
            <a mat-raised-button color="primary" href="/date-night">Open the test page</a>
            <button mat-stroked-button [disabled]="preparingTest" (click)="resetDryRun()">
              {{ preparingTest ? 'Preparing five movies…' : 'Start over with a fresh test' }}
            </button>
          </div>

          <details class="recoverable" *ngIf="testCycle.recoverable.length">
            <summary>Recoverable from testing ({{ testCycle.recoverable.length }})</summary>
            <div class="recoverable-row" *ngFor="let r of testCycle.recoverable">
              <span>{{ r.title }} — {{ r.reason }} ({{ r.since | date: 'MMM d' }})</span>
              <button mat-button (click)="restoreMovie(r.movieId)">Restore</button>
            </div>
          </details>

          <details class="live-cycle-status" *ngIf="data.cycle as cycle">
            <summary>Live household status — not part of this test</summary>

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
              No live cycle issued yet.
            </p>

            <table class="cycle-table" *ngIf="cycle.movies.length">
              <thead>
                <tr><th>Movie</th><th>Mom</th><th>Dad</th></tr>
              </thead>
              <tbody>
                <tr *ngFor="let m of cycle.movies" [class.resolved-row]="m.movieId === cycle.resolvedMovieId">
                  <td>{{ m.title }}</td>
                  <td><span class="vote-mark"><mat-icon>{{ voteIconName(m.momVote) }}</mat-icon></span></td>
                  <td><span class="vote-mark"><mat-icon>{{ voteIconName(m.dadVote) }}</mat-icon></span></td>
                </tr>
              </tbody>
            </table>

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
    .vote-mark { display: inline-flex; min-width: 1.4em; vertical-align: middle; }
    .vote-mark mat-icon { width: 18px; height: 18px; font-size: 18px; }
    .dry-run-panel h2 { display: flex; align-items: center; gap: 6px; }
    .warning-text { color: #8a5a00; font-size: 0.9em; }
    .live-cycle-status { margin-top: 18px; padding-top: 12px; border-top: 1px solid rgba(128,128,128,0.24); }
    .live-cycle-status > summary { cursor: pointer; font-weight: 600; }
    .cycle-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; }
    .test-download-state { display: flex; align-items: center; gap: 6px; margin: 8px 0; }
    .test-download-state mat-icon { margin: 0; }
    .test-countdown {
      display: inline-block; margin: 2px 0 10px; padding: 7px 12px;
      border-radius: 6px; background: rgba(0,0,0,.07); font-variant-numeric: tabular-nums;
      font-weight: 600;
    }
    .cycle-list-summary { margin: 8px 0 0; font-size: 0.85em; }
    .pitch-line { margin: 4px 0 0; font-style: italic; font-size: 0.88em; }
    .recoverable { margin-top: 10px; font-size: 0.88em; }
    .recoverable summary { cursor: pointer; }
    .recoverable-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 0; }
    .dry-run-panel { background: rgba(255, 209, 102, 0.08); }
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
  testSecondsLeft = 0;
  preparingTest = false;

  private poll?: Subscription;
  private testCountdownTimer?: ReturnType<typeof setInterval>;

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
          this.syncTestCountdown(data);
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
    if (this.testCountdownTimer) clearInterval(this.testCountdownTimer);
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

  voteIconName(vote?: string): string {
    switch (vote) {
      case 'Up': return 'thumb_up';
      case 'Down': return 'thumb_down';
      case 'Never': return 'block';
      default: return 'remove';
    }
  }

  /** Re-fetches immediately rather than waiting on the 15s pool-status poll. */
  private refreshNow(): void {
    this.api.getPool().subscribe({
      next: data => {
        this.data = data;
        this.syncTestCountdown(data);
      }
    });
  }

  resetDryRun(): void {
    if (this.preparingTest) return;
    this.preparingTest = true;
    this.error = null;
    this.api.resetDryRun().subscribe({
      next: () => {
        this.preparingTest = false;
        this.refreshNow();
      },
      error: () => {
        this.preparingTest = false;
        this.error = 'Could not prepare five movies for the test.';
      }
    });
  }

  restoreMovie(movieId: number): void {
    this.api.restoreMovie(movieId).subscribe({ next: () => this.refreshNow() });
  }

  clearSkip(): void {
    this.api.clearSkip().subscribe({ next: () => this.refreshNow() });
  }

  resetAnnouncement(person: string): void {
    this.api.resetAnnouncement(person).subscribe({
      next: () => this.refreshNow(),
      error: () => { this.error = `Could not reset the announcement for ${person}.`; }
    });
  }

  goLive(): void {
    this.api.goLive().subscribe({ next: () => this.refreshNow() });
  }

  goDark(): void {
    this.api.goDark().subscribe({ next: () => this.refreshNow() });
  }

  formatSlots(slots: ProposedSlot[]): string {
    return slots.map(formatHawaiiSlot).join(', ');
  }

  get testCountdownLabel(): string {
    return formatCountdown(this.testSecondsLeft);
  }

  resolvedTestMovie(cycle: CycleAdminView) {
    return cycle.movies.find(movie => movie.movieId === cycle.resolvedMovieId);
  }

  downloadStatusLabel(cycle: CycleAdminView): string {
    if (this.resolvedTestMovie(cycle)?.hasFile) return 'Downloaded and ready to play';
    switch (cycle.schedule?.downloadStatus) {
      case 'Searching': return 'Searching Radarr for a release…';
      case 'Requested': return 'Sent to Radarr — downloading now';
      case 'Monitoring': return 'Radarr is monitoring it; no acceptable release was available yet';
      case 'Failed': return 'Radarr could not start this download — retry from the test page';
      default: return 'Waiting for Radarr download status';
    }
  }

  private syncTestCountdown(data: DateNightPoolResponse): void {
    if (this.testCountdownTimer) clearInterval(this.testCountdownTimer);
    this.testCountdownTimer = undefined;
    const slot = data.testCycle?.schedule?.status === 'Locked'
      ? data.testCycle.schedule.lockedSlot
      : undefined;
    if (!slot) {
      this.testSecondsLeft = 0;
      return;
    }

    const targetUtc = hawaiiSlotToUtcIso(slot);
    const tick = () => { this.testSecondsLeft = secondsUntil(targetUtc); };
    tick();
    if (this.testSecondsLeft > 0) this.testCountdownTimer = setInterval(tick, 1000);
  }
}

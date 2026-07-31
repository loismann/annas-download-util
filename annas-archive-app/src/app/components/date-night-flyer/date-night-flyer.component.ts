import { Component, Inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { CycleMovieView, CycleView, DateNightApiService, ProposedSlot } from '../../services/date-night-api.service';
import { DateNightScheduleFormComponent } from '../date-night-schedule-form/date-night-schedule-form.component';

export interface DateNightFlyerData {
  cycle: CycleView;
  person: 'Mom' | 'Dad';
}

/**
 * The weekly draw, shown once a day (per DateNightCycleService.IsFlyerOwedToday)
 * until both people have voted on everything. A modal rather than a page — mirrors
 * the announcement's one-time popup pattern, which the user chose over folding the
 * carousel into the /date-night page itself.
 *
 * Reuses the shared theater frame (bulbs/curtains/stage) from
 * src/styles/theater.scss so this, the poster, and the lobby all read as the same
 * marquee rather than three different UIs bolted together.
 */
@Component({
  selector: 'app-date-night-flyer',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatIconModule, DateNightScheduleFormComponent],
  template: `
    <div class="thtr-stage flyer-stage">
      <div class="thtr-bulbs" aria-hidden="true">
        <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
      </div>

      <div class="thtr-inner">
        <button type="button" class="thtr-close" aria-label="Close" [disabled]="saving || scheduleSaving" (click)="close()">
          <mat-icon>close</mat-icon>
        </button>

        <p class="thtr-eyebrow">This Week's Bill</p>
        <h1 class="thtr-title flyer-title">PICK&nbsp;YOUR&nbsp;PICTURES</h1>
        <p class="thtr-tagline">Vote thumbs up on anything you'd watch together.</p>

        <div class="thtr-flyer-track" *ngIf="!allVoted && current as movie">
          <button
            type="button"
            class="thtr-flyer-arrow"
            [disabled]="saving || index === 0"
            (click)="prev()"
            aria-label="Previous"
          ><mat-icon>chevron_left</mat-icon></button>

          <div class="thtr-flyer-card">
            <img
              *ngIf="movie.posterUrl"
              class="thtr-flyer-poster"
              [src]="movie.posterUrl"
              alt=""
              (error)="onPosterError($event)"
            />
            <div *ngIf="!movie.posterUrl" class="thtr-flyer-poster-placeholder" aria-hidden="true"></div>
            <h2 class="thtr-flyer-name">{{ movie.title }}</h2>
            <p class="thtr-flyer-meta">
              <ng-container *ngIf="movie.year || movie.genre; else noMeta">
                <ng-container *ngIf="movie.year">{{ movie.year }}</ng-container>
                <ng-container *ngIf="movie.year && movie.genre"> · </ng-container>
                <ng-container *ngIf="movie.genre">{{ movie.genre }}</ng-container>
              </ng-container>
              <ng-template #noMeta>&nbsp;</ng-template>
            </p>
            <p class="thtr-flyer-pitch">{{ movie.summary || movie.overview || 'A picture worth the popcorn.' }}</p>

            <div class="thtr-flyer-votes">
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Up'"
                [disabled]="saving"
                (click)="vote(movie.movieId, 'Up')"
                aria-label="Thumbs up"
              ><mat-icon>thumb_up</mat-icon></button>
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Down'"
                [disabled]="saving"
                (click)="vote(movie.movieId, 'Down')"
                aria-label="Thumbs down"
              ><mat-icon>thumb_down</mat-icon></button>
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Never'"
                [disabled]="saving"
                (click)="vote(movie.movieId, 'Never')"
                aria-label="Never show again"
              ><mat-icon>block</mat-icon></button>
            </div>
          </div>

          <button
            type="button"
            class="thtr-flyer-arrow"
            [disabled]="saving || index === movies.length - 1"
            (click)="next()"
            aria-label="Next"
          ><mat-icon>chevron_right</mat-icon></button>
        </div>

        <div class="thtr-flyer-dots" *ngIf="!allVoted && movies.length > 1">
          <span *ngFor="let m of movies; let i = index" [class.active]="i === index"></span>
        </div>

        <div class="ballot-schedule" *ngIf="needsInitialProposal">
          <p class="thtr-eyebrow">One More Thing</p>
          <h2 class="thtr-flyer-name">When could movie night work?</h2>
          <p class="thtr-tagline">Offer a few possibilities with your movie ballot.</p>
          <app-date-night-schedule-form
            [cycleId]="data.cycle.cycleId"
            [submitLabel]="scheduleSaving ? 'Saving…' : 'Send votes & times'"
            (submitted)="proposeTimes($event)"
          ></app-date-night-schedule-form>
        </div>

        <p class="vote-error" *ngIf="error">{{ error }}</p>
        <button *ngIf="!needsInitialProposal" class="thtr-btn" [disabled]="saving || scheduleSaving" (click)="close()">
          {{ saving ? 'Saving vote…' : allVoted ? "That's the lot — close" : 'Decide later' }}
        </button>
        <div class="flyer-skip" *ngIf="data.cycle.cycleId !== 'test'">
          <span>Need a rain check?</span>
          <button type="button" class="thtr-link" [disabled]="saving || scheduleSaving" (click)="skip('week')">Skip this week</button>
          <button type="button" class="thtr-link" [disabled]="saving || scheduleSaving" (click)="skip('month')">Skip this month</button>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .flyer-stage { max-width: 480px; margin: 0 auto; container-type: inline-size; }
    .flyer-title.flyer-title {
      max-width: 100%;
      font-size: min(3.4rem, 11cqw);
      letter-spacing: 0;
      white-space: nowrap;
    }
    .thtr-flyer-name { margin: 0 0 6px; font-size: 1.1rem; color: var(--thtr-gilt-bright); }
    .thtr-btn.thtr-btn { margin-top: 16px; }
    .vote-error { color: #ff9b9b; margin: 12px 0 0; }
    .ballot-schedule { margin-top: 16px; }
    .flyer-skip { display: flex; flex-wrap: wrap; justify-content: center; gap: 8px 14px; margin-top: 18px; color: var(--thtr-parchment); font-size: .78rem; }
    @media (max-width: 620px) {
      .flyer-title.flyer-title { font-size: min(2.5rem, 11cqw); }
    }
  `]
})
export class DateNightFlyerComponent implements OnInit {
  movies: CycleMovieView[] = [];
  /** Local copy so a vote reflects immediately without waiting on a re-fetch. */
  myVotes: Record<number, string> = {};
  index = 0;
  saving = false;
  scheduleSaving = false;
  error: string | null = null;

  readonly hBulbs = Array.from({ length: 64 });
  readonly vBulbs = Array.from({ length: 64 });

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: DateNightFlyerData,
    private dialogRef: MatDialogRef<DateNightFlyerComponent>,
    private api: DateNightApiService
  ) {
    this.movies = data.cycle.movies;
    this.myVotes = { ...data.cycle.myVotes };
  }

  ngOnInit(): void {
    // Recorded on open, not on close/all-voted — "once a day" is about the popup
    // appearing, not about finishing the vote.
    this.api.recordFlyerShown().subscribe();
  }

  get current(): CycleMovieView | undefined {
    return this.movies[this.index];
  }

  get allVoted(): boolean {
    return this.movies.every(m => this.myVotes[m.movieId] != null);
  }

  get needsInitialProposal(): boolean {
    return this.allVoted &&
      (!this.data.cycle.schedule || this.data.cycle.schedule.status === 'AwaitingProposal');
  }

  voteFor(movieId: number): string | undefined {
    return this.myVotes[movieId];
  }

  vote(movieId: number, vote: 'Up' | 'Down' | 'Never'): void {
    if (this.saving) return;

    this.saving = true;
    this.error = null;
    this.api.castVote(movieId, vote).subscribe({
      next: () => {
        this.myVotes[movieId] = vote;
        this.saving = false;
        if (this.allVoted &&
            this.data.cycle.schedule?.status === 'AwaitingApproval' &&
            this.data.cycle.schedule.proposedBy !== this.data.person) {
          this.dialogRef.close('respond-to-schedule');
        } else if (this.index < this.movies.length - 1) {
          this.next();
        }
      },
      error: () => {
        this.saving = false;
        this.error = 'That vote did not save. Please try again.';
      }
    });
  }

  prev(): void {
    if (this.index > 0) this.index--;
  }

  next(): void {
    if (this.index < this.movies.length - 1) this.index++;
  }

  close(): void {
    if (this.saving || this.scheduleSaving) return;
    this.dialogRef.close();
  }

  proposeTimes(slots: ProposedSlot[]): void {
    if (this.scheduleSaving) return;
    this.scheduleSaving = true;
    this.error = null;
    this.api.proposeSchedule(slots).subscribe({
      next: () => this.dialogRef.close('proposal-sent'),
      error: () => {
        this.scheduleSaving = false;
        this.error = 'Your votes saved, but those times did not. Please try again.';
      }
    });
  }

  skip(scope: 'week' | 'month'): void {
    if (this.saving || this.scheduleSaving) return;
    this.saving = true;
    this.error = null;
    this.api.setSkip(scope).subscribe({
      next: () => this.dialogRef.close('skipped'),
      error: () => {
        this.saving = false;
        this.error = 'Could not save that rain check. Please try again.';
      }
    });
  }

  onPosterError(event: Event): void {
    (event.target as HTMLImageElement).style.display = 'none';
  }
}

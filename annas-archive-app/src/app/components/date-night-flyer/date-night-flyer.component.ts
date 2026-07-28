import { Component, Inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { CycleMovieView, CycleView, DateNightApiService } from '../../services/date-night-api.service';

export interface DateNightFlyerData {
  cycle: CycleView;
}

/**
 * The weekly draw, shown once a day (per DateNightCycleService.IsFlyerOwedToday)
 * until both people have voted on all three. A modal rather than a page — mirrors
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
  imports: [CommonModule, MatDialogModule],
  template: `
    <div class="thtr-stage flyer-stage">
      <div class="thtr-bulbs" aria-hidden="true">
        <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
      </div>

      <div class="thtr-inner">
        <button type="button" class="thtr-close" aria-label="Close" (click)="close()">✕</button>

        <p class="thtr-eyebrow">This Week's Bill</p>
        <h1 class="thtr-title flyer-title">PICK&nbsp;THREE</h1>
        <p class="thtr-tagline">Vote thumbs up on anything you'd watch together.</p>

        <div class="thtr-flyer-track" *ngIf="current as movie">
          <button
            type="button"
            class="thtr-flyer-arrow"
            [disabled]="index === 0"
            (click)="prev()"
            aria-label="Previous"
          >‹</button>

          <div class="thtr-flyer-card">
            <img
              *ngIf="movie.posterUrl"
              class="thtr-flyer-poster"
              [src]="movie.posterUrl"
              alt=""
              (error)="onPosterError($event)"
            />
            <h2 class="thtr-flyer-name">{{ movie.title }}</h2>
            <p class="thtr-flyer-pitch">{{ movie.summary || movie.overview || 'A picture worth the popcorn.' }}</p>

            <div class="thtr-flyer-votes">
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Up'"
                (click)="vote(movie.movieId, 'Up')"
                aria-label="Thumbs up"
              >👍</button>
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Down'"
                (click)="vote(movie.movieId, 'Down')"
                aria-label="Thumbs down"
              >👎</button>
              <button
                type="button"
                class="thtr-flyer-vote"
                [class.thtr-flyer-vote--active]="voteFor(movie.movieId) === 'Never'"
                (click)="vote(movie.movieId, 'Never')"
                aria-label="Never show again"
              >🚫</button>
            </div>
          </div>

          <button
            type="button"
            class="thtr-flyer-arrow"
            [disabled]="index === movies.length - 1"
            (click)="next()"
            aria-label="Next"
          >›</button>
        </div>

        <div class="thtr-flyer-dots" *ngIf="movies.length > 1">
          <span *ngFor="let m of movies; let i = index" [class.active]="i === index"></span>
        </div>

        <button class="thtr-btn" (click)="close()">
          {{ allVoted ? "That's the lot — close" : 'Decide later' }}
        </button>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .flyer-stage { max-width: 480px; margin: 0 auto; }
    .thtr-flyer-name { margin: 0 0 6px; font-size: 1.1rem; color: var(--thtr-gilt-bright); }
    .thtr-btn.thtr-btn { margin-top: 16px; }
  `]
})
export class DateNightFlyerComponent implements OnInit {
  movies: CycleMovieView[] = [];
  /** Local copy so a vote reflects immediately without waiting on a re-fetch. */
  myVotes: Record<number, string> = {};
  index = 0;

  readonly hBulbs = Array.from({ length: 22 });
  readonly vBulbs = Array.from({ length: 14 });

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

  voteFor(movieId: number): string | undefined {
    return this.myVotes[movieId];
  }

  vote(movieId: number, vote: 'Up' | 'Down' | 'Never'): void {
    this.myVotes[movieId] = vote;
    this.api.castVote(movieId, vote).subscribe();
    if (this.index < this.movies.length - 1) {
      setTimeout(() => this.next(), 250);
    }
  }

  prev(): void {
    if (this.index > 0) this.index--;
  }

  next(): void {
    if (this.index < this.movies.length - 1) this.index++;
  }

  close(): void {
    this.dialogRef.close();
  }

  onPosterError(event: Event): void {
    (event.target as HTMLImageElement).style.display = 'none';
  }
}

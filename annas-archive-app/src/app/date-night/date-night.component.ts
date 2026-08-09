import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { CommonModule } from '@angular/common';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { CycleMovieView, CycleView, DateNightApiService, ProposedSlot } from '../services/date-night-api.service';
import { AuthService } from '../services/auth.service';
import { DateNightImpersonationService } from '../services/date-night-impersonation.service';
import { DateNightPlaybackService } from '../services/date-night-playback.service';
import { DateNightPosterComponent } from '../components/date-night-poster/date-night-poster.component';
import { DateNightFlyerComponent, DateNightFlyerData } from '../components/date-night-flyer/date-night-flyer.component';
import { DateNightScheduleFormComponent } from '../components/date-night-schedule-form/date-night-schedule-form.component';
import {
  DateNightScheduleModalComponent, DateNightScheduleModalData
} from '../components/date-night-schedule-modal/date-night-schedule-modal.component';
import { formatCountdown, formatHawaiiSlot, hawaiiSlotToUtcIso, secondsUntil } from './countdown.util';
import {
  canRetryDownload, canStartMovie, downloadStatusLabel, myVotesComplete, otherPerson,
  otherVotesComplete, resolvedMovie, shouldOpenScheduleModal, showtimePassed
} from './date-night-view';

/**
 * The Date Night page — where Mom and Dad pick movies and settle on a night.
 *
 * Gated by whether the feature is switched on for them (DateNightAnnouncement.
 * live, see DateNightCycleService.IsLive): while off, a real Mom/Dad session
 * sees exactly what it always has — the "coming soon" poster — no matter how
 * much of phases 3-7 is actually built and deployed behind it. The one
 * exception is Paul himself: while he's impersonating (`dryRun`), the lobby
 * renders regardless of `live`, driving a completely separate test cycle —
 * see DateNightImpersonationService and DateNightCycleService's isTest
 * parameter. Once live (or dry-running), it's the real lobby: cycle status,
 * the schedule handshake, playback controls, and the persisted post-show card.
 *
 * The flyer and the schedule modal are both dialogs, not part of this page's
 * own template — each opens once per new thing-to-respond-to (a day of
 * unfinished voting; a proposal or cancellation not yet acknowledged), same
 * "modal now, page always available after" relationship the announcement
 * pioneered. Voting itself stays secret between Mom and Dad while a cycle is
 * Active — this page only ever reports whether the other person is *done*,
 * never what they voted.
 */
@Component({
  selector: 'app-date-night',
  standalone: true,
  imports: [
    CommonModule, MatProgressSpinnerModule, MatButtonModule, MatIconModule,
    DateNightPosterComponent, DateNightScheduleFormComponent
  ],
  template: `
    <div class="date-night-page">
      <div *ngIf="loading" class="loading">
        <mat-spinner diameter="32"></mat-spinner>
      </div>

      <div class="testing-as" *ngIf="!loading && isAdmin">
        <span class="testing-as-label">Testing as</span>
        <button type="button" class="thtr-chip" [class.thtr-chip--active]="!isViewingAs('Mom') && !isViewingAs('Dad')" (click)="setViewAs(null)">Paul</button>
        <button type="button" class="thtr-chip" [class.thtr-chip--active]="isViewingAs('Mom')" (click)="setViewAs('Mom')">Mom</button>
        <button type="button" class="thtr-chip" [class.thtr-chip--active]="isViewingAs('Dad')" (click)="setViewAs('Dad')">Dad</button>
      </div>

      <p class="dry-run-banner" *ngIf="!loading && dryRun && !live">
        <mat-icon>science</mat-icon> Dry run — isolated test cycle, real Radarr actions, nothing Mom/Dad ever see
      </p>

      <app-date-night-poster
        *ngIf="!loading && !live && !dryRun"
        [posters]="posters"
      ></app-date-night-poster>

      <div class="lobby" *ngIf="!loading && (live || dryRun)">
        <h1>Date Night</h1>

        <div class="lobby-card" *ngIf="skipped">
          <p>Skipped for now — check back once the skip ends.</p>
        </div>

        <ng-container *ngIf="!skipped && cycle as c">
          <div class="lobby-card" *ngIf="!c.cycleId">
            <p>Nothing drawn yet — check back Monday.</p>
          </div>

          <ng-container *ngIf="c.status === 'Active'">
            <div class="lobby-card" *ngIf="!myVotesComplete(c)">
              <p>This week's {{ c.movies.length }} picks are up for a vote.</p>
              <button mat-raised-button color="primary" (click)="openFlyer()">See this week's picks</button>
              <p class="deadline" *ngIf="c.deadlineUtc">Vote by {{ c.deadlineUtc | date: 'EEEE, h:mm a' }}.</p>
            </div>
            <div class="lobby-card" *ngIf="myVotesComplete(c) && c.schedule?.status === 'AwaitingProposal'">
              <p><mat-icon>event</mat-icon> Your movie votes are saved. Add the dates and times that could work.</p>
              <app-date-night-schedule-form
                [cycleId]="c.cycleId"
                submitLabel="Send possible times"
                (submitted)="propose($event)"
              ></app-date-night-schedule-form>
            </div>
            <div class="lobby-card" *ngIf="myVotesComplete(c) && c.schedule?.status !== 'AwaitingProposal' && !otherVotesComplete(c)">
              <p><mat-icon>local_movies</mat-icon> Waiting for the other lolo to vote.</p>
              <ng-container *ngIf="c.schedule?.status === 'AwaitingApproval' && c.schedule?.proposedBy === myName">
                <p class="muted">You offered:</p>
                <ul class="slot-list">
                  <li *ngFor="let slot of c.schedule?.proposedSlots">{{ formatSlot(slot) }}</li>
                </ul>
              </ng-container>
              <p class="muted">Votes stay secret until they're in — resolves the instant they finish.</p>
            </div>
            <div class="lobby-card" *ngIf="myVotesComplete(c) && otherVotesComplete(c)">
              <p><mat-icon>movie</mat-icon> Tallying the votes…</p>
            </div>
          </ng-container>

          <div class="lobby-card" *ngIf="c.status === 'Cancelled'">
            <p>This week didn't come together in time — new picks Monday.</p>
          </div>

          <div class="lobby-card" *ngIf="c.status === 'NoMatch'">
            <p>No mutual favorites this week — new picks Monday.</p>
          </div>

          <ng-container *ngIf="c.status === 'Resolved' && c.schedule as s">
            <div class="thtr-stage encore-stage" *ngIf="s.status === 'Concluded'; else currentFeature">
              <div class="thtr-bulbs" aria-hidden="true">
                <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
              </div>
              <div class="thtr-inner">
                <div class="thtr-searchlight thtr-searchlight--left" aria-hidden="true"></div>
                <div class="thtr-searchlight thtr-searchlight--right" aria-hidden="true"></div>
                <div class="thtr-halftone" aria-hidden="true"></div>
                <div class="encore-content">
                  <p class="thtr-eyebrow">The End</p>
                  <h1 class="thtr-title encore-heading">THANKS FOR<br />JOINING US</h1>
                  <p class="encore-for">for</p>
                  <h2 class="encore-movie-title">{{ s.conclusionTitle || c.resolvedTitle || 'this week’s feature' }}</h2>
                  <div class="encore-rule" aria-hidden="true"><span>★</span></div>
                  <p class="encore-copy">Tune in next week for the next feature.</p>
                  <span class="thtr-ticket encore-ticket">Another Date Night Attraction</span>
                </div>
              </div>
            </div>

            <ng-template #currentFeature>
            <div class="thtr-stage winner-stage">
              <div class="thtr-bulbs" aria-hidden="true">
                <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
                <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
              </div>

              <div class="thtr-inner">
                <div class="thtr-searchlight thtr-searchlight--left" aria-hidden="true"></div>
                <div class="thtr-searchlight thtr-searchlight--right" aria-hidden="true"></div>
                <div class="thtr-halftone" aria-hidden="true"></div>

                <div class="winner-content">
                  <header class="winner-masthead">
                    <span class="winner-masthead-rule" aria-hidden="true"></span>
                    <span>Date Night</span>
                    <strong>Feature Presentation</strong>
                    <span class="winner-masthead-rule" aria-hidden="true"></span>
                  </header>

                  <div class="winner-layout">
                    <section class="winner-movie">
                      <p class="thtr-eyebrow">This Week's Atomic Attraction</p>
                      <ng-container *ngIf="resolvedMovie(c) as m">
                        <div class="winner-poster-lockup">
                          <img *ngIf="m.posterUrl" class="winner-poster" [src]="m.posterUrl" alt="" (error)="onPosterError($event)" />
                          <span class="thtr-burst thtr-burst--teal winner-match-burst">A Perfect<br />Match!</span>
                        </div>
                        <div class="winner-movie-copy">
                          <h1 class="thtr-title winner-title">{{ c.resolvedTitle }}</h1>
                          <p class="thtr-flyer-meta winner-meta" *ngIf="m.year || m.genre">
                            <ng-container *ngIf="m.year">{{ m.year }}</ng-container>
                            <ng-container *ngIf="m.year && m.genre"> · </ng-container>
                            <ng-container *ngIf="m.genre">{{ m.genre }}</ng-container>
                          </p>
                          <p class="winner-pitch">{{ m.summary || m.overview }}</p>
                        </div>
                      </ng-container>
                    </section>

                    <section class="winner-details">
                      <span class="thtr-burst thtr-burst--magenta winner-night-burst" aria-hidden="true">One Night<br />Only!</span>
                      <div class="winner-details-inner">
                        <p class="thtr-eyebrow">Your Reserved Showtime</p>
                        <ng-container [ngSwitch]="s.status">
                        <div *ngSwitchCase="'AwaitingProposal'">
                          <p>Pick a time that works and send it over.</p>
                          <app-date-night-schedule-form [cycleId]="c.cycleId" (submitted)="propose($event)"></app-date-night-schedule-form>
                        </div>

                        <div *ngSwitchCase="'AwaitingApproval'">
                          <ng-container *ngIf="s.proposedBy === myName; else myTurn">
                            <p>Waiting on {{ otherPerson() }} to respond to your proposal.</p>
                            <ul class="slot-list">
                              <li *ngFor="let slot of s.proposedSlots">{{ formatSlot(slot) }}</li>
                            </ul>
                            <button type="button" class="thtr-link" (click)="cancel()">Cancel this week</button>
                          </ng-container>
                          <ng-template #myTurn>
                            <p>It's your turn — {{ s.proposedBy }} is waiting on you.</p>
                            <button type="button" class="thtr-btn" (click)="openScheduleModal()">Respond now</button>
                          </ng-template>
                        </div>

                        <div class="locked-details" *ngSwitchCase="'Locked'">
                          <div class="showtime-lockup" *ngIf="s.lockedSlot as locked">
                            <p class="showtime-date">{{ formatShowDate(locked) }}</p>
                            <p class="showtime-time">{{ formatShowTime(locked) }}</p>
                            <p class="showtime-zone">Hawaii Standard Time</p>
                          </div>

                          <div class="winner-countdown-block" *ngIf="s.lockedSlot && !showtimePassed(s.lockedSlot)">
                            <p class="countdown-caption">Show Starts In</p>
                            <div class="thtr-countdown-clock winner-countdown" [class.thtr-countdown-clock--zero]="lockedSecondsLeft <= 0">
                              {{ lockedCountdownLabel }}
                            </div>
                          </div>

                          <p class="download-state" *ngIf="downloadStatusLabel(c) as download">
                            <mat-icon>{{ resolvedMovie(c)?.hasFile ? 'download_done' : 'downloading' }}</mat-icon>
                            {{ download }}
                          </p>
                          <button
                            *ngIf="canRetryDownload(c)"
                            type="button"
                            class="thtr-secondary-btn"
                            [disabled]="retryingDownload"
                            (click)="retryDownload()"
                          >{{ retryingDownload ? 'Retrying…' : 'Retry Radarr download' }}</button>
                          <button
                            *ngIf="canStartMovie(s.lockedSlot) && resolvedMovie(c)?.hasFile && resolvedMovie(c) as playable"
                            type="button"
                            class="thtr-btn"
                            [disabled]="startingMovie"
                            (click)="startMovie(playable)"
                          ><mat-icon>play_arrow</mat-icon> {{ startingMovie ? 'Starting…' : 'Play movie' }}</button>
                          <ng-container *ngIf="s.playbackStartedUtc">
                            <button type="button" class="thtr-btn" (click)="markWatched()">Finished watching</button>
                          </ng-container>
                          <button *ngIf="!s.playbackStartedUtc" type="button" class="winner-cancel" (click)="cancel()">Cancel this date night</button>
                        </div>

                        <div *ngSwitchCase="'Cancelled'">
                          <p>This one's off — nothing scheduled.</p>
                        </div>
                        </ng-container>
                      </div>
                    </section>
                  </div>
                </div>
              </div>
            </div>
            </ng-template>
          </ng-container>
        </ng-container>

        <p *ngIf="error" class="error">{{ error }}</p>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      min-height: 100vh;
      background: #000;
    }

    .date-night-page {
      max-width: 1180px;
      margin: 0 auto;
      padding: 24px 16px 48px;
    }
    .loading { display: flex; justify-content: center; padding: 64px; }

    .lobby { color: #eee; text-align: center; }
    .lobby h1 { font-family: Georgia, serif; margin: 0 0 20px; }
    .testing-as {
      display: flex; gap: 6px; justify-content: center; align-items: center;
      margin: 0 0 16px; font-size: 0.85em; color: #eee;
    }
    .testing-as-label { opacity: 0.7; margin-right: 4px; }
    .dry-run-banner {
      display: flex; align-items: center; justify-content: center; gap: 4px;
      margin: 0 0 16px; text-align: center; color: #ffd166; font-size: 0.85em;
    }
    .dry-run-banner mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .lobby-card {
      margin: 0 auto 16px; padding: 20px; max-width: 480px; border-radius: 8px;
      background: rgba(255,255,255,0.06);
    }
    .lobby-card mat-icon { vertical-align: middle; margin-right: 4px; }
    .deadline, .muted { opacity: 0.7; font-size: 0.9em; }
    .slot-list { list-style: none; padding: 0; margin: 8px 0; text-align: left; display: inline-block; }
    .error { color: #f88; }
    .download-state {
      display: flex; align-items: center; justify-content: center; gap: 6px;
      color: var(--thtr-gilt-bright); margin: 10px 0 4px;
    }
    .download-state mat-icon { margin: 0; }

    /* ── Post-show card ────────────────────────────────────────────────── */
    .encore-stage { max-width: 880px; margin: 0 auto 16px; }
    .encore-stage .thtr-inner {
      min-height: min(640px, calc(100vh - 110px));
      display: grid;
      place-items: center;
      padding: clamp(44px, 8vw, 90px) clamp(28px, 8vw, 82px);
    }
    .encore-content {
      position: relative;
      z-index: 2;
      width: min(100%, 680px);
      text-align: center;
    }
    .encore-heading.encore-heading {
      margin: 8px 0 24px;
      font-size: clamp(3.2rem, 9vw, 6.8rem);
      line-height: .86;
      text-wrap: balance;
    }
    .encore-for.encore-for {
      margin: 0 0 4px;
      color: var(--thtr-parchment);
      font-family: Georgia, 'Times New Roman', serif;
      font-size: 1rem;
      font-style: italic;
    }
    .encore-movie-title {
      margin: 0 auto;
      color: var(--thtr-scream-yellow);
      font-family: Georgia, 'Times New Roman', serif;
      font-size: clamp(1.8rem, 5vw, 3.3rem);
      line-height: 1.08;
      text-wrap: balance;
      text-shadow: 3px 3px 0 var(--thtr-velvet);
    }
    .encore-rule {
      display: grid;
      grid-template-columns: 1fr auto 1fr;
      align-items: center;
      gap: 14px;
      margin: 28px 0 20px;
      color: var(--thtr-gilt-bright);
    }
    .encore-rule::before, .encore-rule::after {
      content: '';
      height: 2px;
      background: linear-gradient(90deg, transparent, var(--thtr-gilt));
    }
    .encore-rule::after { background: linear-gradient(90deg, var(--thtr-gilt), transparent); }
    .encore-copy.encore-copy {
      margin: 0 0 24px;
      color: var(--thtr-cream);
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: clamp(1rem, 2.4vw, 1.35rem);
      font-weight: 700;
      letter-spacing: .08em;
      line-height: 1.4;
      text-transform: uppercase;
    }
    .encore-ticket { max-width: 100%; line-height: 1.4; }

    /* ── Winner reveal ───────────────────────────────────────────────────
       The composition follows the advertising hierarchy of a 1950s one-sheet:
       masthead → dominant key art → oversized title/showtime → small supporting
       copy. The earlier all-centred layout made every element ask for equal
       attention and left the right column floating in unused space. */
    .winner-stage { max-width: 1120px; margin: 0 auto 16px; }
    .winner-stage .thtr-inner {
      min-height: min(760px, calc(100vh - 90px));
      padding: clamp(28px, 4vw, 46px);
    }
    .winner-content { position: relative; z-index: 2; }
    .winner-masthead {
      display: grid;
      grid-template-columns: minmax(24px, 1fr) auto auto minmax(24px, 1fr);
      align-items: center;
      gap: 14px;
      margin: 0 0 clamp(26px, 4vw, 44px);
      color: var(--thtr-gilt);
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: .72rem;
      font-weight: 700;
      letter-spacing: .22em;
      text-transform: uppercase;
    }
    .winner-masthead strong {
      padding: 7px 12px;
      background: var(--thtr-action-red);
      color: var(--thtr-cream);
      font-size: .8rem;
      letter-spacing: .12em;
      transform: rotate(-1deg);
      box-shadow: 4px 4px 0 rgba(0, 0, 0, .4);
    }
    .winner-masthead-rule {
      height: 2px;
      background: linear-gradient(90deg, transparent, var(--thtr-gilt));
    }
    .winner-masthead-rule:last-child {
      background: linear-gradient(90deg, var(--thtr-gilt), transparent);
    }
    .winner-layout {
      display: grid;
      grid-template-columns: minmax(0, 1.02fr) minmax(300px, .98fr);
      gap: clamp(30px, 5vw, 62px);
      align-items: stretch;
      text-align: left;
    }
    .winner-movie, .winner-details { min-width: 0; }
    .winner-movie {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
    }
    .winner-movie > .thtr-eyebrow { margin: 0 0 10px; }
    .winner-poster-lockup {
      position: relative;
      width: min(100%, 390px);
      margin: 0 auto 22px;
      transform: rotate(-.35deg);
    }
    .winner-poster {
      width: 100%;
      height: min(50vh, 500px);
      object-fit: contain;
      border: 4px solid var(--thtr-gilt);
      box-shadow:
        9px 10px 0 rgba(76, 8, 12, .86),
        0 18px 34px rgba(0, 0, 0, .58);
      display: block;
    }
    .winner-match-burst {
      position: absolute;
      left: -28px;
      bottom: 24px;
      z-index: 2;
      width: 96px;
      height: 96px;
      font-size: .78rem;
      transform: rotate(-11deg);
      animation: none;
    }
    .winner-movie-copy {
      width: 100%;
      padding: 0 0 0 18px;
      border-left: 6px solid var(--thtr-shock-red);
    }
    .winner-title.winner-title {
      max-width: 11ch;
      margin: 0 0 10px;
      font-size: clamp(2.25rem, 4.1vw, 3.75rem);
      line-height: .92;
      text-align: left;
      text-transform: uppercase;
      text-wrap: balance;
    }
    .winner-meta.winner-meta {
      margin: 0 0 12px;
      min-height: 0;
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: .72rem;
      font-weight: 700;
      letter-spacing: .15em;
    }
    .winner-pitch {
      max-width: 48ch;
      margin: 0;
      color: var(--thtr-cream);
      font-family: Georgia, 'Times New Roman', serif;
      font-size: clamp(.9rem, 1.25vw, 1.05rem);
      font-style: italic;
      line-height: 1.5;
    }
    .winner-details {
      position: relative;
      align-self: center;
      padding: 9px;
      border: 2px solid var(--thtr-gilt);
      background:
        linear-gradient(var(--thtr-shock-red), var(--thtr-shock-red)) top / 100% 8px no-repeat,
        rgba(5, 6, 18, .72);
      box-shadow:
        inset 0 0 0 4px var(--thtr-house),
        inset 0 0 0 6px rgba(217, 164, 65, .6),
        10px 12px 0 rgba(0, 0, 0, .24);
    }
    .winner-details-inner {
      min-height: 430px;
      padding: clamp(38px, 5vw, 56px) clamp(18px, 3vw, 34px) 28px;
      display: flex;
      flex-direction: column;
      justify-content: center;
      text-align: center;
    }
    .winner-details-inner > .thtr-eyebrow {
      margin: 0 0 20px;
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: .76rem;
      font-weight: 700;
      letter-spacing: .25em;
    }
    .winner-night-burst {
      position: absolute;
      z-index: 3;
      top: -40px;
      right: -24px;
      width: 92px;
      height: 92px;
      font-size: .72rem;
      transform: rotate(9deg);
      animation: none;
    }
    .locked-details { display: flex; flex-direction: column; align-items: center; gap: 14px; }
    .locked-details > * { margin-top: 0; margin-bottom: 0; }
    .showtime-lockup {
      width: 100%;
      padding: 0 0 18px;
      border-bottom: 1px solid rgba(217, 164, 65, .65);
    }
    .showtime-date.showtime-date {
      margin: 0 0 5px;
      color: var(--thtr-cream);
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: clamp(.9rem, 1.6vw, 1.12rem);
      font-weight: 700;
      letter-spacing: .1em;
      line-height: 1.25;
      text-transform: uppercase;
    }
    .showtime-time.showtime-time {
      margin: 0;
      color: var(--thtr-gilt-bright);
      font-family: Impact, Haettenschweiler, 'Arial Narrow Bold', sans-serif;
      font-size: clamp(3.7rem, 7vw, 6.2rem);
      letter-spacing: .025em;
      line-height: .94;
      text-shadow:
        .045em .045em 0 var(--thtr-velvet),
        .09em .09em 0 rgba(0, 0, 0, .55);
      white-space: nowrap;
    }
    .showtime-zone.showtime-zone {
      margin: 8px 0 0;
      color: var(--thtr-parchment);
      font-size: .68rem;
      letter-spacing: .18em;
      text-transform: uppercase;
    }
    .winner-countdown-block {
      width: 100%;
      padding: 15px 0 11px;
      border-bottom: 1px solid rgba(217, 164, 65, .65);
    }
    .countdown-caption.countdown-caption {
      margin: 0 0 1px;
      color: var(--thtr-neon);
      font-family: Futura, 'Century Gothic', 'Avenir Next', Arial, sans-serif;
      font-size: .72rem;
      font-weight: 800;
      letter-spacing: .24em;
      text-transform: uppercase;
    }
    .winner-countdown {
      margin: 0;
      font-size: clamp(3.8rem, 7.5vw, 6.5rem);
      line-height: .94;
      white-space: nowrap;
    }
    .winner-cancel {
      padding: 4px;
      border: 0;
      background: transparent;
      color: var(--thtr-parchment);
      font-family: Georgia, 'Times New Roman', serif;
      font-size: .84rem;
      text-decoration: underline;
      opacity: .7;
      cursor: pointer;
    }
    .winner-cancel:hover { color: var(--thtr-gilt-bright); opacity: 1; }

    /* A regular iPad is 768–834 CSS pixels wide in portrait. Keep the locked
       showtime in its intended two-column composition there; just spend less
       space on gutters/type so the poster and controls remain comfortable. */
    @media (min-width: 701px) and (max-width: 900px) {
      .date-night-page { padding-left: 10px; padding-right: 10px; }
      .winner-stage .thtr-inner { padding: 28px 22px; min-height: 0; }
      .winner-masthead { gap: 8px; margin-bottom: 28px; font-size: .62rem; }
      .winner-masthead strong { padding: 6px 8px; font-size: .66rem; }
      .winner-layout {
        grid-template-columns: minmax(0, 1.02fr) minmax(270px, .98fr);
        gap: 22px;
      }
      .winner-poster-lockup { width: min(100%, 290px); margin-bottom: 18px; }
      .winner-poster { height: min(39vh, 390px); }
      .winner-match-burst { left: -14px; bottom: 14px; width: 74px; height: 74px; font-size: .61rem; }
      .winner-movie-copy { padding-left: 12px; border-left-width: 4px; }
      .winner-title.winner-title { font-size: clamp(1.75rem, 4.4vw, 2.55rem); }
      .winner-pitch { font-size: .86rem; line-height: 1.4; }
      .winner-details-inner { min-height: 390px; padding: 36px 14px 22px; }
      .winner-night-burst { top: -31px; right: -15px; width: 72px; height: 72px; font-size: .57rem; }
      .showtime-date.showtime-date { font-size: .8rem; }
      .showtime-time.showtime-time { font-size: clamp(3.15rem, 7.6vw, 4.25rem); }
      .winner-countdown { font-size: clamp(3.2rem, 8vw, 4.5rem); }
      .download-state { font-size: .82rem; }
    }

    /* Phones still stack: below this width, two useful columns would make both
       the poster and the showtime controls too narrow to read or tap. */
    @media (max-width: 700px) {
      .date-night-page { max-width: 580px; }
      .winner-stage { max-width: 520px; }
      .winner-stage .thtr-inner { min-height: 0; padding: 28px 18px; }
      .winner-masthead {
        grid-template-columns: 1fr auto 1fr;
        gap: 8px;
        margin-bottom: 30px;
      }
      .winner-masthead > span:not(.winner-masthead-rule) { display: none; }
      .winner-layout { grid-template-columns: 1fr; gap: 24px; }
      .winner-poster { height: min(52vh, 440px); }
      .winner-title.winner-title { max-width: none; }
      .winner-details { margin-top: 18px; }
      .winner-details-inner { min-height: 0; }
      .winner-night-burst { right: -13px; }
      .encore-stage .thtr-inner { min-height: 520px; padding: 56px 24px; }
      .encore-heading.encore-heading { font-size: clamp(2.8rem, 14vw, 4.4rem); }
      .encore-ticket { font-size: .68rem; letter-spacing: .1em; }
    }
  `]
})
export class DateNightComponent implements OnInit, OnDestroy {
  posters: string[] = [];
  loading = true;
  live = false;
  skipped = false;
  cycle: CycleView | null = null;
  error: string | null = null;
  retryingDownload = false;
  startingMovie = false;

  readonly hBulbs = Array.from({ length: 64 });
  readonly vBulbs = Array.from({ length: 64 });

  /** Ticks once a second while the schedule is Locked and showtime hasn't
   * passed — a persistent on-page countdown, distinct from
   * DateNightCountdownComponent's popup which only appears in the last 10
   * minutes (see AppComponent's showtime poll). Both share countdown.util's
   * tick/format logic. */
  lockedSecondsLeft = 0;
  private countdownTimer?: ReturnType<typeof setInterval>;
  private lockedStatusPollTimer?: ReturnType<typeof setInterval>;

  /** The real logged-in identity. See `myName` for the effective identity,
   * which an admin's "Testing as" selection overrides. */
  realName: 'Paul' | 'Mom' | 'Dad' | null = null;

  constructor(
    private api: DateNightApiService,
    private auth: AuthService,
    private impersonation: DateNightImpersonationService,
    private playback: DateNightPlaybackService,
    private dialog: MatDialog
  ) {}

  /** Effective identity for this page: an admin's "Testing as" selection, if
   * any, otherwise the real logged-in identity. Mom and Dad's real sessions
   * never set an impersonation, so this is always their own name for them. */
  get myName(): 'Paul' | 'Mom' | 'Dad' | null {
    return this.impersonation.current() ?? this.realName;
  }

  get isAdmin(): boolean {
    return this.auth.isAdmin();
  }

  /** True exactly when admin impersonation is active — see
   * DateNightImpersonationService and the interceptor that attaches
   * X-Date-Night-As. The backend routes every action for this session at the
   * completely separate test cycle whenever this is true, so the lobby can
   * safely render regardless of the real `live` gate: Mom and Dad are
   * governed by `live` alone and never see dry-run state. */
  get dryRun(): boolean {
    return this.isAdmin && (this.isViewingAs('Mom') || this.isViewingAs('Dad'));
  }

  isViewingAs(person: 'Mom' | 'Dad'): boolean {
    return this.impersonation.current() === person;
  }

  setViewAs(person: 'Mom' | 'Dad' | null): void {
    this.impersonation.set(person);
    this.loadCycle();
  }

  ngOnInit(): void {
    this.realName = this.auth.getOwnerName();

    // preview=true asks for the poster's contents / the live flag without
    // consuming this person's one-time showing of the announcement dialog —
    // that dialog is triggered separately, app-wide, from AppComponent.
    this.api.getAnnouncement(true).pipe(takeUntil(this.destroy$)).subscribe({
      next: a => {
        this.posters = a.posters;
        this.live = a.live;
        this.loading = false;
        // dryRun is always false here — impersonation starts unset each page
        // load — but keeping the check makes the rule ("load whenever the
        // lobby can render") explicit rather than relying on that timing.
        if (this.live || this.dryRun) this.loadCycle();
      },
      error: () => { this.loading = false; }
    });
  }

  /**
   * Ends in-flight *reads* when the component goes.
   *
   * Their response handlers restart polling and countdown timers, so a read
   * that resolved after destroy used to start a timer on a dead component that
   * nothing would ever clear. Writes are deliberately NOT routed through this:
   * unsubscribing an HttpClient call aborts the request, which would mean
   * navigating away cancelled the user's action.
   */
  private readonly destroy$ = new Subject<void>();

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    if (this.countdownTimer) clearInterval(this.countdownTimer);
    if (this.lockedStatusPollTimer) clearInterval(this.lockedStatusPollTimer);
  }

  private loadCycle(): void {
    this.api.getCycle().pipe(takeUntil(this.destroy$)).subscribe({
      next: c => {
        this.cycle = c;
        this.skipped = c.skipped;
        this.syncLockedCountdown(c);
        this.syncLockedStatusPoll(c);
        if (c.shouldShowFlyerToday) {
          this.openFlyer();
          return;
        }
        this.maybeOpenScheduleModal(c);
      },
      error: () => { this.error = 'Could not load this week’s picks.'; }
    });
  }

  /** Starts/stops the on-page Locked countdown to match the current cycle —
   * running only while there's a locked showtime still ahead, so it isn't
   * silently ticking in the background once watched/cancelled/off this page. */
  private syncLockedCountdown(c: CycleView): void {
    if (this.countdownTimer) clearInterval(this.countdownTimer);
    this.countdownTimer = undefined;

    const slot = c.schedule?.status === 'Locked' ? c.schedule.lockedSlot : undefined;
    if (!slot || this.showtimePassed(slot)) return;

    const showtimeUtc = hawaiiSlotToUtcIso(slot);
    const tick = () => { this.lockedSecondsLeft = secondsUntil(showtimeUtc); };
    tick();
    this.countdownTimer = setInterval(tick, 1000);
  }

  /** A Radarr grab can finish after the lock-in request returns. Keep the winner
   * card current so "requested" becomes "downloaded and ready" without reload. */
  private syncLockedStatusPoll(c: CycleView): void {
    if (this.lockedStatusPollTimer) clearInterval(this.lockedStatusPollTimer);
    this.lockedStatusPollTimer = undefined;
    if (c.schedule?.status !== 'Locked') return;

    this.lockedStatusPollTimer = setInterval(() => {
      this.api.getCycle().pipe(takeUntil(this.destroy$)).subscribe({
        next: latest => {
          this.cycle = latest;
          if (latest.schedule?.status !== 'Locked') {
            this.syncLockedCountdown(latest);
            this.syncLockedStatusPoll(latest);
          }
        }
      });
    }, 15000);
  }

  get lockedCountdownLabel(): string {
    return formatCountdown(this.lockedSecondsLeft);
  }

  /** The full drawn-movie record (poster/summary/year/genre) behind this
   * week's resolved pick — CycleView.movies already carries every drawn
   * movie's metadata, so no separate backend lookup is needed. */
  resolvedMovie(c: CycleView): CycleMovieView | undefined {
    return resolvedMovie(c);
  }

  onPosterError(event: Event): void {
    (event.target as HTMLImageElement).style.display = 'none';
  }

  /** Auto-opens a daily "your turn" reminder for an unanswered proposal, or
   * the one-time "called off" notice for the person who didn't cancel. */
  private maybeOpenScheduleModal(c: CycleView): void {
    if (shouldOpenScheduleModal(c, this.myName)) this.openScheduleModal();
  }

  /** Whether every one of this week's movies has my vote recorded. */
  myVotesComplete(c: CycleView): boolean {
    return myVotesComplete(c);
  }

  /** Whether the other person has voted on everything too — never exposes
   *  *what* they voted, only whether they've finished. */
  otherVotesComplete(c: CycleView): boolean {
    return otherVotesComplete(c, this.myName);
  }

  openFlyer(): void {
    if (!this.cycle || (this.myName !== 'Mom' && this.myName !== 'Dad')) return;
    this.dialog.open<DateNightFlyerComponent, DateNightFlyerData>(DateNightFlyerComponent, {
      data: { cycle: this.cycle, person: this.myName },
      panelClass: 'thtr-dialog-panel'
    }).afterClosed().subscribe(() => this.loadCycle());
  }

  openScheduleModal(): void {
    if (!this.cycle?.schedule) return;
    const s = this.cycle.schedule;
    const otherPersonLabel = (s.status === 'Cancelled' ? s.cancelledBy : s.proposedBy) ?? this.otherPerson();
    this.dialog.open<DateNightScheduleModalComponent, DateNightScheduleModalData>(DateNightScheduleModalComponent, {
      data: { cycle: this.cycle, otherPersonLabel },
      panelClass: 'thtr-dialog-panel'
    }).afterClosed().subscribe(() => this.loadCycle());
  }

  otherPerson(): string {
    return otherPerson(this.myName);
  }

  formatSlot(slot: ProposedSlot): string {
    return formatHawaiiSlot(slot);
  }

  formatShowDate(slot: ProposedSlot): string {
    return new Date(hawaiiSlotToUtcIso(slot)).toLocaleDateString(undefined, {
      timeZone: 'Pacific/Honolulu',
      weekday: 'long',
      month: 'long',
      day: 'numeric'
    });
  }

  formatShowTime(slot: ProposedSlot): string {
    return new Date(hawaiiSlotToUtcIso(slot)).toLocaleTimeString(undefined, {
      timeZone: 'Pacific/Honolulu',
      hour: 'numeric',
      minute: '2-digit'
    });
  }

  showtimePassed(slot?: ProposedSlot): boolean {
    return showtimePassed(slot, Date.now());
  }

  canStartMovie(slot?: ProposedSlot): boolean {
    return canStartMovie(slot, Date.now());
  }

  startMovie(movie: CycleMovieView): void {
    if (this.startingMovie) return;
    this.startingMovie = true;
    this.error = null;
    this.playback.play(movie.title, movie.tmdbId).subscribe({
      next: () => {
        this.startingMovie = false;
        this.loadCycle();
      },
      error: () => {
        this.startingMovie = false;
        this.error = 'The movie is not ready in Jellyfin yet. Try Play again in a moment.';
      }
    });
  }

  downloadStatusLabel(c: CycleView): string | null {
    return downloadStatusLabel(c);
  }

  canRetryDownload(c: CycleView): boolean {
    return canRetryDownload(c);
  }

  retryDownload(): void {
    if (this.retryingDownload) return;
    this.retryingDownload = true;
    this.api.retryDownload().subscribe({
      next: () => {
        this.retryingDownload = false;
        this.loadCycle();
      },
      error: () => {
        this.retryingDownload = false;
        this.error = 'Could not retry the Radarr download.';
      }
    });
  }

  propose(slots: ProposedSlot[]): void {
    this.api.proposeSchedule(slots).subscribe({
      next: () => this.loadCycle(),
      error: () => { this.error = 'Could not send that proposal.'; }
    });
  }

  cancel(): void {
    this.api.cancelSchedule().subscribe({
      next: () => this.loadCycle(),
      error: () => { this.error = 'Could not cancel.'; }
    });
  }

  markWatched(): void {
    this.api.markWatched().subscribe({
      next: () => this.loadCycle(),
      error: () => { this.error = 'Could not mark that watched.'; }
    });
  }
}

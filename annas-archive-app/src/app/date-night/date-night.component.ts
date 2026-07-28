import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatRadioModule } from '@angular/material/radio';
import { MatDialog } from '@angular/material/dialog';
import { CycleView, DateNightApiService, ProposedSlot } from '../services/date-night-api.service';
import { AuthService } from '../services/auth.service';
import { DateNightPosterComponent } from '../components/date-night-poster/date-night-poster.component';
import { DateNightFlyerComponent, DateNightFlyerData } from '../components/date-night-flyer/date-night-flyer.component';

/** One selectable day in the propose-a-time form. */
interface DayOption {
  date: string; // "yyyy-MM-dd"
  label: string; // "Fri, Aug 1"
  checked: boolean;
}

/**
 * The Date Night page — where Mom and Dad pick movies and settle on a night.
 *
 * Gated entirely by whether the feature is switched on for them
 * (DateNightAnnouncement.live, see DateNightCycleService.IsLive): while off,
 * this renders exactly what it always has — the "coming soon" poster — no
 * matter how much of phases 3-7 is actually built and deployed behind it.
 * Once live, it becomes the real lobby: cycle status, the schedule handshake,
 * and the "Mark as watched" control once showtime has passed.
 *
 * The flyer itself is a modal (DateNightFlyerComponent), not part of this
 * page's own template — opened here once per day when the backend says one is
 * owed, same trigger shape as the announcement.
 */
@Component({
  selector: 'app-date-night',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatProgressSpinnerModule, MatButtonModule,
    MatCheckboxModule, MatRadioModule, DateNightPosterComponent
  ],
  template: `
    <div class="date-night-page">
      <div *ngIf="loading" class="loading">
        <mat-spinner diameter="32"></mat-spinner>
      </div>

      <app-date-night-poster
        *ngIf="!loading && !live"
        [posters]="posters"
      ></app-date-night-poster>

      <div class="lobby" *ngIf="!loading && live">
        <h1>Date Night</h1>

        <div class="lobby-card" *ngIf="skipped">
          <p>Skipped for now — check back once the skip ends.</p>
        </div>

        <ng-container *ngIf="!skipped && cycle as c">
          <div class="lobby-card" *ngIf="!c.cycleId">
            <p>Nothing drawn yet — check back Monday.</p>
          </div>

          <div class="lobby-card" *ngIf="c.status === 'Active'">
            <p>This week's three are up for a vote.</p>
            <button mat-raised-button color="primary" (click)="openFlyer()">See this week's picks</button>
            <p class="deadline" *ngIf="c.deadlineUtc">Vote by {{ c.deadlineUtc | date: 'EEEE, h:mm a' }}.</p>
          </div>

          <div class="lobby-card" *ngIf="c.status === 'Cancelled'">
            <p>This week didn't come together in time — new picks Monday.</p>
          </div>

          <div class="lobby-card" *ngIf="c.status === 'NoMatch'">
            <p>No mutual favorites this week — new picks Monday.</p>
          </div>

          <ng-container *ngIf="c.status === 'Resolved' && c.schedule as s">
            <div class="lobby-card">
              <p class="picked-title">🎬 {{ c.resolvedTitle }}</p>

              <ng-container [ngSwitch]="s.status">
                <div *ngSwitchCase="'AwaitingProposal'">
                  <p>Pick a time that works and send it over.</p>
                  <div class="day-options">
                    <mat-checkbox *ngFor="let d of dayOptions" [(ngModel)]="d.checked">{{ d.label }}</mat-checkbox>
                  </div>
                  <label class="time-row">
                    Time
                    <input type="time" step="1800" [(ngModel)]="proposedTime" />
                  </label>
                  <button mat-raised-button color="primary" [disabled]="!canPropose()" (click)="propose()">
                    Send proposal
                  </button>
                </div>

                <div *ngSwitchCase="'AwaitingApproval'">
                  <ng-container *ngIf="s.proposedBy === myName; else approverView">
                    <p>Waiting on {{ otherPerson() }} to approve one of your proposed times.</p>
                    <ul class="slot-list">
                      <li *ngFor="let slot of s.proposedSlots">{{ formatSlot(slot) }}</li>
                    </ul>
                  </ng-container>
                  <ng-template #approverView>
                    <p>{{ s.proposedBy }} proposed these — pick one:</p>
                    <mat-radio-group [(ngModel)]="selectedSlotIndex">
                      <div class="slot-list" *ngFor="let slot of s.proposedSlots; let i = index">
                        <mat-radio-button [value]="i">{{ formatSlot(slot) }}</mat-radio-button>
                      </div>
                    </mat-radio-group>
                    <div class="btn-row">
                      <button mat-raised-button color="primary" [disabled]="selectedSlotIndex === null" (click)="approve(s.proposedSlots)">
                        Approve
                      </button>
                      <button mat-stroked-button (click)="cancel()">Cancel this week</button>
                    </div>
                  </ng-template>
                </div>

                <div *ngSwitchCase="'Locked'">
                  <p *ngIf="s.lockedSlot">{{ formatSlot(s.lockedSlot) }} — it's on!</p>
                  <p class="muted" *ngIf="!showtimePassed(s.lockedSlot)">
                    We'll count you down when it's close.
                  </p>
                  <ng-container *ngIf="showtimePassed(s.lockedSlot) && !justWatched">
                    <button mat-raised-button color="primary" (click)="markWatched()">Mark as watched</button>
                  </ng-container>
                  <p class="muted" *ngIf="justWatched">Enjoy? It's back in the vault for next time.</p>
                  <button mat-stroked-button (click)="cancel()">Cancel this date night</button>
                </div>

                <div *ngSwitchCase="'Cancelled'">
                  <p>This one's off — nothing scheduled.</p>
                </div>
              </ng-container>
            </div>
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
      max-width: 760px;
      margin: 0 auto;
      padding: 24px 16px 48px;
    }
    .loading { display: flex; justify-content: center; padding: 64px; }

    .lobby { color: #eee; text-align: center; }
    .lobby h1 { font-family: Georgia, serif; margin: 0 0 20px; }
    .lobby-card {
      margin: 0 auto 16px; padding: 20px; max-width: 480px; border-radius: 8px;
      background: rgba(255,255,255,0.06);
    }
    .picked-title { font-size: 1.3rem; margin: 0 0 12px; }
    .deadline, .muted { opacity: 0.7; font-size: 0.9em; }
    .day-options { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; margin: 10px 0; }
    .time-row { display: flex; align-items: center; justify-content: center; gap: 8px; margin: 10px 0; }
    .slot-list { list-style: none; padding: 0; margin: 8px 0; text-align: left; display: inline-block; }
    .btn-row { display: flex; gap: 8px; justify-content: center; margin-top: 10px; }
    .error { color: #f88; }
  `]
})
export class DateNightComponent implements OnInit {
  posters: string[] = [];
  loading = true;
  live = false;
  skipped = false;
  cycle: CycleView | null = null;
  error: string | null = null;
  justWatched = false;

  dayOptions: DayOption[] = [];
  proposedTime = '19:00';
  selectedSlotIndex: number | null = null;

  myName: 'Paul' | 'Mom' | 'Dad' | null = null;

  constructor(
    private api: DateNightApiService,
    private auth: AuthService,
    private dialog: MatDialog
  ) {}

  ngOnInit(): void {
    this.myName = this.auth.getOwnerName();
    this.buildDayOptions();

    // preview=true asks for the poster's contents / the live flag without
    // consuming this person's one-time showing of the announcement dialog —
    // that dialog is triggered separately, app-wide, from AppComponent.
    this.api.getAnnouncement(true).subscribe({
      next: a => {
        this.posters = a.posters;
        this.live = a.live;
        this.loading = false;
        if (this.live) this.loadCycle();
      },
      error: () => { this.loading = false; }
    });
  }

  private loadCycle(): void {
    this.api.getCycle().subscribe({
      next: c => {
        this.cycle = c;
        this.skipped = c.skipped;
        if (c.shouldShowFlyerToday) this.openFlyer();
      },
      error: () => { this.error = 'Could not load this week’s picks.'; }
    });
  }

  private buildDayOptions(): void {
    const today = new Date();
    this.dayOptions = Array.from({ length: 7 }, (_, i) => {
      const d = new Date(today);
      d.setDate(d.getDate() + i);
      const iso = d.toISOString().slice(0, 10);
      const label = d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
      return { date: iso, label, checked: false };
    });
  }

  openFlyer(): void {
    if (!this.cycle) return;
    this.dialog.open<DateNightFlyerComponent, DateNightFlyerData>(DateNightFlyerComponent, {
      data: { cycle: this.cycle },
      panelClass: 'thtr-dialog-panel'
    }).afterClosed().subscribe(() => this.loadCycle());
  }

  otherPerson(): string {
    return this.myName === 'Mom' ? 'Dad' : 'Mom';
  }

  formatSlot(slot: ProposedSlot): string {
    const dt = new Date(`${slot.date}T${slot.time}:00`);
    return dt.toLocaleString(undefined, { weekday: 'long', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  }

  showtimePassed(slot?: ProposedSlot): boolean {
    if (!slot) return false;
    return new Date(`${slot.date}T${slot.time}:00`).getTime() < Date.now();
  }

  canPropose(): boolean {
    return this.dayOptions.some(d => d.checked) && !!this.proposedTime;
  }

  propose(): void {
    const slots: ProposedSlot[] = this.dayOptions
      .filter(d => d.checked)
      .map(d => ({ date: d.date, time: this.proposedTime }));

    this.api.proposeSchedule(slots).subscribe({
      next: () => this.loadCycle(),
      error: () => { this.error = 'Could not send that proposal.'; }
    });
  }

  approve(slots: ProposedSlot[]): void {
    if (this.selectedSlotIndex === null) return;
    this.api.approveSchedule(slots[this.selectedSlotIndex]).subscribe({
      next: () => { this.selectedSlotIndex = null; this.loadCycle(); },
      error: () => { this.error = 'Could not lock that in.'; }
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
      next: () => { this.justWatched = true; },
      error: () => { this.error = 'Could not mark that watched.'; }
    });
  }
}

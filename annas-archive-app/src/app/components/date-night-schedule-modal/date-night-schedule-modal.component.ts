import { Component, Inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { CycleView, DateNightApiService, ProposedSlot } from '../../services/date-night-api.service';
import { DateNightScheduleFormComponent } from '../date-night-schedule-form/date-night-schedule-form.component';
import { formatHawaiiSlot, hawaiiSlotToUtcIso } from '../../date-night/countdown.util';

export interface DateNightScheduleModalData {
  cycle: CycleView;
  /** Whoever proposed/cancelled, by name, so the copy can say it directly. */
  otherPersonLabel: string;
}

/**
 * The "your turn" / "called off" popup — surfaces a schedule proposal or
 * cancellation the other person made, the same "modal now, page always
 * available after" relationship the weekly flyer has to the lobby. Reuses the
 * flyer's theater-frame shell for visual continuity.
 *
 * Acknowledging happens on open (mirrors DateNightFlyerComponent recording
 * "shown" on open, not on close) — the point is "did they see this state,"
 * not "did they act on it." If they close without acting, the backend offers
 * another gentle reminder on the next Hawaii calendar day; changing the
 * proposal makes the new state eligible immediately.
 */
@Component({
  selector: 'app-date-night-schedule-modal',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatIconModule, DateNightScheduleFormComponent],
  template: `
    <div class="thtr-stage sched-modal-stage">
      <div class="thtr-bulbs" aria-hidden="true">
        <span class="thtr-bulbs-edge thtr-bulbs-edge--top"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--right"><i *ngFor="let b of vBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom"><i *ngFor="let b of hBulbs"></i></span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--left"><i *ngFor="let b of vBulbs"></i></span>
      </div>

      <div class="thtr-inner">
        <button type="button" class="thtr-close" aria-label="Close" [disabled]="acting" (click)="close()">
          <mat-icon>close</mat-icon>
        </button>

        <ng-container *ngIf="isCancelled; else respond">
          <p class="thtr-eyebrow">Rain Check</p>
          <h1 class="thtr-title sched-modal-title">CALLED&nbsp;OFF</h1>
          <p class="thtr-tagline">{{ data.otherPersonLabel }} cancelled this week's date night.</p>
          <button class="thtr-btn" (click)="close()">Aw, Okay</button>
        </ng-container>

        <ng-template #respond>
          <ng-container *ngIf="!showCounterForm; else counterForm">
            <p class="thtr-eyebrow">Your Turn</p>
            <h1 class="thtr-title sched-modal-title">NAME&nbsp;THE&nbsp;HOUR</h1>
            <p class="thtr-tagline">{{ data.otherPersonLabel }} proposed these — pick one, counter, or call it off.</p>

            <div class="slot-list">
              <label *ngFor="let slot of schedule.proposedSlots; let i = index" class="sched-slot-option" [class.sched-slot-option--past]="slotPassed(slot)">
                <input type="radio" name="slot" [value]="i" [(ngModel)]="selectedSlotIndex" [disabled]="acting || slotPassed(slot)" />
                {{ formatSlot(slot) }} <span *ngIf="slotPassed(slot)">— passed</span>
              </label>
            </div>

            <p *ngIf="error" class="error">{{ error }}</p>

            <div class="sched-modal-actions">
              <button class="thtr-btn" [disabled]="acting || selectedSlotIndex === null" (click)="approve()">
                {{ acting ? 'Locking it in…' : 'Approve' }}
              </button>
              <button type="button" class="thtr-link" [disabled]="acting" (click)="showCounterForm = true">Propose a different night</button>
              <button type="button" class="thtr-link" [disabled]="acting" (click)="cancel()">Cancel this week</button>
            </div>
          </ng-container>

          <ng-template #counterForm>
            <p class="thtr-eyebrow">Counter-Offer</p>
            <h1 class="thtr-title sched-modal-title">NAME&nbsp;THE&nbsp;HOUR</h1>
            <app-date-night-schedule-form
              [multiple]="false"
              [cycleId]="data.cycle.cycleId"
              [showBack]="true"
              submitLabel="Send counter-proposal"
              (back)="showCounterForm = false"
              (submitted)="proposeDifferent($event)"
            ></app-date-night-schedule-form>
            <p *ngIf="error" class="error">{{ error }}</p>
          </ng-template>
        </ng-template>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .sched-modal-stage { max-width: 480px; margin: 0 auto; }
    .sched-modal-title.sched-modal-title { font-size: 3.4rem; }
    .sched-slot-option {
      display: block; margin: 8px 0; padding: 10px 14px; border-radius: 6px;
      border: 1px solid var(--thtr-gilt); text-align: left; cursor: pointer;
    }
    .sched-slot-option input { margin-right: 8px; }
    .sched-slot-option--past { opacity: .52; cursor: not-allowed; }
    .sched-modal-actions { display: flex; flex-direction: column; align-items: center; gap: 10px; margin-top: 14px; }
    .error { color: #f88; margin: 10px 0 0; }
  `]
})
export class DateNightScheduleModalComponent implements OnInit {
  showCounterForm = false;
  selectedSlotIndex: number | null = null;
  error: string | null = null;
  acting = false;

  readonly hBulbs = Array.from({ length: 64 });
  readonly vBulbs = Array.from({ length: 64 });

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: DateNightScheduleModalData,
    private dialogRef: MatDialogRef<DateNightScheduleModalComponent>,
    private api: DateNightApiService
  ) {}

  ngOnInit(): void {
    this.api.acknowledgeSchedule().subscribe();
  }

  get schedule() {
    return this.data.cycle.schedule!;
  }

  get isCancelled(): boolean {
    return this.schedule.status === 'Cancelled';
  }

  formatSlot(slot: ProposedSlot): string {
    return formatHawaiiSlot(slot);
  }

  slotPassed(slot: ProposedSlot): boolean {
    return new Date(hawaiiSlotToUtcIso(slot)).getTime() <= Date.now();
  }

  approve(): void {
    if (this.acting || this.selectedSlotIndex === null) return;
    this.acting = true;
    this.error = null;
    this.api.approveSchedule(this.schedule.proposedSlots[this.selectedSlotIndex]).subscribe({
      next: () => this.dialogRef.close('acted'),
      error: () => {
        this.acting = false;
        this.error = 'Could not lock that in. Please try again.';
      }
    });
  }

  proposeDifferent(slots: ProposedSlot[]): void {
    if (this.acting) return;
    this.acting = true;
    this.error = null;
    this.api.proposeSchedule(slots).subscribe({
      next: () => this.dialogRef.close('acted'),
      error: () => {
        this.acting = false;
        this.error = 'Could not send that proposal.';
      }
    });
  }

  cancel(): void {
    if (this.acting) return;
    this.acting = true;
    this.error = null;
    this.api.cancelSchedule().subscribe({
      next: () => this.dialogRef.close('acted'),
      error: () => {
        this.acting = false;
        this.error = 'Could not cancel.';
      }
    });
  }

  close(): void {
    if (this.acting) return;
    this.dialogRef.close();
  }
}

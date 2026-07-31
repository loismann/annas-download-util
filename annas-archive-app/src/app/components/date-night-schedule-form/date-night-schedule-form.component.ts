import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ProposedSlot } from '../../services/date-night-api.service';

interface DayOption {
  date: string; // "yyyy-MM-dd"
  weekday: string; // "FRI"
  dayNum: number; // 1
}

/** One 30-minute mark on the noon–11:30pm grid. */
interface TimeOption {
  time: string; // "HH:mm"
  label: string; // "7:00 PM"
}

/**
 * The day/time picker used in two modes. The initial proposer can select up to
 * every displayed date and multiple times, producing every date/time combination. A
 * counter-proposer selects one explicit replacement slot. The responder always
 * confirms exactly one offered slot in DateNightScheduleModalComponent.
 */
@Component({
  selector: 'app-date-night-schedule-form',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="sched-form">
      <p class="sched-form-label">{{ multiple ? 'Pick any dates that could work' : 'Pick a date' }}</p>
      <div class="sched-days">
        <button
          type="button"
          *ngFor="let d of dayOptions"
          class="thtr-day-cell"
          [class.thtr-day-cell--active]="selectedDates.has(d.date)"
          [attr.aria-pressed]="selectedDates.has(d.date)"
          (click)="selectDay(d)"
        >
          <span class="thtr-day-cell-weekday">{{ d.weekday }}</span>
          <span class="thtr-day-cell-num">{{ d.dayNum }}</span>
        </button>
      </div>

      <p class="sched-form-label">{{ multiple ? 'Pick any times that could work' : 'Pick a time' }}</p>
      <div class="sched-times">
        <button
          type="button"
          *ngFor="let t of timeOptions"
          class="thtr-chip thtr-chip--time"
          [class.thtr-chip--active]="selectedTimes.has(t.time)"
          [attr.aria-pressed]="selectedTimes.has(t.time)"
          [disabled]="isTimeDisabled(t.time)"
          (click)="selectTime(t.time)"
        >{{ t.label }}</button>
      </div>

      <div class="sched-form-actions">
        <button *ngIf="showBack" type="button" class="thtr-link" (click)="back.emit()">‹ Back</button>
        <button type="button" class="thtr-btn" [disabled]="!canSubmit()" (click)="submit()">
          {{ submitLabel }}
        </button>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .sched-form-label { margin: 0 0 8px; font-size: .85rem; opacity: .8; }
    .sched-days, .sched-times { display: flex; flex-wrap: wrap; gap: 8px; margin: 0 0 18px; justify-content: center; }
    .sched-form-actions { display: flex; align-items: center; justify-content: center; gap: 16px; margin-top: 6px; }
  `]
})
export class DateNightScheduleFormComponent {
  /** Initial proposals offer combinations; counter-proposals are one slot. */
  @Input() multiple = true;
  /** Monday's yyyy-MM-dd cycle id. A real cycle is limited to its remaining
   * Monday–Sunday dates; the dry-run id (`test`) keeps a rolling seven days. */
  @Input() set cycleId(value: string | null | undefined) {
    this.dayOptions = DateNightScheduleFormComponent.buildHawaiiDayOptions(value);
    this.selectedDates = new Set(
      [...this.selectedDates].filter(date => this.dayOptions.some(day => day.date === date))
    );
  }
  /** Shows a "‹ Back" link — used when this is embedded in the schedule modal's
   *  "propose a different night" view, not when it's the lobby's only option. */
  @Input() showBack = false;
  @Input() submitLabel = 'Send proposal';

  @Output() submitted = new EventEmitter<ProposedSlot[]>();
  @Output() back = new EventEmitter<void>();

  selectedDates = new Set<string>();
  selectedTimes = new Set<string>();

  dayOptions: DayOption[] = DateNightScheduleFormComponent.buildHawaiiDayOptions();

  timeOptions: TimeOption[] = Array.from({ length: 24 }, (_, i) => {
    const totalMinutes = 12 * 60 + i * 30; // noon through 11:30pm
    const hour24 = Math.floor(totalMinutes / 60);
    const minute = totalMinutes % 60;
    const hour12 = hour24 % 12 === 0 ? 12 : hour24 % 12;
    const time = `${String(hour24).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
    const label = `${hour12}:${String(minute).padStart(2, '0')} PM`;
    return { time, label };
  });

  private static buildHawaiiDayOptions(cycleId?: string | null): DayOption[] {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone: 'Pacific/Honolulu',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit'
    }).formatToParts(new Date());
    const value = (type: Intl.DateTimeFormatPartTypes) =>
      Number(parts.find(p => p.type === type)?.value);
    const today = `${String(value('year')).padStart(4, '0')}-${String(value('month')).padStart(2, '0')}-${String(value('day')).padStart(2, '0')}`;
    const realCycle = /^\d{4}-\d{2}-\d{2}$/.test(cycleId ?? '');
    const weekStart = realCycle ? cycleId! : today;
    const startDate = weekStart > today ? weekStart : today;
    const base = new Date(`${startDate}T12:00:00Z`);
    const count = realCycle
      ? Math.max(0, 7 - Math.round((base.getTime() - new Date(`${weekStart}T12:00:00Z`).getTime()) / 86_400_000))
      : 7;

    return Array.from({ length: count }, (_, i) => {
      const d = new Date(base);
      d.setUTCDate(d.getUTCDate() + i);
      return {
        date: d.toISOString().slice(0, 10),
        weekday: d.toLocaleDateString(undefined, { weekday: 'short', timeZone: 'UTC' }).toUpperCase(),
        dayNum: d.getUTCDate()
      };
    });
  }

  private static currentHawaiiDateTime(): { date: string; time: string } {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone: 'Pacific/Honolulu',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23'
    }).formatToParts(new Date());
    const value = (type: Intl.DateTimeFormatPartTypes) =>
      parts.find(p => p.type === type)?.value ?? '';
    return {
      date: `${value('year')}-${value('month')}-${value('day')}`,
      time: `${value('hour')}:${value('minute')}`
    };
  }

  selectDay(day: DayOption): void {
    if (this.multiple) {
      if (this.selectedDates.has(day.date)) {
        this.selectedDates.delete(day.date);
      } else {
        this.selectedDates.add(day.date);
      }
    } else {
      this.selectedDates = this.selectedDates.has(day.date)
        ? new Set<string>()
        : new Set([day.date]);
    }

    this.selectedTimes = new Set(
      [...this.selectedTimes].filter(time => !this.isTimeDisabled(time))
    );
  }

  selectTime(time: string): void {
    if (this.isTimeDisabled(time)) return;
    if (this.multiple) {
      if (this.selectedTimes.has(time)) this.selectedTimes.delete(time);
      else this.selectedTimes.add(time);
    } else {
      this.selectedTimes = this.selectedTimes.has(time)
        ? new Set<string>()
        : new Set([time]);
    }
  }

  isTimeDisabled(time: string): boolean {
    if (this.selectedDates.size === 0) return false;
    const now = DateNightScheduleFormComponent.currentHawaiiDateTime();
    return [...this.selectedDates].every(date =>
      date < now.date || (date === now.date && time <= now.time)
    );
  }

  canSubmit(): boolean {
    return this.selectedDates.size > 0 && this.selectedTimes.size > 0;
  }

  submit(): void {
    if (!this.canSubmit()) return;

    const slots = [...this.selectedDates].flatMap(date =>
      [...this.selectedTimes]
        .filter(time => {
          const now = DateNightScheduleFormComponent.currentHawaiiDateTime();
          return date > now.date || (date === now.date && time > now.time);
        })
        .map(time => ({ date, time }))
    );
    if (slots.length > 0) this.submitted.emit(slots);
  }
}

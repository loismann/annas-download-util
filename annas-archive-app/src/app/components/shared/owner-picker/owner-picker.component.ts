import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HOUSEHOLD_OWNERS } from '../../../constants/owners';

/**
 * Paul/Mom/Dad pill toggles used by every media edit dialog. Multi-select by
 * default (media); set [multi]="false" for single-owner flows (books' owner
 * tag). Emits the full selection on every change — the parent owns
 * persistence and any tag-name mapping (e.g. books' "Paul's Books").
 */
@Component({
  selector: 'app-owner-picker',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="section-label" *ngIf="label">{{ label }}</div>
    <div class="owner-toggles">
      <button
        type="button"
        *ngFor="let opt of displayOptions"
        class="owner-toggle"
        [class.active]="selected.includes(opt.value)"
        (click)="toggle(opt.value)"
      >
        {{ opt.label }}
      </button>
    </div>
  `,
  styles: [`
    .section-label {
      font-size: 0.8rem;
      color: #64748b;
      margin-bottom: 6px;
    }
    .owner-toggles {
      display: flex;
      gap: 8px;
      margin-bottom: 20px;
    }
    .owner-toggle {
      border: 1px solid #cbd5f5;
      background: #ffffff;
      color: #3f51b5;
      padding: 6px 16px;
      border-radius: 999px;
      font-size: 0.85rem;
      cursor: pointer;
      transition: all 0.15s ease;
    }
    .owner-toggle.active {
      background: #3f51b5;
      color: #ffffff;
      border-color: #3f51b5;
    }
  `]
})
export class OwnerPickerComponent {
  @Input() owners: readonly string[] = HOUSEHOLD_OWNERS;
  /** Optional label/value pairs for flows where the stored value differs from the
   * display name — e.g. books select the tag "Dad's Books" but show "Dad's". */
  @Input() options: ReadonlyArray<{ value: string; label: string }> | null = null;
  @Input() selected: string[] = [];
  @Input() label = 'Owners';
  @Input() multi = true;
  @Output() selectedChange = new EventEmitter<string[]>();

  get displayOptions(): ReadonlyArray<{ value: string; label: string }> {
    return this.options ?? this.owners.map(o => ({ value: o, label: o }));
  }

  toggle(value: string): void {
    if (this.selected.includes(value)) {
      this.selected = this.selected.filter(o => o !== value);
    } else {
      this.selected = this.multi ? [...this.selected, value] : [value];
    }
    this.selectedChange.emit(this.selected);
  }
}

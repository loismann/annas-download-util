import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { Lens } from '../reader2.models';

/**
 * How this book is being read.
 *
 * <p><b>Renders whatever `GET /lenses` returns, and never a hard-coded list.</b>
 * That is the frontend half of the extensibility guarantee: a fourth book type
 * is one class and one DI registration on the server, and it appears here with
 * no change to this file. A test drives it with a lens the server does not have.
 * </p>
 */
@Component({
  selector: 'app-reader2-lens-picker',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="lenses" role="radiogroup" aria-label="Book type">
      <button
        *ngFor="let lens of lenses"
        type="button"
        role="radio"
        class="lens"
        [class.selected]="lens.key === selectedKey"
        [attr.aria-checked]="lens.key === selectedKey"
        [title]="lens.description"
        [disabled]="disabled"
        (click)="choose.emit(lens.key)">
        <mat-icon>{{ lens.icon }}</mat-icon>
        <span class="name">{{ lens.displayName }}</span>
      </button>
    </div>
  `,
  styleUrl: './lens-picker.component.scss'
})
export class LensPickerComponent {
  @Input() lenses: Lens[] = [];
  @Input() selectedKey: string | null = null;
  @Input() disabled = false;

  @Output() choose = new EventEmitter<string>();
}

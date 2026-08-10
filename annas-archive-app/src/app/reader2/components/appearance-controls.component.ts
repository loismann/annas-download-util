import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { DEFAULT_PREFERENCES, ReadingPreferences } from '../reader2.models';

type FontFamily = ReadingPreferences['fontFamily'];
type Theme = ReadingPreferences['theme'];

/**
 * Font, size, and theme.
 *
 * <p>Emits a whole {@link ReadingPreferences} rather than a field at a time, so
 * the shell has one thing to persist and cannot save a partial set. The values
 * are the reader's and live on the server — Reader I kept them in
 * <c>localStorage</c>, which made them per-browser, so the same person got a
 * different reader on their phone and two people sharing a machine overwrote
 * each other.</p>
 */
@Component({
  selector: 'app-reader2-appearance-controls',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="appearance" role="group" aria-label="Appearance">
      <div class="row">
        <span class="label">Type</span>
        <button
          *ngFor="let font of fonts"
          type="button"
          class="choice"
          [class.selected]="preferences.fontFamily === font"
          [attr.aria-pressed]="preferences.fontFamily === font"
          [style.font-family]="sample[font]"
          (click)="emit({ fontFamily: font })">
          Aa
        </button>
      </div>

      <div class="row">
        <span class="label">Size</span>
        <button
          type="button"
          class="choice"
          [disabled]="preferences.fontSize <= min"
          aria-label="Smaller text"
          (click)="emit({ fontSize: preferences.fontSize - 1 })">
          <mat-icon>text_decrease</mat-icon>
        </button>
        <span class="size" aria-live="polite">{{ preferences.fontSize }}px</span>
        <button
          type="button"
          class="choice"
          [disabled]="preferences.fontSize >= max"
          aria-label="Larger text"
          (click)="emit({ fontSize: preferences.fontSize + 1 })">
          <mat-icon>text_increase</mat-icon>
        </button>
      </div>

      <div class="row">
        <span class="label">Theme</span>
        <button
          *ngFor="let theme of themes"
          type="button"
          class="choice theme"
          [class.selected]="preferences.theme === theme"
          [attr.data-theme]="theme"
          [attr.aria-pressed]="preferences.theme === theme"
          [title]="theme"
          (click)="emit({ theme: theme })">
        </button>
      </div>
    </div>
  `,
  styleUrl: './appearance-controls.component.scss'
})
export class AppearanceControlsComponent {
  @Input() preferences: ReadingPreferences = DEFAULT_PREFERENCES;

  @Output() change = new EventEmitter<ReadingPreferences>();

  /**
   * The bounds the server enforces, repeated here so the control disables rather
   * than sending a value that comes back a 400. The server is still the
   * authority; this only keeps the reader from being told off for pressing a
   * button that was offered to them.
   */
  protected readonly min = 8;
  protected readonly max = 48;

  protected readonly fonts: FontFamily[] = ['serif', 'sans', 'mono'];
  protected readonly themes: Theme[] = ['light', 'sepia', 'dark'];

  protected readonly sample: Record<FontFamily, string> = {
    serif: 'Georgia, serif',
    sans: 'Inter, Arial, sans-serif',
    mono: 'Menlo, monospace'
  };

  protected emit(change: Partial<ReadingPreferences>): void {
    this.change.emit({ ...this.preferences, ...change });
  }
}

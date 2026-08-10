import {
  ChangeDetectionStrategy, Component, ElementRef, EventEmitter, HostListener, Input, Output, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';

/** The narrowest either pane may become, as a fraction of the container. */
export const MIN_SPLIT = 0.2;
export const MAX_SPLIT = 0.8;

/**
 * Where a pointer at <paramref name="clientX"/> puts the split.
 *
 * <p>Exported and pure so the arithmetic — which is where an off-by-one puts a
 * pane at zero width and traps the reader — is testable without a browser. The
 * component below only supplies the numbers.</p>
 */
export function ratioFrom(
  clientX: number, left: number, width: number,
  min = MIN_SPLIT, max = MAX_SPLIT
): number {
  if (width <= 0) return min;

  const ratio = (clientX - left) / width;
  return Math.min(Math.max(ratio, min), max);
}

/**
 * The draggable divider between the text and the analysis pane.
 *
 * <p>Emits continuously while dragging so the panes follow the pointer, and
 * once more on release — the shell persists only the second. Saving on every
 * pointer move would be a write per frame for a value the reader is still in the
 * middle of choosing.</p>
 *
 * <p>Also focusable and operable with the arrow keys. A split that can only be
 * moved by dragging is one a keyboard user cannot move at all.</p>
 */
@Component({
  selector: 'app-reader2-split-handle',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="handle"
      role="separator"
      tabindex="0"
      aria-orientation="vertical"
      aria-label="Resize the reading pane"
      [attr.aria-valuenow]="percent"
      aria-valuemin="20"
      aria-valuemax="80"
      [class.dragging]="dragging"
      (pointerdown)="start($event)"
      (keydown.arrowleft)="nudge(-STEP, $event)"
      (keydown.arrowright)="nudge(STEP, $event)">
    </div>
  `,
  styleUrl: './split-handle.component.scss'
})
export class SplitHandleComponent {
  private readonly host = inject(ElementRef<HTMLElement>);

  @Input() ratio = 0.6;

  /** While dragging. The shell applies it but does not save it. */
  @Output() ratioChange = new EventEmitter<number>();

  /** On release, or on a key press. This is the one worth persisting. */
  @Output() commit = new EventEmitter<number>();

  protected dragging = false;
  protected readonly STEP = 0.02;

  protected get percent(): number {
    return Math.round(this.ratio * 100);
  }

  protected start(event: PointerEvent): void {
    this.dragging = true;
    (event.target as HTMLElement).setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  @HostListener('document:pointermove', ['$event'])
  protected move(event: PointerEvent): void {
    if (!this.dragging) return;

    const next = this.fromPointer(event.clientX);
    if (next !== null) this.ratioChange.emit(next);
  }

  @HostListener('document:pointerup', ['$event'])
  protected end(event: PointerEvent): void {
    if (!this.dragging) return;

    this.dragging = false;

    const next = this.fromPointer(event.clientX);
    this.commit.emit(next ?? this.ratio);
  }

  protected nudge(by: number, event: Event): void {
    event.preventDefault();
    this.commit.emit(Math.min(Math.max(this.ratio + by, MIN_SPLIT), MAX_SPLIT));
  }

  /**
   * Measured against the parent element, which is the grid the split divides.
   * Null when there is no parent to measure, so a handle rendered outside one
   * does nothing rather than snapping the panes to a minimum.
   */
  private fromPointer(clientX: number): number | null {
    const container = this.host.nativeElement.parentElement;
    if (!container) return null;

    const bounds = container.getBoundingClientRect();
    return ratioFrom(clientX, bounds.left, bounds.width);
  }
}

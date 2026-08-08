/** The horizontal bounds of the element the panes live in. */
export interface SplitBounds {
  left: number;
  width: number;
}

/**
 * The draggable divider between the reading pane and the analysis pane.
 *
 * A plain class rather than an `@Injectable`, because every reader instance
 * needs its own divider position — the other services in this folder are
 * `providedIn: 'root'` and would share one, which is the wrong lifetime for
 * something a drag mutates.
 *
 * It was two identical ten-line blocks in the component, one for mouse and one
 * for touch, differing only in where the x coordinate came from. Splitting them
 * apart is also what made the clamp untestable: the panes may not go past 20/80
 * either way, and neither branch could be exercised without a real pointer over
 * a real laid-out element.
 */
export class ReaderSplitter {
  /** Narrowest either pane may become, as a percentage of the container. */
  static readonly MinPanePercent = 20;

  leftFlex = '1 1 0';
  rightFlex = '1 1 0';

  private dragging = false;

  get isDragging(): boolean {
    return this.dragging;
  }

  /** Called on mousedown/touchstart on the divider. */
  start(event: Event): void {
    this.dragging = true;
    // Without this the browser starts a text selection or a scroll, and the
    // drag reads as a gesture on the page rather than on the divider.
    event.preventDefault();
  }

  /**
   * Moves the divider. Returns whether the panes actually moved, which is the
   * caller's cue to repaginate — page size is measured from the pane's width,
   * so it is stale the moment this returns true.
   */
  dragTo(clientX: number, bounds: SplitBounds | null | undefined): boolean {
    if (!this.dragging || !bounds || bounds.width <= 0) return false;

    const percent = this.clamp(((clientX - bounds.left) / bounds.width) * 100);
    const left = percent / 100;

    const leftFlex = `${left} 1 0`;
    const rightFlex = `${1 - left} 1 0`;
    if (leftFlex === this.leftFlex && rightFlex === this.rightFlex) return false;

    this.leftFlex = leftFlex;
    this.rightFlex = rightFlex;
    return true;
  }

  /**
   * Called on mouseup/touchend, wherever they land. Returns whether a drag was
   * in progress, so a stray click on the page does not trigger a repagination.
   */
  end(): boolean {
    const wasDragging = this.dragging;
    this.dragging = false;
    return wasDragging;
  }

  private clamp(percent: number): number {
    const min = ReaderSplitter.MinPanePercent;
    return Math.min(100 - min, Math.max(min, percent));
  }
}

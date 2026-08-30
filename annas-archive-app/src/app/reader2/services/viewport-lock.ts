import { Injectable, OnDestroy, effect, inject } from '@angular/core';
import { AppChromeService } from '../../services/app-chrome.service';

/** Marks the document as pinned. The rules are in `styles.scss`. */
const LOCKED = 'reader-locked';

/**
 * Holds the page still under a reading finger.
 *
 * <p>Engaged whenever the app chrome has been reduced — which is to say
 * whenever the reader has the screen, on a tablet or in fullscreen — and does
 * nothing at all the rest of the time.</p>
 *
 * <h3>What it is for</h3>
 *
 * <p>The reader is a column of boxes that scroll independently. Reach the end of
 * one and keep swiping and the browser passes the rest of the gesture <b>up</b>:
 * to the page, and then to Safari, which slides its address bar in and shifts
 * everything under the reader's finger. Nothing is broken about the pane. The
 * gesture simply does not stop where it should.</p>
 *
 * <p>Three attempts at this failed in three different ways, and the lesson is
 * that on a tablet the polite mechanisms are not reliable. So this is the blunt
 * one, in two halves:</p>
 *
 * <p><b>The document cannot move.</b> `reader-locked` pins `body` with
 * `position: fixed`, which is the only technique that holds on every version of
 * iOS rather than the ones new enough for `overscroll-behavior`. A page that has no
 * scroll position has nothing to give a gesture, so there is nothing for the
 * browser to do with one.</p>
 *
 * <p><b>The panes keep their gestures.</b> A touch that starts inside a scroller
 * and asks for more than the scroller has left is cancelled outright, rather
 * than being allowed to chain outwards.</p>
 *
 * <h3>The restraint that matters</h3>
 *
 * <p><b>Only gestures that began inside a scroller are ever cancelled.</b> A
 * touch starting on the page of text is left completely alone, because that is
 * where a reader long-presses and drags to select a passage, and a guard that
 * cancelled those would break the feature it sits next to in order to fix a
 * scrolling complaint. It can afford the restraint precisely because of the
 * other half: over a non-scrolling area, a pinned document means the gesture had
 * nowhere to go anyway.</p>
 */
@Injectable()
export class ViewportLock implements OnDestroy {
  private readonly chrome = inject(AppChromeService);

  /** The scroller this gesture began in, and where it began. Null means the
   *  gesture is none of our business — see the class note. */
  private pane: HTMLElement | null = null;
  private startY = 0;
  private engaged = false;

  constructor() {
    effect(() => (this.chrome.showsToolbar() ? this.release() : this.engage()));
  }

  ngOnDestroy(): void {
    this.release();
  }

  private engage(): void {
    if (this.engaged) return;

    document.documentElement.classList.add(LOCKED);

    // `passive: false` on the move, or `preventDefault` is ignored and half of
    // this is decoration — Chrome and Safari both default touch listeners on the
    // document to passive. The start listener genuinely is passive: it only reads.
    document.addEventListener('touchstart', this.onStart, { passive: true });
    document.addEventListener('touchmove', this.onMove, { passive: false });
    this.engaged = true;
  }

  private release(): void {
    if (!this.engaged) return;

    document.documentElement.classList.remove(LOCKED);
    document.removeEventListener('touchstart', this.onStart);
    document.removeEventListener('touchmove', this.onMove);
    this.pane = null;
    this.engaged = false;
  }

  private readonly onStart = (event: TouchEvent): void => {
    const touch = event.touches[0];

    this.pane = touch ? this.scrollerAround(touch.target as Element | null) : null;
    this.startY = touch?.clientY ?? 0;
  };

  private readonly onMove = (event: TouchEvent): void => {
    const pane = this.pane;

    // Two fingers is a pinch, which is the reader's to zoom with. A
    // non-cancelable move is one the browser has already committed to — iOS
    // sends those during momentum, and calling preventDefault only logs a
    // warning.
    if (!pane || event.touches.length !== 1 || !event.cancelable) return;

    // Positive: the finger is travelling down the screen, which scrolls the pane
    // *up*, which needs room above it.
    const travel = event.touches[0].clientY - this.startY;
    if (travel === 0) return;

    const room = travel > 0
      ? pane.scrollTop
      : pane.scrollHeight - pane.clientHeight - pane.scrollTop;

    // Half a pixel, not zero: scroll positions are fractional on a retina
    // display, and a pane resting at its end reports something like 0.5 short.
    if (room > 0.5) return;

    event.preventDefault();
  };

  /**
   * The nearest box that both can scroll and is allowed to — an element only
   * takes a gesture if its content overflows *and* its overflow is auto or
   * scroll. `hidden` overflows too, and never scrolls; the reader is full of
   * those.
   */
  private scrollerAround(from: Element | null): HTMLElement | null {
    for (let element: Element | null = from; element; element = element.parentElement) {
      if (!(element instanceof HTMLElement)) continue;
      if (element.scrollHeight <= element.clientHeight) continue;

      const overflow = getComputedStyle(element).overflowY;
      if (overflow === 'auto' || overflow === 'scroll') return element;
    }

    return null;
  }
}

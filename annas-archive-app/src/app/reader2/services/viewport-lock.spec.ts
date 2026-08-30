import { TestBed } from '@angular/core/testing';
import { ViewportLock } from './viewport-lock';
import { AppChromeService } from '../../services/app-chrome.service';

/**
 * The complaint, exactly: a summary pane that scrolls perfectly well is swiped
 * downwards while already at the top, the browser passes the gesture up to
 * Safari, and the reader loses fullscreen to an address bar.
 *
 * <p>Real elements in the real document, because every decision this service
 * makes is a layout question — `scrollHeight` against `clientHeight`, the
 * computed `overflow-y`, how much room is left above the scroll position. A
 * mocked DOM would answer whatever the test told it to and prove nothing.</p>
 */
describe('ViewportLock', () => {
  let chrome: AppChromeService;
  let pane: HTMLElement;
  let page: HTMLElement;

  /** A box with more content than height, that is allowed to scroll. */
  function scroller(): HTMLElement {
    const box = document.createElement('div');
    box.style.cssText = 'height: 50px; overflow-y: auto;';
    box.innerHTML = '<div style="height: 500px"></div>';
    document.body.appendChild(box);
    return box;
  }

  /** The page of text: taller than its box and deliberately not scrollable, the
   *  way the reading surface is. */
  function clipped(): HTMLElement {
    const box = document.createElement('div');
    box.style.cssText = 'height: 50px; overflow: hidden;';
    box.innerHTML = '<div style="height: 500px"></div>';
    document.body.appendChild(box);
    return box;
  }

  function touch(target: EventTarget, clientY: number): Touch {
    return new Touch({ identifier: 1, target, clientY, clientX: 0 });
  }

  /** Returns whether the browser was told to keep its hands off the gesture. */
  function swipe(target: HTMLElement, from: number, to: number): boolean {
    target.dispatchEvent(new TouchEvent('touchstart', {
      bubbles: true, cancelable: true, touches: [touch(target, from)]
    }));

    const move = new TouchEvent('touchmove', {
      bubbles: true, cancelable: true, touches: [touch(target, to)]
    });
    target.dispatchEvent(move);

    return move.defaultPrevented;
  }

  /** Down the screen scrolls a pane upwards, and needs room above it. */
  const swipeDown = (target: HTMLElement): boolean => swipe(target, 100, 160);
  const swipeUp = (target: HTMLElement): boolean => swipe(target, 160, 100);

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ViewportLock] });

    chrome = TestBed.inject(AppChromeService);
    TestBed.inject(ViewportLock);

    pane = scroller();
    page = clipped();
  });

  afterEach(() => {
    pane.remove();
    page.remove();
    TestBed.resetTestingModule();
    chrome.reset();
  });

  /** The effect that engages the guard runs on the first flush, not on
   *  construction. */
  function readerTakesTheScreen(): void {
    chrome.setLevel('rail');
    TestBed.flushEffects();
  }

  it('does nothing at all while the app still has its toolbar', () => {
    expect(swipeDown(pane)).toBeFalse();
    expect(document.documentElement.classList).not.toContain('reader-locked');
  });

  /** The half that does not depend on any gesture arriving: a page pinned by
   *  `position: fixed` has no scroll position to hand a browser, so there is
   *  nothing for one to interpret. */
  it('pins the document while the reader has the screen, and lets it go after', () => {
    readerTakesTheScreen();
    expect(document.documentElement.classList).toContain('reader-locked');

    chrome.setLevel('full');
    TestBed.flushEffects();
    expect(document.documentElement.classList).not.toContain('reader-locked');
  });

  /** The reported bug, as a test: at the top, with the finger going down, there
   *  is nowhere for the pane to go and the gesture must stop here rather than
   *  reaching Safari. */
  it('stops a swipe the pane has no room for', () => {
    readerTakesTheScreen();

    expect(swipeDown(pane)).toBeTrue();
  });

  it('leaves a swipe the pane can use alone', () => {
    readerTakesTheScreen();
    pane.scrollTop = 100;

    expect(swipeDown(pane)).toBeFalse();
  });

  it('stops a swipe past the bottom too', () => {
    readerTakesTheScreen();
    pane.scrollTop = pane.scrollHeight;

    expect(swipeUp(pane)).toBeTrue();
  });

  /**
   * The restraint that matters most. A touch beginning on the page of text is
   * how a reader selects a passage, and cancelling those would break *Explain
   * this passage* in order to fix a scrolling complaint. The reading surface
   * overflows its box and is `overflow: hidden`, so it is not a scroller and is
   * none of this service's business.
   */
  it('never interferes with a gesture that began outside a scroller', () => {
    readerTakesTheScreen();

    expect(swipeDown(page)).toBeFalse();
    expect(swipeUp(page)).toBeFalse();
  });

  it('lets go the moment fullscreen ends', () => {
    readerTakesTheScreen();
    chrome.setLevel('full');
    TestBed.flushEffects();

    expect(swipeDown(pane)).toBeFalse();
  });

  it('lets go when the reader is destroyed', () => {
    readerTakesTheScreen();
    TestBed.resetTestingModule();

    expect(swipeDown(pane)).toBeFalse();
  });
});

import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ReaderChrome } from './reader-chrome';
import { ReaderStore } from './reader-store';
import { ViewportLock } from './viewport-lock';
import { AppChromeService } from '../../services/app-chrome.service';
import { DEFAULT_PREFERENCES } from '../reader2.models';

/**
 * Fullscreen here is two things that are allowed to fail apart: the app chrome
 * getting out of the way, which always works, and the browser handing over the
 * display, which may be refused. These specs are mostly about that seam — a
 * refusal must still leave the reader with the whole page, because "still shows
 * the blue toolbar" is the complaint this feature exists to answer.
 */
describe('ReaderChrome', () => {
  let chrome: ReaderChrome;
  let app: AppChromeService;
  let root: HTMLElement;

  /** Whatever the runner's browser really does with fullscreen is not the
   *  subject of any test here, and asking for it without a user gesture is
   *  refused anyway — so the request is always a spy. */
  function browserAllowsFullscreen(allowed: boolean): void {
    spyOn(root, 'requestFullscreen').and.returnValue(
      allowed ? Promise.resolve() : Promise.reject(new Error('refused')));
  }

  /** The browser leaving fullscreen on its own — Esc, or a native exit control. */
  function browserLeavesFullscreen(): void {
    document.dispatchEvent(new Event('fullscreenchange'));
  }

  beforeEach(() => {
    root = document.documentElement;

    TestBed.configureTestingModule({
      providers: [
        ReaderChrome,
        // Real, and provided the way the shell provides it: it is inert until
        // immersive mode engages it, and it has a spec of its own.
        ViewportLock,
        { provide: ReaderStore, useValue: { preferences: signal(DEFAULT_PREFERENCES) } }
      ]
    });

    app = TestBed.inject(AppChromeService);
    chrome = TestBed.inject(ReaderChrome);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    app.reset();
  });

  it('takes the app chrome away and gives it back', () => {
    browserAllowsFullscreen(true);

    chrome.toggleFullscreen();
    expect(chrome.immersive()).toBeTrue();

    chrome.toggleFullscreen();
    expect(chrome.immersive()).toBeFalse();
  });

  it('asks the browser for the display, once, on the way in', () => {
    browserAllowsFullscreen(true);

    chrome.toggleFullscreen();

    expect(root.requestFullscreen).toHaveBeenCalledTimes(1);
  });

  /**
   * The iPad case, and the reason immersive mode is not derived from
   * `document.fullscreenElement`. A refused request is not a failure the reader
   * should see: filling the page is every pixel this app can give, and it is
   * strictly more than they had before pressing the button.
   */
  it('still fills the page when the browser refuses the display', async () => {
    browserAllowsFullscreen(false);

    chrome.toggleFullscreen();
    await Promise.resolve();

    expect(chrome.immersive()).toBeTrue();
  });

  /**
   * The button is the only way out, and this is the test that says so.
   *
   * <p>No page can stop a reader leaving native fullscreen — Esc, a pull down
   * from the top edge, a tab switch — and no page should be able to. But losing
   * the window is not the same as being finished reading, and treating it as
   * though it were meant a stray downward swipe put the app toolbar back over
   * the book. The browser takes its own chrome back; the app keeps its chrome
   * off until asked.</p>
   */
  it('keeps the reading view when the browser leaves fullscreen on its own', () => {
    browserAllowsFullscreen(true);
    chrome.toggleFullscreen();

    browserLeavesFullscreen();

    expect(chrome.immersive()).toBeTrue();
  });

  /** And the button still works afterwards, which is what stops that being a
   *  trap rather than a decision. */
  it('still hands the chrome back on the next press', () => {
    browserAllowsFullscreen(true);
    chrome.toggleFullscreen();
    browserLeavesFullscreen();

    chrome.toggleFullscreen();

    expect(chrome.immersive()).toBeFalse();
  });

  /**
   * The tablet answer to the same question, and the reason it is not one flag.
   * A device is a tablet before anyone presses anything, so the reader arrives
   * with the toolbar already gone — and the sidebar deliberately still there,
   * because with no toolbar it is the only way off the page.
   */
  describe('on a tablet', () => {
    beforeEach(() => {
      TestBed.resetTestingModule();

      // Before the service is constructed: it reads this once and keeps the
      // answer, so that a keyboard opening cannot be mistaken for a resize.
      spyOn(window, 'matchMedia').and.returnValue({ matches: true } as MediaQueryList);

      TestBed.configureTestingModule({
        providers: [
          ReaderChrome, ViewportLock,
          { provide: ReaderStore, useValue: { preferences: signal(DEFAULT_PREFERENCES) } }
        ]
      });

      app = TestBed.inject(AppChromeService);
      chrome = TestBed.inject(ReaderChrome);
    });

    it('opens with no toolbar and the sidebar at its rail', () => {
      expect(app.level()).toBe('rail');
      expect(app.showsToolbar()).toBeFalse();
      expect(app.showsNav()).toBeTrue();
    });

    /** And fullscreen still goes further than that, and comes back to it rather
     *  than to a toolbar the tablet never wanted. */
    it('returns to the rail after fullscreen, not to the toolbar', () => {
      spyOn(root, 'requestFullscreen').and.returnValue(Promise.resolve());

      chrome.toggleFullscreen();
      expect(app.level()).toBe('none');

      chrome.toggleFullscreen();
      expect(app.level()).toBe('rail');
    });
  });

  it('gives the chrome back when the reader is destroyed', () => {
    browserAllowsFullscreen(true);
    chrome.toggleFullscreen();

    TestBed.resetTestingModule();

    expect(app.level()).toBe('full');
  });

});

import { Injectable, OnDestroy, computed, effect, inject } from '@angular/core';
import { ReaderStore } from './reader-store';
import { ViewportLock } from './viewport-lock';
import { AppChromeService, ChromeLevel } from '../../services/app-chrome.service';

/**
 * Safari on an iPad reached the unprefixed Fullscreen API late, and an iPad is
 * the device this feature is most wanted on. Both spellings are tried; neither
 * existing is a supported outcome rather than an error.
 */
type FullscreenElement = HTMLElement & { webkitRequestFullscreen?: () => Promise<void> };
type FullscreenDocument = Document & {
  webkitExitFullscreen?: () => Promise<void>;
  webkitFullscreenElement?: Element | null;
};

/**
 * How the reader looks, as opposed to what it shows.
 *
 * <p>Two things live here, and what they have in common is that neither is a
 * question about the book: which tone the app is wearing, and whether the window
 * is full. The shell answers "which book, which chapter, where on the page", and
 * had run out of room to answer anything else.</p>
 *
 * <p>Reading in sepia beside a white sidebar reads as a rendering fault rather
 * than a theme — the same thing the Date Night pages fixed by giving their nav
 * the theater palette. But the reader's tone is a stored per-user preference,
 * not something the URL can be asked for, so the route-sniffing that colours
 * those pages cannot answer this one.</p>
 *
 * <p>Its own service, alongside {@link ReaderMeasure}, for the same reason: "what
 * colour is the app" is not a which-book question, and the shell should not grow
 * a constructor and a destroy hook to answer it. Provided by the shell, so it
 * lives and dies with the reader — which is what puts the tone back.</p>
 *
 * <p>The reader's own <i>light</i> theme publishes as `plain`: the chrome is
 * already white, so there is nothing for it to change to.</p>
 */
@Injectable()
export class ReaderChrome implements OnDestroy {
  private readonly store = inject(ReaderStore);
  private readonly chrome = inject(AppChromeService);

  /** Whether the reader currently has the screen entirely to itself. */
  readonly immersive = computed(() => this.chrome.level() === 'none');

  /**
   * Injected for its existence rather than for anything it is asked: it watches
   * the same level this service sets, and pins the page while the chrome is
   * reduced. Injecting it here is what brings it into being at all.
   */
  private readonly lock = inject(ViewportLock);

  /**
   * What the reader looks like when it is not fullscreen, which is not the same
   * answer on every device.
   *
   * <p>A tablet gets `rail`: no toolbar, and the sidebar fixed at its icon
   * width. The toolbar's jobs there are a name, a logout and a button to open
   * the sidebar, and it charges a strip across the top of the book for them —
   * while the sidebar it opens is the only way off this page, so it has to stay
   * whatever happens to the toolbar.</p>
   *
   * <p>The condition is a coarse pointer <i>and</i> a tablet's width. Coarse
   * alone would take the toolbar away on a phone, where the sidebar is an
   * overlay that only the toolbar's button can pull out — chrome with no way
   * back into it. Width alone would take it away from a small desktop window,
   * where nothing about this is wanted: a mouse cannot produce the problem it
   * solves. 768px is the app's established boundary between the two layouts.</p>
   *
   * <p>Read once. A device does not stop being a tablet while a book is open,
   * and re-answering it on every resize would mean the toolbar coming and going
   * as the on-screen keyboard opens.</p>
   */
  private readonly resting: ChromeLevel =
    window.matchMedia('(pointer: coarse) and (min-width: 768px)').matches ? 'rail' : 'full';

  constructor() {
    this.chrome.setLevel(this.resting);

    effect(() => {
      const theme = this.store.preferences().theme;
      this.chrome.set(theme === 'light' ? 'plain' : theme);
    });
  }

  /** Or the tone — and the missing toolbar — outlive the page that asked for it. */
  ngOnDestroy(): void {
    this.exit();
    this.chrome.reset();
  }

  /**
   * Full screen, or back out of it. <b>The only way back out of it.</b>
   *
   * <p>Two things happen, and they are kept separate on purpose. The app chrome
   * is told to get out of the way, which is what the reader actually asked for
   * and is the part that always works; and the browser is asked for the window,
   * which is the part that may be refused — no user gesture, an iframe policy,
   * an iPad whose Safari does not offer it. A refusal is not a reader-facing
   * failure: the reader still fills the whole page, which is every pixel this
   * app was ever able to give it.</p>
   *
   * <p>Because the two are separate, <b>losing the window does not end the
   * reading view</b>. Every browser lets the reader out of native fullscreen
   * without asking the page — Esc, a pull down from the top edge, a tab switch —
   * and no page may prevent it; that is a deliberate part of the API and not
   * something to work around. What a page <i>can</i> decide is what it does
   * about it, and this one does nothing. Pulling down gives the browser its own
   * chrome back and leaves the reader reading. Only this button puts the app's
   * chrome back, which is what was asked for: a gesture made by accident should
   * not end up somewhere the reader has to navigate out of.</p>
   *
   * <p>Both promises are deliberately dropped for the same reason.</p>
   */
  toggleFullscreen(): void {
    const entering = !this.immersive();
    this.chrome.setLevel(entering ? 'none' : this.resting);

    if (entering) {
      const root = document.documentElement as FullscreenElement;
      void (root.requestFullscreen?.() ?? root.webkitRequestFullscreen?.())?.catch(() => undefined);
      return;
    }

    this.exit();
  }

  private exit(): void {
    const doc = document as FullscreenDocument;
    if (!doc.fullscreenElement && !doc.webkitFullscreenElement) return;

    void (doc.exitFullscreen?.() ?? doc.webkitExitFullscreen?.())?.catch(() => undefined);
  }
}

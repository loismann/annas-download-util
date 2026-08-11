import { Injectable, OnDestroy, effect, inject } from '@angular/core';
import { ReaderStore } from './reader-store';
import { ChromeToneService } from '../../services/chrome-tone.service';

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
  private readonly chrome = inject(ChromeToneService);

  constructor() {
    effect(() => {
      const theme = this.store.preferences().theme;
      this.chrome.set(theme === 'light' ? 'plain' : theme);
    });
  }

  /** Or the tone outlives the page that asked for it. */
  ngOnDestroy(): void {
    this.chrome.reset();
  }

  /**
   * Full window, or back out of it.
   *
   * <p>Both promises are deliberately dropped: a browser may refuse fullscreen
   * outright — no user gesture, an iframe policy, an unsupported build — and a
   * refusal is not a reader-facing failure. The window simply stays as it
   * was.</p>
   */
  toggleFullscreen(): void {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen().catch(() => undefined);
  }
}

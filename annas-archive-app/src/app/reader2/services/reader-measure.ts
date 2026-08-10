import { Injectable, effect, inject } from '@angular/core';
import { ReaderStore } from './reader-store';
import { measuredFit } from './page-fit';

/**
 * Keeps the store's page fit in step with the real reading surface.
 *
 * <p>Its own service because "how big is a page" is a layout question, not a
 * which-book question — the shell forwards resize events here and decides
 * nothing. Font and theme changes re-measure automatically, one paint after
 * they land, so the probe never clones the styles they just replaced.</p>
 */
@Injectable()
export class ReaderMeasure {
  private readonly store = inject(ReaderStore);

  constructor() {
    effect(() => {
      this.store.preferences();
      requestAnimationFrame(() => this.remeasure());
    });
  }

  /**
   * Points the store at a fit measured from the rendered surface, so pages end
   * where the text actually ends. Quietly does nothing before the surface
   * exists — the store's estimate covers the first paint.
   */
  remeasure(): void {
    const surface = document.querySelector<HTMLElement>('.reading-surface .surface');
    if (!surface) return;

    this.store.resize(measuredFit(surface, () => this.store.chapterWords()));
  }
}

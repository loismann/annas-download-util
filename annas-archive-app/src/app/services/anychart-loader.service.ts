import { Injectable } from '@angular/core';

/** AnyChart's bundles are UMD and attach to `window.anychart`, so they can't be
 *  `import`ed as ES modules — they have to be injected as script tags. */
declare const anychart: unknown;

/**
 * Loads AnyChart on demand, once.
 *
 * These two files used to sit in angular.json's global `scripts` array, which
 * meant ~963 kB (239 kB over the wire) downloaded on *every* page load for a
 * feature exactly one component uses — the character graph, itself reachable
 * only from inside the ebook reader. Now the cost is paid by whoever actually
 * opens a graph.
 *
 * The in-flight promise is cached, so opening the modal repeatedly (or twice at
 * once) injects the scripts a single time.
 */
@Injectable({ providedIn: 'root' })
export class AnychartLoaderService {
  private loading?: Promise<void>;

  load(): Promise<void> {
    if (typeof anychart !== 'undefined') return Promise.resolve();
    // Order matters: the graph module registers itself onto core.
    this.loading ??= this.injectSequentially([
      'assets/anychart/anychart-core.min.js',
      'assets/anychart/anychart-graph.min.js'
    ]).catch(err => {
      // Let a later attempt retry rather than caching the failure forever.
      this.loading = undefined;
      throw err;
    });
    return this.loading;
  }

  private async injectSequentially(srcs: string[]): Promise<void> {
    for (const src of srcs) {
      await this.injectOne(src);
    }
  }

  private injectOne(src: string): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const existing = document.querySelector<HTMLScriptElement>(`script[src="${src}"]`);
      if (existing) {
        resolve();
        return;
      }
      const script = document.createElement('script');
      script.src = src;
      script.async = false;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error(`Failed to load ${src}`));
      document.body.appendChild(script);
    });
  }
}

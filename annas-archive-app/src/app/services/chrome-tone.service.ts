import { Injectable, signal } from '@angular/core';

/**
 * A tone the app's chrome can wear.
 *
 * <p>`plain` is not a third colour scheme — it is the absence of one, and it is
 * where every page outside the reader sits. The reader's own <i>light</i> theme
 * publishes as `plain` rather than as a fourth value, because white chrome
 * around a white page is exactly what the app already looks like: a value that
 * asked for no change would only be a second name for one.</p>
 */
export type Tone = 'plain' | 'sepia' | 'dark';

/**
 * What tone the app's chrome is wearing.
 *
 * <p>A sepia reader beside a white sidebar reads as a rendering fault rather
 * than a theme, in exactly the way the Date Night pages did before their nav got
 * the theater palette. But the reader's choice is a stored per-user preference,
 * not something the URL can be asked for, so the route-sniffing that colours
 * Date Night cannot answer this one.</p>
 *
 * <p>A signal, published by whoever owns the choice and read by the shell that
 * has to render it. The alternative was importing the reader's store into
 * <code>AppComponent</code>, which would make the app shell depend on the
 * internals of one feature — and would instantiate a reader store for people who
 * never open a book.</p>
 *
 * <p>Whoever sets it is responsible for putting it back: a tone that outlived
 * the page that asked for it would tint the whole app on the way out.</p>
 */
@Injectable({ providedIn: 'root' })
export class ChromeToneService {
  readonly tone = signal<Tone>('plain');

  set(tone: Tone): void {
    this.tone.set(tone);
  }

  reset(): void {
    this.tone.set('plain');
  }
}

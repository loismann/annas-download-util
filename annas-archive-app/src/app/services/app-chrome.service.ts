import { Injectable, computed, signal } from '@angular/core';

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
 * How much of the app's chrome a page wants around it.
 *
 * <ul>
 *   <li><b>full</b> — the toolbar and a sidebar the reader can open and close.
 *       Everywhere, by default.</li>
 *   <li><b>rail</b> — no toolbar; the sidebar stays, locked to its icon rail.
 *       This is the reader on a tablet. The toolbar's only jobs there are a name,
 *       a logout and the button that opens the sidebar, and it costs a strip of
 *       the page across the top of the book.</li>
 *   <li><b>none</b> — nothing at all. The fullscreen button.</li>
 * </ul>
 *
 * <p><b>Why `rail` keeps the sidebar and `none` does not.</b> The button that
 * expands and collapses the sidebar lives in the toolbar, so a level with no
 * toolbar and a collapsible sidebar would be a sidebar nobody could open. `rail`
 * answers that by fixing it open at its icon width, which is also the only way
 * left to leave the reader — there is no other navigation on the page. `none` is
 * reached by a button that is still on screen and still says how to undo itself,
 * so it can afford to take everything.</p>
 */
export type ChromeLevel = 'full' | 'rail' | 'none';

/**
 * How the app's chrome — the toolbar and the sidebar beside the page — should
 * present itself, as told by the page it is wrapped around.
 *
 * <p>Two questions live here, and they are the same question twice: what colour
 * should the chrome be, and how much of it should there be. A sepia reader
 * beside a white sidebar reads as a rendering fault rather than a theme, in
 * exactly the way the Date Night pages did before their nav got the theater
 * palette. But the reader's choice is a stored per-user preference, not
 * something the URL can be asked for, so the route-sniffing that colours Date
 * Night cannot answer either of them.</p>
 *
 * <p>Signals, published by whoever owns the choice and read by the shell that
 * has to render it. The alternative was importing the reader's store into
 * <code>AppComponent</code>, which would make the app shell depend on the
 * internals of one feature — and would instantiate a reader store for people who
 * never open a book.</p>
 *
 * <p>Whoever sets these is responsible for putting them back, which is what
 * {@link reset} is for: a tone that outlived the page that asked for it would
 * tint the whole app on the way out, and a level that outlived it would leave
 * the app with no toolbar and no way to reach one.</p>
 */
@Injectable({ providedIn: 'root' })
export class AppChromeService {
  readonly tone = signal<Tone>('plain');

  readonly level = signal<ChromeLevel>('full');

  /** The three questions the shell actually asks, named once here rather than
   *  spelled as string comparisons across a template. */
  readonly showsToolbar = computed(() => this.level() === 'full');
  readonly showsNav = computed(() => this.level() !== 'none');
  readonly railOnly = computed(() => this.level() === 'rail');

  set(tone: Tone): void {
    this.tone.set(tone);
  }

  setLevel(level: ChromeLevel): void {
    this.level.set(level);
  }

  reset(): void {
    this.tone.set('plain');
    this.level.set('full');
  }
}

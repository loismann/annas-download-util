import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * The Date Night lobby card, as a 1950s drive-in poster.
 *
 * Extracted from the announcement dialog so the one-time splash and the
 * permanent Date Night page render the same artwork from one definition — the
 * two are shown side by side in practice ("see it again on the Date Night
 * page"), so any drift between them would be obvious.
 *
 * Styling comes from the shared theater design language in
 * src/styles/theater.scss; this contributes layout only. The period devices are
 * genuine conventions rather than generic retro: a starburst badge slapped over
 * the artwork, stacked "SEE! / THRILL! / GASP!" copy, halftone dot texture from
 * photomechanical printing, and an auditorium showing Robot Monster (1953).
 */
@Component({
  selector: 'app-date-night-poster',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="thtr-stage">
      <div class="thtr-bulbs" aria-hidden="true">
        <span class="thtr-bulbs-edge thtr-bulbs-edge--top">
          <i *ngFor="let b of hBulbs"></i>
        </span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--right">
          <i *ngFor="let b of vBulbs"></i>
        </span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--bottom">
          <i *ngFor="let b of hBulbs"></i>
        </span>
        <span class="thtr-bulbs-edge thtr-bulbs-edge--left">
          <i *ngFor="let b of vBulbs"></i>
        </span>
      </div>
      <div class="thtr-curtains" aria-hidden="true"></div>

      <div class="thtr-inner">
        <!-- Hinged at the two lower corners of this well, sweeping the sky. -->
        <div class="thtr-searchlight thtr-searchlight--left" aria-hidden="true"></div>
        <div class="thtr-searchlight thtr-searchlight--right" aria-hidden="true"></div>
        <div class="thtr-halftone" aria-hidden="true"></div>

        <button
          *ngIf="dismissible"
          type="button"
          class="thtr-close"
          aria-label="Close"
          (click)="closed.emit()"
        >✕</button>

        <div class="content">
          <!-- House lights down, picture already running. Still: Robot Monster
               (1953) — Ro-Man in the gorilla suit and diving helmet, which is
               the pool's whole personality in one frame. -->
          <div class="thtr-auditorium" aria-hidden="true">
            <div class="thtr-beam"></div>
            <div class="thtr-screen">
              <img src="assets/date-night-screen-still.jpg" alt="" />
            </div>
            <div class="thtr-seats thtr-seats--far">
              <span *ngFor="let s of farRow"></span>
            </div>
            <div class="thtr-seats thtr-seats--mid">
              <span *ngFor="let s of midRow"></span>
            </div>
            <div class="thtr-seats thtr-seats--near">
              <span *ngFor="let s of nearRow"></span>
            </div>
          </div>

          <p class="thtr-eyebrow">One Night Only! · (Every Single Week)</p>
          <h1 class="thtr-title">DATE&nbsp;NIGHT</h1>
          <p class="thtr-tagline">A Motion Picture Event — For Two Lolos!</p>

          <div class="pitch">
            <ul class="thtr-see">
              <li data-verb="SEE!">One of Three chillers chosen for you each week!</li>
              <li data-verb="THRILL!">To monsters, kung fu and flying saucers!</li>
              <li data-verb="GASP!">As you both agree on the best B-Movies!</li>
            </ul>
            <div class="thtr-burst burst-1">Coming<br />Soon!</div>
          </div>

          <div class="thtr-reel" *ngIf="posters.length">
            <div class="thtr-reel-track">
              <img
                *ngFor="let p of doubledPosters"
                [src]="p"
                alt=""
                loading="lazy"
                (error)="onPosterError($event)"
              />
            </div>
          </div>

          <div class="thtr-deco" aria-hidden="true"></div>

          <div class="bill">
            <div class="thtr-card">
              <span class="thtr-card-icon">🎞️</span>
              <span class="thtr-card-label">Three Options a Week</span>
              <p>Hand-picked from a vault of nearly three hundred pictures.</p>
            </div>
            <div class="thtr-card">
              <span class="thtr-card-icon">👍</span>
              <span class="thtr-card-label">You Both Vote</span>
              <p>Thumbs up or down, in secret. Only mutual favourites make the bill.</p>
            </div>
            <div class="thtr-card">
              <span class="thtr-card-icon">🍿</span>
              <span class="thtr-card-label">Name the Hour</span>
              <p>Settle on a night together. We'll count you down and roll it.</p>
            </div>
          </div>

          <p class="thtr-blink">★ &nbsp; FIND IT ANY TIME UNDER “DATE NIGHT” IN THE MENU &nbsp; ★</p>

          <button *ngIf="dismissible" class="thtr-btn" (click)="closed.emit()">
            Swell — Can't Wait!
          </button>

          <p class="fine-print">
            No need to do a thing. We'll bring the first showing to you.
          </p>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    /* Above the searchlights and halftone, both of which are absolute. */
    .content { position: relative; z-index: 2; }

    .thtr-auditorium {
      margin: -30px -26px 18px;
      border-bottom: 3px solid var(--thtr-gilt);
    }

    /* Exclamation copy beside the badge, the badge kicked off-axis — these were
       pasted onto artwork by hand and never sat straight. */
    .pitch {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 22px;
      margin: 4px 0 20px;
    }
    .burst-1 { transform: rotate(9deg); flex: 0 0 auto; }

    .thtr-deco { margin: 0 -26px 18px; }

    .bill {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 16px;
      margin: 0 0 18px;
    }

    .fine-print {
      margin: 14px 0 0;
      font-size: .72rem;
      font-style: italic;
      opacity: .65;
    }

    @media (max-width: 620px) {
      .thtr-auditorium { margin: -24px -16px 14px; }
      .thtr-deco { margin: 0 -16px 14px; }
      .bill { grid-template-columns: 1fr; gap: 8px; }
      .pitch { flex-direction: column; gap: 14px; }
      .thtr-see li { font-size: .9rem; }
    }
  `]
})
export class DateNightPosterComponent {
  /** Pool posters for the marquee reel. Empty hides the reel entirely. */
  @Input() posters: string[] = [];

  /** Shows the close button and the call-to-action. The permanent page turns
   *  both off — there's nothing to dismiss when the poster *is* the page. */
  @Input() dismissible = false;

  /** Fired by either the ✕ or the call-to-action — both mean the same thing, so
   *  the host handles them identically. */
  @Output() closed = new EventEmitter<void>();

  /** Bulb counts per edge. Spacing comes from space-between, so these only set
   *  density — the vertical edges get more because the card is taller than it
   *  is wide. */
  readonly hBulbs = Array.from({ length: 26 });
  readonly vBulbs = Array.from({ length: 36 });
  /** Three rows; each row further back holds more, smaller seats so it shows
   *  through the gaps of the row in front and reads as receding. */
  readonly farRow = Array.from({ length: 9 });
  readonly midRow = Array.from({ length: 7 });
  readonly nearRow = Array.from({ length: 5 });

  /** Rendered twice so the reel's -50% loop lands on an identical frame. */
  get doubledPosters(): string[] {
    return [...this.posters, ...this.posters];
  }

  onPosterError(event: Event): void {
    (event.target as HTMLImageElement).style.display = 'none';
  }
}

import {
  AfterViewInit, Component, ElementRef, HostListener, Inject, NgZone, OnDestroy, ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { DateNightPosterComponent } from '../date-night-poster/date-night-poster.component';

export interface DateNightAnnouncementData {
  posters: string[];
}

/**
 * One-time "coming soon" splash for Mom and Dad — a full-viewport dialog around
 * the shared poster (see DateNightPosterComponent, which owns the artwork and is
 * also what the permanent /date-night page renders).
 *
 * The poster is SCALED to fit rather than allowed to scroll. Its bottom half
 * carries the actual point — how the weekly picks work, and where to find the
 * page afterwards — and anything below the fold on a phone would simply never be
 * read before someone tapped the ✕. Scaling keeps the whole bill on screen at
 * once on every device, at the cost of smaller text on small screens.
 *
 * Width is `min(720px, 100vw)` so a phone lays out at its own width and inherits
 * the compact mobile styles, instead of shrinking the 720px desktop composition
 * down to something illegible; only the vertical overflow is taken up by scale.
 *
 * Both the ✕ and the call-to-action close it, and both count as having seen it:
 * the same poster lives permanently on the Date Night page, so there is nothing
 * to gain by nagging someone who has already waved it away.
 */
@Component({
  selector: 'app-date-night-announcement',
  standalone: true,
  imports: [CommonModule, MatDialogModule, DateNightPosterComponent],
  template: `
    <div class="fit-viewport" #viewport>
      <div class="fit-content" #content [style.transform]="'scale(' + scale + ')'">
        <app-date-night-poster
          [posters]="data.posters"
          [dismissible]="true"
          (closed)="dismiss()"
        ></app-date-night-poster>
      </div>
    </div>
  `,
  styles: [`
    .fit-viewport {
      width: 100vw;
      height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      overflow: hidden;
      background: rgba(6, 5, 10, .96);
    }

    .fit-content {
      /* Natural size. Transform does not affect layout, so measuring this
         element still reports its unscaled dimensions. */
      width: min(720px, 100vw);
      flex: 0 0 auto;
      transform-origin: center center;
    }
  `]
})
export class DateNightAnnouncementComponent implements AfterViewInit, OnDestroy {
  scale = 1;

  @ViewChild('viewport') private viewport!: ElementRef<HTMLElement>;
  @ViewChild('content') private content!: ElementRef<HTMLElement>;

  private observer?: ResizeObserver;

  constructor(
    public dialogRef: MatDialogRef<DateNightAnnouncementComponent>,
    @Inject(MAT_DIALOG_DATA) public data: DateNightAnnouncementData,
    private zone: NgZone
  ) {}

  ngAfterViewInit(): void {
    this.updateScale();

    // Poster images load asynchronously and the reel has a fixed height, so the
    // natural height settles shortly after first paint rather than during it.
    // Watching the element is more reliable than guessing with a timeout.
    if (typeof ResizeObserver !== 'undefined') {
      this.observer = new ResizeObserver(() =>
        this.zone.run(() => this.updateScale())
      );
      this.observer.observe(this.content.nativeElement);
    }
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }

  @HostListener('window:resize')
  onResize(): void {
    this.updateScale();
  }

  /** Never scales up — a poster blown past its design size just looks soft. */
  private updateScale(): void {
    const box = this.viewport?.nativeElement;
    const el = this.content?.nativeElement;
    if (!box || !el) return;

    const naturalWidth = el.offsetWidth;
    const naturalHeight = el.offsetHeight;
    if (!naturalWidth || !naturalHeight) return;

    // offsetWidth/Height are pre-transform, so dividing by them gives the factor
    // that makes the element exactly fit — no feedback loop with the observer.
    this.scale = Math.min(
      1,
      box.clientWidth / naturalWidth,
      box.clientHeight / naturalHeight
    );
  }

  dismiss(): void {
    this.dialogRef.close(true);
  }
}

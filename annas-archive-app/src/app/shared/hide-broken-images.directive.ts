import { Directive, ElementRef, OnDestroy, OnInit } from '@angular/core';

/**
 * Hides images inside the host that fail to load.
 *
 * This replaces an `onerror="this.style.display='none'"` attribute that was
 * being string-injected into every `<img>` of model-written HTML. That worked
 * only because the HTML was passed through `bypassSecurityTrustHtml` — an
 * inline handler is exactly what sanitising removes, and exactly what made the
 * whole payload dangerous. The behaviour is worth keeping and the mechanism was
 * not.
 *
 * The listener is registered in the **capture** phase because `error` does not
 * bubble: a listener on the container never sees a descendant image fail unless
 * it captures.
 *
 * Broken images are expected here rather than exceptional — the learn-more
 * prompt asks the model for image URLs and warns it not to invent them, which
 * means some will be invented anyway.
 */
@Directive({
  selector: '[appHideBrokenImages]',
  standalone: true
})
export class HideBrokenImagesDirective implements OnInit, OnDestroy {
  constructor(private readonly host: ElementRef<HTMLElement>) {}

  ngOnInit(): void {
    this.host.nativeElement.addEventListener('error', this.hide, true);
  }

  ngOnDestroy(): void {
    this.host.nativeElement.removeEventListener('error', this.hide, true);
  }

  private readonly hide = (event: Event): void => {
    const target = event.target;
    if (target instanceof HTMLImageElement) {
      target.style.display = 'none';
    }
  };
}

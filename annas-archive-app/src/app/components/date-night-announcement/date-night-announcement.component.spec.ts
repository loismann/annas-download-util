import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ElementRef } from '@angular/core';

import { DateNightAnnouncementComponent } from './date-night-announcement.component';

/**
 * Characterization tests for the one-time "coming soon" splash.
 *
 * Almost all of this component is one scaling calculation, and the interesting
 * property is that it can only ever shrink. Scaling up would blow a poster past
 * its design size and just look soft, and — because the observer watches the
 * element it is scaling — a factor derived from post-transform dimensions would
 * feed back on itself.
 */
describe('DateNightAnnouncementComponent (characterization)', () => {
  let fixture: ComponentFixture<DateNightAnnouncementComponent>;
  let component: DateNightAnnouncementComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<DateNightAnnouncementComponent>>;

  /** Installs a viewport of `box` around content of `content`. */
  function measure(box: { w: number; h: number }, content: { w: number; h: number }): void {
    const set = (name: string, el: unknown) =>
      Object.defineProperty(component, name, { value: new ElementRef(el), writable: true });

    set('viewport', { clientWidth: box.w, clientHeight: box.h });
    set('content', { offsetWidth: content.w, offsetHeight: content.h });
  }

  /** Runs the private scaling pass the way a resize would. */
  function rescale(): void {
    component.onResize();
  }

  beforeEach(async () => {
    dialogRef = jasmine.createSpyObj<MatDialogRef<DateNightAnnouncementComponent>>('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [DateNightAnnouncementComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: { posters: ['a.jpg', 'b.jpg'] } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DateNightAnnouncementComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('scaling to fit', () => {
    it('should start at natural size', () => {
      expect(component.scale).toBe(1);
    });

    it('should shrink to whichever axis runs out first', () => {
      // Width is the tighter constraint here: 400/800 beats 600/900.
      measure({ w: 400, h: 600 }, { w: 800, h: 900 });

      rescale();

      expect(component.scale).toBe(0.5);
    });

    it('should shrink to height when that is the tighter one', () => {
      measure({ w: 900, h: 300 }, { w: 1000, h: 1200 });

      rescale();

      expect(component.scale).toBe(0.25);
    });

    it('should never scale up', () => {
      // A poster blown past its design size just looks soft.
      measure({ w: 2000, h: 2000 }, { w: 400, h: 600 });

      rescale();

      expect(component.scale).toBe(1);
    });

    it('should leave the scale alone when nothing has been laid out yet', () => {
      // offsetWidth is 0 before first paint; dividing by it would be Infinity.
      measure({ w: 800, h: 600 }, { w: 0, h: 0 });

      rescale();

      expect(component.scale).toBe(1);
    });

    it('should do nothing before the view exists', () => {
      expect(() => component.onResize()).not.toThrow();
      expect(component.scale).toBe(1);
    });

    it('should settle rather than feed back on itself', () => {
      // The observer watches the element being scaled, so the factor has to
      // come from pre-transform dimensions — offsetWidth/Height are, which is
      // why running it repeatedly converges instead of shrinking each pass.
      measure({ w: 400, h: 600 }, { w: 800, h: 900 });

      rescale();
      const first = component.scale;
      rescale();
      rescale();

      expect(component.scale).toBe(first);
    });
  });

  describe('dismissing', () => {
    it('should report that it was acknowledged', () => {
      // The `true` is what marks it seen — closing with nothing would show it
      // again on the next page load.
      component.dismiss();

      expect(dialogRef.close).toHaveBeenCalledWith(true);
    });
  });

  describe('lifecycle', () => {
    it('should stop observing when the dialog closes', () => {
      const disconnect = jasmine.createSpy('disconnect');
      (component as unknown as { observer?: { disconnect: () => void } }).observer = { disconnect };

      component.ngOnDestroy();

      expect(disconnect).toHaveBeenCalled();
    });

    it('should cope with a browser that has no ResizeObserver', () => {
      (component as unknown as { observer?: unknown }).observer = undefined;

      expect(() => component.ngOnDestroy()).not.toThrow();
    });

    it('should pass the posters through to the artwork', () => {
      expect(component.data.posters).toEqual(['a.jpg', 'b.jpg']);
    });
  });
});

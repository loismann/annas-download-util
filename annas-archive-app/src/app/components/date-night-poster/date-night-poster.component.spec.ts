import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { DateNightPosterComponent } from './date-night-poster.component';

/**
 * Characterization tests for the Date Night "coming soon" poster.
 *
 * Presentational, and used two ways from one component: as a dismissible dialog
 * and as the permanent page. The tests are mostly about that difference, plus
 * the marquee reel's doubling — which is not decoration but the mechanism that
 * makes its -50% loop land on an identical frame.
 */
describe('DateNightPosterComponent (characterization)', () => {
  let fixture: ComponentFixture<DateNightPosterComponent>;
  let component: DateNightPosterComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DateNightPosterComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(DateNightPosterComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('the marquee reel', () => {
    it('should render each poster twice so the loop is seamless', () => {
      // The animation translates by -50%. Anything other than an exact doubling
      // makes it jump at the wrap.
      component.posters = ['a.jpg', 'b.jpg'];

      expect(component.doubledPosters).toEqual(['a.jpg', 'b.jpg', 'a.jpg', 'b.jpg']);
    });

    it('should hide the reel entirely with no posters', () => {
      component.posters = [];
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.thtr-reel')).toBeNull();
    });

    it('should show the reel once there is something to show', () => {
      component.posters = ['a.jpg'];
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.thtr-reel')).toBeTruthy();
      expect(fixture.nativeElement.querySelectorAll('.thtr-reel img').length).toBe(2);
    });

    it('should hide a poster that will not load rather than show a torn image', () => {
      const img = document.createElement('img');

      component.onPosterError({ target: img } as unknown as Event);

      expect(img.style.display).toBe('none');
    });
  });

  describe('the two ways it is used', () => {
    it('should offer no way out when the poster is the page', () => {
      // There is nothing to dismiss it back to.
      component.dismissible = false;
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.thtr-close')).toBeNull();
      expect(fixture.nativeElement.querySelector('.thtr-btn')).toBeNull();
    });

    it('should offer both a close and a call-to-action as a dialog', () => {
      component.dismissible = true;
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.thtr-close')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('.thtr-btn')).toBeTruthy();
    });

    it('should report the same thing from the cross and the button', () => {
      // Both mean "I am done with this", so the host handles them identically.
      const closed = jasmine.createSpy('closed');
      component.closed.subscribe(closed);
      component.dismissible = true;
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('.thtr-close') as HTMLElement).click();
      (fixture.nativeElement.querySelector('.thtr-btn') as HTMLElement).click();

      expect(closed).toHaveBeenCalledTimes(2);
    });
  });

  describe('the theater dressing', () => {
    it('should keep one bulb pitch on both axes', () => {
      // The counts are deliberately more than fit; each edge clips its own, so
      // the spacing does not stretch with the card.
      expect(component.hBulbs.length).toBe(64);
      expect(component.vBulbs.length).toBe(64);
    });

    it('should put more, smaller seats in each row further back', () => {
      // That is what makes the rows read as receding rather than stacked.
      expect(component.farRow.length).toBeGreaterThan(component.midRow.length);
      expect(component.midRow.length).toBeGreaterThan(component.nearRow.length);
    });
  });
});

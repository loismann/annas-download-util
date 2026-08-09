import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { EventEmitter } from '@angular/core';

import { MediaTileComponent } from './media-tile.component';

/**
 * Characterization tests for the shared media tile.
 *
 * One tile serves the movie, TV, ebook and audiobook grids, so nearly every
 * affordance is optional and the defaults decide what four pages look like.
 * The click behaviour is the part worth pinning: the whole tile opens or plays
 * the item, and every overlay button sits on top of that surface — a button
 * that failed to stop the event would open the thing it was meant to act on.
 */
describe('MediaTileComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaTileComponent>;
  let component: MediaTileComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MediaTileComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(MediaTileComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('overlay buttons', () => {
    it('should never let a button also trigger the tile', () => {
      const event = new MouseEvent('click');
      const stop = spyOn(event, 'stopPropagation');
      const emitter = new EventEmitter<Event>();
      const emitted = jasmine.createSpy('emitted');
      emitter.subscribe(emitted);

      component.emitStopped(emitter, event);

      expect(stop).toHaveBeenCalled();
      expect(emitted).toHaveBeenCalledWith(event);
    });

    it('should pass the original event on to the host', () => {
      // Callers use it for their own stopPropagation on nested handlers.
      const event = new MouseEvent('click');
      let received: Event | undefined;
      component.edit.subscribe(e => { received = e; });

      component.emitStopped(component.edit, event);

      expect(received).toBe(event);
    });
  });

  describe('what shows by default', () => {
    it('should show edit and favourite, and hide delete and download', () => {
      // The four grids all want the first two; only some want the rest, and a
      // stray delete button is the one worth being conservative about.
      expect(component.showEdit).toBe(true);
      expect(component.showFavorite).toBe(true);
      expect(component.showDelete).toBe(false);
      expect(component.showDownload).toBe(false);
    });

    it('should default to a book/poster shape', () => {
      // Audiobook art is square because Audible's is; everything else is 2:3,
      // so the shape has to be the caller's choice rather than baked in.
      expect(component.coverAspect).toBe('2 / 3');
    });

    it('should start unselected and out of bulk mode', () => {
      expect(component.bulkMode).toBe(false);
      expect(component.bulkSelected).toBe(false);
      expect(component.favorited).toBe(false);
    });

    it('should carry no owner badge until given one', () => {
      expect(component.ownerLabel).toBeNull();
    });
  });

  describe('rendering', () => {
    it('should show the title', () => {
      component.title = 'Them!';
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('Them!');
    });

    it('should render the badge only when there is an owner', () => {
      fixture.detectChanges();
      const before = (fixture.nativeElement as HTMLElement).textContent;
      expect(before).not.toContain('Mom');

      component.ownerLabel = 'Mom';
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('Mom');
    });

    it('should apply the aspect ratio it was given', () => {
      component.coverAspect = '1 / 1';
      fixture.detectChanges();

      const html = (fixture.nativeElement as HTMLElement).innerHTML;
      expect(html).toContain('1 / 1');
    });
  });

  describe('bulk selection', () => {
    it('should report a bulk toggle', () => {
      const toggled = jasmine.createSpy('bulkToggle');
      component.bulkToggle.subscribe(toggled);

      component.bulkToggle.emit();

      expect(toggled).toHaveBeenCalled();
    });
  });
});

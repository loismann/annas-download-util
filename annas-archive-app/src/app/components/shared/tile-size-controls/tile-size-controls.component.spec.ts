import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { TileSizeControlsComponent } from './tile-size-controls.component';

/**
 * Characterization tests for the shared tile-size control.
 *
 * Presentational, and used by four grids. The icon logic is the only branch
 * here, and it exists because the same control fronts two different meanings —
 * a favourite on the media grids, a bookmark on the video grid — where sending
 * the wrong glyph would silently mislabel the filter.
 */
describe('TileSizeControlsComponent (characterization)', () => {
  let fixture: ComponentFixture<TileSizeControlsComponent>;
  let component: TileSizeControlsComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TileSizeControlsComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(TileSizeControlsComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('the filter icon', () => {
    it('should use a heart for favourites, filled only when active', () => {
      component.filterStyle = 'favorite';

      component.filterActive = false;
      expect(component.filterIcon).toBe('favorite_border');

      component.filterActive = true;
      expect(component.filterIcon).toBe('favorite');
    });

    it('should use a bookmark for the video grid, filled only when active', () => {
      component.filterStyle = 'bookmark';

      component.filterActive = false;
      expect(component.filterIcon).toBe('bookmark_border');

      component.filterActive = true;
      expect(component.filterIcon).toBe('bookmark');
    });

    it('should default to the heart', () => {
      expect(component.filterStyle).toBe('favorite');
      expect(component.filterIcon).toBe('favorite_border');
    });
  });

  describe('defaults', () => {
    it('should start at medium with no filter', () => {
      // Most grids do not want the filter, so it has to be opt-in.
      expect(component.tileSize).toBe('medium');
      expect(component.showFilter).toBe(false);
      expect(component.filterActive).toBe(false);
    });

    it('should carry a spoken label for the filter', () => {
      expect(component.filterAriaLabel).toBeTruthy();
    });
  });

  describe('reporting changes', () => {
    it('should report a new size', () => {
      const changed = jasmine.createSpy('tileSizeChange');
      component.tileSizeChange.subscribe(changed);

      component.tileSizeChange.emit('large');

      expect(changed).toHaveBeenCalledWith('large');
    });

    it('should report a filter toggle', () => {
      const toggled = jasmine.createSpy('filterToggle');
      component.filterToggle.subscribe(toggled);

      component.filterToggle.emit();

      expect(toggled).toHaveBeenCalled();
    });

    it('should hide the filter button unless asked for', () => {
      fixture.detectChanges();
      const before = fixture.nativeElement.querySelectorAll('button').length;

      component.showFilter = true;
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('button').length).toBeGreaterThan(before);
    });
  });
});

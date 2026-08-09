import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { FavoriteToggleComponent } from './favorite-toggle.component';

/**
 * Characterization tests for the shared favourite toggle.
 *
 * It emits the *desired* state rather than "I was clicked". Every caller pairs
 * this with an optimistic update and a revert on failure, and that only works
 * if the intended value travels with the event instead of each caller deriving
 * it from its own copy of the current state.
 */
describe('FavoriteToggleComponent (characterization)', () => {
  let fixture: ComponentFixture<FavoriteToggleComponent>;
  let component: FavoriteToggleComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FavoriteToggleComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(FavoriteToggleComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  it('should start unfavourited', () => {
    expect(component.favorited).toBe(false);
  });

  it('should ask to favourite when it is not', () => {
    const toggled = jasmine.createSpy('toggled');
    component.toggled.subscribe(toggled);
    component.favorited = false;
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button') as HTMLElement).click();

    expect(toggled).toHaveBeenCalledWith(true);
  });

  it('should ask to unfavourite when it is', () => {
    const toggled = jasmine.createSpy('toggled');
    component.toggled.subscribe(toggled);
    component.favorited = true;
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button') as HTMLElement).click();

    expect(toggled).toHaveBeenCalledWith(false);
  });

  it('should not change its own state', () => {
    // The caller owns it: the star only fills once the save comes back, or is
    // put back when it fails.
    component.favorited = false;
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button') as HTMLElement).click();

    expect(component.favorited).toBe(false);
  });

  it('should show a different glyph for each state', () => {
    fixture.detectChanges();
    const empty = (fixture.nativeElement as HTMLElement).textContent;

    component.favorited = true;
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).not.toBe(empty);
  });
});

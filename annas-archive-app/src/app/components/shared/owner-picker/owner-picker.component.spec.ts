import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

import { OwnerPickerComponent } from './owner-picker.component';
import { HOUSEHOLD_OWNERS } from '../../../constants/owners';

/**
 * Characterization tests for the shared owner picker.
 *
 * The label/value split is the interesting part: the ebook flows store
 * ownership as a tag ("Dad's Books") but must show a person's name, so what is
 * displayed and what is emitted are deliberately different things.
 */
describe('OwnerPickerComponent (characterization)', () => {
  let fixture: ComponentFixture<OwnerPickerComponent>;
  let component: OwnerPickerComponent;
  let emitted: string[][];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [OwnerPickerComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(OwnerPickerComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.selectedChange.subscribe(v => emitted.push(v));
  });

  afterEach(() => fixture.destroy());

  describe('the options', () => {
    it('should offer the household by default', () => {
      expect(component.displayOptions.map(o => o.value)).toEqual([...HOUSEHOLD_OWNERS]);
    });

    it('should show a name as its own label by default', () => {
      expect(component.displayOptions.every(o => o.label === o.value)).toBe(true);
    });

    it('should let a caller separate what is shown from what is stored', () => {
      // Ebooks store ownership as a tag but must not show one.
      component.options = [{ value: "Dad's Books", label: "Dad's" }];

      expect(component.displayOptions).toEqual([{ value: "Dad's Books", label: "Dad's" }]);
    });
  });

  describe('choosing', () => {
    it('should add and remove on the same action', () => {
      component.toggle('Mom');
      expect(component.selected).toEqual(['Mom']);

      component.toggle('Mom');
      expect(component.selected).toEqual([]);
      expect(emitted).toEqual([['Mom'], []]);
    });

    it('should accumulate when several owners are allowed', () => {
      component.toggle('Mom');
      component.toggle('Dad');

      expect(component.selected).toEqual(['Mom', 'Dad']);
    });

    it('should keep only the last one when it is single-choice', () => {
      component.multi = false;

      component.toggle('Mom');
      component.toggle('Dad');

      expect(component.selected).toEqual(['Dad']);
    });

    it('should still deselect in single-choice mode', () => {
      component.multi = false;
      component.toggle('Mom');

      component.toggle('Mom');

      expect(component.selected).toEqual([]);
    });

    it('should emit the stored value, not the label', () => {
      component.options = [{ value: "Dad's Books", label: "Dad's" }];

      component.toggle("Dad's Books");

      expect(emitted).toEqual([["Dad's Books"]]);
    });

    it('should replace the array rather than edit it in place', () => {
      // The parent binds to this input; mutating it would change the parent's
      // own array before anything was saved.
      const original = ['Mom'];
      component.selected = original;

      component.toggle('Dad');

      expect(original).toEqual(['Mom']);
    });
  });
});

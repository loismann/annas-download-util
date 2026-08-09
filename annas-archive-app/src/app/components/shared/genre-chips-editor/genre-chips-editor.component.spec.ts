import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';

import { GenreChipsEditorComponent } from './genre-chips-editor.component';

/**
 * Characterization tests for the shared genre chip editor.
 *
 * Used by every edit dialog that touches genres or owners. The case-insensitive
 * deduplication is the part worth pinning: these values become filter facets, so
 * "Sci-Fi" and "sci-fi" arriving as two entries would split one genre in two
 * across every grid that filters on them.
 */
describe('GenreChipsEditorComponent (characterization)', () => {
  let fixture: ComponentFixture<GenreChipsEditorComponent>;
  let component: GenreChipsEditorComponent;
  let dialog: jasmine.SpyObj<MatDialog>;
  let emitted: string[][];

  function chipInput(): { clear: jasmine.Spy } {
    return jasmine.createSpyObj('chipInput', ['clear']);
  }

  beforeEach(async () => {
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(null) } as any);

    await TestBed.configureTestingModule({
      imports: [GenreChipsEditorComponent, NoopAnimationsModule],
      providers: [{ provide: MatDialog, useValue: dialog }]
    })
      .overrideProvider(MatDialog, { useValue: dialog })
      .compileComponents();

    fixture = TestBed.createComponent(GenreChipsEditorComponent);
    component = fixture.componentInstance;
    component.values = ['Sci-Fi'];
    component.available = ['Sci-Fi', 'Horror', 'Comedy'];
    emitted = [];
    component.valuesChange.subscribe(v => emitted.push(v));
  });

  afterEach(() => fixture.destroy());

  describe('the add dropdown', () => {
    it('should offer only what is not already on', () => {
      expect(component.availableOptions).toEqual(['Horror', 'Comedy']);
    });

    it('should not re-offer a value that differs only in case', () => {
      component.values = ['sci-fi'];

      expect(component.availableOptions).toEqual(['Horror', 'Comedy']);
    });

    it('should cope with no list of options at all', () => {
      component.available = undefined as unknown as string[];

      expect(component.availableOptions).toEqual([]);
    });

    it('should add a chosen option and report it', () => {
      component.onOptionSelected('Horror');

      expect(component.values).toEqual(['Sci-Fi', 'Horror']);
      expect(emitted).toEqual([['Sci-Fi', 'Horror']]);
    });

    it('should ignore an empty selection', () => {
      component.onOptionSelected(null);
      component.onOptionSelected('');

      expect(emitted).toEqual([]);
    });
  });

  describe('typing a value', () => {
    it('should trim it and clear the box', () => {
      const input = chipInput();

      component.addFromInput({ value: '  Horror  ', chipInput: input } as any);

      expect(component.values).toEqual(['Sci-Fi', 'Horror']);
      expect(input.clear).toHaveBeenCalled();
    });

    it('should clear the box even for a value it rejects', () => {
      // Otherwise a duplicate sits in the input looking like it did not register.
      const input = chipInput();

      component.addFromInput({ value: '   ', chipInput: input } as any);

      expect(input.clear).toHaveBeenCalled();
      expect(emitted).toEqual([]);
    });

    it('should not add the same value twice, whatever the casing', () => {
      // These become filter facets; two spellings would split one genre in two
      // across every grid that filters on it.
      component.addFromInput({ value: 'sci-fi', chipInput: chipInput() } as any);

      expect(component.values).toEqual(['Sci-Fi']);
      expect(emitted).toEqual([]);
    });
  });

  describe('removing', () => {
    it('should remove a chip and report the rest', () => {
      component.values = ['Sci-Fi', 'Horror'];

      component.remove('Sci-Fi');

      expect(component.values).toEqual(['Horror']);
      expect(emitted).toEqual([['Horror']]);
    });

    it('should replace the array rather than edit it in place', () => {
      // The parent binds to this input; mutating it would change the parent's
      // own array before anything was saved.
      const original = ['Sci-Fi', 'Horror'];
      component.values = original;

      component.remove('Sci-Fi');

      expect(original).toEqual(['Sci-Fi', 'Horror']);
    });
  });

  describe('creating a new genre', () => {
    it('should open the create dialog on the sentinel option', () => {
      component.onOptionSelected('__create_new__');

      expect(dialog.open).toHaveBeenCalled();
      // And the sentinel itself must never become a chip.
      expect(component.values).toEqual(['Sci-Fi']);
    });

    it('should add what the dialog returned', () => {
      dialog.open.and.returnValue({ afterClosed: () => of('Film Noir') } as any);

      component.onOptionSelected('__create_new__');

      expect(component.values).toEqual(['Sci-Fi', 'Film Noir']);
    });

    it('should add nothing when the dialog is cancelled', () => {
      dialog.open.and.returnValue({ afterClosed: () => of(null) } as any);

      component.onOptionSelected('__create_new__');

      expect(emitted).toEqual([]);
    });
  });

  describe('defaults', () => {
    it('should show the dropdown and allow creating', () => {
      // The bulk dialogs turn the dropdown off; everything else wants it.
      expect(component.showAddDropdown).toBe(true);
      expect(component.allowCreate).toBe(true);
    });

    it('should label itself as genres unless told otherwise', () => {
      expect(component.label).toBe('Genres');
      expect(component.addLabel).toBe('Add a Genre');
    });
  });
});

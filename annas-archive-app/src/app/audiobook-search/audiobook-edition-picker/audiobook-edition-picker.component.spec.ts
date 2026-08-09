import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import {
  AudiobookEditionPickerComponent, AudiobookEditionPickerData
} from './audiobook-edition-picker.component';
import { AudiobookSearchResult } from '../../services/audiobook-request-api.service';

/**
 * Characterization tests for the edition disambiguator.
 *
 * It exists because an AI suggestion can match several real Audible editions,
 * and they are not interchangeable — narrator and abridgement in particular are
 * what someone is choosing between. So the tests care most about the dialog
 * refusing to guess: no pre-selection, and nothing closed without a real pick.
 */
describe('AudiobookEditionPickerComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobookEditionPickerComponent>;
  let component: AudiobookEditionPickerComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<AudiobookEditionPickerComponent>>;

  function choice(over: Partial<AudiobookSearchResult> = {}): AudiobookSearchResult {
    return {
      asin: 'A1', title: 'Dune', authors: ['Frank Herbert'], narrators: ['A Narrator'],
      genres: [], series: [], availability: 'available', ...over
    } as AudiobookSearchResult;
  }

  async function build(over: Partial<AudiobookEditionPickerData> = {}): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<AudiobookEditionPickerComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [AudiobookEditionPickerComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            suggestedTitle: 'Dune',
            choices: [choice({ asin: 'A1' }), choice({ asin: 'A2', narrators: ['Another'] })],
            ...over
          } as AudiobookEditionPickerData
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobookEditionPickerComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  afterEach(() => fixture.destroy());

  describe('refusing to guess', () => {
    it('should pre-select nothing', () => {
      // Editions differ by narrator and abridgement — silently picking one is
      // exactly what this dialog exists to prevent.
      expect(component.selectedAsin).toBeNull();
    });

    it('should not close without a pick', () => {
      component.choose();

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should ignore a selection that matches no edition', () => {
      component.selectedAsin = 'not-a-real-asin';

      component.choose();

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should hand back the whole edition, not just its id', () => {
      component.selectedAsin = 'A2';

      component.choose();

      expect(dialogRef.close).toHaveBeenCalledWith(component.data.choices[1]);
    });

    it('should hand back nothing on cancel', () => {
      component.cancel();

      expect(dialogRef.close).toHaveBeenCalledWith(undefined);
    });
  });

  describe('the runtime label', () => {
    it('should read as hours and minutes past an hour', () => {
      expect(component.runtimeLabel(125)).toBe('2h 5m');
    });

    it('should read as minutes below an hour', () => {
      expect(component.runtimeLabel(45)).toBe('45m');
    });

    it('should show a whole number of hours as such', () => {
      expect(component.runtimeLabel(120)).toBe('2h 0m');
    });

    it('should say nothing when the runtime is unknown', () => {
      // A row reading "0m" would look like a broken edition rather than a
      // missing field.
      expect(component.runtimeLabel(undefined)).toBeNull();
      expect(component.runtimeLabel(0)).toBeNull();
    });
  });

  describe('the availability note', () => {
    it('should flag an edition already in the library', () => {
      expect(component.availabilityLabel(choice({ availability: 'owned' })))
        .toBe('Already in your library');
    });

    it('should flag one already requested', () => {
      expect(component.availabilityLabel(choice({ availability: 'requested' })))
        .toBe('Already requested');
    });

    it('should say nothing about an ordinary edition', () => {
      // Most rows are available; a note on every one would be noise.
      expect(component.availabilityLabel(choice({ availability: 'available' }))).toBeNull();
    });
  });
});

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import { AudiobookReleasePickerComponent } from './audiobook-release-picker.component';
import {
  AudiobookReleaseOption, AudiobookRequestApiService
} from '../../services/audiobook-request-api.service';

/**
 * Characterization tests for the manual release picker.
 *
 * Sending a release to the download client is not undoable from here, so the
 * dialog will not close itself while one is in flight and will not send without
 * a selection. The size formatting gets its own coverage because it is the main
 * thing a person compares releases on.
 */
describe('AudiobookReleasePickerComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobookReleasePickerComponent>;
  let component: AudiobookReleasePickerComponent;
  let api: jasmine.SpyObj<AudiobookRequestApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<AudiobookReleasePickerComponent>>;

  function release(over: Partial<AudiobookReleaseOption> = {}): AudiobookReleaseOption {
    return {
      selectionToken: 'tok-1', expiresAt: '', title: 'Dune [M4B]', source: 'Indexer',
      downloadType: 'torrent', size: 500 * 1024 ** 2, grabs: 10, files: 1, score: 100, ...over
    };
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<AudiobookRequestApiService>('AudiobookRequestApiService', ['grabRelease']);
    api.grabRelease.and.returnValue(of({ listenarrId: 1, accepted: true } as any));
    dialogRef = jasmine.createSpyObj<MatDialogRef<AudiobookReleasePickerComponent>>('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [AudiobookReleasePickerComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: { listenarrId: 7, asin: 'A1', title: 'Dune', releases: [release()] }
        },
        { provide: AudiobookRequestApiService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobookReleasePickerComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  describe('grabbing', () => {
    it('should send the chosen release for this book', () => {
      component.selectedToken = 'tok-1';

      component.grab();

      expect(api.grabRelease).toHaveBeenCalledWith(7, 'tok-1');
      expect(dialogRef.close).toHaveBeenCalled();
    });

    it('should do nothing with nothing chosen', () => {
      component.grab();

      expect(api.grabRelease).not.toHaveBeenCalled();
    });

    it('should not send the same release twice', () => {
      api.grabRelease.and.returnValue(new Observable<any>(() => {}));
      component.selectedToken = 'tok-1';

      component.grab();
      component.grab();

      expect(api.grabRelease).toHaveBeenCalledTimes(1);
      expect(component.busy).toBe(true);
    });

    it('should surface the download client\'s own reason for refusing', () => {
      api.grabRelease.and.returnValue(throwError(() => ({ error: { error: 'Selection expired' } })));
      component.selectedToken = 'tok-1';

      component.grab();

      expect(component.error).toBe('Selection expired');
      expect(component.busy).toBe(false);
      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should fall back to its own wording when there is no reason', () => {
      api.grabRelease.and.returnValue(throwError(() => new Error('500')));
      component.selectedToken = 'tok-1';

      component.grab();

      expect(component.error).toContain('could not be sent');
    });
  });

  describe('closing', () => {
    it('should refuse to close while a grab is in flight', () => {
      api.grabRelease.and.returnValue(new Observable<any>(() => {}));
      component.selectedToken = 'tok-1';
      component.grab();

      component.cancel();

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should hand back nothing on cancel', () => {
      component.cancel();

      expect(dialogRef.close).toHaveBeenCalledWith(undefined);
    });

    it('should not let the backdrop dismiss it', () => {
      expect(dialogRef.disableClose).toBe(true);
    });
  });

  describe('the size label', () => {
    it('should scale the unit to the size', () => {
      expect(component.sizeLabel(500)).toBe('500 B');
      expect(component.sizeLabel(2 * 1024)).toBe('2 KB');
      expect(component.sizeLabel(350 * 1024 ** 2)).toBe('350 MB');
      expect(component.sizeLabel(2.5 * 1024 ** 3)).toBe('2.5 GB');
    });

    it('should show a decimal only where it distinguishes anything', () => {
      // Whole megabytes are enough to compare audiobook releases by; a decimal
      // gigabyte is not.
      expect(component.sizeLabel(1024 ** 2 * 1.5)).toBe('2 MB');
      expect(component.sizeLabel(1024 ** 3 * 1.5)).toBe('1.5 GB');
    });

    it('should say so rather than print a zero for a release with no size', () => {
      expect(component.sizeLabel(0)).toBe('Size unknown');
      expect(component.sizeLabel(undefined as unknown as number)).toBe('Size unknown');
    });

    it('should not run off the end of its units', () => {
      expect(component.sizeLabel(1024 ** 6)).toContain('TB');
    });
  });
});

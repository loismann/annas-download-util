import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import { AudiobookSeriesRequestDialogComponent } from './audiobook-series-request-dialog.component';
import {
  AudiobookRequestApiService, AudiobookSeriesMemberPreview, AudiobookSeriesPreview
} from '../../services/audiobook-request-api.service';

/**
 * Characterization tests for the series request dialog.
 *
 * This is the only place in the app where one click can request a dozen books,
 * so the request ceiling and what gets pre-checked are the whole point. The
 * dialog also refuses to close itself while a request is in flight, which the
 * tests cover because it is the only protection against a half-sent batch.
 */
describe('AudiobookSeriesRequestDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobookSeriesRequestDialogComponent>;
  let component: AudiobookSeriesRequestDialogComponent;
  let api: jasmine.SpyObj<AudiobookRequestApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<AudiobookSeriesRequestDialogComponent>>;

  function member(over: Partial<AudiobookSeriesMemberPreview> = {}): AudiobookSeriesMemberPreview {
    return { classification: 'requestable', title: 'Book', asin: 'A1', ...over };
  }

  async function build(over: Partial<AudiobookSeriesPreview> = {}): Promise<void> {
    api = jasmine.createSpyObj<AudiobookRequestApiService>('AudiobookRequestApiService', ['confirmSeries']);
    api.confirmSeries.and.returnValue(of({
      seriesAsin: 'S1', requestedCount: 1, alreadyExistedCount: 0, failedCount: 0, outcomes: []
    }));
    dialogRef = jasmine.createSpyObj<MatDialogRef<AudiobookSeriesRequestDialogComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [AudiobookSeriesRequestDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            previewToken: 'tok', expiresAt: '', seriesAsin: 'S1', seriesName: 'Dune', region: 'us',
            ownedCount: 0, requestedCount: 0, requestableCount: 1, unavailableCount: 0,
            requestCeiling: 5, exceedsCeiling: false,
            members: [member()], ...over
          } as AudiobookSeriesPreview
        },
        { provide: AudiobookRequestApiService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobookSeriesRequestDialogComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  describe('what starts checked', () => {
    it('should pre-check everything requestable', () => {
      // The common case is "yes, all of them" — one confirmation, not a dozen
      // ticks.
      expect(component.selectedAsins).toEqual(['A1']);
    });

    it('should leave alone what cannot be requested', async () => {
      await build({
        members: [
          member({ asin: 'owned', classification: 'owned' }),
          member({ asin: 'already', classification: 'requested' }),
          member({ asin: 'vague', classification: 'ambiguous' }),
          member({ asin: 'gone', classification: 'unavailable' }),
          member({ asin: 'yes', classification: 'requestable' })
        ]
      });

      expect(component.selectedAsins).toEqual(['yes']);
    });

    it('should skip a requestable book with no id to request by', async () => {
      await build({ members: [member({ asin: undefined })] });

      expect(component.selectedAsins).toEqual([]);
      expect(component.isSelectable(member({ asin: undefined }))).toBe(false);
    });

    it('should stop pre-checking at the ceiling', () => {
      // The ceiling has to be visible rather than a surprise at confirm time.
      const members = Array.from({ length: 10 }, (_, i) => member({ asin: `A${i}` }));
      return build({ members, requestCeiling: 3 }).then(() => {
        expect(component.selectedAsins.length).toBe(3);
        expect(component.overCeiling).toBe(false);
      });
    });

    it('should notice when the user goes past the ceiling', async () => {
      await build({
        members: [member({ asin: 'A1' }), member({ asin: 'A2' }), member({ asin: 'A3' })],
        requestCeiling: 2
      });
      expect(component.overCeiling).toBe(false);

      component.selected['A3'] = true;

      expect(component.overCeiling).toBe(true);
    });
  });

  describe('the per-book labels', () => {
    it('should say plainly what will happen to each book', () => {
      expect(component.classificationLabel(member({ classification: 'owned' }))).toBe('In your library');
      expect(component.classificationLabel(member({ classification: 'requested' }))).toBe('Already requested');
      expect(component.classificationLabel(member({ classification: 'requestable' }))).toBe('Will be requested');
      expect(component.classificationLabel(member({ classification: 'ambiguous' }))).toBe('Needs a manual search');
      expect(component.classificationLabel(member({ classification: 'unavailable' }))).toBe('Not available');
    });

    it('should show an unknown classification rather than a blank', () => {
      expect(component.classificationLabel(member({ classification: 'something-new' as never })))
        .toBe('something-new');
    });
  });

  describe('confirming', () => {
    it('should send the token, the picks and whether the ceiling was passed', () => {
      component.confirm();

      expect(api.confirmSeries).toHaveBeenCalledWith('tok', ['A1'], false);
    });

    it('should close with the result when everything went through', () => {
      component.confirm();

      expect(dialogRef.close).toHaveBeenCalled();
      expect(component.busy).toBe(false);
    });

    it('should stay open on a partial failure so the outcomes can be read', () => {
      // Closing would hide which books failed and why.
      api.confirmSeries.and.returnValue(of({
        seriesAsin: 'S1', requestedCount: 2, alreadyExistedCount: 0, failedCount: 1, outcomes: []
      }));

      component.confirm();

      expect(dialogRef.close).not.toHaveBeenCalled();
      expect(component.result?.failedCount).toBe(1);
    });

    it('should do nothing with nothing selected', async () => {
      await build({ members: [member({ classification: 'owned' })] });

      component.confirm();

      expect(api.confirmSeries).not.toHaveBeenCalled();
    });

    it('should not send the batch twice', () => {
      api.confirmSeries.and.returnValue(new Observable<any>(() => {}));

      component.confirm();
      component.confirm();

      expect(api.confirmSeries).toHaveBeenCalledTimes(1);
      expect(component.busy).toBe(true);
    });

    it('should surface the server\'s own reason for refusing', () => {
      api.confirmSeries.and.returnValue(throwError(() => ({ error: { error: 'Preview expired' } })));

      component.confirm();

      expect(component.error).toBe('Preview expired');
      expect(component.busy).toBe(false);
    });

    it('should fall back to its own wording when there is no reason', () => {
      api.confirmSeries.and.returnValue(throwError(() => new Error('500')));

      component.confirm();

      expect(component.error).toContain('could not be completed');
    });
  });

  describe('closing', () => {
    it('should refuse to close mid-request', () => {
      // Half a batch sent with the dialog gone would leave no way to see what
      // landed.
      api.confirmSeries.and.returnValue(new Observable<any>(() => {}));
      component.confirm();

      component.close();

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should hand back the result when closed after a partial failure', () => {
      api.confirmSeries.and.returnValue(of({
        seriesAsin: 'S1', requestedCount: 1, alreadyExistedCount: 0, failedCount: 1, outcomes: []
      }));
      component.confirm();

      component.close();

      expect(dialogRef.close).toHaveBeenCalledWith(component.result!);
    });

    it('should hand back nothing when closed without requesting', () => {
      component.close();

      expect(dialogRef.close).toHaveBeenCalledWith(undefined);
    });

    it('should not let the backdrop dismiss it', () => {
      // Same reason as the mid-request guard.
      expect(dialogRef.disableClose).toBe(true);
    });
  });
});

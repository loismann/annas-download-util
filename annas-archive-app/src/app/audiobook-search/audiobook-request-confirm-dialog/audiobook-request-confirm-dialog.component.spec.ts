import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, of, throwError } from 'rxjs';

import { AudiobookRequestConfirmDialogComponent } from './audiobook-request-confirm-dialog.component';
import {
  AudiobookRequestApiService, AudiobookRequestPreview
} from '../../services/audiobook-request-api.service';

/**
 * Characterization tests for the request confirmation dialog.
 *
 * The button label is the interesting part and it is deliberate: a book no
 * indexer carries must not be one OK click away from looking like an ordinary
 * request. The acknowledgement flag that goes with it has to mirror what the
 * dialog actually showed, because the server refuses the confirm without it.
 */
describe('AudiobookRequestConfirmDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobookRequestConfirmDialogComponent>;
  let component: AudiobookRequestConfirmDialogComponent;
  let api: jasmine.SpyObj<AudiobookRequestApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<AudiobookRequestConfirmDialogComponent>>;

  async function build(over: Partial<AudiobookRequestPreview> = {}): Promise<void> {
    api = jasmine.createSpyObj<AudiobookRequestApiService>('AudiobookRequestApiService', ['confirmRequest']);
    api.confirmRequest.and.returnValue(of({ listenarrId: 1 } as any));
    dialogRef = jasmine.createSpyObj<MatDialogRef<AudiobookRequestConfirmDialogComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [AudiobookRequestConfirmDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            previewToken: 'tok', expiresAt: '', asin: 'A1', title: 'Dune',
            authors: ['Frank Herbert'], narrators: ['A Narrator'], abridged: false,
            qualityProfile: 'Standard', autoSearch: true, autoSearchReason: 'releases found',
            alreadyRequested: false, releasesAvailable: true, ...over
          } as AudiobookRequestPreview
        },
        { provide: AudiobookRequestApiService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobookRequestConfirmDialogComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  describe('naming the consequence', () => {
    it('should offer an ordinary request when releases exist', () => {
      expect(component.confirmLabel).toBe('Add monitored request');
    });

    it('should say what adding really means when nothing carries the book', async () => {
      // Otherwise this looks identical to a request that will actually arrive.
      await build({ releasesAvailable: false });

      expect(component.confirmLabel).toBe('Add anyway to keep watching');
    });

    it('should say so when the book is already requested by someone else', async () => {
      await build({ alreadyRequested: true });

      expect(component.confirmLabel).toBe('Add me as requester');
    });

    it('should let the no-releases warning win over already-requested', async () => {
      // The more surprising fact is the one to name.
      await build({ releasesAvailable: false, alreadyRequested: true });

      expect(component.confirmLabel).toBe('Add anyway to keep watching');
    });
  });

  describe('confirming', () => {
    it('should send the token without an acknowledgement when releases exist', () => {
      component.confirm();

      expect(api.confirmRequest).toHaveBeenCalledWith('tok', false);
      expect(dialogRef.close).toHaveBeenCalled();
    });

    it('should acknowledge the warning it actually showed', async () => {
      // The server refuses the confirm unless this comes back set, so it has to
      // mirror the dialog rather than be decided independently.
      await build({ releasesAvailable: false });

      component.confirm();

      expect(api.confirmRequest).toHaveBeenCalledWith('tok', true);
    });

    it('should not send the request twice', () => {
      api.confirmRequest.and.returnValue(new Observable<any>(() => {}));

      component.confirm();
      component.confirm();

      expect(api.confirmRequest).toHaveBeenCalledTimes(1);
      expect(component.busy).toBe(true);
    });

    it('should surface the server\'s own reason for refusing', () => {
      api.confirmRequest.and.returnValue(throwError(() => ({ error: { error: 'Preview expired' } })));

      component.confirm();

      expect(component.error).toBe('Preview expired');
      expect(component.busy).toBe(false);
      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should fall back to its own wording when there is no reason', () => {
      api.confirmRequest.and.returnValue(throwError(() => new Error('500')));

      component.confirm();

      expect(component.error).toContain('could not be confirmed');
    });
  });

  describe('closing', () => {
    it('should refuse to close mid-request', () => {
      api.confirmRequest.and.returnValue(new Observable<any>(() => {}));
      component.confirm();

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
});

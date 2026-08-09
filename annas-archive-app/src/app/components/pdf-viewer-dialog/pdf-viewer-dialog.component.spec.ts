import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Subject, of, throwError } from 'rxjs';

import { PdfViewerDialogComponent } from './pdf-viewer-dialog.component';
import { LibraryApiService } from '../../services/library-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the PDF reader dialog.
 *
 * The resume position lives in localStorage, which can throw outright in
 * private browsing or at quota — so every path through it is covered here:
 * losing someone's page is a nuisance, but a viewer that refuses to open
 * because storage was unavailable would be a defect.
 */
describe('PdfViewerDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<PdfViewerDialogComponent>;
  let component: PdfViewerDialogComponent;
  let api: jasmine.SpyObj<LibraryApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<PdfViewerDialogComponent>>;

  const KEY = 'pdf-viewer-last-page:manual.pdf';

  beforeEach(async () => {
    localStorage.removeItem(KEY);

    api = jasmine.createSpyObj<LibraryApiService>('LibraryApiService', ['getLibraryBookFile']);
    api.getLibraryBookFile.and.returnValue(of(new Blob(['%PDF-'])));
    dialogRef = jasmine.createSpyObj<MatDialogRef<PdfViewerDialogComponent>>('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [PdfViewerDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: { title: 'A Manual', fileName: 'manual.pdf' } },
        { provide: LibraryApiService, useValue: api },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PdfViewerDialogComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    localStorage.removeItem(KEY);
    fixture.destroy();
  });

  describe('loading the file', () => {
    it('should fetch the book and stop the spinner', () => {
      component.ngOnInit();

      expect(api.getLibraryBookFile).toHaveBeenCalledWith('manual.pdf');
      expect(component.pdfSrc).toBeTruthy();
      expect(component.isLoading).toBe(false);
      expect(component.hasError).toBe(false);
    });

    it('should show an error rather than an endless spinner', () => {
      api.getLibraryBookFile.and.returnValue(throwError(() => new Error('404')));

      component.ngOnInit();

      expect(component.hasError).toBe(true);
      expect(component.isLoading).toBe(false);
    });

    it('should not apply a file that arrives after the dialog closed', () => {
      const late = new Subject<Blob>();
      api.getLibraryBookFile.and.returnValue(late.asObservable());
      component.ngOnInit();

      fixture.destroy();
      late.next(new Blob(['%PDF-']));

      expect(component.pdfSrc).toBeNull();
    });
  });

  describe('the resume position', () => {
    it('should open on page one for a book never opened', () => {
      component.ngOnInit();

      expect(component.currentPage).toBe(1);
    });

    it('should open on the page it was left at', () => {
      localStorage.setItem(KEY, '42');

      component.ngOnInit();

      expect(component.currentPage).toBe(42);
    });

    it('should remember each page turn', () => {
      component.ngOnInit();

      component.onPageChange(7);

      expect(component.currentPage).toBe(7);
      expect(localStorage.getItem(KEY)).toBe('7');
    });

    it('should ignore a page change with no page', () => {
      component.ngOnInit();
      component.onPageChange(5);

      component.onPageChange(undefined);

      expect(component.currentPage).toBe(5);
    });

    it('should key the position per book', () => {
      // One stored page shared across books would drop everyone at the same
      // place in whatever they opened next.
      component.ngOnInit();
      component.onPageChange(9);

      expect(localStorage.getItem(KEY)).toBe('9');
      expect(localStorage.getItem('pdf-viewer-last-page:other.pdf')).toBeNull();
    });

    it('should ignore rubbish in storage', () => {
      localStorage.setItem(KEY, 'not-a-number');

      component.ngOnInit();

      expect(component.currentPage).toBe(1);
    });

    it('should ignore a stored page of nought or less', () => {
      localStorage.setItem(KEY, '0');

      component.ngOnInit();

      expect(component.currentPage).toBe(1);
    });

    it('should still open when storage cannot be read', () => {
      // Private browsing throws on access rather than returning null.
      spyOn(localStorage, 'getItem').and.throwError('SecurityError');

      component.ngOnInit();

      expect(component.currentPage).toBe(1);
      expect(component.pdfSrc).toBeTruthy();
    });

    it('should keep reading when the page cannot be saved', () => {
      // Losing the resume position is not worth interrupting someone over.
      spyOn(localStorage, 'setItem').and.throwError('QuotaExceededError');
      component.ngOnInit();

      expect(() => component.onPageChange(3)).not.toThrow();
      expect(component.currentPage).toBe(3);
    });
  });

  describe('fullscreen and closing', () => {
    it('should close on Escape', () => {
      const event = new KeyboardEvent('keydown', { key: 'Escape' });

      component.handleEscapeKey(event);

      expect(dialogRef.close).toHaveBeenCalled();
    });

    it('should let Escape leave fullscreen without also closing the dialog', () => {
      // The browser already exits fullscreen on Escape; closing on top of that
      // would make one keypress do two things.
      spyOnProperty(document, 'fullscreenElement').and.returnValue(document.body);

      component.handleEscapeKey(new KeyboardEvent('keydown', { key: 'Escape' }));

      expect(dialogRef.close).not.toHaveBeenCalled();
    });

    it('should follow the browser\'s own fullscreen state', () => {
      // Not a flag it sets itself: the user can leave fullscreen by ways this
      // component never hears about directly.
      const el = spyOnProperty(document, 'fullscreenElement');

      el.and.returnValue(document.body);
      component.handleFullscreenChange();
      expect(component.isFullscreen).toBe(true);

      el.and.returnValue(null);
      component.handleFullscreenChange();
      expect(component.isFullscreen).toBe(false);
    });

    it('should exit fullscreen when already in it', () => {
      spyOnProperty(document, 'fullscreenElement').and.returnValue(document.body);
      const exit = spyOn(document, 'exitFullscreen').and.resolveTo();

      component.toggleFullscreen();

      expect(exit).toHaveBeenCalled();
    });

    it('should close on the close button', () => {
      component.onClose();

      expect(dialogRef.close).toHaveBeenCalled();
    });
  });
});

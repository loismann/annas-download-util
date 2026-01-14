import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DeleteCacheDialogComponent, DeleteCacheDialogData } from './delete-cache-dialog.component';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('DeleteCacheDialogComponent', () => {
  let component: DeleteCacheDialogComponent;
  let fixture: ComponentFixture<DeleteCacheDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<DeleteCacheDialogComponent>>;
  let mockOnExport: jasmine.Spy;

  const mockDialogData: DeleteCacheDialogData = {
    bookTitle: 'Test Book',
    summaryCount: 5,
    onExport: jasmine.createSpy('onExport').and.returnValue(Promise.resolve())
  };

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    mockOnExport = mockDialogData.onExport as jasmine.Spy;
    // Reset the spy to return a resolved promise before each test
    mockOnExport.calls.reset();
    mockOnExport.and.returnValue(Promise.resolve());

    await TestBed.configureTestingModule({
      imports: [
        DeleteCacheDialogComponent,
        NoopAnimationsModule
      ],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DeleteCacheDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should display the book title', () => {
    const compiled = fixture.nativeElement;
    expect(compiled.textContent).toContain('Test Book');
  });

  it('should display the summary count', () => {
    const compiled = fixture.nativeElement;
    expect(compiled.textContent).toContain('5 chapter summaries');
  });

  describe('onCancel', () => {
    it('should close dialog with "cancel" result', () => {
      component.onCancel();
      expect(mockDialogRef.close).toHaveBeenCalledWith('cancel');
    });
  });

  describe('onDelete', () => {
    it('should close dialog with "delete" result', () => {
      component.onDelete();
      expect(mockDialogRef.close).toHaveBeenCalledWith('delete');
    });
  });

  describe('onExport', () => {
    it('should call the export callback', async () => {
      await component.onExport();
      expect(mockOnExport).toHaveBeenCalled();
    });

    it('should set exporting to true while exporting', async () => {
      const exportPromise = component.onExport();
      expect(component.exporting).toBe(true);
      await exportPromise;
      expect(component.exporting).toBe(false);
    });

    it('should set exported to true after successful export', async () => {
      await component.onExport();
      expect(component.exported).toBe(true);
    });

    it('should handle export errors', async () => {
      mockOnExport.and.returnValue(Promise.reject(new Error('Export failed')));

      try {
        await component.onExport();
      } catch {
        // Expected to throw
      }

      expect(component.exporting).toBe(false);
    });
  });

  describe('when summaryCount is 0', () => {
    beforeEach(async () => {
      await TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [
          DeleteCacheDialogComponent,
          NoopAnimationsModule
        ],
        providers: [
          { provide: MatDialogRef, useValue: mockDialogRef },
          { provide: MAT_DIALOG_DATA, useValue: { ...mockDialogData, summaryCount: 0 } }
        ]
      }).compileComponents();

      fixture = TestBed.createComponent(DeleteCacheDialogComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
    });

    it('should not show export section when no summaries', () => {
      const compiled = fixture.nativeElement;
      expect(compiled.querySelector('.export-section')).toBeFalsy();
    });
  });
});

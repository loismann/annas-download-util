import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RemoveFromReaderDialogComponent, RemoveFromReaderDialogData } from './remove-from-reader-dialog.component';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';

describe('RemoveFromReaderDialogComponent', () => {
  let component: RemoveFromReaderDialogComponent;
  let fixture: ComponentFixture<RemoveFromReaderDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<RemoveFromReaderDialogComponent>>;

  const mockDialogData: RemoveFromReaderDialogData = {
    bookTitle: 'Test Book'
  };

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [
        RemoveFromReaderDialogComponent,
        NoopAnimationsModule
      ],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(RemoveFromReaderDialogComponent);
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

  it('should display preserved data message', () => {
    const compiled = fixture.nativeElement;
    expect(compiled.textContent).toContain('Your data will be preserved');
  });

  it('should list items that will be preserved', () => {
    const compiled = fixture.nativeElement;
    expect(compiled.textContent).toContain('All chapter summaries');
    expect(compiled.textContent).toContain('All section summaries');
    expect(compiled.textContent).toContain('Character relationship graph');
    expect(compiled.textContent).toContain('Vocabulary and flashcards');
  });

  it('should display re-add message', () => {
    const compiled = fixture.nativeElement;
    expect(compiled.textContent).toContain('re-add this book');
  });

  describe('onCancel', () => {
    it('should close dialog with "cancel" result', () => {
      component.onCancel();
      expect(mockDialogRef.close).toHaveBeenCalledWith('cancel');
    });
  });

  describe('onRemove', () => {
    it('should close dialog with "remove" result', () => {
      component.onRemove();
      expect(mockDialogRef.close).toHaveBeenCalledWith('remove');
    });
  });

  describe('button visibility', () => {
    it('should have Cancel button', () => {
      const compiled = fixture.nativeElement;
      const cancelButton = compiled.querySelector('.cancel-button');
      expect(cancelButton).toBeTruthy();
      expect(cancelButton.textContent).toContain('Cancel');
    });

    it('should have Remove from Reader button', () => {
      const compiled = fixture.nativeElement;
      const removeButton = compiled.querySelector('.remove-button');
      expect(removeButton).toBeTruthy();
      expect(removeButton.textContent).toContain('Remove from Reader');
    });
  });
});

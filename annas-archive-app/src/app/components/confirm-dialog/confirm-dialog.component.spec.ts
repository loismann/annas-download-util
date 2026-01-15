import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { ConfirmDialogComponent, ConfirmDialogData } from './confirm-dialog.component';

describe('ConfirmDialogComponent', () => {
  let component: ConfirmDialogComponent;
  let fixture: ComponentFixture<ConfirmDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<ConfirmDialogComponent>>;

  const createComponent = (data: Partial<ConfirmDialogData> = {}) => {
    const defaultData: ConfirmDialogData = {
      title: 'Test Title',
      message: 'Test Message',
      ...data
    };

    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);

    TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: defaultData }
      ]
    });

    fixture = TestBed.createComponent(ConfirmDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  };

  it('should create', () => {
    createComponent();
    expect(component).toBeTruthy();
  });

  it('should use default confirm text when not provided', () => {
    createComponent({ title: 'Test', message: 'Message' });
    expect(component.data.confirmText).toBe('Confirm');
  });

  it('should use default cancel text when not provided', () => {
    createComponent({ title: 'Test', message: 'Message' });
    expect(component.data.cancelText).toBe('Cancel');
  });

  it('should use custom confirm text when provided', () => {
    createComponent({ title: 'Test', message: 'Message', confirmText: 'Delete' });
    expect(component.data.confirmText).toBe('Delete');
  });

  it('should use custom cancel text when provided', () => {
    createComponent({ title: 'Test', message: 'Message', cancelText: 'Go Back' });
    expect(component.data.cancelText).toBe('Go Back');
  });

  it('should default isDanger to true when not provided', () => {
    createComponent({ title: 'Test', message: 'Message' });
    expect(component.data.isDanger).toBe(true);
  });

  it('should preserve isDanger=false when explicitly set', () => {
    createComponent({ title: 'Test', message: 'Message', isDanger: false });
    expect(component.data.isDanger).toBe(false);
  });

  it('should close dialog with true when confirmed', () => {
    createComponent();
    component.onConfirm();
    expect(mockDialogRef.close).toHaveBeenCalledWith(true);
  });

  it('should close dialog with false when cancelled', () => {
    createComponent();
    component.onCancel();
    expect(mockDialogRef.close).toHaveBeenCalledWith(false);
  });

  it('should display title and message from data', () => {
    createComponent({ title: 'Delete Item?', message: 'This action cannot be undone.' });
    expect(component.data.title).toBe('Delete Item?');
    expect(component.data.message).toBe('This action cannot be undone.');
  });
});

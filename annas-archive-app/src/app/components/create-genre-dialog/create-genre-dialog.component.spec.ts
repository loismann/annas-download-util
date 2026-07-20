import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { CreateGenreDialogComponent } from './create-genre-dialog.component';

describe('CreateGenreDialogComponent', () => {
  let component: CreateGenreDialogComponent;
  let fixture: ComponentFixture<CreateGenreDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<CreateGenreDialogComponent>>;

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [CreateGenreDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CreateGenreDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should initialize with empty genre name', () => {
    expect(component.genreName).toBe('');
  });

  describe('onSubmit', () => {
    it('should close dialog with trimmed genre name', () => {
      component.genreName = '  Science Fiction  ';
      component.onSubmit();

      expect(mockDialogRef.close).toHaveBeenCalledWith('Science Fiction');
    });

    it('should not close dialog when genre name is empty', () => {
      component.genreName = '';
      component.onSubmit();

      expect(mockDialogRef.close).not.toHaveBeenCalled();
    });

    it('should not close dialog when genre name is only whitespace', () => {
      component.genreName = '   ';
      component.onSubmit();

      expect(mockDialogRef.close).not.toHaveBeenCalled();
    });

    it('should close dialog with valid genre name', () => {
      component.genreName = 'Fantasy';
      component.onSubmit();

      expect(mockDialogRef.close).toHaveBeenCalledWith('Fantasy');
    });
  });

  describe('onCancel', () => {
    it('should close dialog with null', () => {
      component.onCancel();

      expect(mockDialogRef.close).toHaveBeenCalledWith(null);
    });
  });
});

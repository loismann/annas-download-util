import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { BulkEditDialogComponent, BookBulkEditDialogData } from './bulk-edit-dialog.component';

describe('BulkEditDialogComponent', () => {
  let component: BulkEditDialogComponent;
  let fixture: ComponentFixture<BulkEditDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<BulkEditDialogComponent>>;

  const createComponent = (data: Partial<BookBulkEditDialogData> = {}) => {
    const defaultData: BookBulkEditDialogData = {
      bookFileNames: ['book1.epub', 'book2.epub'],
      bookTitles: ['Book One', 'Book Two'],
      availableGenres: ['Fiction', 'Non-Fiction', 'Science Fiction'],
      ...data
    };

    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);

    TestBed.configureTestingModule({
      imports: [BulkEditDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: defaultData }
      ]
    });

    fixture = TestBed.createComponent(BulkEditDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  };

  it('should create', () => {
    createComponent();
    expect(component).toBeTruthy();
  });

  it('should initialize genres from data', () => {
    createComponent({ availableGenres: ['Horror', 'Romance'] });
    expect(component.genres).toEqual(['Horror', 'Romance']);
  });

  it('should default to empty genres when not provided', () => {
    createComponent({ availableGenres: undefined });
    expect(component.genres).toEqual([]);
  });

  describe('addTag', () => {
    beforeEach(() => createComponent());

    it('should add a new tag', () => {
      const mockEvent = {
        value: 'New Tag',
        chipInput: { clear: jasmine.createSpy('clear') }
      };

      component.addTag(mockEvent as any);

      expect(component.tags).toContain('New Tag');
      expect(mockEvent.chipInput.clear).toHaveBeenCalled();
    });

    it('should trim tag value', () => {
      const mockEvent = {
        value: '  Trimmed Tag  ',
        chipInput: { clear: jasmine.createSpy('clear') }
      };

      component.addTag(mockEvent as any);

      expect(component.tags).toContain('Trimmed Tag');
    });

    it('should not add duplicate tags', () => {
      component.tags = ['Existing'];
      const mockEvent = {
        value: 'Existing',
        chipInput: { clear: jasmine.createSpy('clear') }
      };

      component.addTag(mockEvent as any);

      expect(component.tags.filter(t => t === 'Existing').length).toBe(1);
    });

    it('should not add empty tags', () => {
      const mockEvent = {
        value: '   ',
        chipInput: { clear: jasmine.createSpy('clear') }
      };

      component.addTag(mockEvent as any);

      expect(component.tags.length).toBe(0);
    });
  });

  describe('removeTag', () => {
    beforeEach(() => createComponent());

    it('should remove existing tag', () => {
      component.tags = ['Tag1', 'Tag2', 'Tag3'];

      component.removeTag('Tag2');

      expect(component.tags).toEqual(['Tag1', 'Tag3']);
    });

    it('should do nothing when tag does not exist', () => {
      component.tags = ['Tag1', 'Tag2'];

      component.removeTag('NonExistent');

      expect(component.tags).toEqual(['Tag1', 'Tag2']);
    });
  });

  describe('addSelectedGenreAsTag', () => {
    beforeEach(() => createComponent());

    it('should add selected genre as tag', () => {
      component.selectedGenre = 'Science Fiction';

      component.addSelectedGenreAsTag();

      expect(component.tags).toContain('Science Fiction');
    });

    it('should not add if genre is empty', () => {
      component.selectedGenre = '';

      component.addSelectedGenreAsTag();

      expect(component.tags.length).toBe(0);
    });

    it('should not add Uncategorized as tag', () => {
      component.selectedGenre = 'Uncategorized';

      component.addSelectedGenreAsTag();

      expect(component.tags).not.toContain('Uncategorized');
    });

    it('should not add duplicate genre tag', () => {
      component.tags = ['Science Fiction'];
      component.selectedGenre = 'Science Fiction';

      component.addSelectedGenreAsTag();

      expect(component.tags.filter(t => t === 'Science Fiction').length).toBe(1);
    });
  });

  describe('onCancel', () => {
    it('should close dialog without result', () => {
      createComponent();

      component.onCancel();

      expect(mockDialogRef.close).toHaveBeenCalledWith();
    });
  });

  describe('onSave', () => {
    beforeEach(() => createComponent());

    it('should close dialog with empty result when nothing entered', () => {
      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith({});
    });

    it('should include authors when entered', () => {
      component.authorsInput = 'Author One, Author Two';

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({ authors: ['Author One', 'Author Two'] })
      );
    });

    it('should filter empty author names', () => {
      component.authorsInput = 'Author One, , Author Two, ';

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({ authors: ['Author One', 'Author Two'] })
      );
    });

    it('should include selected genre', () => {
      component.selectedGenre = 'Fiction';

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({ primaryGenre: 'Fiction' })
      );
    });

    it('should include tags when present', () => {
      component.tags = ['Tag1', 'Tag2'];

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({ tags: ['Tag1', 'Tag2'] })
      );
    });

    it('should include series when entered', () => {
      component.series = '  My Series  ';

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({ series: 'My Series' })
      );
    });

    it('should include all fields when all entered', () => {
      component.authorsInput = 'Author Name';
      component.selectedGenre = 'Fiction';
      component.tags = ['Adventure'];
      component.series = 'My Series';

      component.onSave();

      expect(mockDialogRef.close).toHaveBeenCalledWith({
        authors: ['Author Name'],
        primaryGenre: 'Fiction',
        tags: ['Adventure'],
        series: 'My Series'
      });
    });
  });
});

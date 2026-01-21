import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookEditDialogComponent, BookEditDialogData } from './book-edit-dialog.component';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { GenreMappingService } from '../../services/genre-mapping.service';
import { LibraryApiService } from '../../services/library-api.service';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';

describe('BookEditDialogComponent', () => {
  let component: BookEditDialogComponent;
  let fixture: ComponentFixture<BookEditDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<BookEditDialogComponent>>;
  let mockGenreMappingService: jasmine.SpyObj<GenreMappingService>;
  let mockLibraryApiService: jasmine.SpyObj<LibraryApiService>;
  let mockRouter: jasmine.SpyObj<Router>;

  const testDialogData: BookEditDialogData = {
    title: 'Test Book',
    authors: ['Test Author'],
    primaryGenre: 'Science Fiction',
    tags: ['test'],
    series: null,
    coverUrl: 'http://example.com/old-cover.jpg',
    availableGenres: ['Science Fiction', 'Fantasy', 'Mystery & Detective'],
    fileName: 'test-book.epub',
    format: 'EPUB',
    canSendToKindle: true,
    readerEnabled: false
  };

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    mockGenreMappingService = jasmine.createSpyObj('GenreMappingService', ['getStandardGenres']);
    mockLibraryApiService = jasmine.createSpyObj('LibraryApiService', [
      'deleteLibraryBook',
      'sendLibraryToKindle',
      'updateLibraryBookReaderEnabled',
      'getLibraryBookSummary'
    ]);
    mockLibraryApiService.getLibraryBookSummary.and.returnValue(of({ summary: null, source: null }));
    mockRouter = jasmine.createSpyObj('Router', ['navigate']);

    mockGenreMappingService.getStandardGenres.and.returnValue([
      'Science Fiction',
      'Fantasy',
      'Mystery & Detective'
    ]);

    await TestBed.configureTestingModule({
      imports: [BookEditDialogComponent, BrowserAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: testDialogData },
        { provide: GenreMappingService, useValue: mockGenreMappingService },
        { provide: LibraryApiService, useValue: mockLibraryApiService },
        { provide: Router, useValue: mockRouter }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(BookEditDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  describe('Cover Selection', () => {
    it('should return coverUrl in result when cover is selected', () => {
      // Arrange
      const newCoverUrl = 'http://example.com/new-cover.jpg';
      component.selectedCoverUrl = newCoverUrl;

      // Act
      component.onSave();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith(
        jasmine.objectContaining({
          coverUrl: newCoverUrl
        })
      );
    });

    it('should not return coverUrl when no new cover is selected', () => {
      // Arrange - selectedCoverUrl is null by default

      // Act
      component.onSave();

      // Assert
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.coverUrl).toBeNull();
    });

    it('should return coverUrl even when selected cover is same as original (for persistence)', () => {
      // Arrange - set selectedCoverUrl to same as original
      // This ensures covers are always persisted even if a previous save failed
      component.selectedCoverUrl = testDialogData.coverUrl || null;

      // Act
      component.onSave();

      // Assert - coverUrl should be returned even if same as original
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.coverUrl).toBe(testDialogData.coverUrl);
    });

    it('should update selectedCoverUrl when applyManualCoverUrl is called', () => {
      // Arrange
      const manualUrl = 'http://example.com/manual-cover.jpg';
      component.manualCoverUrl = manualUrl;

      // Act
      component.applyManualCoverUrl();

      // Assert
      expect(component.selectedCoverUrl).toBe(manualUrl);
      expect(component.manualCoverUrl).toBe(''); // Should be cleared
    });

    it('should not update selectedCoverUrl when applyManualCoverUrl is called with empty string', () => {
      // Arrange
      component.manualCoverUrl = '   '; // Whitespace only
      const originalSelectedUrl = component.selectedCoverUrl;

      // Act
      component.applyManualCoverUrl();

      // Assert
      expect(component.selectedCoverUrl).toBe(originalSelectedUrl);
    });

    it('should display original cover when no cover is selected', () => {
      // Arrange
      component.selectedCoverUrl = null;

      // Act & Assert
      expect(component.displayCoverUrl).toBe(testDialogData.coverUrl || component.placeholderUrl);
    });

    it('should display placeholder when no original cover and no selection', () => {
      // Arrange
      component.data.coverUrl = null;
      component.selectedCoverUrl = null;

      // Act & Assert
      expect(component.displayCoverUrl).toBe(component.placeholderUrl);
    });
  });

  describe('Cancel Behavior', () => {
    it('should close dialog without returning data when onCancel is called', () => {
      // Act
      component.onCancel();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith();
    });
  });

  describe('Save Behavior with All Fields', () => {
    it('should include all modified fields in result', () => {
      // Arrange
      component.title = 'Updated Title';
      component.authorsInput = 'Updated Author';
      component.tags = ['Science Fiction', 'updated-tag']; // Genre is now a tag
      component.series = 'Updated Series';
      component.selectedCoverUrl = 'http://example.com/new-cover.jpg';
      component.selectedOwner = null;

      // Act
      component.onSave();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith({
        primaryGenre: 'Science Fiction', // First genre tag becomes primary
        tags: ['Science Fiction', 'updated-tag'],
        series: 'Updated Series',
        title: 'Updated Title',
        authors: ['Updated Author'],
        coverUrl: 'http://example.com/new-cover.jpg',
        owner: null
      });
    });

    it('should set primaryGenre to Uncategorized when no genre tags', () => {
      // Arrange
      component.title = 'Test Book';
      component.authorsInput = 'Test Author';
      component.tags = ['non-genre-tag'];
      component.series = null;
      component.selectedCoverUrl = null;
      component.selectedOwner = null;

      // Act
      component.onSave();

      // Assert
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.primaryGenre).toBe('Uncategorized');
    });
  });

  describe('Owner Selection', () => {
    it('should extract owner from initial tags', () => {
      // The test data doesn't include an owner tag, so selectedOwner should be null
      expect(component.selectedOwner).toBeNull();
    });

    it('should filter owner tag from displayed tags and set selectedOwner when present in data', () => {
      // Create a new component with owner tag in the data
      const dataWithOwner: BookEditDialogData = {
        ...testDialogData,
        tags: ['test', "Dad's Books", 'another-tag']
      };

      // We need to create a new component instance with this data
      const mockDialog = jasmine.createSpyObj('MatDialog', ['open']);
      const testComponent = new BookEditDialogComponent(
        mockDialogRef,
        dataWithOwner,
        mockGenreMappingService,
        mockLibraryApiService,
        mockRouter,
        { log: () => {}, error: () => {}, warn: () => {} } as any, // mock logger
        mockDialog
      );

      expect(testComponent.selectedOwner).toBe("Dad's Books");
      expect(testComponent.tags).toEqual(['test', 'another-tag']);
      expect(testComponent.tags).not.toContain("Dad's Books");
    });

    it('should include owner tag in saved tags when owner is selected', () => {
      // Arrange
      component.selectedOwner = "Dad's Books";
      component.tags = ['test-tag'];

      // Act
      component.onSave();

      // Assert
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.tags).toContain("Dad's Books");
      expect(callArgs.tags).toContain('test-tag');
      expect(callArgs.owner).toBe("Dad's Books");
    });

    it('should not include owner tag in saved tags when owner is null', () => {
      // Arrange
      component.selectedOwner = null;
      component.tags = ['test-tag'];

      // Act
      component.onSave();

      // Assert
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.tags).toEqual(['test-tag']);
      expect(callArgs.owner).toBeNull();
    });

    it('should filter owner tags from genres list', () => {
      // Owner tags should not appear in the genres dropdown
      expect(component.genres).not.toContain("Dad's Books");
      expect(component.genres).not.toContain("Mom's Books");
      expect(component.genres).not.toContain("Paul's Books");
    });
  });

  describe('Genre Selection', () => {
    it('should return available genres excluding those already in tags', () => {
      // Arrange
      component.tags = ['Science Fiction'];

      // Act
      const available = component.availableGenres;

      // Assert
      expect(available).not.toContain('Science Fiction');
      expect(available).toContain('Fantasy');
      expect(available).toContain('Mystery & Detective');
      expect(available).not.toContain('Uncategorized'); // Uncategorized should be excluded
    });

    it('should add genre to tags when onGenreSelected is called', () => {
      // Arrange
      component.tags = ['existing-tag'];

      // Act
      component.onGenreSelected('Fantasy');

      // Assert
      expect(component.tags).toContain('Fantasy');
      expect(component.tags).toContain('existing-tag');
    });

    it('should not add duplicate genres to tags (case-insensitive)', () => {
      // Arrange
      component.tags = ['Fantasy'];

      // Act
      component.onGenreSelected('fantasy'); // lowercase

      // Assert - should still only have one Fantasy tag
      const fantasyCount = component.tags.filter(t => t.toLowerCase() === 'fantasy').length;
      expect(fantasyCount).toBe(1);
    });

    it('should handle null genre selection', () => {
      // Arrange
      const initialTags = [...component.tags];

      // Act
      component.onGenreSelected(null);

      // Assert
      expect(component.tags).toEqual(initialTags);
    });
  });

  describe('Keyboard Delete Shortcut', () => {
    beforeEach(() => {
      // Reset state before each test
      component.isDeleting = false;
      component.deleteConfirmPending = false;
      component.data.fileName = 'test-book.epub';
    });

    it('should set deleteConfirmPending when initiateDeleteConfirm is called', () => {
      // Act
      component.initiateDeleteConfirm();

      // Assert
      expect(component.deleteConfirmPending).toBe(true);
    });

    it('should not set deleteConfirmPending if already deleting', () => {
      // Arrange
      component.isDeleting = true;

      // Act
      component.initiateDeleteConfirm();

      // Assert
      expect(component.deleteConfirmPending).toBe(false);
    });

    it('should not set deleteConfirmPending if no fileName', () => {
      // Arrange
      component.data.fileName = null;

      // Act
      component.initiateDeleteConfirm();

      // Assert
      expect(component.deleteConfirmPending).toBe(false);
    });

    it('should cancel delete confirmation when cancelDeleteConfirm is called', () => {
      // Arrange
      component.deleteConfirmPending = true;

      // Act
      component.cancelDeleteConfirm();

      // Assert
      expect(component.deleteConfirmPending).toBe(false);
    });

    it('should execute delete when executeDelete is called', () => {
      // Arrange
      mockLibraryApiService.deleteLibraryBook.and.returnValue(of({ success: true }));

      // Act
      (component as any).executeDelete();

      // Assert
      expect(mockLibraryApiService.deleteLibraryBook).toHaveBeenCalledWith('test-book.epub');
    });

    it('should close dialog with deleted true after successful delete', () => {
      // Arrange
      mockLibraryApiService.deleteLibraryBook.and.returnValue(of({ success: true }));

      // Act
      (component as any).executeDelete();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith({ deleted: true });
    });

    it('should set isDeleting to false on delete error', () => {
      // Arrange
      mockLibraryApiService.deleteLibraryBook.and.returnValue(throwError(() => new Error('Delete failed')));

      // Act
      (component as any).executeDelete();

      // Assert
      expect(component.isDeleting).toBe(false);
    });

    it('should handle enter key by executing delete when confirmation is pending', () => {
      // Arrange
      component.deleteConfirmPending = true;
      mockLibraryApiService.deleteLibraryBook.and.returnValue(of({ success: true }));
      const event = new KeyboardEvent('keydown', { key: 'Enter' });
      spyOn(event, 'preventDefault');

      // Act
      component.handleEnterKey(event);

      // Assert
      expect(mockLibraryApiService.deleteLibraryBook).toHaveBeenCalled();
    });

    it('should handle escape key by canceling delete confirmation', () => {
      // Arrange
      component.deleteConfirmPending = true;
      const event = new KeyboardEvent('keydown', { key: 'Escape' });
      spyOn(event, 'preventDefault');

      // Act
      component.handleEscapeKey(event);

      // Assert
      expect(component.deleteConfirmPending).toBe(false);
      expect(mockDialogRef.close).not.toHaveBeenCalled();
    });
  });
});

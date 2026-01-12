import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookEditDialogComponent, BookEditDialogData } from './book-edit-dialog.component';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { GenreMappingService } from '../../services/genre-mapping.service';
import { AnnaArchiveApiService } from '../../services/anna-archive-api.service';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';

describe('BookEditDialogComponent', () => {
  let component: BookEditDialogComponent;
  let fixture: ComponentFixture<BookEditDialogComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<BookEditDialogComponent>>;
  let mockGenreMappingService: jasmine.SpyObj<GenreMappingService>;
  let mockApiService: jasmine.SpyObj<AnnaArchiveApiService>;
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
    mockApiService = jasmine.createSpyObj('AnnaArchiveApiService', [
      'deleteLibraryBook',
      'sendLibraryToKindle',
      'updateLibraryBookReaderEnabled'
    ]);
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
        { provide: AnnaArchiveApiService, useValue: mockApiService },
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

    it('should not return coverUrl when selected cover is same as original', () => {
      // Arrange
      component.selectedCoverUrl = testDialogData.coverUrl || null; // Same as original

      // Act
      component.onSave();

      // Assert
      const callArgs = mockDialogRef.close.calls.mostRecent().args[0];
      expect(callArgs.coverUrl).toBeNull();
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
      component.selectedGenre = 'Science Fiction';
      component.tags = ['updated-tag'];
      component.series = 'Updated Series';
      component.selectedCoverUrl = 'http://example.com/new-cover.jpg';

      // Act
      component.onSave();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith({
        primaryGenre: 'Science Fiction',
        tags: ['updated-tag'],
        series: 'Updated Series',
        title: 'Updated Title',
        authors: ['Updated Author'],
        coverUrl: 'http://example.com/new-cover.jpg'
      });
    });
  });
});

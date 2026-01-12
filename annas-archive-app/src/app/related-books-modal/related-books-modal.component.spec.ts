import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RelatedBooksModalComponent, RelatedBooksModalData } from './related-books-modal.component';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

describe('RelatedBooksModalComponent', () => {
  let component: RelatedBooksModalComponent;
  let fixture: ComponentFixture<RelatedBooksModalComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<RelatedBooksModalComponent>>;
  let mockApiService: jasmine.SpyObj<AnnaArchiveApiService>;

  const mockDialogData: RelatedBooksModalData = {
    bookTitle: 'Test Book',
    author: 'Test Author',
    sameSeries: [],
    otherSeries: [],
    seriesSummary: null,
    loading: false,
    mode: 'related'
  };

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    mockApiService = jasmine.createSpyObj('AnnaArchiveApiService', [
      'searchBooks',
      'matchSeriesBooks',
      'sendToLibrary',
      'sendToBoox',
      'sendToKindle',
      'fetchCover'
    ]);

    // Default mock implementations
    mockApiService.fetchCover.and.returnValue(of({ coverUrl: 'http://example.com/cover.jpg' }));

    await TestBed.configureTestingModule({
      imports: [
        RelatedBooksModalComponent,
        NoopAnimationsModule
      ],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: AnnaArchiveApiService, useValue: mockApiService }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(RelatedBooksModalComponent);
    component = fixture.componentInstance;
  });

  describe('removeMatchResult', () => {
    it('should remove a match result from the matchResults array', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: [],
        reason: 'Test reason 1'
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'ambiguous' as const,
        candidates: [],
        reason: 'Test reason 2'
      };
      const result3 = {
        key: 'book3',
        title: 'Book Three',
        status: 'missing' as const,
        candidates: [],
        reason: 'Test reason 3'
      };
      component.matchResults = [result1, result2, result3];

      // Act
      component.removeMatchResult(result2);

      // Assert
      expect(component.matchResults.length).toBe(2);
      expect(component.matchResults).toEqual([result1, result3]);
      expect(component.matchResults).not.toContain(result2);
    });

    it('should remove the first match result in the array', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'matched' as const,
        candidates: []
      };
      component.matchResults = [result1, result2];

      // Act
      component.removeMatchResult(result1);

      // Assert
      expect(component.matchResults.length).toBe(1);
      expect(component.matchResults).toEqual([result2]);
    });

    it('should remove the last match result in the array', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'matched' as const,
        candidates: []
      };
      component.matchResults = [result1, result2];

      // Act
      component.removeMatchResult(result2);

      // Assert
      expect(component.matchResults.length).toBe(1);
      expect(component.matchResults).toEqual([result1]);
    });

    it('should handle removing a match result that does not exist', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'matched' as const,
        candidates: []
      };
      const resultNotInArray = {
        key: 'book99',
        title: 'Not in Array',
        status: 'matched' as const,
        candidates: []
      };
      component.matchResults = [result1, result2];

      // Act
      component.removeMatchResult(resultNotInArray);

      // Assert - array should remain unchanged
      expect(component.matchResults.length).toBe(2);
      expect(component.matchResults).toEqual([result1, result2]);
    });

    it('should handle removing from an empty array', () => {
      // Arrange
      component.matchResults = [];
      const result = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };

      // Act
      component.removeMatchResult(result);

      // Assert
      expect(component.matchResults.length).toBe(0);
    });

    it('should remove only one instance when duplicate match results exist', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'matched' as const,
        candidates: []
      };
      component.matchResults = [result1, result2, result1]; // result1 appears twice

      // Act
      component.removeMatchResult(result1);

      // Assert - only first instance should be removed
      expect(component.matchResults.length).toBe(2);
      expect(component.matchResults).toEqual([result2, result1]);
    });

    it('should work correctly after removing all match results one by one', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: []
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'ambiguous' as const,
        candidates: []
      };
      const result3 = {
        key: 'book3',
        title: 'Book Three',
        status: 'missing' as const,
        candidates: []
      };
      component.matchResults = [result1, result2, result3];

      // Act
      component.removeMatchResult(result1);
      expect(component.matchResults.length).toBe(2);

      component.removeMatchResult(result2);
      expect(component.matchResults.length).toBe(1);

      component.removeMatchResult(result3);

      // Assert
      expect(component.matchResults.length).toBe(0);
      expect(component.matchResults).toEqual([]);
    });
  });

  describe('canSend getter with removed match results', () => {
    it('should return false when all match results have been removed', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: [],
        selected: {
          md5: 'test-md5',
          title: 'Book One',
          authors: ['Author'],
          format: 'EPUB',
          fileSize: '1MB',
          language: 'English',
          source: 'test',
          bookType: 'book',
          publisher: 'Test',
          year: 2024,
          isbn: null,
          sendState: 'idle' as const,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const,
          coverCandidates: []
        }
      };
      component.matchResults = [result1];
      component.preparingMatches = false;
      component.sending = false;

      // Initially canSend should be true
      expect(component.canSend).toBe(true);

      // Act - remove the only match result
      component.removeMatchResult(result1);

      // Assert
      expect(component.canSend).toBe(false);
    });

    it('should still return true when some match results remain after removal', () => {
      // Arrange
      const result1 = {
        key: 'book1',
        title: 'Book One',
        status: 'matched' as const,
        candidates: [],
        selected: {
          md5: 'test-md5-1',
          title: 'Book One',
          authors: ['Author'],
          format: 'EPUB',
          fileSize: '1MB',
          language: 'English',
          source: 'test',
          bookType: 'book',
          publisher: 'Test',
          year: 2024,
          isbn: null,
          sendState: 'idle' as const,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const,
          coverCandidates: []
        }
      };
      const result2 = {
        key: 'book2',
        title: 'Book Two',
        status: 'matched' as const,
        candidates: [],
        selected: {
          md5: 'test-md5-2',
          title: 'Book Two',
          authors: ['Author'],
          format: 'EPUB',
          fileSize: '1MB',
          language: 'English',
          source: 'test',
          bookType: 'book',
          publisher: 'Test',
          year: 2024,
          isbn: null,
          sendState: 'idle' as const,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const,
          coverCandidates: []
        }
      };
      component.matchResults = [result1, result2];
      component.preparingMatches = false;
      component.sending = false;

      // Act - remove one result
      component.removeMatchResult(result1);

      // Assert - should still be able to send remaining result
      expect(component.canSend).toBe(true);
      expect(component.matchResults.length).toBe(1);
    });
  });

  describe('Component initialization', () => {
    it('should initialize with empty matchResults array', () => {
      expect(component.matchResults).toEqual([]);
    });

    it('should initialize with empty selectedKeys set', () => {
      expect(component.selectedKeys.size).toBe(0);
    });

    it('should initialize with EPUB as default format', () => {
      expect(component.selectedFormat).toBe('EPUB');
    });

    it('should initialize with preparingMatches as false', () => {
      expect(component.preparingMatches).toBe(false);
    });

    it('should initialize with sending as false', () => {
      expect(component.sending).toBe(false);
    });
  });

  describe('Dialog actions', () => {
    it('should close dialog when onClose is called', () => {
      // Act
      component.onClose();

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalled();
    });

    it('should close dialog with search data when onSearchBook is called', () => {
      // Act
      component.onSearchBook('Harry Potter');

      // Assert
      expect(mockDialogRef.close).toHaveBeenCalledWith({
        searchBook: 'Harry Potter',
        author: 'Test Author'
      });
    });
  });

  describe('Description Source Icons', () => {
    it('should display robot icon when descriptionSource is "gpt"', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'gpt'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcon = compiled.querySelector('.robot-icon');

      // Assert
      expect(robotIcon).toBeTruthy();
      expect(robotIcon?.textContent?.trim()).toBe('smart_toy');
      expect(robotIcon?.getAttribute('aria-label')).toBe('AI-generated description');
    });

    it('should display leaf icon when descriptionSource is "openlibrary"', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'openlibrary'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const leafIcon = compiled.querySelector('.leaf-icon');

      // Assert
      expect(leafIcon).toBeTruthy();
      expect(leafIcon?.textContent?.trim()).toBe('eco');
      expect(leafIcon?.getAttribute('aria-label')).toBe('OpenLibrary description');
    });

    it('should display book icon when descriptionSource is "googlebooks"', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'googlebooks'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const bookIcon = compiled.querySelector('.book-icon');

      // Assert
      expect(bookIcon).toBeTruthy();
      expect(bookIcon?.textContent?.trim()).toBe('menu_book');
      expect(bookIcon?.getAttribute('aria-label')).toBe('Google Books description');
    });

    it('should not display any icon when descriptionSource is null', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: null
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcon = compiled.querySelector('.robot-icon');
      const bookIcon = compiled.querySelector('.book-icon');
      const leafIcon = compiled.querySelector('.leaf-icon');

      // Assert
      expect(robotIcon).toBeFalsy();
      expect(bookIcon).toBeFalsy();
      expect(leafIcon).toBeFalsy();
    });

    it('should not display any icon when descriptionSource is undefined', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg'
        // descriptionSource is undefined
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcon = compiled.querySelector('.robot-icon');
      const bookIcon = compiled.querySelector('.book-icon');
      const leafIcon = compiled.querySelector('.leaf-icon');

      // Assert
      expect(robotIcon).toBeFalsy();
      expect(bookIcon).toBeFalsy();
      expect(leafIcon).toBeFalsy();
    });

    it('should display icons in "Same Series" section', () => {
      // Arrange
      component.data.sameSeries = [
        {
          title: 'Book One',
          order: 1,
          description: 'GPT description',
          coverUrl: 'http://example.com/cover1.jpg',
          descriptionSource: 'gpt'
        },
        {
          title: 'Book Two',
          order: 2,
          description: 'Google Books description',
          coverUrl: 'http://example.com/cover2.jpg',
          descriptionSource: 'googlebooks'
        },
        {
          title: 'Book Three',
          order: 3,
          description: 'OpenLibrary description',
          coverUrl: 'http://example.com/cover3.jpg',
          descriptionSource: 'openlibrary'
        }
      ];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcons = compiled.querySelectorAll('.robot-icon');
      const bookIcons = compiled.querySelectorAll('.book-icon');
      const leafIcons = compiled.querySelectorAll('.leaf-icon');

      // Assert
      expect(robotIcons.length).toBe(1);
      expect(bookIcons.length).toBe(1);
      expect(leafIcons.length).toBe(1);
    });

    it('should display icons in "Other Series" section', () => {
      // Arrange
      component.data.mode = 'related'; // Ensure other series section is visible
      component.data.otherSeries = [{
        seriesName: 'Test Series',
        bookCount: 3,
        description: 'Series description',
        summary: 'Series summary',
        books: [
          {
            title: 'Book One',
            order: 1,
            description: 'GPT description',
            coverUrl: 'http://example.com/cover1.jpg',
            descriptionSource: 'gpt'
          },
          {
            title: 'Book Two',
            order: 2,
            description: 'Google Books description',
            coverUrl: 'http://example.com/cover2.jpg',
            descriptionSource: 'googlebooks'
          },
          {
            title: 'Book Three',
            order: 3,
            description: 'OpenLibrary description',
            coverUrl: 'http://example.com/cover3.jpg',
            descriptionSource: 'openlibrary'
          }
        ]
      }];
      // Expand the series to make books visible
      component.expandedSeries.add('Test Series');
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcons = compiled.querySelectorAll('.robot-icon');
      const bookIcons = compiled.querySelectorAll('.book-icon');
      const leafIcons = compiled.querySelectorAll('.leaf-icon');

      // Assert
      expect(robotIcons.length).toBeGreaterThanOrEqual(1);
      expect(bookIcons.length).toBeGreaterThanOrEqual(1);
      expect(leafIcons.length).toBeGreaterThanOrEqual(1);
    });

    it('should display robot icon with correct CSS classes', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'gpt'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcon = compiled.querySelector('.robot-icon');

      // Assert
      expect(robotIcon?.classList.contains('description-source-icon')).toBe(true);
      expect(robotIcon?.classList.contains('robot-icon')).toBe(true);
    });

    it('should display leaf icon with correct CSS classes', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'openlibrary'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const leafIcon = compiled.querySelector('.leaf-icon');

      // Assert
      expect(leafIcon?.classList.contains('description-source-icon')).toBe(true);
      expect(leafIcon?.classList.contains('leaf-icon')).toBe(true);
    });

    it('should display book icon with correct CSS classes', () => {
      // Arrange
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'googlebooks'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const bookIcon = compiled.querySelector('.book-icon');

      // Assert
      expect(bookIcon?.classList.contains('description-source-icon')).toBe(true);
      expect(bookIcon?.classList.contains('book-icon')).toBe(true);
    });

    it('should display icons in AI search results mode', () => {
      // Arrange
      component.data.mode = 'ai';
      component.data.sameSeries = [{
        title: 'Test Book',
        order: 1,
        description: 'Test description',
        coverUrl: 'http://example.com/cover.jpg',
        descriptionSource: 'gpt'
      }];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcon = compiled.querySelector('.robot-icon');

      // Assert
      expect(robotIcon).toBeTruthy();
      expect(robotIcon?.textContent?.trim()).toBe('smart_toy');
    });

    it('should handle mixed descriptionSource values in same series', () => {
      // Arrange
      component.data.sameSeries = [
        {
          title: 'Book One',
          order: 1,
          description: 'GPT description',
          descriptionSource: 'gpt'
        },
        {
          title: 'Book Two',
          order: 2,
          description: 'Google Books description',
          descriptionSource: 'googlebooks'
        },
        {
          title: 'Book Three',
          order: 3,
          description: 'OpenLibrary description',
          descriptionSource: 'openlibrary'
        },
        {
          title: 'Book Four',
          order: 4,
          description: 'No source description',
          descriptionSource: null
        }
      ];
      fixture.detectChanges();

      // Act
      const compiled = fixture.nativeElement;
      const robotIcons = compiled.querySelectorAll('.robot-icon');
      const bookIcons = compiled.querySelectorAll('.book-icon');
      const leafIcons = compiled.querySelectorAll('.leaf-icon');
      const allIcons = compiled.querySelectorAll('.description-source-icon');

      // Assert
      expect(robotIcons.length).toBe(1);
      expect(bookIcons.length).toBe(1);
      expect(leafIcons.length).toBe(1);
      expect(allIcons.length).toBe(3); // Only 3 books have icons
    });
  });
});

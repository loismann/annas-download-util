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
});
